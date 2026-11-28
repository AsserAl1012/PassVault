using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using PassVault.Core.Machine;

namespace PassVault.Linux;

/// <summary>
/// The clipboard, and the promise attached to it: what PassVault puts there is gone after the set
/// time, and nothing else you copied is touched.
///
/// Every copy is marked as a secret (x-kde-passwordManagerHint), which KDE's Klipper and GNOME's
/// clipboard managers honour by not keeping it in their history - the terminal app's tools cannot
/// do that. Like the Windows app's ClipboardGuard, it lives on the application: copy a password,
/// lock, go and paste it is the workflow, and a timer on the vault screen died with the screen.
/// </summary>
internal static class GuiClipboard
{
    private static readonly DataFormat<string> SecretHint = DataFormat.CreateStringPlatformFormat("x-kde-passwordManagerHint");

    private static IClipboard? _clipboard;
    private static string? _ours;
    private static int _copies;

    /// <summary>The time is up, but the wipe has not happened yet.</summary>
    private static bool _due;

    /// <summary>The wipe under way. A copy waits for it: landing between its read and its clear, the new copy was wiped at once.</summary>
    private static Task _wiping = Task.CompletedTask;

    /// <summary>
    /// A click or key has reached the window since it last came to the front. Under Wayland only
    /// then does a clipboard change from the window count: GNOME ignored one made right after the
    /// user came back with Alt+Tab, while the clipboard said it was done.
    /// </summary>
    public static bool Touched { get; set; }

    public static async Task<bool> Put(IClipboard? clipboard, string value)
    {
        if (clipboard is null) return false;
        await _wiping;
        try
        {
            var item = new DataTransferItem();
            item.SetText(value);
            item.Set(SecretHint, "secret");
            var data = new DataTransfer();
            data.Add(item);
            await clipboard.SetDataAsync(data);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or TimeoutException)
        {
            return false;
        }

        (_clipboard, _ours, _due) = (clipboard, value, false);
        int mine = ++_copies;   // an older copy's timer must not wipe a newer copy early
        DispatcherTimer.RunOnce(() => { if (mine == _copies) { _due = true; _wiping = Wipe(); } },
                                TimeSpan.FromSeconds(Program.Settings.ClearClipboardAfterSeconds));
        return true;
    }

    /// <summary>For each click or key in the window: a wipe that could not happen yet happens now.</summary>
    public static Task WipeIfDue() => _due ? _wiping = Wipe() : Task.CompletedTask;

    /// <summary>
    /// Said with each copy when it applies: with no wl-clipboard under Wayland, the password stays
    /// on the clipboard while the user is in another window - until they click or type in PassVault.
    /// </summary>
    public static string? Limit => DesktopClipboard.Wayland && !DesktopClipboard.CanReach
        ? " Install wl-clipboard, so it clears while you are in another window too - until then, it clears the next time you click or type in PassVault."
        : null;

    /// <summary>
    /// Only if it still holds exactly what PassVault put there. Cleared by putting nothing there:
    /// under WSL, clearing did not reach the Windows side of the clipboard and the text came back.
    ///
    /// Under Wayland a window can change the clipboard only while it is in front and has had a
    /// click or key since coming there - and after a copy it is not, the user is pasting somewhere
    /// else. Avalonia does not say so: it reads back its own copy, "clears" it, and the desktop goes
    /// on handing the password out. Tested on GNOME, it stayed pasteable until PassVault was closed.
    /// So otherwise the desktop's own tools do it, which can - tried again every few seconds if they
    /// fail - and without them the wipe waits for the next click or key in the window.
    /// </summary>
    public static async Task Wipe()
    {
        if (_clipboard is not { } clipboard || _ours is not { } ours) return;

        bool inFront = Touched && (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.IsActive == true;
        if (inFront || !await Task.Run(() => DesktopClipboard.WipeIf(ours)))
        {
            if (!inFront && DesktopClipboard.Wayland)
            {
                if (DesktopClipboard.CanReach)   // behind the lock screen, say: the tools could not get in yet
                    DispatcherTimer.RunOnce(() => { if (ReferenceEquals(_ours, ours)) _wiping = Wipe(); }, TimeSpan.FromSeconds(3));
                return;
            }
            try
            {
                if (await clipboard.TryGetTextAsync() == ours) await clipboard.SetTextAsync("");
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or TimeoutException)
            {
                return;
            }
        }

        if (ReferenceEquals(_ours, ours)) (_ours, _due) = (null, false);   // not a newer copy made meanwhile
    }
}
