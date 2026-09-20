using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PassVault.Core.Licensing;
using PassVault.Core.Machine;
using PassVault.Core.Model;
using PassVault.Core.Usb;
using PassVault.Core.Vault;

namespace PassVault.App;

public partial class MainWindow : Window
{
    private static readonly TimeSpan LockAfterIdle = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ClearClipboardAfter = TimeSpan.FromSeconds(20);
    private const string Dots = "••••••••••";

    private readonly string _stickRoot;
    private readonly byte[] _masterKey;
    private readonly List<Entry> _all;

    private readonly DispatcherTimer _idleWatch = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _clipboardWatch = new() { Interval = ClearClipboardAfter };
    private readonly DispatcherTimer _toastWatch = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _confirmWatch = new() { Interval = TimeSpan.FromSeconds(8) };

    private Entry? _chosen;
    private string _realPassword = "";
    private bool _passwordHidden = true;
    private bool _filling;
    private DateTime _lastActive = DateTime.UtcNow;
    private string? _lastCopied;
    private bool _locking;
    private bool _holdingMessage;
    private string _restingDetail = "";

    public MainWindow(string stickRoot, byte[] masterKey)
    {
        InitializeComponent();

        _stickRoot = stickRoot;
        _masterKey = masterKey;
        _all = VaultFile.ReadEntries(VaultStorage.ReadVault(stickRoot), masterKey);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DarkTitleBar.Apply(this);
        ApplyFilter();
        ShowStatus();

        _idleWatch.Tick += (_, _) => Countdown();
        _idleWatch.Start();

        _clipboardWatch.Tick += (_, _) => ClearClipboard();
        _toastWatch.Tick += (_, _) => ClearMessage();
        _confirmWatch.Tick += (_, _) => CancelDelete();

        ShowLicence();
        SearchBox.Focus();
    }

    /// <summary>
    /// The free version is complete and safe, so this is a label rather than a nag. Once
    /// bought it shows the owner's name, which is the point of buying it.
    /// </summary>
    private void ShowLicence()
    {
        var licence = LicenceStore.Load(MachineStore.DefaultFolder);
        LicenceButton.Content = licence is null ? "Free version" : licence.Name;
    }

    private void OnLicence(object sender, RoutedEventArgs e)
    {
        new LicenceWindow { Owner = this }.ShowDialog();
        ShowLicence();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _idleWatch.Stop();
        _clipboardWatch.Stop();
        _toastWatch.Stop();
        _confirmWatch.Stop();
        ClearClipboard();
        Array.Clear(_masterKey);
    }

    private void OnUserActive(object sender, InputEventArgs e) => _lastActive = DateTime.UtcNow;

    // ---- the list ----

    private List<Entry> Living => _all.Where(e => !e.Deleted).ToList();

    private void ApplyFilter()
    {
        var search = SearchBox.Text;
        SearchHint.Visibility = string.IsNullOrEmpty(search) ? Visibility.Visible : Visibility.Collapsed;

        var shown = Living.Where(e => e.Matches(search))
                          .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                          .ToList();

        var keep = _chosen;
        EntryList.ItemsSource = shown;
        EmptyListText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyListText.Text = Living.Count == 0 ? "Nothing saved yet" : "Nothing matches that";

        if (keep is not null && shown.Contains(keep)) EntryList.SelectedItem = keep;
    }

    private void OnSearchChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    private void OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not Entry entry)
        {
            Details.Visibility = Visibility.Collapsed;
            NothingChosen.Visibility = Visibility.Visible;
            _chosen = null;
            return;
        }

        CancelDelete();

        _chosen = entry;
        _filling = true;

        DetailMark.Site = entry.Name;
        NameBox.Text = entry.Name;
        UsernameBox.Text = entry.Username ?? "";
        EmailBox.Text = entry.Email ?? "";
        NotesBox.Text = entry.Notes ?? "";

        _realPassword = entry.Password ?? "";
        _passwordHidden = true;
        PaintPassword();

        _filling = false;
        SaveButton.IsEnabled = false;
        Details.Visibility = Visibility.Visible;
        NothingChosen.Visibility = Visibility.Collapsed;
    }

    private void OnFieldEdited(object sender, RoutedEventArgs e)
    {
        if (_filling) return;

        SaveButton.IsEnabled = true;

        // The mark follows what you are typing, so it turns into the real logo the moment
        // the name is recognisable rather than waiting for a save.
        if (ReferenceEquals(sender, NameBox))
        {
            DetailMark.Site = NameBox.Text;
        }
    }

    // ---- the password box ----

    private void PaintPassword()
    {
        _filling = true;
        PasswordBoxText.Text = _passwordHidden && _realPassword.Length > 0 ? Dots : _realPassword;
        PasswordBoxText.IsReadOnly = _passwordHidden && _realPassword.Length > 0;
        ShowHideGlyph.Text = _passwordHidden ? "" : "";
        _filling = false;
    }

    private void OnToggleShowPassword(object sender, RoutedEventArgs e)
    {
        if (!_passwordHidden) _realPassword = PasswordBoxText.Text;
        _passwordHidden = !_passwordHidden;
        PaintPassword();
    }

    private string CurrentPassword() => _passwordHidden ? _realPassword : PasswordBoxText.Text;

    // ---- adding, saving, deleting ----

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var entry = new Entry { Name = "New credential" };
        _all.Add(entry);
        _chosen = entry;

        SearchBox.Clear();
        ApplyFilter();
        EntryList.SelectedItem = entry;

        NameBox.SelectAll();
        NameBox.Focus();
        SaveButton.IsEnabled = true;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_chosen is null) return;

        var name = NameBox.Text.Trim();
        if (name.Length == 0) { Toast("Give it a name first", good: false); NameBox.Focus(); return; }

        _chosen.Name = name;
        _chosen.Username = Blank(UsernameBox.Text);
        _chosen.Email = Blank(EmailBox.Text);
        _chosen.Password = Blank(CurrentPassword());
        _chosen.Notes = Blank(NotesBox.Text);
        _chosen.UpdatedAt = Entry.Now();
        _realPassword = _chosen.Password ?? "";

        if (!Persist()) return;

        SaveButton.IsEnabled = false;
        ApplyFilter();
        ShowStatus();
        Toast("Saved to your stick and the spare copy");
    }

    /// <summary>Ask, in the footer. Backs out on its own after 8 seconds of no answer.</summary>
    private void OnDeleteAsked(object sender, RoutedEventArgs e)
    {
        if (_chosen is null) return;

        ConfirmText.Text = $"Delete “{_chosen.Name}” for good?";
        CrossFadeFooter(toConfirm: true);

        _confirmWatch.Stop();
        _confirmWatch.Start();
    }

    private void OnDeleteCancelled(object sender, RoutedEventArgs e) => CancelDelete();

    private void CancelDelete()
    {
        _confirmWatch.Stop();
        if (FooterConfirm.Visibility == Visibility.Visible) CrossFadeFooter(toConfirm: false);
    }

    private void CrossFadeFooter(bool toConfirm)
    {
        UIElement going = toConfirm ? FooterNormal : FooterConfirm;
        UIElement coming = toConfirm ? FooterConfirm : FooterNormal;
        var travel = (Duration)FindResource("Travel");

        coming.Visibility = Visibility.Visible;
        coming.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = travel });

        var leaving = new DoubleAnimation { To = 0, Duration = travel };
        leaving.Completed += (_, _) => going.Visibility = Visibility.Collapsed;
        going.BeginAnimation(OpacityProperty, leaving);
    }

    private void OnDeleteConfirmed(object sender, RoutedEventArgs e)
    {
        _confirmWatch.Stop();
        CrossFadeFooter(toConfirm: false);

        if (_chosen is null) return;

        // Kept as a tombstone rather than removed, so a deleted entry cannot come
        // back from another device once sync exists.
        _chosen.Deleted = true;
        _chosen.Name = "";
        _chosen.Username = _chosen.Email = _chosen.Password = _chosen.Notes = null;
        _chosen.UpdatedAt = Entry.Now();

        if (!Persist()) return;

        _chosen = null;
        EntryList.SelectedItem = null;
        Details.Visibility = Visibility.Collapsed;
        NothingChosen.Visibility = Visibility.Visible;
        ApplyFilter();
        ShowStatus();
        Toast("Deleted");
    }

    private bool Persist()
    {
        try
        {
            var saved = VaultFile.Save(VaultStorage.ReadVault(_stickRoot), _masterKey, _all);
            Enrolment.SaveVault(MachineStore.DefaultFolder, _stickRoot, saved);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not save.\n\n{ex.Message}\n\nIs your stick still plugged in?",
                "PassVault", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // ---- copying ----

    private void OnCopyUsername(object sender, RoutedEventArgs e) => Copy(UsernameBox.Text, "Username");
    private void OnCopyEmail(object sender, RoutedEventArgs e) => Copy(EmailBox.Text, "Email");
    private void OnCopyPassword(object sender, RoutedEventArgs e) => Copy(CurrentPassword(), "Password");

    private void Copy(string? value, string what)
    {
        if (string.IsNullOrEmpty(value)) return;

        try
        {
            Clipboard.SetText(value);
            _lastCopied = value;
            _clipboardWatch.Stop();
            _clipboardWatch.Start();
            Toast($"{what} copied - the clipboard clears in {ClearClipboardAfter.TotalSeconds:0} seconds");
        }
        catch (Exception)
        {
            Toast("Windows would not let go of the clipboard - try again", good: false);
        }
    }

    /// <summary>
    /// Only wipe what we put there. If the user has copied something else since, leaving
    /// their clipboard alone matters more.
    /// </summary>
    private void ClearClipboard()
    {
        _clipboardWatch.Stop();
        if (_lastCopied is null) return;

        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText() == _lastCopied) Clipboard.Clear();
        }
        catch (Exception)
        {
            // The clipboard was busy. Not worth bothering the user about.
        }

        _lastCopied = null;
    }

    // ---- backing up ----

    /// <summary>
    /// The backup is just the encrypted vault. That file plus your 12 words is a complete
    /// recovery - no stick, no PC needed. Safe in a cloud folder because it is encrypted,
    /// but copying the whole stick there would NOT be: that hands over the USB secret too.
    /// </summary>
    private void OnBackup(object sender, RoutedEventArgs e)
    {
        byte[] vault;
        try
        {
            vault = VaultStorage.ReadVault(_stickRoot);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read your vault.\n\n{ex.Message}", "PassVault",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var picker = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save a backup of your vault",
            Filter = "PassVault backup (*.pv)|*.pv",
            FileName = Recovery.SuggestBackupName(vault, DateTime.Now),
        };
        if (picker.ShowDialog() != true) return;

        try
        {
            Recovery.ExportBackup(vault, picker.FileName);
            MessageBox.Show(
                "Backup saved.\n\nThis file plus your 12 words gets everything back, with no stick and no PC.\n\n"
                + "It is encrypted, so a cloud folder is fine. Do not copy the whole stick there though - "
                + "that would hand over the USB secret as well.",
                "PassVault", MessageBoxButton.OK, MessageBoxImage.Information);
            Toast("Backup saved");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save the backup.\n\n{ex.Message}", "PassVault",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---- locking ----

    private void OnLock(object sender, RoutedEventArgs e) => Lock();

    private void Lock()
    {
        if (_locking) return;
        _locking = true;

        var unlock = new UnlockWindow();
        Application.Current.MainWindow = unlock;
        unlock.Show();
        Close();
    }

    // ---- little bits ----

    private void ShowStatus()
    {
        _restingDetail = $"{_stickRoot}  ·  {Living.Count} credential{(Living.Count == 1 ? "" : "s")}";
        if (!_holdingMessage)
        {
            Band.State = "Vault open";
            Band.Detail = _restingDetail;
            Band.Mood = Mood.Good;
        }
    }

    /// <summary>
    /// How long until this locks itself. Ticking every second is the point - the old timer
    /// ticked every 20 seconds and showed nothing, so the vault just vanished on you.
    /// </summary>
    private void Countdown()
    {
        var left = LockAfterIdle - (DateTime.UtcNow - _lastActive);

        if (left <= TimeSpan.Zero) { Lock(); return; }

        Band.Trailing = $"locks in {left.Minutes}:{left.Seconds:00}";
        Band.TrailingAlert = left.TotalSeconds <= 60;
    }

    /// <summary>
    /// A success clears itself after four seconds. Anything you have to act on stays until
    /// you act. No timer ever clears an error - otherwise "Windows would not let go of the
    /// clipboard" looks exactly like success, four seconds later.
    /// </summary>
    private void Toast(string text, bool good = true)
    {
        _holdingMessage = true;
        Band.Detail = text;
        Band.Mood = good ? Mood.Good : Mood.Bad;

        _toastWatch.Stop();
        if (good) _toastWatch.Start();
    }

    private void ClearMessage()
    {
        _toastWatch.Stop();
        _holdingMessage = false;
        ShowStatus();
    }

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
