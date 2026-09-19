namespace PassVault.Core.Usb;

/// <summary>A removable drive we can actually read and write.</summary>
public sealed record UsbStick(string Root, string Label, long FreeBytes)
{
    /// <summary>True if this stick already has a PassVault keyfile and vault on it.</summary>
    public bool IsSetUp => VaultStorage.IsSetUp(Root);

    public override string ToString()
    {
        var label = string.IsNullOrWhiteSpace(Label) ? "no label" : Label;
        return $"{Root}  ({label}, {FreeBytes / 1024 / 1024 / 1024.0:0.0} GB free)";
    }
}

public static class UsbDetect
{
    /// <summary>
    /// Every removable drive Windows can read. A stick whose filesystem Windows cannot read -
    /// a Linux drive, say - has no drive letter and will not show up here at all.
    /// </summary>
    public static List<UsbStick> Find()
    {
        var sticks = new List<UsbStick>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Removable || !drive.IsReady) continue;
                sticks.Add(new UsbStick(drive.RootDirectory.FullName, drive.VolumeLabel, drive.AvailableFreeSpace));
            }
            catch (IOException)
            {
                // A drive that vanished or went unreadable between listing and asking. Skip it.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return sticks;
    }

    /// <summary>The plugged-in stick that has a vault on it, if there is exactly one.</summary>
    public static UsbStick? FindTheSetUpOne()
    {
        var withVault = Find().Where(s => s.IsSetUp).ToList();
        return withVault.Count == 1 ? withVault[0] : null;
    }
}
