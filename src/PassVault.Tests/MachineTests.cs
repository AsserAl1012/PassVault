using System.Runtime.Versioning;
using System.Security.Cryptography;
using PassVault.Core.Machine;
using PassVault.Core.Model;
using PassVault.Core.Usb;
using PassVault.Core.Vault;

namespace PassVault.Tests;

/// <summary>
/// A temp folder stands in for the stick, another for this PC's local app data, and random
/// bytes stand in for the machine secret. The real TPM is exercised separately below.
/// </summary>
public class MachineTests : IDisposable
{
    private readonly string _stick = NewFolder("stick");
    private readonly string _pc = NewFolder("pc");
    private readonly byte[] _deviceSecret = RandomNumberGenerator.GetBytes(32);
    private readonly string _phrase;

    private const string Pin = "4821";

    public MachineTests()
    {
        (_phrase, _) = VaultStorage.Initialise(_stick);
    }

    public void Dispose()
    {
        foreach (var folder in new[] { _stick, _pc })
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string NewFolder(string tag)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pv-{tag}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(path);
        return path;
    }

    private MachineFile Enrol() =>
        Enrolment.Enrol(_pc, _stick, _phrase, Pin, _deviceSecret, DeviceProtection.Tpm).Machine;

    // ---- setting a machine up ----

    [Fact]
    public void Enrolling_writes_the_machine_file_and_a_spare_vault()
    {
        var machine = Enrol();

        Assert.True(MachineStore.IsEnrolled(_pc));
        Assert.True(MachineStore.HasMirrorVault(_pc));
        Assert.Equal(KeyWrapNames.Pin, machine.PinSlot.Name);
        Assert.NotEmpty(machine.MachineId);
    }

    [Fact]
    public void The_spare_vault_on_the_pc_opens_with_the_phrase()
    {
        Enrol();

        // This is the whole point: a dead stick must not mean losing everything.
        var spare = MachineStore.ReadMirrorVault(_pc);
        Assert.NotNull(VaultFile.UnlockWithPhrase(spare, _phrase));
    }

    [Fact]
    public void Enrolling_with_the_wrong_phrase_is_refused()
    {
        var wrong = PassVault.Core.Crypto.Mnemonic.Generate();

        var error = Assert.Throws<UnlockException>(
            () => Enrolment.Enrol(_pc, _stick, wrong, Pin, _deviceSecret, DeviceProtection.Tpm));

        Assert.Contains("do not open this vault", error.Message);
        Assert.False(MachineStore.IsEnrolled(_pc));
    }

    [Fact]
    public void A_typo_in_the_phrase_says_which_word()
    {
        var words = _phrase.Split(' ');
        words[4] = "nonsenseword";

        var error = Assert.Throws<UnlockException>(
            () => Enrolment.Enrol(_pc, _stick, string.Join(' ', words), Pin, _deviceSecret, DeviceProtection.Tpm));

        Assert.Contains("Word 5", error.Message);
    }

    // ---- the daily unlock ----

    [Fact]
    public void Stick_plus_machine_plus_pin_opens_the_vault()
    {
        Enrol();

        var (masterKey, _) = Enrolment.UnlockWithPin(_pc, _stick, Pin, _deviceSecret);

        Assert.Equal(32, masterKey.Length);
        Assert.Equal(masterKey, VaultFile.UnlockWithPhrase(VaultStorage.ReadVault(_stick), _phrase));
    }

    [Fact]
    public void A_different_machine_secret_does_not_open_it()
    {
        Enrol();

        Assert.Throws<UnlockException>(
            () => Enrolment.UnlockWithPin(_pc, _stick, Pin, RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public void A_different_stick_does_not_open_it()
    {
        Enrol();

        // Another stick, set up properly, but holding a different vault.
        var otherStick = NewFolder("other");
        try
        {
            VaultStorage.Initialise(otherStick);

            var error = Assert.Throws<UnlockException>(
                () => Enrolment.UnlockWithPin(_pc, otherStick, Pin, _deviceSecret));

            Assert.Contains("not your stick", error.Message);
        }
        finally
        {
            Directory.Delete(otherStick, recursive: true);
        }
    }

    [Fact]
    public void An_unplugged_stick_says_so_in_plain_words()
    {
        Enrol();

        var error = Assert.Throws<UnlockException>(
            () => Enrolment.UnlockWithPin(_pc, "Z:\\", Pin, _deviceSecret));

        Assert.Contains("not plugged in", error.Message);
        Assert.DoesNotContain("keyfile", error.Message);
    }

    [Fact]
    public void A_drive_with_no_vault_on_it_says_so_too()
    {
        Enrol();
        var empty = NewFolder("empty");
        try
        {
            var error = Assert.Throws<UnlockException>(
                () => Enrolment.UnlockWithPin(_pc, empty, Pin, _deviceSecret));

            Assert.Contains("no vault there", error.Message);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public void An_unenrolled_machine_says_to_use_the_phrase()
    {
        var error = Assert.Throws<UnlockException>(() => Enrolment.UnlockWithPin(_pc, _stick, Pin, _deviceSecret));

        Assert.Contains("not set up", error.Message);
    }

    // ---- getting the PIN wrong ----

    [Fact]
    public void A_wrong_pin_says_how_many_tries_are_left()
    {
        Enrol();

        var error = Assert.Throws<UnlockException>(() => Enrolment.UnlockWithPin(_pc, _stick, "0000", _deviceSecret));

        Assert.Contains("Wrong PIN", error.Message);
        Assert.Contains("9 tries left", error.Message);
        Assert.Equal(1, MachineStore.Read(_pc).FailedAttempts);
    }

    [Fact]
    public void The_count_is_written_down_before_the_pin_is_checked()
    {
        // Otherwise yanking the power on a wrong guess would rewind the counter.
        Enrol();
        Assert.Throws<UnlockException>(() => Enrolment.UnlockWithPin(_pc, _stick, "0000", _deviceSecret));
        Assert.Throws<UnlockException>(() => Enrolment.UnlockWithPin(_pc, _stick, "1111", _deviceSecret));

        Assert.Equal(2, MachineStore.Read(_pc).FailedAttempts);
    }

    [Fact]
    public void The_right_pin_clears_the_count()
    {
        Enrol();
        Assert.Throws<UnlockException>(() => Enrolment.UnlockWithPin(_pc, _stick, "0000", _deviceSecret));

        Enrolment.UnlockWithPin(_pc, _stick, Pin, _deviceSecret);

        Assert.Equal(0, MachineStore.Read(_pc).FailedAttempts);
    }

    [Fact]
    public void The_pin_switches_itself_off_after_ten_wrong_tries()
    {
        Enrol();

        // Wind the counter to one short, rather than burning ten real attempts.
        var machine = MachineStore.Read(_pc);
        machine.FailedAttempts = Enrolment.MaxAttempts - 1;
        MachineStore.Write(_pc, machine);

        var error = Assert.Throws<UnlockException>(() => Enrolment.UnlockWithPin(_pc, _stick, "0000", _deviceSecret));
        Assert.Contains("switched off", error.Message);
        Assert.True(MachineStore.Read(_pc).PinDisabled);

        // Even the right PIN is no good now.
        Assert.Throws<UnlockException>(() => Enrolment.UnlockWithPin(_pc, _stick, Pin, _deviceSecret));
    }

    [Fact]
    public void The_phrase_switches_the_pin_back_on()
    {
        Enrol();
        var machine = MachineStore.Read(_pc);
        machine.FailedAttempts = Enrolment.MaxAttempts;
        machine.PinDisabled = true;
        MachineStore.Write(_pc, machine);

        Enrolment.UnlockWithPhrase(_pc, _stick, _phrase);

        Assert.False(MachineStore.Read(_pc).PinDisabled);
        Assert.Equal(0, MachineStore.Read(_pc).FailedAttempts);
        Assert.NotNull(Enrolment.UnlockWithPin(_pc, _stick, Pin, _deviceSecret).MasterKey);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(5, 4)]
    [InlineData(7, 16)]
    [InlineData(9, 30)]
    [InlineData(50, 30)]
    public void The_wait_between_tries_grows_then_stops_growing(int failed, int expectedSeconds)
    {
        Assert.Equal(expectedSeconds, Enrolment.DelayBeforeNextTry(failed).TotalSeconds);
    }

    // ---- putting an old vault back ----

    [Fact]
    public void Saving_and_then_putting_the_old_copy_straight_back_is_refused()
    {
        // The real shape of the attack: you unlock once, save some changes, and someone
        // swaps the old file back while you are still logged in. There is no second unlock
        // in between, so saving MUST be what records how new the vault is.
        Enrol();
        var original = VaultStorage.ReadVault(_stick);
        var masterKey = Enrolment.UnlockWithPin(_pc, _stick, Pin, _deviceSecret).MasterKey;

        Enrolment.SaveVault(_pc, _stick, VaultFile.Save(original, masterKey, [new Entry { Name = "bank.com", Password = "rotated" }]));

        VaultStorage.WriteVault(_stick, original);

        var error = Assert.Throws<UnlockException>(() => Enrolment.UnlockWithPin(_pc, _stick, Pin, _deviceSecret));
        Assert.Contains("older than", error.Message);
    }

    [Fact]
    public void Saving_records_how_new_the_vault_is()
    {
        Enrol();
        var masterKey = Enrolment.UnlockWithPin(_pc, _stick, Pin, _deviceSecret).MasterKey;
        var before = MachineStore.Read(_pc).HighestRevisionSeen;

        Enrolment.SaveVault(_pc, _stick, VaultFile.Save(VaultStorage.ReadVault(_stick), masterKey, [new Entry { Name = "a.com" }]));

        Assert.Equal(before + 1, MachineStore.Read(_pc).HighestRevisionSeen);
    }

    [Fact]
    public void The_note_of_how_new_the_vault_is_never_moves_backwards()
    {
        Enrol();
        var machine = MachineStore.Read(_pc);
        machine.HighestEpochSeen = 3;
        machine.HighestRevisionSeen = 40;
        MachineStore.Write(_pc, machine);

        Enrolment.NoteHowNew(_pc, new VaultHeader { Epoch = 2, Revision = 9 });

        Assert.Equal(3, MachineStore.Read(_pc).HighestEpochSeen);
        Assert.Equal(40, MachineStore.Read(_pc).HighestRevisionSeen);
    }

    [Fact]
    public void An_older_vault_on_the_stick_is_refused()
    {
        Enrol();
        var original = VaultStorage.ReadVault(_stick);
        var masterKey = Enrolment.UnlockWithPin(_pc, _stick, Pin, _deviceSecret).MasterKey;

        // Save twice so the machine has seen a higher revision...
        VaultStorage.WriteVault(_stick, VaultFile.Save(original, masterKey, [new Entry { Name = "bank.com", Password = "new-one" }]));
        Enrolment.UnlockWithPin(_pc, _stick, Pin, _deviceSecret);

        // ...then someone puts the old copy back.
        VaultStorage.WriteVault(_stick, original);

        var error = Assert.Throws<UnlockException>(() => Enrolment.UnlockWithPin(_pc, _stick, Pin, _deviceSecret));
        Assert.Contains("older than", error.Message);
    }

    [Fact]
    public void An_older_vault_never_overwrites_the_good_spare_copy()
    {
        Enrol();
        var original = VaultStorage.ReadVault(_stick);
        var masterKey = Enrolment.UnlockWithPin(_pc, _stick, Pin, _deviceSecret).MasterKey;
        VaultStorage.WriteVault(_stick, VaultFile.Save(original, masterKey, [new Entry { Name = "bank.com" }]));
        Enrolment.UnlockWithPin(_pc, _stick, Pin, _deviceSecret);

        VaultStorage.WriteVault(_stick, original);
        Enrolment.UnlockWithPhrase(_pc, _stick, _phrase);

        // The spare must still be the newer one, not the old copy just plugged in.
        var spare = MachineStore.ReadMirrorVault(_pc);
        Assert.Equal(2, VaultFile.ReadHeader(spare).Revision);
    }

    // ---- after a re-key ----

    [Fact]
    public void A_rekeyed_stick_tells_you_to_set_the_machine_up_again()
    {
        Enrol();
        var masterKey = Enrolment.UnlockWithPin(_pc, _stick, Pin, _deviceSecret).MasterKey;

        var (fresh, _, _) = VaultFile.ReKey(VaultStorage.ReadVault(_stick), masterKey);
        VaultStorage.WriteVault(_stick, fresh);

        var error = Assert.Throws<UnlockException>(() => Enrolment.UnlockWithPin(_pc, _stick, Pin, _deviceSecret));
        Assert.Contains("set this machine up again", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the machine file itself ----

    [Fact]
    public void The_machine_file_never_holds_the_usb_secret_or_the_pin()
    {
        Enrol();

        var text = File.ReadAllText(MachineStore.PathIn(_pc));
        var keyfile = VaultStorage.ReadKeyfile(_stick);

        Assert.DoesNotContain(Pin, text);
        Assert.DoesNotContain(Convert.ToBase64String(keyfile.UsbSecret), text);
        Assert.DoesNotContain(Convert.ToBase64String(_deviceSecret), text);
    }

    [Fact]
    public void Forgetting_a_machine_leaves_the_vault_alone()
    {
        Enrol();
        MachineStore.Forget(_pc);

        Assert.False(MachineStore.IsEnrolled(_pc));
        Assert.NotNull(Enrolment.UnlockWithPhrase(_pc, _stick, _phrase));
    }
}

internal static class KeyWrapNames
{
    public const string Pin = "pin";
}

/// <summary>The real TPM on this machine. Uses its own key name so it cannot disturb a real setup.</summary>
[SupportedOSPlatform("windows")]
public class DeviceSecretTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pv-dev-" + Guid.NewGuid().ToString("N")[..8]);
    private const string TestKeyName = "PassVault.Test.DeviceKey";

    public void Dispose()
    {
        DeviceSecret.Forget(_folder, TestKeyName);
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_same_secret_comes_back_every_time()
    {
        var (first, protection) = DeviceSecret.Load(_folder, TestKeyName);
        var (second, again) = DeviceSecret.Load(_folder, TestKeyName);

        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);
        Assert.Equal(protection, again);
    }

    [Fact]
    public void The_secret_is_not_sitting_in_the_file_in_the_clear()
    {
        var (secret, _) = DeviceSecret.Load(_folder, TestKeyName);
        var onDisk = File.ReadAllBytes(Path.Combine(_folder, DeviceSecret.FileName));

        Assert.DoesNotContain(Convert.ToHexString(secret), Convert.ToHexString(onDisk));
    }

    [Fact]
    public void Forgetting_it_means_a_new_one_next_time()
    {
        var (first, _) = DeviceSecret.Load(_folder, TestKeyName);
        DeviceSecret.Forget(_folder, TestKeyName);
        var (second, _) = DeviceSecret.Load(_folder, TestKeyName);

        Assert.NotEqual(first, second);
    }
}
