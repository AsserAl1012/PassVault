using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PassVault.App;

/// <summary>
/// The clipboard, and the promise attached to it: what PassVault puts there is gone twenty
/// seconds later, and nothing else you copied is touched.
///
/// This lives on the application rather than on the vault window for one reason. The obvious
/// thing to do is copy a password, lock the vault, and go and paste it - and the window's own
/// timer died the moment it closed, so the old code wiped the clipboard on the way out. You
/// were told you had twenty seconds and got none.
/// </summary>
internal static class ClipboardGuard
{
    public static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(20);

    private static readonly DispatcherTimer Watch = new() { Interval = ClearAfter };

    /// <summary>What we put there. Only this gets wiped - never anything the user copied.</summary>
    private static string? _ours;

    static ClipboardGuard() => Watch.Tick += (_, _) => ClearNow();

    /// <summary>
    /// Copy, and tell Windows this is a secret.
    ///
    /// Plain Clipboard.SetText is not enough. With Clipboard History on (Win+V) the password
    /// stays in that list long after we have cleared the clipboard itself, and with cloud
    /// clipboard on it is uploaded to Microsoft - which would break the one promise the whole
    /// product rests on. These three formats are how you opt out of both.
    /// </summary>
    public static bool Put(string value)
    {
        var data = new DataObject();
        data.SetText(value);
        foreach (var format in new[]
                 {
                     "ExcludeClipboardContentFromMonitorProcessing",
                     "CanIncludeInClipboardHistory",
                     "CanUploadToCloudClipboard",
                 })
        {
            data.SetData(format, new MemoryStream(new byte[] { 0, 0, 0, 0 }));
        }

        try
        {
            // copy: true, so it survives this window closing - that is the whole point.
            Clipboard.SetDataObject(data, copy: true);
        }
        catch (Exception)
        {
            return false;   // Another program had the clipboard open. Nothing was copied.
        }

        _ours = value;
        Watch.Stop();
        Watch.Interval = ClearAfter;
        Watch.Start();
        return true;
    }

    /// <summary>
    /// Wipe it, but only if it is still ours. If the user has copied something else since,
    /// leaving their clipboard alone matters more than tidying up after ourselves.
    /// </summary>
    public static void ClearNow()
    {
        Watch.Stop();
        if (_ours is null) return;

        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText() == _ours) Clipboard.Clear();
            _ours = null;
        }
        catch (Exception)
        {
            // The clipboard was busy. Try again on the next tick rather than giving up and
            // leaving the password sitting there for good.
            Watch.Interval = TimeSpan.FromSeconds(2);
            Watch.Start();
        }
    }
}
