using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private const string Dots = "••••••••••";

    private readonly string _stickRoot;
    private readonly byte[] _masterKey;
    private readonly List<Entry> _all;

    /// <summary>
    /// Which vault this window opened, and how new it was. Checked again before every save -
    /// see Persist. Without this, saving writes your entries into whatever file happens to be
    /// at that drive letter now.
    /// </summary>
    private readonly string _vaultId;
    private readonly int _epoch;
    private long _revision;

    private readonly DispatcherTimer _idleWatch = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _toastWatch = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _confirmWatch = new() { Interval = TimeSpan.FromSeconds(8) };

    private Entry? _chosen;

    /// <summary>
    /// A new credential that has not been saved yet. It is deliberately NOT in _all: a save
    /// writes the whole of _all, so an entry that joined the list the moment you pressed Add
    /// got committed to the stick by the next save of something else - a row called
    /// "New credential" that you never asked for and cannot get rid of without a tombstone.
    /// </summary>
    private Entry? _draft;

    /// <summary>Which credential the delete question in the footer is about.</summary>
    private Entry? _deleting;

    /// <summary>Where to go once the unsaved-changes question has been answered.</summary>
    private Action? _afterAnswer;

    private Footer _footer = Footer.Normal;
    private bool _reselecting;
    private string _realPassword = "";
    private bool _passwordHidden = true;
    private bool _filling;
    private DateTime _lastActive = DateTime.UtcNow;
    private bool _locking;
    private bool _closingForReal;
    private bool _holdingMessage;
    private bool _badMessage;
    private string _restingDetail = "";

    public MainWindow(string stickRoot, byte[] masterKey)
    {
        InitializeComponent();

        _stickRoot = stickRoot;
        _masterKey = masterKey;

        var vault = VaultStorage.ReadVault(stickRoot);
        var header = VaultFile.ReadHeader(vault);
        _vaultId = header.VaultId;
        _epoch = header.Epoch;
        _revision = header.Revision;
        _all = VaultFile.ReadEntries(vault, masterKey);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DarkTitleBar.Apply(this);
        ApplyFilter();
        ShowStatus();

        _idleWatch.Tick += (_, _) => Countdown();
        _idleWatch.Start();

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
        WhileDialogIsUp(() => new LicenceWindow { Owner = this }.ShowDialog());
        ShowLicence();
    }

    /// <summary>
    /// Nothing typed is thrown away just because the window was closed. The X is answered
    /// in the footer like everything else.
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingForReal || _locking || !Dirty) return;

        e.Cancel = true;
        Activate();
        Settled(() => { _closingForReal = true; Close(); });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _idleWatch.Stop();
        _toastWatch.Stop();
        _confirmWatch.Stop();
        Array.Clear(_masterKey);

        // The clipboard is deliberately NOT cleared here. Copy a password, lock the vault,
        // go and paste it - that is the workflow the app invites, and wiping on the way out
        // broke exactly it. ClipboardGuard keeps its own twenty seconds, and App clears it
        // when the whole program exits.
    }

    private void OnUserActive(object sender, InputEventArgs e) => _lastActive = DateTime.UtcNow;

    // ---- the list ----

    private List<Entry> Living => _all.Where(e => !e.Deleted).ToList();

    private void ApplyFilter()
    {
        var search = SearchBox.Text;

        // IsNullOrWhiteSpace, to agree with Entry.Matches. With IsNullOrEmpty a single space
        // hid the hint while matching everything, so the box looked like it was filtering.
        SearchHint.Visibility = string.IsNullOrWhiteSpace(search) ? Visibility.Visible : Visibility.Collapsed;

        var shown = Living.Where(e => e.Matches(search))
                          .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                          .ToList();

        if (_draft is not null && _draft.Matches(search)) shown.Add(_draft);

        // Handing a ListBox a new ItemsSource clears its selection, which used to fire
        // OnSelectionChanged with null and collapse the panel mid-edit - one keystroke in the
        // search box and your typing was gone. Swallow that, then put the highlight back.
        _reselecting = true;
        EntryList.ItemsSource = shown;
        if (_chosen is not null && shown.Contains(_chosen)) EntryList.SelectedItem = _chosen;
        _reselecting = false;

        EmptyListText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyListText.Text = Living.Count == 0 ? "Nothing saved yet" : "Nothing matches that";
    }

    private void OnSearchChanged(object sender, RoutedEventArgs e)
    {
        ClearBadMessage();
        ApplyFilter();
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_reselecting) return;

        var wanted = EntryList.SelectedItem as Entry;
        if (ReferenceEquals(wanted, _chosen)) return;

        if (!Settled(() => Choose(wanted)))
        {
            Reselect(_chosen);      // keep the highlight where the unsaved edit is
            return;
        }

        Choose(wanted);
    }

    private void Reselect(Entry? entry)
    {
        _reselecting = true;
        EntryList.SelectedItem = entry;
        _reselecting = false;
    }

    /// <summary>Put a credential on screen. Everything that changes the panel comes through here.</summary>
    private void Choose(Entry? entry)
    {
        ClearBadMessage();
        CancelDelete();
        Reselect(entry);

        _chosen = entry;

        if (entry is null)
        {
            Details.Visibility = Visibility.Collapsed;
            NothingChosen.Visibility = Visibility.Visible;
            SaveButton.IsEnabled = false;
            return;
        }

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

        ClearBadMessage();
        SaveButton.IsEnabled = true;

        // The mark follows what you are typing, so it turns into the real logo the moment
        // the name is recognisable rather than waiting for a save.
        if (ReferenceEquals(sender, NameBox)) DetailMark.Site = NameBox.Text;
    }

    // ---- unsaved changes ----

    private bool Dirty => SaveButton.IsEnabled;

    /// <summary>
    /// Gate for anything that would replace what is on screen. Returns true when there is
    /// nothing to lose. Otherwise it asks in the footer and remembers where you were going.
    ///
    /// Everything used to just overwrite the panel - clicking another row, locking, closing -
    /// and the only sign was the Save button quietly going grey.
    /// </summary>
    private bool Settled(Action go)
    {
        if (!Dirty) return true;

        _afterAnswer = go;
        UnsavedText.Text = $"Save your changes to {Quoted(NameBox.Text)}?";
        ShowFooter(Footer.Unsaved);
        return false;
    }

    private void OnSaveAndGo(object sender, RoutedEventArgs e)
    {
        if (SaveNow()) Carry();
    }

    private void OnDiscardAndGo(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(_chosen, _draft)) _draft = null;
        SaveButton.IsEnabled = false;
        ApplyFilter();
        Carry();
    }

    private void Carry()
    {
        var go = _afterAnswer;
        _afterAnswer = null;
        ShowFooter(Footer.Normal);
        go?.Invoke();
    }

    // ---- the password box ----

    /// <summary>
    /// True only when the box is actually showing dots instead of the password. An empty
    /// password has nothing to hide, so the box stays editable even with the eye shut.
    /// </summary>
    private bool Masked => _passwordHidden && _realPassword.Length > 0;

    private void PaintPassword()
    {
        _filling = true;
        PasswordBoxText.Text = Masked ? Dots : _realPassword;
        PasswordBoxText.IsReadOnly = Masked;

        // The icon follows what the box is really doing, not the flag. Driving it from
        // _passwordHidden meant a new credential showed its password in plain text under an
        // icon claiming it was hidden, and the first press changed the icon and nothing else.
        ShowHideGlyph.Text = Masked ? "" : "";
        ShowHideButton.ToolTip = Masked ? "Show the password" : "Hide the password";
        _filling = false;
    }

    private void OnToggleShowPassword(object sender, RoutedEventArgs e)
    {
        bool plainTextOnScreen = !PasswordBoxText.IsReadOnly;

        // Take what is on screen before repainting, or typing then pressing the eye loses it.
        if (plainTextOnScreen) _realPassword = PasswordBoxText.Text;

        // Toggle from what the box is ACTUALLY doing, not from the flag. On a new credential
        // the flag still said "hidden" while the box sat there showing the password in plain
        // text, so flipping the flag turned hiding off and the first press did nothing - you
        // had to press it twice to get dots.
        _passwordHidden = plainTextOnScreen;
        PaintPassword();
    }

    /// <summary>
    /// Where the real password is right now.
    ///
    /// One rule: the box holds the truth unless it is read-only, in which case it is showing
    /// dots and the truth is in _realPassword. Asking _passwordHidden instead was a bug - on
    /// a new credential the eye is shut but the box is editable, so everything typed was
    /// thrown away on save.
    /// </summary>
    private string CurrentPassword() => PasswordBoxText.IsReadOnly ? _realPassword : PasswordBoxText.Text;

    // ---- adding, saving, deleting ----

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (!Settled(() => OnAdd(sender, e))) return;

        ClearBadMessage();
        _draft = new Entry { Name = "New credential" };

        SearchBox.Clear();
        ApplyFilter();
        Choose(_draft);
        SaveButton.IsEnabled = true;

        // The panel has only just become visible, so focusing it now does nothing at all -
        // typing straight after pressing Add landed nowhere. Queue it behind layout instead.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { NameBox.Focus(); NameBox.SelectAll(); });
    }

    private void OnSave(object sender, RoutedEventArgs e) => SaveNow();

    private bool SaveNow()
    {
        if (_chosen is null) return false;

        var name = NameBox.Text.Trim();
        if (name.Length == 0) { Toast("Give it a name first", good: false); NameBox.Focus(); return false; }

        var before = Snapshot(_chosen);
        bool isNew = ReferenceEquals(_chosen, _draft);

        _chosen.Name = name;
        _chosen.Username = AsTyped(UsernameBox.Text);
        _chosen.Email = AsTyped(EmailBox.Text);
        _chosen.Password = AsTyped(CurrentPassword());
        _chosen.Notes = AsTyped(NotesBox.Text);
        _chosen.UpdatedAt = Entry.Now();

        if (isNew) _all.Add(_chosen);

        if (!Persist())
        {
            // Put it back exactly as it was. Leaving a half-applied change in memory means
            // the list and the panel disagree, and the next save of anything else commits it.
            if (isNew) _all.Remove(_chosen);
            Restore(_chosen, before);
            ApplyFilter();
            return false;
        }

        if (isNew) _draft = null;

        // Repaint so a password typed in the clear goes behind dots as soon as it is saved,
        // instead of staying on screen until you click somewhere else.
        _realPassword = _chosen.Password ?? "";
        PaintPassword();

        SaveButton.IsEnabled = false;
        ApplyFilter();
        ShowStatus();
        Toast("Saved to your stick and the spare copy");
        return true;
    }

    /// <summary>Ask, in the footer. Backs out on its own after 8 seconds of no answer.</summary>
    private void OnDeleteAsked(object sender, RoutedEventArgs e)
    {
        if (_chosen is null) return;

        _deleting = _chosen;
        ConfirmText.Text = $"Delete {Quoted(NameBox.Text)} for good?";
        ShowFooter(Footer.Confirm);

        _confirmWatch.Stop();
        _confirmWatch.Start();
    }

    private void OnDeleteCancelled(object sender, RoutedEventArgs e) => CancelDelete();

    private void CancelDelete()
    {
        _confirmWatch.Stop();
        _deleting = null;
        if (_footer == Footer.Confirm) ShowFooter(Footer.Normal);
    }

    private void OnDeleteConfirmed(object sender, RoutedEventArgs e)
    {
        _confirmWatch.Stop();

        var target = _deleting;
        _deleting = null;
        ShowFooter(Footer.Normal);

        // The question named one credential. If the selection has moved since, the answer
        // does not apply to whatever is selected now.
        if (target is null || !ReferenceEquals(target, _chosen)) return;

        if (ReferenceEquals(target, _draft))
        {
            // Never saved, so there is nothing on disk to tombstone. Just drop it.
            _draft = null;
            SaveButton.IsEnabled = false;
            Choose(null);
            ApplyFilter();
            return;
        }

        var before = Snapshot(target);

        // Kept as a tombstone rather than removed, so a deleted entry cannot come
        // back from another device once sync exists.
        target.Deleted = true;
        target.Name = "";
        target.Username = target.Email = target.Password = target.Notes = null;
        target.UpdatedAt = Entry.Now();

        if (!Persist())
        {
            // Without this the credential was destroyed in memory by a delete that failed to
            // write. The screen looked untouched, and the next successful save of anything
            // committed the tombstone - with the real password inside it, unreachable.
            Restore(target, before);
            ApplyFilter();
            return;
        }

        SaveButton.IsEnabled = false;
        Choose(null);
        ApplyFilter();
        ShowStatus();
        Toast("Deleted");
    }

    /// <summary>
    /// Write everything to the stick and the spare copy.
    ///
    /// The three checks before the write matter as much as the write. This window holds a
    /// snapshot taken when it opened, and a save replaces the whole file - so if the drive
    /// now holds a different vault, a re-keyed one, or one something else has written since,
    /// saving would destroy it. Refusing costs the user one lock-and-unlock.
    /// </summary>
    private bool Persist()
    {
        byte[] onDisk;
        VaultHeader header;

        try
        {
            onDisk = VaultStorage.ReadVault(_stickRoot);
            header = VaultFile.ReadHeader(onDisk);
        }
        catch (Exception ex)
        {
            Toast($"Could not reach your stick - {Plain(ex)}. Nothing was written.", good: false);
            return false;
        }

        if (header.VaultId != _vaultId)
        {
            Toast("The drive holds a different vault from the one you opened. Nothing was written.", good: false);
            return false;
        }

        if (header.Epoch != _epoch)
        {
            Toast("This stick has been re-keyed since you opened it. Lock and unlock, then try again.", good: false);
            return false;
        }

        if (header.Revision > _revision)
        {
            Toast("Something else has changed this vault since you opened it. Lock and unlock, then make your change again.",
                  good: false);
            return false;
        }

        try
        {
            var saved = VaultFile.Save(onDisk, _masterKey, _all);
            Enrolment.SaveVault(MachineStore.DefaultFolder, _stickRoot, saved);
            _revision = header.Revision + 1;
            return true;
        }
        catch (Exception ex)
        {
            Toast($"Could not save - {Plain(ex)}. Is your stick still plugged in?", good: false);
            return false;
        }
    }

    /// <summary>
    /// A plain sentence instead of a .NET message. The old text put full file paths on
    /// screen, which tells the user nothing and looks like the app has fallen over.
    /// </summary>
    private static string Plain(Exception ex) => ex switch
    {
        FileNotFoundException => "there is no vault on that drive",
        DirectoryNotFoundException => "that drive is not there any more",
        UnauthorizedAccessException => "Windows would not let PassVault write to it",
        IOException => "the drive stopped responding",
        InvalidDataException => "the file there is damaged",
        _ => ex.Message,
    };

    private static Entry Snapshot(Entry e) => new()
    {
        Id = e.Id, Name = e.Name, Username = e.Username, Email = e.Email,
        Password = e.Password, Notes = e.Notes, UpdatedAt = e.UpdatedAt, Deleted = e.Deleted,
    };

    private static void Restore(Entry into, Entry from)
    {
        into.Name = from.Name;
        into.Username = from.Username;
        into.Email = from.Email;
        into.Password = from.Password;
        into.Notes = from.Notes;
        into.UpdatedAt = from.UpdatedAt;
        into.Deleted = from.Deleted;
    }

    // ---- copying ----

    private void OnCopyUsername(object sender, RoutedEventArgs e) => Copy(UsernameBox.Text, "Username");
    private void OnCopyEmail(object sender, RoutedEventArgs e) => Copy(EmailBox.Text, "Email");
    private void OnCopyPassword(object sender, RoutedEventArgs e) => Copy(CurrentPassword(), "Password");

    /// <summary>
    /// Ctrl+C is the obvious thing to press once a password is on screen, and a plain Ctrl+C
    /// puts it on the clipboard with no timer, no history opt-out and nothing to wipe it.
    /// Send it through the same door as the copy buttons instead.
    /// </summary>
    private void OnFieldCopyKey(object sender, KeyEventArgs e)
    {
        if ((e.Key != Key.C && e.Key != Key.X) || Keyboard.Modifiers != ModifierKeys.Control) return;

        e.Handled = true;

        if (ReferenceEquals(sender, PasswordBoxText)) Copy(CurrentPassword(), "Password");
        else if (ReferenceEquals(sender, UsernameBox)) Copy(UsernameBox.Text, "Username");
        else Copy(EmailBox.Text, "Email");
    }

    private void Copy(string? value, string what)
    {
        if (string.IsNullOrEmpty(value))
        {
            // Silently doing nothing made these look like broken buttons.
            Toast($"There is no {what.ToLowerInvariant()} here to copy", good: false);
            return;
        }

        if (!ClipboardGuard.Put(value))
        {
            Toast("Windows would not let go of the clipboard - try again", good: false);
            return;
        }

        Toast($"{what} copied - the clipboard clears in {ClipboardGuard.ClearAfter.TotalSeconds:0} seconds");
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
            Toast($"Could not read your vault - {Plain(ex)}", good: false);
            return;
        }

        var picker = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save a backup of your vault",
            Filter = "PassVault backup (*.pv)|*.pv",
            FileName = Recovery.SuggestBackupName(vault, DateTime.Now),
        };

        if (WhileDialogIsUp(() => picker.ShowDialog()) != true) return;

        try
        {
            Recovery.ExportBackup(vault, picker.FileName);
            Toast("Backup saved - that file plus your 12 words gets everything back, with no stick and no PC");
        }
        catch (Exception ex)
        {
            Toast($"Could not save the backup - {Plain(ex)}", good: false);
        }
    }

    /// <summary>
    /// Hold the idle countdown while a dialog owns the input.
    ///
    /// The timer kept ticking inside the dialog's own message loop, but the mouse and key
    /// events that reset it never reached this window - so the vault locked out from under an
    /// open Back up dialog, and the backup was then written after the vault had locked.
    /// </summary>
    private T WhileDialogIsUp<T>(Func<T> show)
    {
        _idleWatch.Stop();
        try
        {
            return show();
        }
        finally
        {
            _lastActive = DateTime.UtcNow;
            if (!_locking) _idleWatch.Start();
        }
    }

    // ---- locking ----

    private void OnLock(object sender, RoutedEventArgs e)
    {
        if (Settled(Lock)) Lock();
    }

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

        if (left <= TimeSpan.Zero) { LockOnIdle(); return; }

        Band.Trailing = $"locks in {left.Minutes}:{left.Seconds:00}";
        Band.TrailingAlert = left.TotalSeconds <= 60;
    }

    /// <summary>
    /// Locking has to win here - it is a promise, and nobody is at the keyboard to answer a
    /// question. But it must not throw typing away either, so save first. Saving is the
    /// action the screen was already offering, and it is the only one of the three that
    /// loses nothing.
    /// </summary>
    private void LockOnIdle()
    {
        _idleWatch.Stop();
        if (Dirty && NameBox.Text.Trim().Length > 0) SaveNow();
        Lock();
    }

    /// <summary>
    /// A success clears itself after four seconds. Anything you have to act on stays until
    /// you act. No timer ever clears an error - otherwise "Windows would not let go of the
    /// clipboard" looks exactly like success, four seconds later.
    /// </summary>
    private void Toast(string text, bool good = true)
    {
        _holdingMessage = true;
        _badMessage = !good;
        Band.Detail = text;
        Band.Mood = good ? Mood.Good : Mood.Bad;

        _toastWatch.Stop();
        if (good) _toastWatch.Start();
    }

    private void ClearMessage()
    {
        _toastWatch.Stop();
        _holdingMessage = false;
        _badMessage = false;
        ShowStatus();
    }

    /// <summary>
    /// Errors wait to be read, not timed out - but they used to wait forever, leaving the
    /// band red and the credential count stale for the rest of the session. Getting on with
    /// something else is the answer, so that is what clears it.
    /// </summary>
    private void ClearBadMessage()
    {
        if (_badMessage) ClearMessage();
    }

    // ---- the footer ----

    private enum Footer { Normal, Confirm, Unsaved }

    private UIElement PanelFor(Footer which) => which switch
    {
        Footer.Confirm => FooterConfirm,
        Footer.Unsaved => FooterUnsaved,
        _ => FooterNormal,
    };

    private void ShowFooter(Footer want)
    {
        if (_footer == want) return;

        var from = _footer;
        _footer = want;

        var going = PanelFor(from);
        var coming = PanelFor(want);
        var travel = (Duration)FindResource("Travel");

        // A WPF element at opacity 0 is still fully clickable, so the fading panel has to
        // stop taking clicks immediately - otherwise "Delete for good" stays hittable for a
        // quarter of a second after the selection has moved on.
        going.IsHitTestVisible = false;

        coming.Visibility = Visibility.Visible;
        coming.IsHitTestVisible = true;
        coming.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = travel });

        var leaving = new DoubleAnimation { To = 0, Duration = travel };
        // Only hide it if we have not come back to it in the meantime. Without that check a
        // fast Delete-then-Cancel could collapse both panels and leave a blank footer with no
        // Save and no Delete for the rest of the session.
        leaving.Completed += (_, _) => { if (_footer != from) going.Visibility = Visibility.Collapsed; };
        going.BeginAnimation(OpacityProperty, leaving);
    }

    /// <summary>A name safe to put in a sentence: never empty, never long enough to push the buttons off.</summary>
    private static string Quoted(string name)
    {
        var one = name.Trim();
        if (one.Length == 0) return "this credential";
        if (one.Length > 32) one = one[..32] + "...";
        return $"“{one}”";
    }

    /// <summary>
    /// What was typed, exactly. Only a completely empty box becomes nothing.
    ///
    /// This used to trim and treat all-whitespace as empty. For a name that is tidy; for a
    /// password it is data loss - "hunter2 " was stored as "hunter2", a different password
    /// from the one the website has, and a password of four spaces was thrown away while the
    /// app said "Saved".
    /// </summary>
    private static string? AsTyped(string value) => value.Length == 0 ? null : value;
}
