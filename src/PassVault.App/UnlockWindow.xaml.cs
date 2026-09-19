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
        var withVault = sticks.FirstOrDefault(s => s.IsSetUp);

        if (withVault is not null)
        {
            _stick = withVault;
            SetStatus($"Stick connected  ({withVault.Root})", ok: true);
            ShowUnlockOrEnrol();
            return;
        }

        if (sticks.Count > 0)
        {
            _stick = sticks[0];
            SetStatus($"Empty stick found  ({sticks[0].Root})", ok: false, amber: true);
            Show(NewStickPanel);
            return;
        }

        _stick = null;
        SetStatus("No stick plugged in", ok: false);
        DrivesFound.Text = "";
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
            SetStatus($"Stick connected  ({_stick.Root})", ok: true);

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

        await RunSlowly("Setting up...", () =>
        {
            var (deviceSecret, protection) = DeviceSecret.Load(_folder);
            return Enrolment.Enrol(_folder, _stick.Root, _phraseForSetup, pin, deviceSecret, protection).MasterKey;
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
        var pin = PinBox.Password;
        if (pin.Length != 4 || !pin.All(char.IsAsciiDigit)) return;
        if (_stick is null) { Say("Plug your stick in first."); return; }

        PinBox.Clear();

        var waitFor = Enrolment.DelayBeforeNextTry(MachineStore.Read(_folder).FailedAttempts);
        if (waitFor > TimeSpan.Zero)
        {
            Say($"Waiting {waitFor.TotalSeconds:0} seconds after earlier wrong tries...", error: false);
            await Task.Delay(waitFor);
        }

        await RunSlowly("Opening...", () =>
        {
            var (deviceSecret, _) = DeviceSecret.Load(_folder);
            return Enrolment.UnlockWithPin(_folder, _stick.Root, pin, deviceSecret).MasterKey;
        });
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

        RecoveryBox.Clear();
        await RunSlowly("Opening...", () => Enrolment.UnlockWithPhrase(_folder, _stick.Root, phrase));
    }

    // ---- plumbing ----

    /// <summary>
    /// Argon2id takes a moment on purpose. Run it off the UI thread so the window does not
    /// freeze, and show what is happening.
    /// </summary>
    private async Task RunSlowly(string busyText, Func<byte[]> work)
    {
        var previous = VisiblePanel();
        BusyText.Text = busyText;
        Show(BusyPanel);
        Say("");

        try
        {
            var masterKey = await Task.Run(work);
            OpenVault(masterKey);
        }
        catch (Exception ex)
        {
            _midFlow = false;
            Say(ex.Message);
            if (previous is not null) Show(previous);
            Refresh();
        }
    }

    private void OpenVault(byte[] masterKey)
    {
        _watchStick.Stop();

        var main = new MainWindow(_stick!.Root, masterKey);
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
    }

    private void SetStatus(string text, bool ok, bool amber = false)
    {
        StickText.Text = text;
        StickDot.Fill = (Brush)FindResource(ok ? "Good" : amber ? "Accent" : "Bad");
    }

    private void Say(string text, bool error = true)
    {
        Message.Text = text;
        Message.Foreground = (Brush)FindResource(error ? "Bad" : "Muted");
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
