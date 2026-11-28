using System.Security.Cryptography;
using PassVault.Core.Config;
using PassVault.Core.Machine;
using PassVault.Core.Model;
using PassVault.Core.Usb;
using PassVault.Core.Vault;

namespace PassVault.Tests;

/// <summary>The way in, as every new front end now decides it - and the fixes from the Linux review.</summary>
public class UnlockerTests : IDisposable
{
    private readonly string _stick = NewFolder("stick");
    private readonly string _pc = NewFolder("pc");
    private readonly byte[] _deviceSecret = RandomNumberGenerator.GetBytes(32);
    private readonly string _phrase;

    public UnlockerTests() => (_phrase, _) = VaultStorage.Initialise(_stick);

    private static string NewFolder(string tag)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pv-{tag}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var folder in new[] { _stick, _pc })
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static UsbStick Stick(string root) => new(root, Path.GetFileName(root), 0);

    private byte[] SetUp(string pin = "4821")
        => Enrolment.Enrol(_pc, _stick, _phrase, pin, _deviceSecret, DeviceProtection.Tpm).MasterKey;

    [Fact]
    public void It_says_what_to_offer_for_each_state()
    {
        var empty = NewFolder("empty");
        try
        {
            Assert.Equal(Situation.NoStick, Unlocker.Look(_pc, []).Situation);
            Assert.Equal(Situation.NewStick, Unlocker.Look(_pc, [Stick(empty)]).Situation);
            Assert.Equal(Situation.SetUpThisPc, Unlocker.Look(_pc, [Stick(_stick)]).Situation);

            SetUp("48291735");
            var here = Unlocker.Look(_pc, [Stick(_stick)]);
            Assert.Equal((Situation.Pin, 8), (here.Situation, here.PinLength));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    /// <summary>Unnoticed, every PIN try said "set this machine up again" and nothing offered set-up.</summary>
    [Fact]
    public void A_stick_re_keyed_on_another_PC_is_noticed()
    {
        SetUp();
        var machine = MachineStore.Read(_pc);
        machine.EpochAtEnrolment = 0;   // as though the stick had been re-keyed after this PC was set up
        MachineStore.Write(_pc, machine);

        Assert.Equal(Situation.Rekeyed, Unlocker.Look(_pc, [Stick(_stick)]).Situation);
    }

    [Fact]
    public void A_second_stick_with_a_vault_is_named()
    {
        SetUp();
        var second = NewFolder("stick2");
        try
        {
            VaultStorage.Initialise(second);
            var here = Unlocker.Look(_pc, [Stick(_stick), Stick(second)]);
            Assert.Equal((_stick, second), (here.Stick!.Root, here.OtherStick!.Root));
        }
        finally
        {
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public void A_damaged_setup_file_and_a_switched_off_PIN_each_say_so()
    {
        SetUp();
        var machine = MachineStore.Read(_pc);
        machine.PinDisabled = true;
        MachineStore.Write(_pc, machine);
        Assert.Equal(Situation.PinOff, Unlocker.Look(_pc, [Stick(_stick)]).Situation);

        foreach (var copy in Directory.GetFiles(_pc, MachineStore.FileName + "*")) File.WriteAllText(copy, "{ half written");
        Assert.Equal(Situation.SetupDamaged, Unlocker.Look(_pc, [Stick(_stick)]).Situation);
    }

    /// <summary>Only the chip's own step failing means "set this PC up again" - not a stick problem.</summary>
    [Fact]
    public void Only_a_secret_that_will_not_open_counts_as_the_chip_lost()
    {
        Enrolment.Enrol(_pc, _stick, _phrase, "4821", _deviceSecret, DeviceProtection.Tpm, sealedDevice: [.. "PVDS"u8, 9]);
        Assert.Throws<ChipLostException>(() => Unlocker.WithPin(_pc, _stick, "4821"));

        var notTheStick = NewFolder("other");
        try
        {
            VaultStorage.Initialise(notTheStick);
            var ex = Assert.Throws<UnlockException>(() => Unlocker.WithPin(_pc, notTheStick, "4821"));
            Assert.Contains("not your stick", ex.Message);
        }
        finally
        {
            Directory.Delete(notTheStick, recursive: true);
        }
    }

    /// <summary>Tools missing, or no permission: the PIN never reached the chip. Kept, ten of these switched the PIN off.</summary>
    [Fact]
    public void A_chip_that_could_not_be_reached_gives_the_try_back()
    {
        SetUp();

        Assert.Throws<ChipUnavailableException>(() => Enrolment.UnlockWithPin(_pc, _stick, "4821",
            _ => throw new ChipUnavailableException("PassVault needs tpm2-tools")));

        Assert.Equal(0, MachineStore.Read(_pc).FailedAttempts);
    }

    /// <summary>Asked every two seconds, it finished another app's save halfway - and that app then reported a save the stick held as failed.</summary>
    [Fact]
    public void Looking_never_finishes_a_save_another_app_is_in_the_middle_of()
    {
        SetUp();
        var path = VaultStorage.VaultPathOn(_stick);
        File.Copy(path, path + VaultStorage.TempSuffix);                       // the new copy is written
        File.Move(path, path + VaultStorage.BackupSuffix, overwrite: true);   // the old one moved aside; the last move is the other app's

        Assert.Equal(Situation.Pin, Unlocker.Look(_pc, [Stick(_stick)]).Situation);
        Assert.True(File.Exists(path + VaultStorage.TempSuffix));
        Assert.False(File.Exists(path));
    }

    /// <summary>Forgotten from a terminal just before the PIN's last digit: reading it took the window app down.</summary>
    [Fact]
    public void A_setup_file_that_cannot_be_read_means_look_again_not_a_crash()
    {
        Assert.Null(Unlocker.WaitBeforeTry(_pc));
        Assert.False(Unlocker.PinSwitchedOff(_pc));

        SetUp();
        Assert.Equal(TimeSpan.Zero, Unlocker.WaitBeforeTry(_pc));
    }

    /// <summary>A damaged header or an unreadable machine.pvm escaped OpenVault as raw errors and took the terminal app down.</summary>
    [Fact]
    public void Every_save_failure_comes_back_as_a_sentence()
    {
        var vault = new OpenVault(_pc, _stick, SetUp());
        vault.Add(new Entry { Name = "github.com" });

        foreach (var copy in Directory.GetFiles(_pc, MachineStore.FileName + "*")) File.WriteAllText(copy, "{ half written");
        Assert.Throws<SaveRefusedException>(() => vault.Add(new Entry { Name = "second" }));

        File.WriteAllText(VaultStorage.VaultPathOn(_stick), "{ not a vault");
        Assert.Throws<SaveRefusedException>(() => vault.Add(new Entry { Name = "third" }));
        Assert.Equal(["github.com"], vault.Living.Select(e => e.Name));
    }
}

public class SettingsReviewTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"pv-config-{Guid.NewGuid().ToString("N")[..8]}.toml");

    public void Dispose()
    {
        if (File.Exists(_file)) File.Delete(_file);
        GC.SuppressFinalize(this);
    }

    private Settings Load(string text)
    {
        File.WriteAllText(_file, text);
        return Settings.Load(_file);
    }

    /// <summary>"D:\Sticks" was read as "D:Sticks" - a different path - and nothing said so.</summary>
    [Fact]
    public void A_backslash_that_is_no_escape_is_pointed_out_and_single_quotes_keep_it()
    {
        var bad = Load("[sticks]\nextra_places = [\"D:\\Sticks\"]\n");
        Assert.Empty(bad.ExtraStickPlaces);
        Assert.Contains(bad.Problems, p => p.Contains("single quotes"));

        var good = Load("[sticks]\nextra_places = ['D:\\Sticks', \"/mnt/a \\\"b\\\"\"]\n");
        Assert.Empty(good.Problems);
        Assert.Equal(["D:\\Sticks", "/mnt/a \"b\""], good.ExtraStickPlaces);
    }

    [Fact]
    public void A_setting_written_twice_is_pointed_out()
    {
        var s = Load("lock.after_minutes = 1\n[lock]\nafter_minutes = 5\n");

        Assert.Equal(5, s.LockAfterMinutes);
        Assert.Contains(s.Problems, p => p.Contains("set twice") && p.Contains("line 3") && p.Contains("line 1"));
    }
}
