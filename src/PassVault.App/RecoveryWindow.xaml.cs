using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PassVault.Core.Crypto;
using PassVault.Core.Machine;
using PassVault.Core.Model;
using PassVault.Core.Usb;

namespace PassVault.App;

/// <summary>
/// Replacing a stick that was stolen, lost or broken. All three end the same way, so this
/// is one walk-through rather than three.
/// </summary>
public partial class RecoveryWindow : Window
{
    private readonly string _folder = MachineStore.DefaultFolder;
    private readonly DispatcherTimer _watchDrives = new() { Interval = TimeSpan.FromSeconds(2) };

    private byte[]? _vault;
    private string _oldPhrase = "";
    private string? _targetStick;
    private string _newPhrase = "";

    public RecoveryWindow() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DarkTitleBar.Apply(this);
        LookForVault();
    }

    private void OnClosed(object? sender, EventArgs e) => _watchDrives.Stop();

    // ---- 1. where the credentials come from ----

    private void LookForVault()
    {
        try
        {
            var stick = UsbDetect.Find().FirstOrDefault(s => s.IsSetUp);
            var found = Recovery.FindVault(_folder, stick?.Root);

            _vault = found.Vault;
            SourceText.Text = found.Where;
            SourceDetail.Text = Describe(found.Vault);
            SourceNext.IsEnabled = true;
        }
        catch (Exception ex)
        {
            // Catching only UnlockException left InvalidDataException and JsonException to
            // escape, and with no handler anywhere the process simply disappeared - on the
            // one screen a user reaches because something has already gone wrong.
            _vault = null;
            SourceText.Text = "Nothing found";
            SourceDetail.Text = ex is UnlockException
                ? ex.Message
                : "The vault files this PC can see are damaged. Use a backup file instead.";
            SourceNext.IsEnabled = false;
        }

        Show(SourcePanel);
    }

    private static string Describe(byte[] vault)
    {
        var header = PassVault.Core.Vault.VaultFile.ReadHeader(vault);
        return $"epoch {header.Epoch}, save {header.Revision}";
    }

    /// <summary>
    /// How new the newest vault this PC can see is. Used to warn when a backup file is
    /// older than what is already on the stick.
    /// </summary>
    private (int Epoch, long Revision)? Newest()
    {
        try
        {
            var stick = UsbDetect.Find().FirstOrDefault(s => s.IsSetUp);
            var found = Recovery.FindVault(_folder, stick?.Root);
            var header = PassVault.Core.Vault.VaultFile.ReadHeader(found.Vault);
            return (header.Epoch, header.Revision);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OnPickBackupFile(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Pick your PassVault backup file",
            Filter = "PassVault backup (*.pv)|*.pv|All files (*.*)|*.*",
        };
        if (picker.ShowDialog() != true) return;

        try
        {
            var bytes = File.ReadAllBytes(picker.FileName);
            var header = PassVault.Core.Vault.VaultFile.ReadHeader(bytes);   // really one of ours?

            _vault = bytes;
            SourceText.Text = picker.FileName;
            SourceDetail.Text = Describe(bytes);
            SourceNext.IsEnabled = true;
            Say("");

            // "It picks whichever is newest" only ever applied to stick-versus-spare-copy.
            // A file picked by hand skipped the comparison entirely, so recovering from an
            // old backup quietly wiped everything saved since - on the stick AND on this PC.
            var newest = Newest();
            if (newest is { } n && (header.Epoch < n.Epoch
                                    || (header.Epoch == n.Epoch && header.Revision < n.Revision)))
            {
                Say($"Careful: this backup is older (epoch {header.Epoch}, save {header.Revision}) than the "
                    + $"vault this PC can already see (epoch {n.Epoch}, save {n.Revision}). Going on with it "
                    + "will lose everything saved since this backup was made.");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Say("That file is not a PassVault backup, or it is damaged.");
        }
    }

    private void OnSourceNext(object sender, RoutedEventArgs e)
    {
        Step("Your 12 words", "The ones you have now. New ones come at the end.");
        Show(PhrasePanel);
        PhraseBox.Focus();
    }

    // ---- 2. the old phrase ----

    private void OnPhraseNext(object sender, RoutedEventArgs e)
    {
        if (!Mnemonic.TryParse(PhraseBox.Text, out _, out var problem)) { Say(problem); return; }

        _oldPhrase = PhraseBox.Text;
        PhraseBox.Clear();
        Say("");

        Step("The new stick", "Plug in the stick you want to use from now on.");
        Show(StickPanel);
        RefreshDrives();

        _watchDrives.Tick += (_, _) => RefreshDrives();
        _watchDrives.Start();
    }

    // ---- 3. which drive ----

    private void RefreshDrives()
    {
        var chosen = (DriveList.SelectedItem as UsbStick)?.Root;
        var drives = UsbDetect.Find();

        DriveList.ItemsSource = drives;
        NoDrives.Visibility = drives.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (chosen is not null)
            DriveList.SelectedItem = drives.FirstOrDefault(d => d.Root == chosen);
    }

    private void OnDriveChosen(object sender, RoutedEventArgs e)
    {
        _targetStick = (DriveList.SelectedItem as UsbStick)?.Root;
        StickNext.IsEnabled = _targetStick is not null;
    }

    private void OnStickNext(object sender, RoutedEventArgs e)
    {
        _watchDrives.Stop();
        Step("A PIN for this PC", "The new keys mean this PC has to be set up again.");
        Show(PinPanel);
        NewPinBox.Focus();
    }

    // ---- 4. the PIN, then do it ----

    private void OnPinKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnGo(sender, e);
    }

    private async void OnGo(object sender, RoutedEventArgs e)
    {
        var pin = NewPinBox.Password;

        if (pin.Length != 4 || !pin.All(char.IsAsciiDigit)) { Say("A PIN is exactly 4 digits."); return; }
        if (pin != NewPinAgainBox.Password) { Say("Those two PINs are not the same."); return; }
        if (_vault is null || _targetStick is null) { Say("Something went wrong - close this and start again."); return; }

        NewPinBox.Clear();
        NewPinAgainBox.Clear();

        BusyText.Text = "Making the new stick...";
        Show(BusyPanel);
        Say("");

        var vault = _vault;
        var oldPhrase = _oldPhrase;
        var target = _targetStick;

        // Step one: re-key the stick. After this the old phrase is dead and the new one is
        // the only way in, so it has to reach the screen whatever happens next.
        string newPhrase;
        List<Entry> toChange;
        try
        {
            (_, newPhrase, toChange) = await Task.Run(() => Recovery.ReplaceStick(vault, oldPhrase, target));
        }
        catch (Exception ex)
        {
            Say(ex.Message);
            Show(PinPanel);
            return;
        }

        _newPhrase = newPhrase;
        _oldPhrase = "";

        // Step two: set this PC up with a PIN. If this fails the stick is still fine - it
        // just needs the new words typed in once. Rolling back to the PIN screen here was
        // what destroyed the only phrase for a stick that had already been rewritten.
        string? enrolProblem = null;
        try
        {
            await Task.Run(() =>
            {
                var (deviceSecret, protection) = DeviceSecret.Load(_folder);
                Enrolment.Enrol(_folder, target, newPhrase, pin, deviceSecret, protection);
            });
        }
        catch (Exception ex)
        {
            enrolProblem = ex.Message;
        }

        ShowDone(toChange);

        if (enrolProblem is not null)
            Say($"Your new stick is ready and the words below are the only way into it - write them "
                + $"down now. Setting a PIN on this PC did not work ({enrolProblem}), so open the vault "
                + "with the 12 words and choose a PIN then.");
    }

    // ---- 5. done ----

    private void ShowDone(List<Entry> toChange)
    {
        Step("Done - your new stick is ready", "The old stick is a dead key from this moment.");

        WordsList.ItemsSource = _newPhrase.Split(' ')
            .Select((word, i) => new { Number = i + 1, Word = word })
            .ToList();

        ChangeList.ItemsSource = toChange;
        Show(DonePanel);

        NewWordsWritten.IsChecked = false;
        FinishButton.IsEnabled = false;
        FinishRow.Visibility = Visibility.Visible;
    }

    private void OnNewWordsWrittenChanged(object sender, RoutedEventArgs e)
        => FinishButton.IsEnabled = NewWordsWritten.IsChecked == true;

    private void OnFinish(object sender, RoutedEventArgs e)
    {
        WordsList.ItemsSource = null;
        _newPhrase = "";

        var unlock = new UnlockWindow();
        Application.Current.MainWindow = unlock;
        unlock.Show();
        Close();
    }

    // ---- plumbing ----

    private IEnumerable<UIElement> AllPanels() =>
        [SourcePanel, PhrasePanel, StickPanel, PinPanel, DonePanel, BusyPanel];

    private void Show(UIElement panel)
    {
        foreach (var p in AllPanels())
            p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Step(string title, string sub)
    {
        StepTitle.Text = title;
        StepSub.Text = sub;
    }

    private void Say(string text)
    {
        Message.Text = text;
        Message.Foreground = (Brush)FindResource("Danger");
    }
}
