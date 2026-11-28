using System.Collections.ObjectModel;
using System.Security.Cryptography;
using PassVault.Core.Config;
using PassVault.Core.Crypto;
using PassVault.Core.Machine;
using PassVault.Core.Model;
using PassVault.Core.Usb;
using PassVault.Core.Vault;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace PassVault.Cli;

/// <summary>
/// PassVault full screen in a terminal - run passvault with nothing after it. The same ways in
/// and the same rules as the window app, all from the keyboard: the stick is noticed on its own,
/// the PIN opens the vault the moment its last digit lands, the way in is decided by Unlocker and
/// every change goes through OpenVault - the same code the Linux window app uses.
/// </summary>
internal sealed class Tui
{
    private const string Keys = "/ search   c copy password   u copy username   r show   n new   e edit   d delete   Ctrl+L lock   q quit";

    private readonly IApplication _app;
    private readonly Settings _settings;
    private readonly string _folder = MachineStore.DefaultFolder;
    private readonly Window _window = new() { Title = "PassVault" };
    // Two lines, wrapped: the honest notices about this PC's chip are longer than one line.
    private readonly Label _message = new() { X = 1, Y = Pos.AnchorEnd(3), Width = Dim.Fill(1), Height = 2 };

    private UsbStick? _stick;
    private OpenVault? _vault;

    /// <summary>Which screen is up and for which stick - the two-second poll repaints only when that changes.</summary>
    private string _screen = "";
    private bool _busy;

    /// <summary>
    /// Part-way through something the stick does not decide - the 12 words, choosing a PIN. The poll
    /// leaves these alone: it put the PIN screen back over them after two seconds, typing and all.
    /// </summary>
    private bool _midFlow;
    private DateTime _lastKey = DateTime.UtcNow;

    /// <summary>What PassVault put on the clipboard. Only this is ever wiped - never anything you copied yourself.</summary>
    private string? _onClipboard;

    /// <summary>Which copy the newest wipe timer belongs to. An older copy's timer wiped a newer copy early.</summary>
    private int _copies;

    /// <summary>
    /// The current screen's keys, asked about every key before any view sees it. On the vault list
    /// they never arrived otherwise: its type-to-jump search swallowed every letter.
    /// </summary>
    private Func<Key, bool>? _shortcuts;

    /// <summary>
    /// Set while a credential is being edited: lock saves it first, and says how that went. Thrown
    /// away, it could hold a password just made and already set on a website - stored nowhere.
    /// </summary>
    private Func<string?>? _saveBeforeLock;

    private Tui(IApplication app, Settings settings)
    {
        _app = app;
        _settings = settings;
    }

    public static int Run(Settings settings)
    {
        using var app = Application.Create().Init();
        var tui = new Tui(app, settings);
        app.Keyboard.KeyDown += (_, key) =>
        {
            tui._lastKey = DateTime.UtcNow;
            if (key == Key.L.WithCtrl && tui._vault is not null) { key.Handled = true; tui.Lock("Locked."); }
            else if (tui._shortcuts?.Invoke(key) == true) key.Handled = true;
        };
        app.AddTimeout(TimeSpan.FromSeconds(2), () => { tui.Tick(); return true; });
        tui.Refresh();
        app.Run(tui._window);

        var how = tui._saveBeforeLock?.Invoke();   // ended mid-edit: kept, as lock keeps it
        tui._vault?.Close();
        tui.WipeClipboard(wait: true);   // the program is ending - finish the wipe before it does
        tui._window.Dispose();
        if (how is not null) Console.WriteLine(how.Trim());
        return 0;
    }

    // ---- deciding which screen to show ----

    /// <summary>Every two seconds: notice a stick going in or out, and lock the vault when it is time.</summary>
    private void Tick()
    {
        if (_vault is not null)
        {
            if (DateTime.UtcNow - _lastKey > TimeSpan.FromMinutes(_settings.LockAfterMinutes))
                Lock($"Locked after {_settings.LockAfterMinutes} minutes without use.");
            else if (_settings.LockWhenStickRemoved && !VaultStorage.IsSetUp(_vault.StickRoot))
                Lock("Locked - your stick was taken out.");
            else if (_settings.LockWhenScreenLocks && ScreenLock.IsLocked())
                Lock("Locked - your screen was locked.");
            return;
        }

        if (!_busy && !_midFlow) Refresh();
    }

    private void Refresh()
    {
        Arrival here;
        try
        {
            here = Unlocker.Look(_folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say("Could not read this PC's setup file just now. Trying again...");
            return;   // the two-second poll comes back here
        }

        _stick = here.Stick;
        // The second stick too: plugged in, or taken out, while this screen is up is a change to say.
        var screen = $"{here.Situation}|{_stick?.Root}|{here.OtherStick?.Root}";
        if (_screen == screen) return;
        _screen = screen;

        switch (here.Situation)
        {
            case Situation.NoStick:
                Show("Plug your PassVault stick in",
                     Text("This screen notices it on its own."),
                     Text($"PassVault looks in /media/{Environment.UserName} and /run/media/{Environment.UserName}, "
                          + "and in sticks.extra_places in your settings ('passvault config')."));
                break;
            case Situation.NewStick: NewStickScreen(); break;
            case Situation.SetUpThisPc: EnrolScreen(); break;
            case Situation.SetupDamaged: EnrolScreen("This PC's setup file is damaged. Type your 12 words to set it up again."); break;
            case Situation.Rekeyed:
                EnrolScreen("This stick was re-keyed on another PC, so the PIN here no longer fits. "
                            + "Type the new 12 words to set this PC up again.");
                break;
            case Situation.PinOff:
                PhraseScreen();
                Say($"The PIN was switched off after {Enrolment.MaxAttempts} wrong tries. Your 12 words switch it back on.");
                break;
            default: PinScreen(here); break;
        }

        // Picking one in silence and then saying "that is not your stick" is the worst of both.
        // Added to the message, not over it: that said why this screen is up.
        if (here.OtherStick is { } other)
            Say($"{_message.Text} Two PassVault sticks are plugged in. Using {_stick!.Root}. Unplug it to use {other.Root} instead.".TrimStart());
    }

    // ---- setting up a new stick, and this PC ----

    private void NewStickScreen()
        => Show($"There is no vault on {_stick!.Root} yet",
                Text("Setting it up makes an empty vault there and shows you your 12 words, once."),
                Button("Set up this stick", () =>
                {
                    try
                    {
                        var (phrase, _) = VaultStorage.Initialise(_stick.Root);
                        WordsScreen(phrase);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                    {
                        Say(ex.Message);   // half a vault on it already, or a stick that is full or read-only
                    }
                }));

    private void WordsScreen(string phrase)
    {
        var words = phrase.Split(' ');
        var lines = Enumerable.Range(0, 4).Select(row => string.Concat(
            Enumerable.Range(0, 3).Select(col => $"{row + col * 4 + 1,2}. {words[row + col * 4],-12}")));

        Show("Write these 12 words down, on paper",
             Text(string.Join("\n", lines)),
             Text("They are shown once and stored nowhere. They are the only way back in if this stick is lost or broken."),
             Button("I have written them down", () => ChoosePinScreen(phrase)));
        _midFlow = true;

        // Esc is Terminal.Gui's quit key. Pressed here, PassVault ended and the words were never shown
        // again - the stick was already set up, so nothing could show them.
        _shortcuts = key =>
        {
            if (key != Key.Esc) return false;
            Say("These words are shown only once. Write them down, then press the button.");
            return true;
        };
    }

    private void EnrolScreen(string? why = null)
    {
        var phrase = new TextField { Width = Dim.Fill(1) };
        Show("Set this PC up",
             Text("Type your 12 words once. After this, your stick and a PIN are all you need here."),
             phrase,
             Button("Continue", () =>
             {
                 if (!Mnemonic.TryParse(phrase.Text, out _, out var problem)) Say(problem);
                 else ChoosePinScreen(phrase.Text);
             }),
             Button("Just open my vault with my 12 words", PhraseScreen));
        if (why is not null) { Say(why); _midFlow = true; }
    }

    private void ChoosePinScreen(string phrase)
    {
        int shortest = _settings.MinPinLength;
        var pin = new TextField { Secret = true, Width = 14 };
        var again = new TextField { Secret = true, Width = 14 };

        Show("Choose a PIN",
             Text($"{shortest} to 12 digits - 6 or more is much harder to guess."), pin,
             Text("Type it again"), again,
             Button("Finish setting up", () =>
             {
                 if (!Enrolment.IsPin(pin.Text) || pin.Text.Length < shortest) Say($"A PIN here is {shortest} to 12 digits, 0 to 9.");
                 else if (pin.Text != again.Text) Say("Those two PINs are not the same.");
                 else
                 {
                     var (chosen, root) = (pin.Text, _stick!.Root);
                     Slowly("Setting this PC up", () =>
                     {
                         var (key, protection) = Unlocker.SetUpThisPc(_folder, root, phrase, chosen);
                         return (key, Unlocker.Weakness(protection));
                     });
                 }
             }),
             // A failed set-up - words that do not open this vault, a resting chip - fails the same way
             // on every try. Without this the only way out was to quit.
             Button("Back", BackFromSetUp));
        _midFlow = true;
    }

    // ---- the everyday unlock ----

    private void PinScreen(Arrival here)
    {
        var pin = new TextField { Secret = true, Width = 14 };
        pin.ValueChanged += (_, _) => { if (pin.Text.Length == here.PinLength) TryPin(pin, here.PinLength); };
        pin.Accepting += (_, e) => { e.Handled = true; TryPin(pin, here.PinLength); };   // Enter

        Show("Enter your PIN", pin, Button("Use my 12 words instead", () => { if (!_busy) PhraseScreen(); }));
        if (here.FailedAttempts > 0) Say($"{Enrolment.MaxAttempts - here.FailedAttempts} tries left.");
    }

    private void TryPin(TextField box, int pinLength)
    {
        var pin = box.Text;
        box.Text = "";
        if (_busy) return;
        if (!Enrolment.IsPin(pin)) { Say("A PIN is 4 to 12 digits, 0 to 9."); return; }

        // It cannot be right, and asking the chip would spend one of its tries.
        if (pin.Length != pinLength) { Say($"Your PIN here is {pinLength} digits."); return; }

        var root = _stick!.Root;
        if (Unlocker.WaitBeforeTry(_folder) is not { } wait) { _screen = ""; Refresh(); return; }   // set-up gone or mid-save
        Slowly(wait > TimeSpan.Zero ? $"Waiting {wait.TotalSeconds:0} seconds after earlier wrong tries" : "Opening your vault", () =>
        {
            Thread.Sleep(wait);
            return (Unlocker.WithPin(_folder, root, pin), null);
        });
    }

    private void PhraseScreen()
    {
        var phrase = new TextField { Width = Dim.Fill(1) };
        Show("Your 12 words",
             Text("This works even if the PIN has been switched off, and it switches it back on."),
             phrase,
             Button("Open my vault", () =>
             {
                 if (!Mnemonic.TryParse(phrase.Text, out _, out var problem)) { Say(problem); return; }
                 var (typed, root) = (phrase.Text, _stick!.Root);
                 Slowly("Opening your vault", () => (Enrolment.UnlockWithPhrase(_folder, root, typed), null));
             }),
             Button("Back", () => { _screen = ""; Refresh(); }));
        _midFlow = true;
    }

    /// <summary>
    /// Argon2 takes a moment on purpose. Do it off the screen's thread, so the screen keeps drawing.
    /// One at a time: a second press of the same button, or the 12 words during a PIN's wait, started
    /// a second unlock that later opened the vault again over the user's work.
    /// </summary>
    private void Slowly(string doing, Func<(byte[] Key, string? Notice)> work)
    {
        if (_busy) return;
        _busy = true;
        Say(doing + "...");
        Task.Run(() =>
        {
            try
            {
                var (key, notice) = work();
                _app.Invoke(() => { _busy = false; Opened(key, notice); });
            }
            catch (Exception ex)
            {
                _app.Invoke(() => { _busy = false; Failed(ex); });
            }
        });
    }

    private void Failed(Exception ex)
    {
        if (ex is ChipLostException)
        {
            _chipLost = ex.Message;
            EnrolScreen(ex.Message);   // no PIN can work here again
            return;
        }

        // The tenth wrong PIN switches it off: show the 12 words - and keep the reason on screen,
        // ahead of anything the fresh screen says, like a second stick.
        if (ex is UnlockException && !_midFlow && Unlocker.PinSwitchedOff(_folder))
        {
            _screen = "";
            Refresh();
            Say($"{ex.Message} {_message.Text}".Trim());
            return;
        }
        Say(ex.Message);
    }

    /// <summary>
    /// This PC's own secret will never open again. Its old setup file still reads as "PIN", so Back
    /// from a failed set-up went to the PIN screen - where no PIN can work, and each try counted.
    /// </summary>
    private string? _chipLost;

    /// <summary>Back from choosing a PIN: to the 12 words when the chip is lost, otherwise to whatever fits.</summary>
    private void BackFromSetUp()
    {
        if (_chipLost is { } why) { EnrolScreen(why); return; }
        _screen = "";
        Refresh();
    }

    // ---- the open vault ----

    private void Opened(byte[] masterKey, string? notice)
    {
        try
        {
            _vault = new OpenVault(_folder, _stick!.Root, masterKey);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or System.Text.Json.JsonException)
        {
            CryptographicOperations.ZeroMemory(masterKey);   // only OpenVault.Close did, never reached
            Say($"Could not read your vault - {ex.Message}");
            return;
        }

        _lastKey = DateTime.UtcNow;
        _chipLost = null;   // set up again, or opened with the 12 words
        VaultScreen();
        if (notice is not null) Say(notice);
    }

    private void VaultScreen(Entry? select = null)
    {
        _screen = "vault";
        _midFlow = false;
        _saveBeforeLock = null;
        var vault = _vault!;
        List<Entry> shown = [];
        bool reveal = false;

        var search = new TextField { X = 10, Y = 0, Width = Dim.Percent(40) - 9 };
        var list = new ListView { X = 1, Y = 2, Width = Dim.Percent(40), Height = Dim.Fill(3) };
        var details = new Label { X = Pos.Percent(40) + 3, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(3) };

        Entry? Chosen() => list.SelectedItem is int i && i >= 0 && i < shown.Count ? shown[i] : null;

        void Describe()
        {
            var e = Chosen();
            details.Text = e is null
                ? (vault.Living.Any() ? "Nothing matches that." : "Nothing saved yet - press n to add a credential.")
                : $"""
                   {e.Name}

                   Username   {e.Username ?? "-"}
                   Email      {e.Email ?? "-"}
                   Password   {(e.Password is null ? "-" : reveal ? e.Password : "••••••••••")}

                   {e.Notes}
                   """;
        }

        void Fill(Entry? keep = null)
        {
            shown = vault.Find(search.Text);
            list.SetSource(new ObservableCollection<Entry>(shown));
            int at = keep is null ? 0 : shown.IndexOf(keep);
            if (shown.Count > 0) list.SelectedItem = Math.Max(at, 0);
            Describe();
        }

        // The search box takes letters only between / and Enter, the down arrow or Esc. Left able to
        // take focus, it got it whenever the list emptied - and q went in as a search.
        search.CanFocus = false;
        void LeaveSearch()
        {
            search.CanFocus = false;
            list.SetFocus();
        }

        search.ValueChanged += (_, _) => Fill();
        search.Accepting += (_, e) => { e.Handled = true; LeaveSearch(); };   // Enter
        list.ValueChanged += (_, _) => { reveal = false; Describe(); };

        // Asked on the message line, as terminal programs do: y deletes, any other key keeps it.
        Entry? deleting = null;

        _shortcuts = key =>
        {
            if (search.HasFocus)
            {
                if (key != Key.Esc && key != Key.CursorDown) return false;   // letters are just letters here
                LeaveSearch();
                return true;
            }
            var e = Chosen();

            if (deleting is not null)
            {
                var target = deleting;
                deleting = null;
                if (key == Key.Y) Delete(target, () => Fill());
                else Say($"Kept \"{target.Name}\".");
            }
            else if (key.AsRune.Value == '/') { search.CanFocus = true; search.SetFocus(); }
            else if (key == Key.C) Copy(e?.Password, "password");
            else if (key == Key.U) Copy(e?.Username ?? e?.Email, "username");
            else if (key == Key.R) { reveal = !reveal; Describe(); }
            else if (key == Key.N) EditScreen(null);
            else if (key == Key.E && e is not null) EditScreen(e);
            else if (key == Key.D && e is not null)
            {
                deleting = e;
                Say($"Delete \"{e.Name}\" for good? Press y to delete - any other key keeps it.");
            }
            else if (key == Key.Q) _app.RequestStop();
            else return false;
            return true;
        };

        _window.RemoveAll();
        (_message.Y, _message.Height) = (Pos.AnchorEnd(3), 2);
        _window.Add(new Label { X = 1, Y = 0, Text = "Search:" }, search, list, details,
                    new Label { X = 1, Y = Pos.AnchorEnd(1), Width = Dim.Fill(1), Text = Keys }, _message);
        Fill(select);
        list.SetFocus();
        Say($"Vault open - {vault.Living.Count()} credential(s). Locks after {_settings.LockAfterMinutes} minutes without use.");
    }

    private void EditScreen(Entry? entry)
    {
        var name = Field(entry?.Name);
        var username = Field(entry?.Username);
        var email = Field(entry?.Email);
        var password = Field(entry?.Password);
        password.Secret = true;

        // Several lines: notes hold recovery codes and imported 2FA secrets. A one-line box quietly
        // flattened them, and saving any other change then wrote the flattened notes back.
#pragma warning disable CS0618   // TextView: superseded by a separate editor package, still the least code here
        var notes = new TextView { Text = entry?.Notes ?? "", Width = Dim.Fill(1), Height = 3, Multiline = true,
                                   WordWrap = true, TabKeyAddsTab = false };
#pragma warning restore CS0618

        // Only what was changed is written back. A box can show a value differently from how it is
        // stored - a tab, a line break - and writing every box changed what nobody touched.
        var shown = (Name: name.Text, User: username.Text, Mail: email.Text, Pass: password.Text, Notes: notes.Text);
        bool Changed() => (name.Text, username.Text, email.Text, password.Text, notes.Text) != shown;

        void SaveNow()
        {
            if (name.Text.Trim().Length == 0) { Say("Give it a name first."); return; }
            if (Apply() is { } refused) Say(refused);
        }

        Show(entry is null ? "New credential" : $"Edit {entry.Name}",
             Text("Website or app"), name, Text("Username"), username, Text("Email"), email,
             Text("Password"), password, Text("Notes"), notes,
             Button("Save", SaveNow),
             Button("Make a strong password", () =>
             {
                 password.Text = PasswordGenerator.Make(_settings.GeneratorLength, _settings.GeneratorSymbols);
                 password.Secret = false;
             }),
             Button("Show / hide", () => password.Secret = !password.Secret),
             Button("Cancel", () => VaultScreen(entry)));

        _saveBeforeLock = () => !Changed() ? null
                              : name.Text.Trim().Length == 0 ? " What you were editing could NOT be saved: it has no name."
                              : Apply() is { } refused ? $" What you were editing could NOT be saved: {refused}"
                              : " What you were editing was saved first.";

        // Copy and Cut in these boxes go through the same door as the copy keys - timer, wipe and all.
        // The box's own copy put a fresh password on the clipboard with nothing to wipe it.
        foreach (var box in new[] { username, email, password })
        {
            box.KeyBindings.Clear(Command.Copy, Command.Cut, Command.CutToEndOfLine, Command.CutToStartOfLine);
            box.MouseBindings.Clear(Command.Context);   // its menu offers Copy and Cut too
        }
        _shortcuts = key =>
        {
            if (key == Key.S.WithCtrl) { SaveNow(); return true; }
            // Esc is Terminal.Gui's quit key. Pressed here as "cancel", it ended PassVault.
            if (key == Key.Esc) { Say("Cancel leaves without saving, Ctrl+S saves."); return true; }
            if (key != Key.C.WithCtrl && key != Key.X.WithCtrl) return false;
            if (password.HasFocus) Copy(password.Text, "password");
            else if (username.HasFocus) Copy(username.Text, "username");
            else if (email.HasFocus) Copy(email.Text, "email");
            else return false;
            return true;
        };

        // Null when saved; otherwise why nothing was written.
        string? Apply()
        {
            void Write(Entry e)
            {
                if (name.Text != shown.Name || entry is null) e.Name = name.Text.Trim();
                if (username.Text != shown.User) e.Username = Typed(username.Text);
                if (email.Text != shown.Mail) e.Email = Typed(email.Text);
                if (password.Text != shown.Pass) e.Password = Typed(password.Text);
                if (notes.Text != shown.Notes) e.Notes = Typed(notes.Text);
            }

            try
            {
                string? warning;
                var target = entry ?? new Entry();
                if (entry is null) { Write(target); warning = _vault!.Add(target); }
                else warning = _vault!.Change(entry, Write);
                VaultScreen(target);
                Say(warning ?? "Saved to your stick and the spare copy.");
                return null;
            }
            catch (SaveRefusedException refused)
            {
                return refused.Message;
            }
        }
    }

    private void Delete(Entry entry, Action after)
    {
        try
        {
            Say(_vault!.Delete(entry) ?? "Deleted.");
            after();
        }
        catch (SaveRefusedException refused)
        {
            Say(refused.Message);
        }
    }

    private void Lock(string why)
    {
        var save = _saveBeforeLock;
        _saveBeforeLock = null;
        var how = save?.Invoke();   // an edit in progress is kept, not thrown away

        _vault?.Close();
        _vault = null;
        _screen = "";
        Refresh();
        Say($"{why}{how} {_message.Text}".Trim());   // ahead of what the fresh screen says, like a second stick
    }

    // ---- the clipboard ----

    private void Copy(string? value, string what)
    {
        if (string.IsNullOrEmpty(value)) { Say($"There is no {what} here to copy."); return; }

        bool copied = OperatingSystem.IsLinux()
            ? DesktopClipboard.Put(value)
            : _app.Clipboard is { IsSupported: true } clipboard && clipboard.TrySetClipboardData(value);
        if (!copied)
        {
            Say("No clipboard reachable from here. On Linux, install wl-clipboard (Wayland) or xclip (X11).");
            return;
        }

        // The terminal's clipboard tools cannot mark it as a secret, so a clipboard history tool may keep
        // its own copy after the wipe. Said once - the window app marks it, and they leave it alone.
        bool first = _copies == 0;
        _onClipboard = value;
        int mine = ++_copies;
        Say($"Copied the {what} - the clipboard clears in {_settings.ClearClipboardAfterSeconds} seconds."
            + (first ? " A clipboard history tool, if you use one, may keep its own copy." : ""));
        _app.AddTimeout(TimeSpan.FromSeconds(_settings.ClearClipboardAfterSeconds), () =>
        {
            if (mine == _copies) WipeClipboard();   // a newer copy has its own timer
            return false;
        });
    }

    /// <summary>
    /// Only if what is there is still what PassVault put there. Off the screen's thread: reading the
    /// clipboard back can take a moment, and the screen stopped answering keys while it did.
    /// </summary>
    private void WipeClipboard(bool wait = false)
    {
        if (_onClipboard is not { } ours) return;
        _onClipboard = null;

        var wiping = Task.Run(() =>
        {
            if (OperatingSystem.IsLinux()) DesktopClipboard.WipeIf(ours);
            else if (_app.Clipboard is { } clipboard && clipboard.TryGetClipboardData(out var now) && now == ours)
                clipboard.TrySetClipboardData("");
        });
        if (wait) wiping.Wait(TimeSpan.FromSeconds(3));
    }

    // ---- building screens ----

    /// <summary>
    /// A heading, then each piece below the last, then the message line. Buttons in a row go side
    /// by side, and a label sits right on top of its box: spaced out one under another, the edit
    /// screen's Save and Cancel were below the bottom of an 80x24 terminal.
    /// </summary>
    private void Show(string heading, params View[] pieces)
    {
        _shortcuts = null;
        _saveBeforeLock = null;
        _midFlow = false;
        _window.RemoveAll();
        View above = new Label { X = 1, Y = 0, Text = heading };
        _window.Add(above);
        foreach (var piece in pieces)
        {
            (piece.X, piece.Y) = piece is Button && above is Button ? (Pos.Right(above) + 2, Pos.Top(above))
                               : (Pos.Absolute(1), Pos.Bottom(above) + (piece is Label || piece == pieces[0] ? 1 : 0));
            _window.Add(piece);
            above = piece;
        }
        // Four lines on the unlock screens, which end near the top: a reason and the two-sticks
        // warning together did not fit in two. The edit screen's buttons sit just above two lines.
        (_message.Y, _message.Height) = _vault is null ? (Pos.AnchorEnd(5), 4) : (Pos.AnchorEnd(3), 2);
        _window.Add(_message);
        Say("");
        (pieces.FirstOrDefault(p => p is TextField) ?? pieces.FirstOrDefault(p => p is Button))?.SetFocus();
    }

    private void Say(string text) => _message.Text = text;

    private static Label Text(string text) => new() { Text = text, Width = Dim.Fill(1) };

    private static TextField Field(string? value) => new() { Text = value ?? "", Width = Dim.Fill(1) };

    private static string? Typed(string text) => text.Length == 0 ? null : text;

    private Button Button(string text, Action pressed)
    {
        var button = new Button { Text = text };
        button.Accepting += (_, e) => { e.Handled = true; pressed(); };
        return button;
    }
}
