using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace PassVault.App;

/// <summary>
/// Starting up, shutting down, and the two things that have to be true for the whole
/// process: only one copy runs, and nothing ever disappears without saying why.
/// </summary>
public partial class App : Application
{
    private const string OnlyOneName = "PassVault.SingleInstance.v1";

    private Mutex? _onlyOne;

    protected override void OnStartup(StartupEventArgs e)
    {
        // One copy at a time. Two windows on one vault is not a cosmetic problem: each holds
        // its own snapshot of the entries, and whichever saves last writes its snapshot over
        // the other one, so a credential added in the first window is silently deleted by a
        // save in the second. Both say "Saved to your stick and the spare copy".
        _onlyOne = new Mutex(initiallyOwned: true, OnlyOneName, out bool mine);
        if (!mine)
        {
            BringTheOtherOneForward();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Closing the app is a deliberate "I am done". Locking is not - see ClipboardGuard.
        ClipboardGuard.ClearNow();
        _onlyOne?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// The last net under everything. A password manager that vanishes silently is worse
    /// than one that admits it broke: the user is left wondering whether their vault went
    /// with it. Everything on disk is written through WriteFileSafely, so it did not.
    /// </summary>
    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ClipboardGuard.ClearNow();

        MessageBox.Show(
            "PassVault has hit a problem and has to close.\n\n"
            + e.Exception.Message
            + "\n\nYour vault is safe. Nothing is ever written over in place, so the file on your "
            + "stick is either the last good save or the one before it.",
            "PassVault", MessageBoxButton.OK, MessageBoxImage.Error);

        Shutdown();
    }

    private static void BringTheOtherOneForward()
    {
        try
        {
            var me = Process.GetCurrentProcess();
            foreach (var other in Process.GetProcessesByName(me.ProcessName))
            {
                if (other.Id == me.Id || other.MainWindowHandle == IntPtr.Zero) continue;
                SetForegroundWindow(other.MainWindowHandle);
                return;
            }
        }
        catch (Exception)
        {
            // Not worth a dialog. The first window is open somewhere.
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
