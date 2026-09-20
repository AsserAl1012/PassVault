using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
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

    private readonly DispatcherTimer _idleWatch = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer _clipboardWatch = new() { Interval = ClearClipboardAfter };
    private readonly DispatcherTimer _toastWatch = new() { Interval = TimeSpan.FromSeconds(4) };

    private Entry? _chosen;
    private string _realPassword = "";
    private bool _passwordHidden = true;
    private bool _filling;
    private DateTime _lastActive = DateTime.UtcNow;
    private string? _lastCopied;
    private bool _locking;

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

        _idleWatch.Tick += (_, _) =>
        {
            if (DateTime.UtcNow - _lastActive > LockAfterIdle) Lock();
        };
        _idleWatch.Start();

        _clipboardWatch.Tick += (_, _) => ClearClipboard();
        _toastWatch.Tick += (_, _) => { ToastText.Text = ""; _toastWatch.Stop(); };

        SearchBox.Focus();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _idleWatch.Stop();
        _clipboardWatch.Stop();
        _toastWatch.Stop();
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

        _chosen = entry;
        _filling = true;

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
        if (!_filling) SaveButton.IsEnabled = true;
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

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_chosen is null) return;

        var confirm = MessageBox.Show(
            $"Delete \"{_chosen.Name}\"?\n\nThis cannot be undone.",
            "PassVault", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

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
        => StatusText.Text = $"{Living.Count} credential(s)   ·   stick {_stickRoot}   ·   locks itself after {LockAfterIdle.TotalMinutes:0} minutes idle";

    private void Toast(string text, bool good = true)
    {
        ToastText.Text = text;
        ToastText.Foreground = (System.Windows.Media.Brush)FindResource(good ? "Good" : "Danger");
        _toastWatch.Stop();
        _toastWatch.Start();
    }

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
