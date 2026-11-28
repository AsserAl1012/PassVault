using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PassVault.Core.Machine;
using PassVault.Core.Usb;

namespace PassVault.Tests;

/// <summary>
/// swtpm, a software TPM, for the Linux chip tests. Unlike the real chip it can be given wrong
/// PINs, locked out and cleared as often as a test likes. Only on Linux, and only where swtpm and
/// tpm2-abrmd are installed - elsewhere these tests have nothing to run against and pass without
/// checking.
///
/// It sits behind tpm2-abrmd, a resource manager, because a real PC's /dev/tpmrm0 has one: it
/// tidies up after every command. Talked to directly, swtpm ran out of room after three.
/// </summary>
public sealed class SoftwareTpm : IDisposable
{
    private readonly List<Process> _running = [];
    private readonly string? _busBefore = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
    private readonly string _state = Path.Combine(Path.GetTempPath(), "pv-swtpm-" + Guid.NewGuid().ToString("N")[..8]);

    public bool Running => _running.Count == 3 && _running.All(p => !p.HasExited);

    /// <summary>What the programs that stopped said, for the test that checks this ran.</summary>
    public string WhyNot => string.Join(" | ", _running.Where(p => p.HasExited)
        .Select(p => $"{p.StartInfo.FileName} exited {p.ExitCode}: {p.StandardError.ReadToEnd().Trim()}"));

    public SoftwareTpm()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/swtpm") || !File.Exists("/usr/sbin/tpm2-abrmd")) return;

        Directory.CreateDirectory(_state);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Start("swtpm", $"socket --tpm2 --server type=tcp,port={port} --ctrl type=tcp,port={port + 1} " +
                       $"--tpmstate dir={_state} --flags not-need-init,startup-clear");

        // The resource manager finds its clients over a D-Bus session bus of its own.
        var bus = Start("dbus-daemon", "--session --nofork --print-address=1");
        Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", bus.StandardOutput.ReadLine());
        Start("/usr/sbin/tpm2-abrmd", $"--session --flush-all --tcti=swtpm:host=127.0.0.1,port={port}");

        // LinuxChip's tools - and so the app - send their commands wherever this says.
        Environment.SetEnvironmentVariable("TPM2TOOLS_TCTI", "tabrmd:bus_type=session");
        for (int i = 0; i < 50 && Tool("tpm2_getrandom", "4").Exit != 0; i++) Thread.Sleep(100);
        WindowsLockout();
    }

    private Process Start(string program, string args)
    {
        var process = Process.Start(new ProcessStartInfo(program, args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        _running.Add(process);
        return process;
    }

    /// <summary>The lockout a Windows PC sets: 32 wrong PINs, then one forgiven every two hours.</summary>
    public void WindowsLockout() => Tool("tpm2_dictionarylockout", "-s", "-n", "32", "-t", "7200", "-l", "86400");

    /// <summary>Forget every wrong PIN so far.</summary>
    public void Forgive() => Tool("tpm2_dictionarylockout", "-c");

    public int WrongPinsCounted()
    {
        var said = Tool("tpm2_getcap", "properties-variable").Output;
        return Convert.ToInt32(Regex.Match(said, @"TPM2_PT_LOCKOUT_COUNTER:\s*0x([0-9A-Fa-f]+)").Groups[1].Value, 16);
    }

    public (int Exit, string Output) Tool(string tool, params string[] args)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TPM2TOOLS_TCTI", null);
        Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", _busBefore);
        foreach (var process in Enumerable.Reverse(_running))
        {
            if (!process.HasExited) process.Kill();
            process.Dispose();
        }
        if (Directory.Exists(_state)) Directory.Delete(_state, recursive: true);
    }
}

/// <summary>
/// The chip on Linux, for real, through the same tools a user's PC runs - only the chip is a
/// software one. In the "chip" collection so nothing else runs while the tools point at it.
/// </summary>
[Collection("chip")]
[UnsupportedOSPlatform("windows")]
public class LinuxChipTests(SoftwareTpm tpm) : IClassFixture<SoftwareTpm>
{
    private const string Pin = "482917";

    /// <summary>
    /// The tests below pass without checking anything when there is no software TPM. Where one is
    /// installed and it did not start, that must be a failure - not 5 green ticks for nothing.
    /// </summary>
    [Fact]
    public void Where_the_software_TPM_is_installed_it_runs()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/swtpm") || !File.Exists("/usr/sbin/tpm2-abrmd")) return;
        Assert.True(tpm.Running, "swtpm and tpm2-abrmd are installed, but the software TPM did not start: " + tpm.WhyNot);
    }

    [Fact]
    public void The_chip_seals_the_secret_and_gives_it_back_for_the_right_PIN()
    {
        if (!tpm.Running) return;

        var made = DeviceSecret.Create(Pin);

        Assert.Equal(DeviceProtection.Tpm, made.Protection);
        Assert.Equal(LinuxChip.Kind, made.Sealed[4]);   // "PVDS" then the kind
        Assert.DoesNotContain(Convert.ToHexString(made.Secret), Convert.ToHexString(made.Sealed));
        Assert.Equal(made.Secret, DeviceSecret.Open(made.Sealed, Pin).Secret);
    }

    [Fact]
    public void A_wrong_PIN_is_refused_and_the_chip_counts_it()
    {
        if (!tpm.Running) return;
        var made = DeviceSecret.Create(Pin);
        int before = tpm.WrongPinsCounted();

        var refused = Assert.Throws<PinRefusedException>(() => DeviceSecret.Open(made.Sealed, "000000"));

        Assert.False(refused.LockedOut);
        Assert.Equal(before + 1, tpm.WrongPinsCounted());
        tpm.Forgive();
    }

    /// <summary>
    /// The case that could never be tried on a real PC: locking the chip locks Windows Hello too.
    /// While it rests even the right PIN is turned away - and it must read as "not now", which the
    /// unlock gives back, never as a wrong PIN.
    /// </summary>
    [Fact]
    public void A_resting_chip_says_not_now_even_to_the_right_PIN()
    {
        if (!tpm.Running) return;
        var made = DeviceSecret.Create(Pin);
        tpm.Tool("tpm2_dictionarylockout", "-s", "-n", "2", "-t", "7200", "-l", "86400");

        try
        {
            for (int i = 0; i < 2; i++)
                Assert.False(Assert.Throws<PinRefusedException>(() => DeviceSecret.Open(made.Sealed, "000000")).LockedOut);

            Assert.True(Assert.Throws<PinRefusedException>(() => DeviceSecret.Open(made.Sealed, Pin)).LockedOut);
        }
        finally
        {
            tpm.Forgive();
            tpm.WindowsLockout();
        }
    }

    /// <summary>A cleared chip has a new seed, so nothing it sealed before loads. That is "set this PC up again", not a wrong PIN.</summary>
    [Fact]
    public void A_cleared_chip_means_setting_up_again_not_a_wrong_PIN()
    {
        if (!tpm.Running) return;
        var made = DeviceSecret.Create(Pin);

        tpm.Tool("tpm2_clear");
        tpm.WindowsLockout();

        Assert.Throws<CryptographicException>(() => DeviceSecret.Open(made.Sealed, Pin));
    }

    /// <summary>
    /// The parent key is made before the PIN is checked. A failure there is only ever "not now" -
    /// it used to escape as a raw error and keep the PIN try it had been counted.
    /// </summary>
    [Fact]
    public void A_chip_that_cannot_make_the_parent_key_says_not_now()
    {
        if (!tpm.Running) return;
        var made = DeviceSecret.Create(Pin);
        var real = Environment.GetEnvironmentVariable("TPM2TOOLS_TCTI");
        Environment.SetEnvironmentVariable("TPM2TOOLS_TCTI", "swtpm:host=127.0.0.1,port=1");   // nothing answers there

        try
        {
            Assert.True(Assert.Throws<PinRefusedException>(() => DeviceSecret.Open(made.Sealed, Pin)).LockedOut);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TPM2TOOLS_TCTI", real);
        }
    }

    /// <summary>
    /// With no lockout password, anyone who can reach the chip can wipe its count of wrong PINs - usual
    /// on a PC that only ever ran Linux. Said to the user, never hidden; and not said when it is set.
    /// </summary>
    [Fact]
    public void A_chip_without_a_lockout_password_is_reported()
    {
        if (!tpm.Running) return;
        Assert.True(LinuxChip.LockoutCanBeReset());   // a fresh software TPM has none
        Assert.Contains("lockout password", Unlocker.Weakness(DeviceProtection.Tpm));

        tpm.Tool("tpm2_changeauth", "-c", "lockout", "a-lockout-password");
        try
        {
            Assert.False(LinuxChip.LockoutCanBeReset());
            Assert.Null(Unlocker.Weakness(DeviceProtection.Tpm));
        }
        finally
        {
            tpm.Tool("tpm2_changeauth", "-c", "lockout", "-p", "a-lockout-password");   // none again, for the other tests
        }
    }

    /// <summary>The tools' working files hold the PIN for a moment. They must all be gone afterwards.</summary>
    [Fact]
    public void Nothing_is_left_in_the_working_folder()
    {
        if (!tpm.Running) return;
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { } r && Directory.Exists(r) ? r : Path.GetTempPath();
        int Leftovers() => Directory.GetDirectories(runtime, "passvault-*").Length;
        int before = Leftovers();

        var made = DeviceSecret.Create(Pin);
        DeviceSecret.Open(made.Sealed, Pin);
        Assert.Throws<PinRefusedException>(() => DeviceSecret.Open(made.Sealed, "000000"));
        tpm.Forgive();

        Assert.Equal(before, Leftovers());
    }
}

/// <summary>Where PassVault keeps things on Linux, and how it finds a stick there.</summary>
[Collection("chip")]
[UnsupportedOSPlatform("windows")]
public class LinuxPlaceTests
{
    [Fact]
    public void This_PCs_folder_is_where_Linux_keeps_program_data()
    {
        if (!OperatingSystem.IsLinux()) return;
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
            ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        Assert.Equal(Path.Combine(data, "passvault"), MachineStore.DefaultFolder);
    }

    /// <summary>
    /// On a fresh Linux account ~/.local/share and ~/.config may not exist yet, and .NET answered ""
    /// for them - so this PC's files went into whatever folder PassVault was started from.
    /// </summary>
    [Fact]
    public void Its_folders_are_always_full_paths_even_before_they_exist()
    {
        var home = Path.Combine(Path.GetTempPath(), "pv-freshhome-" + Guid.NewGuid().ToString("N")[..8]);
        var before = (Environment.GetEnvironmentVariable("HOME"), Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
                      Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"));
        Environment.SetEnvironmentVariable("HOME", home);   // a home with nothing in it at all
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", null);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);

        try
        {
            Assert.True(Path.IsPathRooted(MachineStore.DefaultFolder), MachineStore.DefaultFolder);
            Assert.True(Path.IsPathRooted(PassVault.Core.Config.Settings.DefaultPath), PassVault.Core.Config.Settings.DefaultPath);
            if (OperatingSystem.IsLinux()) Assert.StartsWith(home, MachineStore.DefaultFolder);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", before.Item1);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", before.Item2);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", before.Item3);
        }
    }

    /// <summary>It holds the PIN slot and the spare vault. Linux makes new folders readable by everyone unless told otherwise.</summary>
    [Fact]
    public void The_folder_is_readable_by_you_alone_even_if_it_already_existed()
    {
        if (!OperatingSystem.IsLinux()) return;
        var folder = Path.Combine(Path.GetTempPath(), "pv-private-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder, (UnixFileMode)0b111_101_101);   // rwxr-xr-x, as Linux would make it

        try
        {
            MachineStore.CreatePrivateFolder(folder);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
        }
        finally
        {
            Directory.Delete(folder);
        }
    }

    [Theory]
    [InlineData("/media/{user}/MSI DISK", true)]
    [InlineData("/run/media/{user}/STICK", true)]
    [InlineData("/media/someone-else/STICK", false)]
    [InlineData("/mnt/c", false)]
    [InlineData("/", false)]
    public void A_stick_is_what_the_desktop_mounted_for_you(string root, bool isStick)
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.Equal(isStick, UsbDetect.IsStick(root.Replace("{user}", Environment.UserName), DriveType.Fixed));
    }

    [Fact]
    public void With_no_chip_the_secret_is_kept_in_a_file_only_you_can_read_and_says_so()
    {
        if (!OperatingSystem.IsLinux() || LinuxChip.HasChip) return;

        var made = DeviceSecret.Create("4821");

        Assert.Equal(DeviceProtection.UserFile, made.Protection);
        Assert.Equal(made.Secret, DeviceSecret.Open(made.Sealed, "4821").Secret);
    }
}
