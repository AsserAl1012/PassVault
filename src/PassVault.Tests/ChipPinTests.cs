using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PassVault.Core.Machine;
using PassVault.Core.Usb;

namespace PassVault.Tests;

/// <summary>
/// What the app does around the chip checking the PIN: longer PINs, a refusal from the chip,
/// and setting up without ever breaking a working set-up. The chip is stood in for, because
/// making the real one refuse a PIN counts against its lockout (see DeviceSecretTests).
/// </summary>
[SupportedOSPlatform("windows")]
public class ChipPinTests : IDisposable
{
    private readonly string _stick = NewFolder("stick");
    private readonly string _pc = NewFolder("pc");
    private readonly byte[] _deviceSecret = RandomNumberGenerator.GetBytes(32);
    private readonly string _phrase;

    public ChipPinTests() => (_phrase, _) = VaultStorage.Initialise(_stick);

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

    private void SetUp(string pin) => Enrolment.Enrol(_pc, _stick, _phrase, pin, _deviceSecret, DeviceProtection.Tpm);

    [Theory]
    [InlineData("4821", true)]
    [InlineData("123456", true)]
    [InlineData("123456789012", true)]
    [InlineData("123", false)]
    [InlineData("1234567890123", false)]
    [InlineData("12a4", false)]
    [InlineData("١٢٣٤", false)]   // Arabic-Indic digits: not what the keypad types
    [InlineData("", false)]
    public void A_PIN_is_4_to_12_ordinary_digits(string pin, bool valid)
        => Assert.Equal(valid, Enrolment.IsPin(pin));

    /// <summary>
    /// The PIN is the tail after two fixed 32-byte secrets, so any length reads back one way. A 4-digit
    /// PIN must give byte-for-byte what it always did - VectorTests checks that against the shared file.
    /// </summary>
    [Fact]
    public void A_longer_PIN_is_just_a_longer_tail_and_every_length_is_different_key_material()
    {
        var usb = new byte[32]; var device = new byte[32];
        Array.Fill(usb, (byte)0x11); Array.Fill(device, (byte)0x22);

        var six = PassVault.Core.Crypto.KeyWrap.PinInput(usb, device, "123456");
        Assert.Equal(70, six.Length);
        Assert.Equal("123456"u8.ToArray(), six[64..]);

        // "1234" and "12340" share their first 68 bytes, but not their whole input - so not their key.
        Assert.NotEqual(PassVault.Core.Crypto.KeyWrap.PinInput(usb, device, "1234"),
                        PassVault.Core.Crypto.KeyWrap.PinInput(usb, device, "12340"));
        Assert.Equal(68, PassVault.Core.Crypto.KeyWrap.PinInput(usb, device, "4821").Length);
    }

    [Fact]
    public void A_longer_PIN_works_and_its_length_is_remembered_for_the_unlock_screen()
    {
        SetUp("48291735");

        Assert.Equal(8, MachineStore.Read(_pc).PinLength);
        Assert.NotEmpty(Enrolment.UnlockWithPin(_pc, _stick, "48291735", _deviceSecret).MasterKey);
    }

    /// <summary>Set-ups from before longer PINs have no length stored, and all of those were 4 digits.</summary>
    [Fact]
    public void An_old_machine_file_without_a_length_reads_as_4_digits()
    {
        SetUp("4821");
        var path = MachineStore.PathIn(_pc);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json.Remove("pinLength");
        File.WriteAllText(path, json.ToJsonString());

        Assert.Equal(4, MachineStore.Read(_pc).PinLength);
    }

    [Fact]
    public void When_the_chip_refuses_the_PIN_the_try_is_counted_and_it_reads_as_a_wrong_PIN()
    {
        SetUp("4821");

        var ex = Assert.Throws<UnlockException>(() => Enrolment.UnlockWithPin(_pc, _stick, "0000",
            _ => throw new PinRefusedException(lockedOut: false)));

        Assert.StartsWith("Wrong PIN. 9 tries left", ex.Message);
        Assert.Equal(1, MachineStore.Read(_pc).FailedAttempts);
    }

    [Fact]
    public void A_chip_that_is_resting_says_so_instead_of_blaming_the_PIN()
    {
        SetUp("4821");

        var ex = Assert.Throws<UnlockException>(() => Enrolment.UnlockWithPin(_pc, _stick, "4821",
            _ => throw new PinRefusedException(lockedOut: true)));

        Assert.Contains("resting", ex.Message);
        Assert.Contains("12 words", ex.Message);
    }

    /// <summary>A resting chip looked at nothing. Counting it switched the PIN off after ten tries with the right one.</summary>
    [Fact]
    public void A_try_the_chip_would_not_check_is_given_back()
    {
        SetUp("4821");

        for (int i = 0; i < Enrolment.MaxAttempts; i++)
            Assert.Throws<UnlockException>(() => Enrolment.UnlockWithPin(_pc, _stick, "4821",
                _ => throw new PinRefusedException(lockedOut: true)));

        var machine = MachineStore.Read(_pc);
        Assert.Equal(0, machine.FailedAttempts);
        Assert.False(machine.PinDisabled);
    }

    /// <summary>A cleared chip is not a wrong PIN: the unlock screen offers setting the PC up again for it.</summary>
    [Fact]
    public void Any_other_chip_failure_is_passed_on_not_turned_into_a_wrong_PIN()
    {
        SetUp("4821");

        Assert.Throws<CryptographicException>(() => Enrolment.UnlockWithPin(_pc, _stick, "4821",
            _ => throw new CryptographicException("the chip was cleared")));
    }

    /// <summary>
    /// The PIN slot and the sealed secret it needs are one file, written once. As two files, a
    /// crash between the writes left a pair from two set-ups that no PIN could ever open.
    /// </summary>
    [Fact]
    public void The_sealed_secret_is_saved_in_the_same_file_as_the_PIN_slot()
    {
        byte[] sealedSecret = [.. "PVDS"u8, 2, 1, 2, 3];

        Enrolment.Enrol(_pc, _stick, _phrase, "4821", _deviceSecret, DeviceProtection.Tpm, sealedSecret);

        Assert.Equal(sealedSecret, MachineStore.Read(_pc).Device);
        Assert.Equal(sealedSecret, DeviceSecret.SealedIn(_pc));
        Assert.Equal(new[] { MachineStore.FileName, MachineStore.MirrorVaultName },
                     Directory.GetFiles(_pc).Select(Path.GetFileName).Order().ToArray());
    }

    /// <summary>The mistake people really make at set-up is mistyping the 12 words. That must write nothing.</summary>
    [Fact]
    public void Wrong_12_words_write_nothing()
    {
        var wrong = "legal winner thank year wave sausage worth useful legal winner thank yellow";

        Assert.ThrowsAny<Exception>(() => Enrolment.Enrol(_pc, _stick, wrong, "4821", _deviceSecret,
            DeviceProtection.Tpm, [.. "PVDS"u8, 2]));

        Assert.Empty(Directory.GetFiles(_pc));
    }

    /// <summary>Set-ups from before 1.0.2 keep the sealed secret in device.bin until their first unlock.</summary>
    [Fact]
    public void An_older_set_up_still_finds_its_secret_in_device_bin()
    {
        SetUp("4821");
        byte[] old = [.. "PVDS"u8, 0, 9, 9];
        File.WriteAllBytes(Path.Combine(_pc, DeviceSecret.FileName), old);

        Assert.Equal(old, DeviceSecret.SealedIn(_pc));
    }
}

/// <summary>Moving this PC's folder out of AppData, where the Store version would lose it.</summary>
public class FolderMoveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pv-move-" + Guid.NewGuid().ToString("N")[..8]);
    private string Old => Path.Combine(_root, "old");
    private string New => Path.Combine(_root, "new");

    public FolderMoveTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_old_folder_moves_across_with_everything_in_it()
    {
        Directory.CreateDirectory(Old);
        File.WriteAllText(Path.Combine(Old, "vault.pv"), "spare copy");

        MachineStore.MoveFromOldPlace(Old, New);

        Assert.False(Directory.Exists(Old));
        Assert.Equal("spare copy", File.ReadAllText(Path.Combine(New, "vault.pv")));
    }

    /// <summary>Picking wrongly between two spare copies is worse than leaving one where it is.</summary>
    [Fact]
    public void Two_folders_are_never_merged()
    {
        Directory.CreateDirectory(Old);
        Directory.CreateDirectory(New);
        File.WriteAllText(Path.Combine(Old, "vault.pv"), "old");
        File.WriteAllText(Path.Combine(New, "vault.pv"), "new");

        MachineStore.MoveFromOldPlace(Old, New);

        Assert.Equal("old", File.ReadAllText(Path.Combine(Old, "vault.pv")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(New, "vault.pv")));
    }

    [Fact]
    public void Nothing_to_move_is_fine()
    {
        MachineStore.MoveFromOldPlace(Old, New);
        Assert.False(Directory.Exists(New));
    }

    /// <summary>
    /// The move failed, or a folder of that name was already there. Asking for the 12 words then set
    /// the PC up a second time and stranded the first set-up, its spare copy and the licence.
    /// </summary>
    [Fact]
    public void While_only_the_old_folder_is_set_up_it_is_the_one_used()
    {
        Directory.CreateDirectory(Old);
        Directory.CreateDirectory(New);
        File.WriteAllText(MachineStore.PathIn(Old), "{}");

        Assert.Equal(Old, MachineStore.FolderToUse(New, Old));

        File.WriteAllText(MachineStore.PathIn(New), "{}");
        Assert.Equal(New, MachineStore.FolderToUse(New, Old));
    }

    [Fact]
    public void With_nothing_set_up_anywhere_the_new_folder_is_used()
        => Assert.Equal(New, MachineStore.FolderToUse(New, Old));
}
