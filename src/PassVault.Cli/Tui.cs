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
/// the PIN opens the vault the moment its last digit lands, and every change goes through
/// OpenVault, so it saves exactly as the app does.
/// </summary>
internal sealed class Tui
{
    private const string Keys = "/ search   c copy password   u copy username   r show   n new   e edit   d delete   Ctrl+L lock   q quit";

    private readonly IApplication _app;
    private readonly Settings _settings;
    private readonly string _folder = MachineStore.DefaultFolder;
    private readonly Window _window = new() { Title = "PassVault" };
    private readonly Label _message = new() { X = 1, Y = Pos.AnchorEnd(2), Width = Dim.Fill(1) };

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

    /// <summary>
    /// The vault screen's one-letter keys, asked about every key before any view sees it. On the
    /// list itself they never arrived: its type-to-jump search swallowed every letter.
    /// </summary>
    private Func<Key, bool>? _shortcuts;

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
            if (tui._shortcuts?.Invoke(key) == true) key.Handled = true;
        };
        tui._window.KeyDown += (_, key) =>
        {
            if (key == Key.L.WithCtrl && tui._vault is not null) { key.Handled = true; tui.Lock("Locked."); }
        };
        app.AddTimeout(TimeSpan.FromSeconds(2), () => { tui.Tick(); return true; });
        tui.Refresh();
        app.Run(tui._window);

        tui._vault?.Close();
        tui.WipeClipboard(wait: true);   // the program is ending - finish the wipe before it does
        tui._window.Dispose();
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
            return;
        }

        if (!_busy && !_midFlow) Refresh();
    }

    private void Refresh()
    {
        var sticks = UsbDetect.Find();
        var withVault = sticks.FirstOrDefault(s => s.IsSetUp);
        _stick = withVault ?? sticks.FirstOrDefault();

        var want = withVault is not null ? (MachineStore.IsEnrolled(_folder) ? "pin" : "enrol")
                 : _stick is not null ? "new-stick"
                 : "no-stick";
        if (_screen == $"{want}|{_stick?.Root}") return;
        _screen = $"{want}|{_stick?.Root}";

        switch (want)
        {
            case "no-stick":
                Show("Plug your PassVault stick in",
                     Text("This screen notices it on its own."),
                     Text($"PassVault looks in /media/{Environment.UserName} and /run/media/{Environment.UserName}, "
                          + "and in sticks.extra_places in your settings ('passvault config')."));
                break;
            case "new-stick": NewStickScreen(); break;
            case "enrol": EnrolScreen(); break;
            default: PinScreen(); break;
        }
    }

    // ---- setting up a new stick, and this PC ----

    private void NewStickScreen()
        => Show($"There is no vault on {_stick!.Root} yet",
                Text("Setting it up makes an empty vault there and shows you your 12 words, once."),
                Button("Set up this stick", () =>
                {
                    var (phrase, _) = VaultStorage.Initialise(_stick.Root);
                    WordsScreen(phrase);
                }));

    private void WordsScreen(string phrase)
    {
        _screen = "words";
        var words = phrase.Split(' ');
        var lines = Enumerable.Range(0, 4).Select(row => string.Concat(
            Enumerable.Range(0, 3).Select(col => $"{row + col * 4 + 1,2}. {words[row + col * 4],-12}")));

        Show("Write these 12 words down, on paper",
             Text(string.Join("\n", lines)),
             Text("They are shown once and stored nowhere. They are the only way back in if this stick is lost or broken."),
             Button("I have written them down", () => ChoosePinScreen(phrase)));
        _midFlow = true;
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
        _screen = "choose-pin";
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
                     var chosen = pin.Text;
                     var root = _stick!.Root;
                     Slowly("Setting this PC up", () =>
                     {
                         var device = DeviceSecret.Create(chosen);
                         return Enrolment.Enrol(_folder, root, phrase, chosen, device.Secret, device.Protection, device.Sealed).MasterKey;
                     });
                 }
             }));
        _midFlow = true;
    }

    // ---- the everyday unlock ----

    private void PinScreen()
    {
        MachineFile machine;
        try
        {
            machine = MachineStore.Read(_folder);
        }
        catch (FileNotFoundException)
        {
            EnrolScreen("This PC's setup file is damaged. Type your 12 words to set it up again.");
            return;
        }

        if (machine.PinDisabled)
        {
            PhraseScreen();
            Say($"The PIN was switched off after {Enrolment.MaxAttempts} wrong tries. Your 12 words switch it back on.");
            return;
        }

        var pin = new TextField { Secret = true, Width = 14 };
        pin.ValueChanged += (_, _) => { if (pin.Text.Length == machine.PinLength) TryPin(pin, machine.PinLength); };
        pin.Accepting += (_, e) => { e.Handled = true; TryPin(pin, machine.PinLength); };   // Enter

        Show("Enter your PIN", pin, Button("Use my 12 words instead", PhraseScreen));
        if (machine.FailedAttempts > 0) Say($"{Enrolment.MaxAttempts - machine.FailedAttempts} tries left.");
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
        var wait = Enrolment.DelayBeforeNextTry(MachineStore.Read(_folder).FailedAttempts);
        Slowly(wait > TimeSpan.Zero ? $"Waiting {wait.TotalSeconds:0} seconds after earlier wrong tries" : "Opening your vault", () =>
        {
            Thread.Sleep(wait);
            var key = Enrolment.UnlockWithPin(_folder, root, pin, typed => DeviceSecret.Open(_folder, typed).Secret).MasterKey;
            DeviceSecret.UpgradeIfOld(_folder, pin);
            return key;
        });
    }

    private void PhraseScreen()
    {
        _screen = "phrase";
        var phrase = new TextField { Width = Dim.Fill(1) };
        Show("Your 12 words",
             Text("This works even if the PIN has been switched off, and it switches it back on."),
             phrase,
             Button("Open my vault", () =>
             {
                 if (!Mnemonic.TryParse(phrase.Text, out _, out var problem)) { Say(problem); return; }
                 var typed = phrase.Text;
                 var root = _stick!.Root;
                 Slowly("Opening your vault", () => Enrolment.UnlockWithPhrase(_folder, root, typed));
             }),
             Button("Back", () => { _screen = ""; Refresh(); }));
        _midFlow = true;
    }

    /// <summary>Argon2 takes a moment on purpose. Do it off the screen's thread, so the screen keeps drawing.</summary>
    private void Slowly(string doing, Func<byte[]> work)
    {
        _busy = true;
        Say(doing + "...");
        Task.Run(() =>
        {
            try
            {
                var key = work();
                _app.Invoke(() => { _busy = false; Opened(key); });
            }
            catch (Exception ex)
            {
                _app.Invoke(() => { _busy = false; Failed(ex); });
            }
        });
    }

    private void Failed(Exception ex)
    {
        switch (ex)
        {
            case UnlockException or PinRefusedException:
                Say(ex.Message);
                if (MachineStore.IsEnrolled(_folder) && MachineStore.Read(_folder).PinDisabled) { _screen = ""; Refresh(); }
                break;
            case InvalidOperationException or InvalidDataException or CryptographicException when MachineStore.IsEnrolled(_folder):
                // The chip was cleared or the board replaced: no PIN can work here again.
                _screen = "enrol";
                EnrolScreen("This PC's own secret can no longer be opened - its chip was cleared, or this is another PC. "
                            + "Type your 12 words to set it up again.");
                break;
            default:
                Say(ex.Message);
                break;
        }
    }

    // ---- the open vault ----

    private void Opened(byte[] masterKey)
    {
        try
        {
            _vault = new OpenVault(_folder, _stick!.Root, masterKey);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException)
        {
            Say($"Could not read your vault - {ex.Message}");
            return;
        }

        _lastKey = DateTime.UtcNow;
        VaultScreen();
        if (MachineStore.IsEnrolled(_folder) && MachineStore.Read(_folder).Protection != DeviceProtection.Tpm)
            Say("No TPM chip checks your PIN on this PC - its secret is only as safe as your files. That is weaker.");
    }

    private void VaultScreen(Entry? select = null)
    {
        _screen = "vault";
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
        _window.Add(new Label { X = 1, Y = 0, Text = "Search:" }, search, list, details,
                    new Label { X = 1, Y = Pos.AnchorEnd(1), Width = Dim.Fill(1), Text = Keys }, _message);
        Fill(select);
        list.SetFocus();
        Say($"Vault open - {vault.Living.Count()} credential(s). Locks after {_settings.LockAfterMinutes} minutes without use.");
    }

    private void EditScreen(Entry? entry)
    {
        _screen = "edit";
        var name = Field(entry?.Name);
        var username = Field(entry?.Username);
        var email = Field(entry?.Email);
        var password = Field(entry?.Password);
        password.Secret = true;
        var notes = Field(entry?.Notes);

        Show(entry is null ? "New credential" : $"Edit {entry.Name}",
             Text("Website or app"), name, Text("Username"), username, Text("Email"), email,
             Text("Password"), password, Text("Notes"), notes,
             Button("Save", () =>
             {
                 if (name.Text.Trim().Length == 0) { Say("Give it a name first."); return; }
                 Apply();
             }),
             Button("Make a strong password", () =>
             {
                 password.Text = PasswordGenerator.Make(_settings.GeneratorLength, _settings.GeneratorSymbols);
                 password.Secret = false;
             }),
             Button("Show / hide password", () => password.Secret = !password.Secret),
             Button("Cancel", () => VaultScreen(entry)));

        void Apply()
        {
            void Write(Entry e)
            {
                e.Name = name.Text.Trim();
                e.Username = Typed(username);
                e.Email = Typed(email);
                e.Password = Typed(password);
                e.Notes = Typed(notes);
            }

            try
            {
                string? warning;
                var target = entry ?? new Entry();
                if (entry is null) { Write(target); warning = _vault!.Add(target); }
                else warning = _vault!.Change(entry, Write);
                VaultScreen(target);
                Say(warning ?? "Saved to your stick and the spare copy.");
            }
            catch (SaveRefusedException refused)
            {
                Say(refused.Message);
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
        _vault?.Close();
        _vault = null;
        _screen = "";
        Refresh();
        Say(why);
    }

    // ---- the clipboard ----

    private void Copy(string? value, string what)
    {
        if (string.IsNullOrEmpty(value)) { Say($"There is no {what} here to copy."); return; }

        bool copied = OperatingSystem.IsLinux()
            ? TerminalClipboard.Put(value)
            : _app.Clipboard is { IsSupported: true } clipboard && clipboard.TrySetClipboardData(value);
        if (!copied)
        {
            Say("No clipboard reachable from here. On Linux, install wl-clipboard (Wayland) or xclip (X11).");
            return;
        }

        _onClipboard = value;
        Say($"Copied the {what} - the clipboard clears in {_settings.ClearClipboardAfterSeconds} seconds.");
        _app.AddTimeout(TimeSpan.FromSeconds(_settings.ClearClipboardAfterSeconds), () => { WipeClipboard(); return false; });
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
            if (OperatingSystem.IsLinux()) TerminalClipboard.WipeIf(ours);
            else if (_app.Clipboard is { } clipboard && clipboard.TryGetClipboardData(out var now) && now == ours)
                clipboard.TrySetClipboardData("");
        });
        if (wait) wiping.Wait(TimeSpan.FromSeconds(3));
    }

    // ---- building screens ----

    /// <summary>A heading, then each piece below the last, then the message line.</summary>
    private void Show(string heading, params View[] pieces)
    {
        _shortcuts = null;
        _midFlow = false;
        _window.RemoveAll();
        View above = new Label { X = 1, Y = 0, Text = heading };
        _window.Add(above);
        foreach (var piece in pieces)
        {
            piece.X = 1;
            piece.Y = Pos.Bottom(above) + (piece is Button ? 0 : 1);
            _window.Add(piece);
            above = piece;
        }
        _window.Add(_message);
        Say("");
        (pieces.FirstOrDefault(p => p is TextField) ?? pieces.FirstOrDefault(p => p is Button))?.SetFocus();
    }

    private void Say(string text) => _message.Text = text;

    private static Label Text(string text) => new() { Text = text, Width = Dim.Fill(1) };

    private static TextField Field(string? value) => new() { Text = value ?? "", Width = Dim.Fill(1) };

    private static string? Typed(TextField field) => field.Text.Length == 0 ? null : field.Text;

    private Button Button(string text, Action pressed)
    {
        var button = new Button { Text = text };
        button.Accepting += (_, e) => { e.Handled = true; pressed(); };
        return button;
    }
}
