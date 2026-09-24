using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PassVault.Core.Crypto;
using PassVault.Core.Machine;
using PassVault.Core.Usb;

namespace PassVault.App;

public partial class UnlockWindow : Window
{
    private readonly DispatcherTimer _watchStick = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly string _folder = MachineStore.DefaultFolder;

    private UsbStick? _stick;
    private string? _phraseForSetup;
    private bool _midFlow;
    private bool _tryingPin;

    public UnlockWindow() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DarkTitleBar.Apply(this);
        Refresh();
        _watchStick.Tick += (_, _) => { if (!_midFlow) Refresh(); };
        _watchStick.Start();
    }

    private void OnClosed(object? sender, EventArgs e) => _watchStick.Stop();

    // ---- deciding which screen to show ----

    private void Refresh()
    {
        var sticks = UsbDetect.Find();
        var setUp = sticks.Where(s => s.IsSetUp).ToList();
        var withVault = setUp.FirstOrDefault();

        if (withVault is not null)
        {
            _stick = withVault;
            SetStatus("Stick connected", Mood.Good, $"{withVault.Root}  ·  {withVault.Label}");
            ShowUnlockOrEnrol();

            // Two set-up sticks is the realistic case right after a replacement. Picking the
            // lower drive letter in silence and then saying "that is not your stick" is the
            // worst of both - say which one is being used so the other can be unplugged.
            if (setUp.Count > 1)
                Say($"Two PassVault sticks are plugged in. Using {withVault.Root}. "
                    + $"Unplug it to use {setUp[1].Root} instead.", error: false);
            return;
        }

        if (sticks.Count > 0)
        {
            _stick = sticks[0];
            SetStatus("Empty stick found", Mood.Idle, $"{sticks[0].Root}  ·  no vault on it yet");
            Show(NewStickPanel);
            return;
        }

        // Not an error - it is where you start every time. A red dot on every launch
        // cries wolf, so no stick is grey.
        _stick = null;
        SetStatus("No stick plugged in", Mood.Idle, "Plug yours in and this screen will notice");

        // This line existed but was only ever assigned "". On the one screen where knowing
        // which drives the app can see matters most, it reserved space for nothing.
        var letters = DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => d.Name.TrimEnd('\\'))
            .ToList();
        DrivesFound.Text = letters.Count == 0
            ? "PassVault cannot see any drives at all."
            : $"Drives PassVault can see: {string.Join(", ", letters)}.  A stick formatted for "
              + "Linux or a Mac gets no drive letter, and cannot be used.";

        Show(NoStickPanel);
    }

    private void ShowUnlockOrEnrol()
    {
        if (!MachineStore.IsEnrolled(_folder))
        {
            Show(EnrolPanel);
            return;
        }

        var machine = MachineStore.Read(_folder);

        if (machine.PinDisabled)
        {
            Show(PhrasePanel);
            Say($"The PIN was switched off after {Enrolment.MaxAttempts} wrong tries. Your 12 words switch it back on.");
            return;
        }

        if (PinPanel.Visibility != Visibility.Visible)
        {
            Show(PinPanel);
            PinBox.Focus();
        }

        if (machine.FailedAttempts > 0)
            Say($"{Enrolment.MaxAttempts - machine.FailedAttempts} tries left.");
    }

    // ---- setting up a brand new stick ----

    private void OnSetUpStick(object sender, RoutedEventArgs e)
    {
        if (_stick is null) return;

        _midFlow = true;
        try
        {
            var (phrase, _) = VaultStorage.Initialise(_stick.Root);
            _phraseForSetup = phrase;
            SetStatus("Stick connected", Mood.Good, $"{_stick.Root}  ·  vault created");

            WordsList.ItemsSource = phrase.Split(' ')
                .Select((word, i) => new { Number = i + 1, Word = word })
                .ToList();

            WrittenDown.IsChecked = false;
            WordsNextButton.IsEnabled = false;
            Show(WordsPanel);
            Say("");
        }
        catch (Exception ex)
        {
            _midFlow = false;
            Say(ex.Message);
        }
    }

    private void OnWrittenDownChanged(object sender, RoutedEventArgs e)
        => WordsNextButton.IsEnabled = WrittenDown.IsChecked == true;

    private void OnWordsDone(object sender, RoutedEventArgs e)
    {
        WordsList.ItemsSource = null;   // stop showing them
        Show(PinSetupPanel);
        NewPinBox.Focus();
    }

    // ---- setting this PC up against an existing stick ----

    private void OnEnrolPhrase(object sender, RoutedEventArgs e)
    {
        var phrase = PhraseBox.Text;

        if (!Mnemonic.TryParse(phrase, out _, out var problem)) { Say(problem); return; }

        _phraseForSetup = phrase;
        _midFlow = true;
        PhraseBox.Clear();
        Show(PinSetupPanel);
        NewPinBox.Focus();
        Say("");
    }

    private void OnPinSetupKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnPinChosen(sender, e);
    }

    private async void OnPinChosen(object sender, RoutedEventArgs e)
    {
        var pin = NewPinBox.Password;

        if (pin.Length != 4 || !pin.All(char.IsAsciiDigit)) { Say("A PIN is exactly 4 digits."); return; }
        if (pin != NewPinAgainBox.Password) { Say("Those two PINs are not the same."); return; }
        if (_stick is null || _phraseForSetup is null) { Say("Something went wrong - start again."); return; }

        NewPinBox.Clear();
        NewPinAgainBox.Clear();

        var stickRoot = _stick.Root;
        var setupPhrase = _phraseForSetup;

        await RunSlowly("Setting this PC up", "Argon2id · 64 MiB · 3 passes", report =>
        {
            report.Report(StickFound);
            var (deviceSecret, protection) = DeviceSecret.Load(_folder);
            report.Report(ChipRead);

            report.Report(Deriving);
            var key = Enrolment.Enrol(_folder, stickRoot, setupPhrase, pin, deviceSecret, protection).MasterKey;
            report.Report(KeyReady);
            return key;
        });
    }

    // ---- the everyday unlock ----

    private void OnPinTyped(object sender, RoutedEventArgs e)
    {
        if (PinBox.Password.Length == 4) TryPin();
    }

    private void OnPinKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TryPin();
    }

    private async void TryPin()
    {
        if (_tryingPin) return;

        var pin = PinBox.Password;

        if (pin.Length != 4 || !pin.All(char.IsAsciiDigit))
        {
            // A bare return made the app look broken: four asterisks sitting in the box,
            // Enter doing nothing, no message at all. The setup screen says "A PIN is exactly
            // 4 digits" for byte-identical input, so the two screens disagreed as well.
            if (pin.Length > 0) { Say("A PIN is 4 digits, 0 to 9."); PinBox.Clear(); }
            return;
        }

        if (_stick is null) { Say("Plug your stick in first."); PinBox.Clear(); return; }

        // Read the drive now. The two-second poll can set _stick to null while the back-off
        // below is running, and the lambda then showed a raw NullReferenceException.
        var stickRoot = _stick.Root;

        _tryingPin = true;
        PinBox.Clear();

        try
        {
            var waitFor = Enrolment.DelayBeforeNextTry(MachineStore.Read(_folder).FailedAttempts);
            if (waitFor > TimeSpan.Zero)
            {
                // _midFlow keeps the two-second poll from wiping this message and leaving the
                // app sitting dead still with a live PIN box - which is what made people
                // retype the PIN mid-wait and start a second unlock over the same files.
                _midFlow = true;
                PinBox.IsEnabled = false;
                Say($"Waiting {waitFor.TotalSeconds:0} seconds after earlier wrong tries...", error: false);
                await Task.Delay(waitFor);
            }

            await RunSlowly("Opening your vault", "Argon2id · 64 MiB · 3 passes", report =>
            {
                report.Report(StickFound);
                var (deviceSecret, _) = DeviceSecret.Load(_folder);
                report.Report(ChipRead);

                report.Report(Deriving);
                var key = Enrolment.UnlockWithPin(_folder, stickRoot, pin, deviceSecret).MasterKey;
                report.Report(KeyReady);
                return key;
            });
        }
        finally
        {
            _tryingPin = false;
            PinBox.IsEnabled = true;
        }
    }

    private void OnRecovery(object sender, RoutedEventArgs e)
    {
        _watchStick.Stop();

        var recovery = new RecoveryWindow();
        Application.Current.MainWindow = recovery;
        recovery.Show();
        Close();
    }

    private void OnUsePhraseInstead(object sender, RoutedEventArgs e)
    {
        _midFlow = true;
        Show(PhrasePanel);
        RecoveryBox.Focus();
        Say("");
    }

    private void OnBackToPin(object sender, RoutedEventArgs e)
    {
        _midFlow = false;
        RecoveryBox.Clear();
        Say("");
        Refresh();
    }

    private async void OnPhraseUnlock(object sender, RoutedEventArgs e)
    {
        var phrase = RecoveryBox.Text;
        if (_stick is null) { Say("Plug your stick in first."); return; }
        if (!Mnemonic.TryParse(phrase, out _, out var problem)) { Say(problem); return; }

        var stickRoot = _stick.Root;
        RecoveryBox.Clear();
        await RunSlowly("Opening your vault", "Argon2id · 64 MiB · 3 passes", report =>
        {
            report.Report(StickFound);

            // The second pin stays up on purpose: your 12 words do not use this PC's chip.
            report.Report(Deriving);
            var key = Enrolment.UnlockWithPhrase(_folder, stickRoot, phrase);
            report.Report(KeyReady);
            return key;
        });
    }

    // ---- plumbing ----

    /// <summary>
    /// Argon2id takes a moment on purpose. Run it off the UI thread so the window does not
    /// freeze, and show what is happening.
    /// </summary>
    private async Task RunSlowly(string state, string detail, Func<IProgress<int>, byte[]> work)
    {
        var previous = VisiblePanel();

        SetStatus(state, Mood.Working, detail);
        Mechanism.Reset();
        Show(BusyPanel);
        Say("");

        // Progress is created here, on the UI thread, so its callback comes back here too.
        var report = new Progress<int>(Stage);

        try
        {
            var masterKey = await Task.Run(() => work(report));

            Mechanism.Seat(4, "Your vault");
            await Task.Delay(200);            // let the last pin land before the window changes
            OpenVault(masterKey);
        }
        catch (Exception ex)
        {
            _midFlow = false;
            Mechanism.Reset();
            SetStatus("Could not open your vault", Mood.Bad);
            Say(ex.Message);
            if (previous is not null) Show(previous);
        }
    }

    /// <summary>
    /// Each step of the unlock, as it genuinely happens. Nothing here is on a timer.
    /// </summary>
    private void Stage(int step)
    {
        switch (step)
        {
            case StickFound: Mechanism.Seat(1, "Your stick"); break;
            case ChipRead: Mechanism.Seat(2, "This PC's chip"); break;
            case Deriving: Mechanism.Breathe(3, "Deriving your key"); break;
            case KeyReady: Mechanism.Seat(3, "Your key"); break;
        }
    }

    private const int StickFound = 1;
    private const int ChipRead = 2;
    private const int Deriving = 3;
    private const int KeyReady = 4;

    /// <summary>
    /// Build the window first, then stop watching. Reading and decrypting the vault happens
    /// in that constructor and can throw - and stopping the timer first left this screen
    /// permanently blind, promising to notice a stick it had stopped looking for.
    /// </summary>
    private void OpenVault(byte[] masterKey)
    {
        if (_stick is null) throw new UnlockException("Your PassVault stick is not plugged in.");

        var main = new MainWindow(_stick.Root, masterKey);

        _watchStick.Stop();
        Application.Current.MainWindow = main;
        main.Show();
        Close();
    }

    private IEnumerable<Panel> AllPanels() =>
        [NoStickPanel, NewStickPanel, WordsPanel, EnrolPanel, PinSetupPanel, PinPanel, PhrasePanel, BusyPanel];

    private Panel? VisiblePanel() => AllPanels().FirstOrDefault(p => p.Visibility == Visibility.Visible);

    private void Show(Panel panel)
    {
        foreach (var p in AllPanels())
            p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;

        _midFlow = panel == WordsPanel || panel == PinSetupPanel || panel == EnrolPanel
                   || panel == PhrasePanel || panel == BusyPanel;

        // Live on every screen including the busy one, this was a way to end up with the
        // recovery wizard and a fully open vault on screen at the same time - and, on the
        // words screen, to walk away from a stick whose only phrase had just been shown.
        RecoveryLink.Visibility = panel == WordsPanel || panel == PinSetupPanel || panel == BusyPanel
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void SetStatus(string state, Mood mood, string detail = "")
    {
        Band.State = state;
        Band.Detail = detail;
        Band.Mood = mood;
    }

    private void Say(string text, bool error = true)
    {
        Message.Text = text;
        Message.Foreground = (Brush)FindResource(error ? "Danger" : "Muted");
    }
}

/// <summary>Makes the window's title bar dark so it matches the app instead of glaring white.</summary>
internal static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            int on = 1;
            DwmSetWindowAttribute(handle, 20, ref on, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
        }
        catch (DllNotFoundException)
        {
            // Older Windows. The app still works, the title bar is just light.
        }
    }
}
