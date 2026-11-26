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
                if (!IsStick(drive.RootDirectory.FullName, drive.DriveType) || !drive.IsReady) continue;
                var label = OperatingSystem.IsWindows() ? drive.VolumeLabel : Path.GetFileName(drive.Name.TrimEnd('/'));
                sticks.Add(new UsbStick(drive.RootDirectory.FullName, label, drive.AvailableFreeSpace));
            }
            catch (IOException)
            {
                // A drive that vanished or went unreadable between listing and asking. Skip it.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        foreach (var place in ExtraPlaces)
            if (Directory.Exists(place) && !sticks.Any(s => s.Root == place))
                sticks.Add(new UsbStick(place, Path.GetFileName(place.TrimEnd('/', '\\')), new DriveInfo(place).AvailableFreeSpace));

        return sticks;
    }

    /// <summary>
    /// Folders to treat as a stick as well: a stick mounted somewhere unusual, or a test folder.
    /// Set by PASSVAULT_STICKS - folders separated by ':' on Linux, ';' on Windows.
    /// </summary>
    public static List<string> ExtraPlaces { get; set; } =
        (Environment.GetEnvironmentVariable("PASSVAULT_STICKS") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>
    /// Windows says which drives are removable. Linux does not, reliably - but desktops mount USB
    /// sticks under /media/you (Ubuntu, Debian) or /run/media/you (Fedora, Arch), and that is what
    /// their file managers show as removable, so that is what counts here.
    /// </summary>
    public static bool IsStick(string root, DriveType type)
    {
        if (OperatingSystem.IsWindows()) return type == DriveType.Removable;

        var user = Environment.UserName;
        return root.StartsWith($"/media/{user}/") || root.StartsWith($"/run/media/{user}/");
    }

    /// <summary>The plugged-in stick that has a vault on it, if there is exactly one.</summary>
    public static UsbStick? FindTheSetUpOne()
    {
        var withVault = Find().Where(s => s.IsSetUp).ToList();
        return withVault.Count == 1 ? withVault[0] : null;
    }
}
