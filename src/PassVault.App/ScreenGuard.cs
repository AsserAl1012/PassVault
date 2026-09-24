using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PassVault.App;

/// <summary>
/// Keeps PassVault's windows out of screenshots, screen recordings and screen sharing, so a
/// revealed password does not end up in a Teams call, a recording or Windows Recall.
///
/// It guards against accidents, not malware. Microsoft says there is "no guarantee": a program
/// already running on this PC can switch it off from inside our process. So an off switch
/// costs nothing in security - and one is needed, because someone using a screen magnifier,
/// or helping over TeamViewer or AnyDesk, sees nothing of these windows at all.
///
/// Captures get a black box rather than nothing (WDA_MONITOR, not WDA_EXCLUDEFROMCAPTURE).
/// The content is hidden just as well, it works the same on every Windows version, and a black
/// box says "something is here, hidden on purpose" where an empty space says "it never opened".
/// </summary>
internal static class ScreenGuard
{
    public const string AllowFlag = "--allow-screen-capture";

    private static bool _allowed;
    private static bool _failed;

    /// <summary>True when this run's windows really are hidden from capture.</summary>
    public static bool On => !_allowed && !_failed;

    public static void Decide(string[] args)
    {
        // In a Remote Desktop session this window is drawn for the person connected, and
        // hiding it from capture can hide it from them. Chromium makes the same exception.
        _allowed = args.Contains(AllowFlag, StringComparer.OrdinalIgnoreCase)
                   || GetSystemMetrics(SmRemoteSession) != 0;
    }

    /// <summary>
    /// Call from every window's constructor. A dialog does not inherit it from its owner.
    ///
    /// It acts at SourceInitialized, when the window exists but has never been drawn. Loaded
    /// is too late: by then the window is on screen, and the unlock window can already be
    /// showing the 12 words.
    /// </summary>
    public static void Protect(Window window)
    {
        if (_allowed) return;

        window.SourceInitialized += (_, _) =>
        {
            if (!SetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, WdaMonitor))
                _failed = true;   // the vault window then says capture is allowed - which is the truth
        };
    }

    private const uint WdaMonitor = 0x1;
    private const int SmRemoteSession = 0x1000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
