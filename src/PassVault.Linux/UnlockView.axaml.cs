using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PassVault.Core.Crypto;
using PassVault.Core.Machine;
using PassVault.Core.Usb;
using PassVault.Core.Vault;

namespace PassVault.Linux;

/// <summary>
/// The way in: notices the stick, sets up a new stick or this PC, takes the PIN or the 12 words.
/// What to offer is decided by Unlocker - the same code the terminal app uses.
/// </summary>
public partial class UnlockView : UserControl
{
    private readonly string _folder = MachineStore.DefaultFolder;
    private readonly DispatcherTimer _watchStick = new() { Interval = TimeSpan.FromSeconds(2) };

    private UsbStick? _stick;
    private string? _phraseForSetup;
    private int _pinLength = Enrolment.MinPinLength;

    /// <summary>Which screen is up and for which stick - the poll repaints only when that changes.</summary>
    private string _screen = "";
    private bool _busy;

    /// <summary>Part-way through something the stick does not decide. The poll leaves it alone, typing and all.</summary>
    private bool _midFlow;

    /// <summary>The vault is open: the window shows it. The notice is anything the user should know about this PC.</summary>
    public event Action<OpenVault, string?>? Opened;

    public UnlockView()
    {
        InitializeComponent();
        WhereText.Text = $"This screen notices it on its own. PassVault looks in /media/{Environment.UserName} and "
                         + $"/run/media/{Environment.UserName}, and in sticks.extra_places in your settings.";
        _watchStick.Tick += (_, _) => { if (!_busy && !_midFlow) Refresh(); };
        AttachedToVisualTree += (_, _) =>
        {
            Refresh();
            _watchStick.Start();
            // Once a run. Written only to the terminal, a desktop launch showed them nowhere - and a
            // stricter setting the file got wrong was silently not in force.
            if (Program.Settings.Problems.Count > 0 && !_toldSettings)
            {
                _toldSettings = true;
                SayMore("Settings: " + string.Join(" ", Program.Settings.Problems));
            }
        };
        DetachedFromVisualTree += (_, _) => _watchStick.Stop();
    }

    private static bool _toldSettings;

    // ---- deciding which screen to show ----

    private void Refresh()
    {
        Arrival here;
        try
        {
            here = Unlocker.Look(_folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say("Could not read this PC's setup file just now. Trying again...", bad: false);
            return;
        }

        _stick = here.Stick;
        // The second stick too: plugged in, or taken out, while this screen is up is a change to say.
        var screen = $"{here.Situation}|{_stick?.Root}|{here.OtherStick?.Root}";
        if (_screen == screen) return;
        _screen = screen;
        Band(here);

        switch (here.Situation)
        {
            case Situation.NoStick: Show(NoStickPanel); break;
            case Situation.NewStick: Show(NewStickPanel); break;
            case Situation.SetUpThisPc: Show(EnrolPanel); break;
            case Situation.SetupDamaged:
                Show(EnrolPanel, midFlow: true);
                Say("This PC's setup file is damaged. Type your 12 words to set it up again.", bad: false);
                break;
            case Situation.Rekeyed:
                Show(EnrolPanel, midFlow: true);
                Say("This stick was re-keyed on another PC, so the PIN here no longer fits. "
                    + "Type the new 12 words to set this PC up again.", bad: false);
                break;
            case Situation.PinOff:
                Show(PhrasePanel, midFlow: true);
                Say($"The PIN was switched off after {Enrolment.MaxAttempts} wrong tries. Your 12 words switch it back on.");
                break;
            default:
                _pinLength = here.PinLength;
                Show(PinPanel);
                if (here.FailedAttempts > 0) Say($"{Enrolment.MaxAttempts - here.FailedAttempts} tries left.");
                break;
        }

        if (here.OtherStick is { } other)
            SayMore($"Two PassVault sticks are plugged in. Using {_stick!.Root}. Unplug it to use {other.Root} instead.");
    }

    private void Band(Arrival here)
    {
        (State.Text, Detail.Text) = here.Situation switch
        {
            Situation.NoStick => ("No stick plugged in", "Plug yours in and this screen will notice"),
            Situation.NewStick => ("Empty stick found", $"{_stick!.Root}  ·  no vault on it yet"),
            _ => ("Stick connected", $"{_stick!.Root}  ·  {_stick.Label}"),
        };
    }

    // ---- setting up a new stick, and this PC ----

    private void OnSetUpStick(object? sender, RoutedEventArgs e)
    {
        if (_stick is null) return;
        try
        {
            var (phrase, _) = VaultStorage.Initialise(_stick.Root);
            _phraseForSetup = phrase;
            WordsGrid.Children.Clear();
            var words = phrase.Split(' ');
            for (int i = 0; i < words.Length; i++)
                WordsGrid.Children.Add(new TextBlock { Text = $"{i + 1,2}. {words[i]}", FontFamily = "monospace", FontSize = 16, Margin = new(0, 4, 24, 4) });
            WrittenDown.IsChecked = false;
            Show(WordsPanel, midFlow: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Say(ex.Message);   // half a vault on it already, or a stick that is full or read-only
        }
    }

    private void OnWrittenDownChanged(object? sender, RoutedEventArgs e) => WordsNext.IsEnabled = WrittenDown.IsChecked == true;

    private void OnWordsDone(object? sender, RoutedEventArgs e)
    {
        WordsGrid.Children.Clear();   // stop showing them
        ShowPinSetup();
    }

    private void OnEnrolPhrase(object? sender, RoutedEventArgs e)
    {
        var phrase = PhraseBox.Text ?? "";
        if (!Mnemonic.TryParse(phrase, out _, out var problem)) { Say(problem); return; }
        _phraseForSetup = phrase;
        PhraseBox.Text = "";
        ShowPinSetup();
    }

    private void ShowPinSetup()
    {
        PinRule.Text = $"{Program.Settings.MinPinLength} to 12 digits - 6 or more is much harder to guess. You will type it every day, with your stick plugged in.";
        NewPinBox.Text = NewPinAgainBox.Text = "";
        Show(PinSetupPanel, midFlow: true);
        NewPinBox.Focus();
    }

    private void OnPinSetupKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnPinChosen(sender, e);
    }

    private void OnPinChosen(object? sender, RoutedEventArgs e)
    {
        var pin = NewPinBox.Text ?? "";
        int shortest = Program.Settings.MinPinLength;
        if (!Enrolment.IsPin(pin) || pin.Length < shortest) { Say($"A PIN here is {shortest} to 12 digits, 0 to 9."); return; }
        if (pin != NewPinAgainBox.Text) { Say("Those two PINs are not the same."); return; }
        if (_stick is null || _phraseForSetup is null) { Say("Something went wrong - start again."); return; }

        var (root, phrase) = (_stick.Root, _phraseForSetup);
        NewPinBox.Text = NewPinAgainBox.Text = "";
        Slowly("Setting this PC up", () =>
        {
            var (key, protection) = Unlocker.SetUpThisPc(_folder, root, phrase, pin);
            return (key, Unlocker.Weakness(protection));
        });
    }

    // ---- the everyday unlock ----

    private void OnPinTyped(object? sender, TextChangedEventArgs e)
    {
        if ((PinBox.Text ?? "").Length == _pinLength) TryPin();
    }

    private void OnPinKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TryPin();
    }

    private void TryPin()
    {
        var pin = PinBox.Text ?? "";
        if (_busy) return;
        PinBox.Text = "";
        if (!Enrolment.IsPin(pin)) { if (pin.Length > 0) Say("A PIN is 4 to 12 digits, 0 to 9."); return; }

        // It cannot be right, and asking the chip would spend one of its tries.
        if (pin.Length != _pinLength) { Say($"Your PIN here is {_pinLength} digits."); return; }
        if (_stick is null) { Say("Plug your stick in first."); return; }

        var root = _stick.Root;
        if (Unlocker.WaitBeforeTry(_folder) is not { } wait) { _screen = ""; Refresh(); return; }   // set-up gone or mid-save
        Slowly(wait > TimeSpan.Zero ? $"Waiting {wait.TotalSeconds:0} seconds after earlier wrong tries" : "Opening your vault", () =>
        {
            Thread.Sleep(wait);
            return (Unlocker.WithPin(_folder, root, pin), null);
        });
    }

    private void OnUsePhraseInstead(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;   // a waiting PIN try still opens the vault when it ends
        RecoveryBox.Text = "";
        Show(PhrasePanel, midFlow: true);
        RecoveryBox.Focus();
    }

    private void OnBack(object? sender, RoutedEventArgs e)
    {
        RecoveryBox.Text = "";
        _screen = "";
        _midFlow = false;
        Refresh();
    }

    private void OnPhraseUnlock(object? sender, RoutedEventArgs e)
    {
        var phrase = RecoveryBox.Text ?? "";
        if (_stick is null) { Say("Plug your stick in first."); return; }
        if (!Mnemonic.TryParse(phrase, out _, out var problem)) { Say(problem); return; }

        var root = _stick.Root;
        RecoveryBox.Text = "";
        Slowly("Opening your vault", () => (Enrolment.UnlockWithPhrase(_folder, root, phrase), null));
    }

    // ---- plumbing ----

    /// <summary>
    /// Argon2 takes a moment on purpose: off the screen's thread, so the window keeps drawing. One at a
    /// time - a second press, or the 12 words during a PIN's wait, opened the vault twice.
    /// </summary>
    private void Slowly(string doing, Func<(byte[] Key, string? Notice)> work)
    {
        if (_busy) return;
        _busy = true;
        var back = Visible();
        BusyText.Text = doing + "...";
        Show(BusyPanel, midFlow: true);
        Say("");

        Task.Run(() =>
        {
            try
            {
                var (key, notice) = work();
                Dispatcher.UIThread.Post(() => { _busy = false; Done(key, notice); });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => { _busy = false; Failed(ex, back); });
            }
        });
    }

    private void Done(byte[] masterKey, string? notice)
    {
        try
        {
            var vault = new OpenVault(_folder, _stick!.Root, masterKey);
            _watchStick.Stop();
            Opened?.Invoke(vault, notice);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Security.Cryptography.CryptographicException
                                       or System.Text.Json.JsonException)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(masterKey);   // only OpenVault.Close did, never reached
            _screen = "";
            Refresh();
            Say($"Could not read your vault - {ex.Message}");
        }
    }

    private void Failed(Exception ex, Panel? back)
    {
        if (ex is ChipLostException)
        {
            _chipLost = true;
            Show(EnrolPanel, midFlow: true);   // no PIN can work here again
            Say(ex.Message, bad: false);
            return;
        }

        // The tenth wrong PIN switches it off: show the 12 words - and keep the reason on screen.
        // A failed set-up starts again from the 12 words: the PIN screen has no way back, and the
        // words, a resting chip, missing tools - nothing there could change on another try. After
        // the chip was lost the old setup file still reads as "PIN" - but no PIN can work there.
        if (back == PinSetupPanel && _chipLost)
        {
            Show(EnrolPanel, midFlow: true);
        }
        else if ((ex is UnlockException && back != PhrasePanel && Unlocker.PinSwitchedOff(_folder)) || back == PinSetupPanel)
        {
            _screen = "";
            _midFlow = false;
            Refresh();
        }
        else if (back is not null)
        {
            Show(back, midFlow: back != PinPanel);
            if (back == PinPanel) PinBox.Focus();
        }
        SayFirst(ex.Message);   // ahead of anything the fresh screen says, like a second stick
    }

    /// <summary>This PC's own secret will never open again: setting up is the only way, whatever else fails.</summary>
    private bool _chipLost;

    private IEnumerable<Panel> AllPanels() =>
        [NoStickPanel, NewStickPanel, WordsPanel, EnrolPanel, PinSetupPanel, PinPanel, PhrasePanel, BusyPanel];

    private Panel? Visible() => AllPanels().FirstOrDefault(p => p.IsVisible);

    private void Show(Panel panel, bool midFlow = false)
    {
        foreach (var p in AllPanels()) p.IsVisible = p == panel;
        _midFlow = midFlow;
        Say("");
        // Digits left from before - the 12 words screen and Back, the stick out and in - made the
        // full PIN auto-submit too early, wrong, and spend one of the chip's tries.
        if (panel == PinPanel) PinBox.Text = "";
        // Somewhere sensible for the keys to go - left alone, the first Tab went to the title bar's buttons.
        Control? first = panel == PinPanel ? PinBox : panel == EnrolPanel ? PhraseBox : panel == PinSetupPanel ? NewPinBox
                       : panel == PhrasePanel ? RecoveryBox : panel == NewStickPanel ? SetUpButton : panel == WordsPanel ? WrittenDown : null;
        if (first is not null) Dispatcher.UIThread.Post(() => first.Focus(), DispatcherPriority.Input);
    }

    public void Say(string text, bool bad = true)
    {
        Message.Text = text;
        Message.Classes.Set("Bad", bad && text.Length > 0);
    }

    /// <summary>Added to what is said already: it said why this screen is up, and replacing it lost that.</summary>
    private void SayMore(string text) => Say($"{Message.Text} {text}".Trim(), Message.Classes.Contains("Bad"));

    /// <summary>Put in front of what is said already - a lock reason replaced the two-sticks warning, for good.</summary>
    public void SayFirst(string text, bool bad = true) => Say($"{text} {Message.Text}".Trim(), bad);
}
