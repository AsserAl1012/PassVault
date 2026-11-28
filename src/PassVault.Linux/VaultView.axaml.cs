using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PassVault.Core.Crypto;
using PassVault.Core.Machine;
using PassVault.Core.Model;
using PassVault.Core.Usb;
using PassVault.Core.Vault;

namespace PassVault.Linux;

/// <summary>
/// The open vault: the list, and the credential being looked at or edited. Every change goes through
/// OpenVault - the same safe save as the terminal app - and only what was changed is written.
/// </summary>
public partial class VaultView : UserControl
{
    private readonly OpenVault _vault = null!;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _lastActive = DateTime.UtcNow;
    private TopLevel? _window;
    private int _sinceScreenCheck;

    private Entry? _chosen;

    /// <summary>A new credential not saved yet - not in the vault until it is, so no other save commits it.</summary>
    private Entry? _draft;

    /// <summary>What the boxes showed when this credential was opened: only boxes that differ are written.</summary>
    private (string Name, string User, string Mail, string Pass, string Notes) _shown;
    private bool _filling;

    /// <summary>The vault was locked - the window goes back to the unlock screen, with the reason.</summary>
    public event Action<string?>? Locked;

    /// <summary>
    /// A close was stopped once for this edit, because it could not be saved. Per edit, not per
    /// unlock: kept, a later refused save was thrown away on close without a word.
    /// </summary>
    internal bool CloseWarned;

    /// <summary>For Avalonia's loader and designer only - the app always opens one with a vault.</summary>
    public VaultView() => InitializeComponent();

    public VaultView(OpenVault vault, string? notice) : this()
    {
        _vault = vault;

        // Pointer movement over the vault counts as use, for the idle lock. Keys are caught at the
        // window - see OnWindowKey.
        AddHandler(PointerMovedEvent, (_, _) => _lastActive = DateTime.UtcNow, RoutingStrategies.Tunnel);

        // Copy and Cut in these boxes go through the same door as the Copy buttons - timer, secret
        // mark and wipe. The box's own copy put the text on the clipboard with nothing to wipe it.
        // This catches its menu and Ctrl+Insert; Ctrl+C and Ctrl+X are caught at the window.
        foreach (var (box, what) in CopyBoxes)
        {
            box.CopyingToClipboard += (_, e) => { e.Handled = true; Copy(box.Text, what); };
            box.CuttingToClipboard += (_, e) => { e.Handled = true; Copy(box.Text, what); };
        }
        foreach (var box in new[] { NameBox, UsernameBox, EmailBox, PasswordBox, NotesBox })
            box.TextChanged += (_, _) => { if (!_filling) SaveButton.IsEnabled = Dirty || ReferenceEquals(_chosen, _draft); };

        _tick.Tick += (_, _) => Watch();
        AttachedToVisualTree += (_, _) =>
        {
            _tick.Start();
            _window = TopLevel.GetTopLevel(this);
            _window?.AddHandler(KeyDownEvent, OnWindowKey, RoutingStrategies.Tunnel);
            // Queued behind layout: straight away the box is not on screen yet, focusing it did nothing,
            // and the keyboard was left with the window - where these keys never reached.
            Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Input);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _tick.Stop();
            _window?.RemoveHandler(KeyDownEvent, OnWindowKey);
        };

        Fill();
        Status();
        Say(notice ?? "");
    }

    /// <summary>
    /// Every key in the window, before any box sees it: it counts as use for the idle lock, and the
    /// shortcuts the terminal app has too work wherever the focus is - even nowhere at all.
    /// </summary>
    private void OnWindowKey(object? sender, KeyEventArgs e)
    {
        _lastActive = DateTime.UtcNow;
        if (e.KeyModifiers != KeyModifiers.Control) return;
        switch (e.Key)
        {
            case Key.N: OnAdd(this, e); break;
            case Key.S: OnSave(this, e); break;
            case Key.F: SearchBox.Focus(); SearchBox.SelectAll(); break;
            case Key.L: Lock("Locked."); break;
            case Key.C or Key.X:
                // The whole box, as the Copy button does. Left to the box, a password box copied
                // nothing at all, and the others only what was selected.
                var focused = _window?.FocusManager?.GetFocusedElement();
                var (box, what) = CopyBoxes.FirstOrDefault(c => ReferenceEquals(c.Box, focused));
                if (box is null) return;
                Copy(box.Text, what);
                break;
            default: return;
        }
        e.Handled = true;
    }

    private (TextBox Box, string What)[] CopyBoxes => [(UsernameBox, "Username"), (EmailBox, "Email"), (PasswordBox, "Password")];

    // ---- the list ----

    private void Fill(Entry? keep = null)
    {
        var shown = _vault.Find(SearchBox.Text ?? "");
        if (_draft is not null) shown.Add(_draft);
        _filling = true;
        EntryList.ItemsSource = shown;
        EntryList.SelectedItem = keep is not null && shown.Contains(keep) ? keep : null;
        _filling = false;
        EmptyText.Text = shown.Count > 0 ? "" : _vault.Living.Any() ? "Nothing matches that." : "Nothing saved yet.";
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        // Only when the user typed it. Add clears the search too - and this ran after Add's save,
        // a moment later, wiping what the save had said.
        if (SearchBox.IsFocused) Say("");
        Fill(_chosen);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        var wanted = EntryList.SelectedItem as Entry;
        if (ReferenceEquals(wanted, _chosen)) return;

        // Moving away keeps what was typed: it is saved first, as lock does.
        if (Dirty)
        {
            if (SaveIfEdited() is { } refused)
            {
                Say(refused);
                Fill(_chosen);   // stay where the unsaved change is
                return;
            }
            Fill(wanted);   // the save filled the list again on the one it saved - and it stayed highlighted
        }
        else
        {
            Say("");   // whatever it said was about the credential being left
            if (ReferenceEquals(_chosen, _draft)) { _draft = null; Fill(wanted); }   // nobody typed in it
        }
        Choose(wanted);
    }

    private void Choose(Entry? entry)
    {
        _chosen = entry;
        CloseWarned = false;   // runs after every save and every move to another credential
        ConfirmDelete.IsVisible = false;
        Details.IsVisible = entry is not null;
        Pick.IsVisible = entry is null;
        if (entry is null) return;

        _filling = true;
        NameBox.Text = entry.Name;
        UsernameBox.Text = entry.Username ?? "";
        EmailBox.Text = entry.Email ?? "";
        PasswordBox.Text = entry.Password ?? "";
        NotesBox.Text = entry.Notes ?? "";
        PasswordBox.RevealPassword = false;
        RevealButton.Content = "Show";
        _shown = (NameBox.Text, UsernameBox.Text, EmailBox.Text, PasswordBox.Text, NotesBox.Text);
        _filling = false;
        SaveButton.IsEnabled = ReferenceEquals(entry, _draft);
        DeleteButton.IsEnabled = true;
    }

    /// <summary>
    /// Something was typed. Only that: a new credential nobody typed in is not an edit - counted as
    /// one, moving away or locking saved an empty "New credential" to the stick.
    /// </summary>
    private bool Dirty => _chosen is not null &&
        (NameBox.Text ?? "", UsernameBox.Text ?? "", EmailBox.Text ?? "", PasswordBox.Text ?? "", NotesBox.Text ?? "") != _shown;

    // ---- changing things ----

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (!Dirty) Say("");   // about the credential being left; a save below says its own
        if (SaveIfEdited() is { } refused) { Say(refused); return; }
        _draft = new Entry { Name = "New credential" };   // a name to see in the list; selected, so typing replaces it
        SearchBox.Text = "";
        Fill(_draft);
        Choose(_draft);

        // The panel has only just become visible: focusing it now does nothing. Queue it behind layout.
        Dispatcher.UIThread.Post(() => { NameBox.Focus(); NameBox.SelectAll(); }, DispatcherPriority.Input);
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_chosen is null) return;
        if ((NameBox.Text ?? "").Trim().Length == 0) { Say("Give it a name first."); NameBox.Focus(); return; }
        if (Save() is { } refused) Say(refused);
    }

    /// <summary>
    /// Save what is being edited, if anything is. Null when nothing went wrong - and an edit with no
    /// name is not "nothing went wrong": passed over in silence, moving away threw it away.
    /// </summary>
    public string? SaveIfEdited() => !Dirty ? null : (NameBox.Text ?? "").Trim().Length == 0 ? "Give it a name first." : Save();

    /// <summary>Null when saved; otherwise why nothing was written.</summary>
    private string? Save()
    {
        var entry = _chosen!;
        bool isNew = ReferenceEquals(entry, _draft);

        // A new one gets every box: a refused save leaves what it wrote on the draft, and a box put
        // back to how it started was then skipped - so the refused value was saved after all.
        void Write(Entry e)
        {
            if (isNew || NameBox.Text != _shown.Name) e.Name = (NameBox.Text ?? "").Trim();
            if (isNew || UsernameBox.Text != _shown.User) e.Username = Typed(UsernameBox.Text);
            if (isNew || EmailBox.Text != _shown.Mail) e.Email = Typed(EmailBox.Text);
            if (isNew || PasswordBox.Text != _shown.Pass) e.Password = Typed(PasswordBox.Text);
            if (isNew || NotesBox.Text != _shown.Notes) e.Notes = Typed(NotesBox.Text);
        }

        try
        {
            string? warning;
            if (isNew) { Write(entry); warning = _vault.Add(entry); _draft = null; }
            else warning = _vault.Change(entry, Write);

            Fill(entry);
            Choose(entry);
            Status();
            Say(warning ?? "Saved to your stick and the spare copy.", bad: warning is not null);
            return null;
        }
        catch (SaveRefusedException refused)
        {
            return refused.Message;
        }
    }

    private void OnMakePassword(object? sender, RoutedEventArgs e)
    {
        PasswordBox.Text = PasswordGenerator.Make(Program.Settings.GeneratorLength, Program.Settings.GeneratorSymbols);
        PasswordBox.RevealPassword = true;
        RevealButton.Content = "Hide";
        Say("A new strong password - save to keep it.", bad: false);
    }

    private void OnReveal(object? sender, RoutedEventArgs e)
    {
        PasswordBox.RevealPassword = !PasswordBox.RevealPassword;
        RevealButton.Content = PasswordBox.RevealPassword ? "Hide" : "Show";
    }

    private void OnDeleteAsked(object? sender, RoutedEventArgs e)
    {
        if (_chosen is null) return;
        var name = _chosen.Name.Trim();   // never long enough to push the buttons off, as on Windows
        ConfirmText.Text = $"Delete \"{(name.Length > 32 ? name[..32] + "..." : name)}\" for good?";
        ConfirmDelete.IsVisible = true;
    }

    private void OnDeleteCancelled(object? sender, RoutedEventArgs e) => ConfirmDelete.IsVisible = false;

    private void OnDeleteConfirmed(object? sender, RoutedEventArgs e)
    {
        ConfirmDelete.IsVisible = false;
        if (_chosen is not { } target) return;

        if (ReferenceEquals(target, _draft))
        {
            _draft = null;   // never saved, so nothing on the stick to delete
        }
        else
        {
            try
            {
                Say(_vault.Delete(target) ?? "Deleted.", bad: false);
            }
            catch (SaveRefusedException refused)
            {
                Say(refused.Message);
                return;
            }
        }
        Choose(null);
        Fill();
        Status();
    }

    // ---- copying ----

    private void OnCopyUsername(object? sender, RoutedEventArgs e) => Copy(UsernameBox.Text, "Username");
    private void OnCopyEmail(object? sender, RoutedEventArgs e) => Copy(EmailBox.Text, "Email");
    private void OnCopyPassword(object? sender, RoutedEventArgs e) => Copy(PasswordBox.Text, "Password");

    private async void Copy(string? value, string what)
    {
        if (string.IsNullOrEmpty(value)) { Say($"There is no {what.ToLowerInvariant()} here to copy."); return; }
        if (!await GuiClipboard.Put(TopLevel.GetTopLevel(this)?.Clipboard, value))
        {
            Say("The desktop would not let go of the clipboard - try again.");
            return;
        }
        Say($"{what} copied - the clipboard clears in {Program.Settings.ClearClipboardAfterSeconds} seconds.{GuiClipboard.Limit}", bad: false);
    }

    // ---- locking ----

    private void OnLock(object? sender, RoutedEventArgs e) => Lock("Locked.");

    /// <summary>Every second: the countdown, and the reasons to lock - idle time, the stick, the screen.</summary>
    private void Watch()
    {
        var idle = DateTime.UtcNow - _lastActive;
        var limit = TimeSpan.FromMinutes(Program.Settings.LockAfterMinutes);
        if (idle >= limit) { Lock($"Locked after {Program.Settings.LockAfterMinutes} minutes without use."); return; }
        if (Program.Settings.LockWhenStickRemoved && !VaultStorage.IsSetUp(_vault.StickRoot)) { Lock("Locked - your stick was taken out."); return; }
        if (Program.Settings.LockWhenScreenLocks && ++_sinceScreenCheck >= 2)
        {
            _sinceScreenCheck = 0;
            if (ScreenLock.IsLocked()) { Lock("Locked - your screen was locked."); return; }
        }

        var left = limit - idle;
        State.Text = $"Vault open  ·  locks in {(int)left.TotalMinutes}:{left.Seconds:00}";
    }

    private void Lock(string why)
    {
        _tick.Stop();
        bool editing = Dirty;
        var refused = SaveIfEdited();   // an edit in progress is kept, not thrown away
        var how = !editing ? ""
                : refused is null ? " What you were editing was saved first."
                : $" What you were editing could NOT be saved: {refused}";
        Close();
        Locked?.Invoke(why + how);
    }

    public void Close() => _vault.Close();

    private void Status() => Detail.Text = $"{_vault.StickRoot}  ·  {_vault.Living.Count()} credential(s)";

    private static string? Typed(string? text) => string.IsNullOrEmpty(text) ? null : text;

    internal void Say(string text, bool bad = true)
    {
        Message.Text = text;
        Message.Classes.Set("Bad", bad && text.Length > 0);
    }
}
