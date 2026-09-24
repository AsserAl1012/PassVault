using System.Security.Cryptography;
using PassVault.Core.Machine;
using PassVault.Core.Model;
using PassVault.Core.Usb;
using PassVault.Core.Vault;

namespace PassVault.Tests;

public class RecoveryTests : IDisposable
{
    private readonly string _oldStick = NewFolder("old");
    private readonly string _newStick = NewFolder("new");
    private readonly string _pc = NewFolder("pc");
    private readonly byte[] _deviceSecret = RandomNumberGenerator.GetBytes(32);
    private readonly string _phrase;

    private const string Pin = "4821";

    public RecoveryTests()
    {
        (_phrase, _) = VaultStorage.Initialise(_oldStick);

        // A vault with something in it, and this PC set up against it.
        var (_, masterKey) = Enrolment.Enrol(_pc, _oldStick, _phrase, Pin, _deviceSecret, DeviceProtection.Tpm);
        var saved = VaultFile.Save(VaultStorage.ReadVault(_oldStick), masterKey,
        [
            new Entry { Name = "bank.com", Password = "old-bank-password" },
            new Entry { Name = "mail.com", Password = "old-mail-password" },
        ]);
        VaultStorage.WriteVault(_oldStick, saved);
        MachineStore.WriteMirrorVault(_pc, saved);
    }

    public void Dispose()
    {
        foreach (var folder in new[] { _oldStick, _newStick, _pc })
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string NewFolder(string tag)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pv-{tag}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(path);
        return path;
    }

    // ---- finding a vault to recover from ----

    [Fact]
    public void The_stick_is_used_when_it_is_plugged_in()
    {
        var found = Recovery.FindVault(_pc, _oldStick);

        Assert.True(found.FromStick);
        Assert.Contains(_oldStick, found.Where);
    }

    [Fact]
    public void The_spare_copy_is_used_when_the_stick_is_gone()
    {
        var found = Recovery.FindVault(_pc, stickRoot: null);

        Assert.False(found.FromStick);
        Assert.Contains("spare copy", found.Where);
        Assert.NotNull(VaultFile.UnlockWithPhrase(found.Vault, _phrase));
    }

    [Fact]
    public void The_newer_of_the_two_wins()
    {
        // Save once more to the PC only, so the spare copy is ahead of the stick.
        var masterKey = VaultFile.UnlockWithPhrase(VaultStorage.ReadVault(_oldStick), _phrase);
        var newer = VaultFile.Save(VaultStorage.ReadVault(_oldStick), masterKey, [new Entry { Name = "newer.com" }]);
        MachineStore.WriteMirrorVault(_pc, newer);

        var found = Recovery.FindVault(_pc, _oldStick);

        Assert.False(found.FromStick);
        Assert.Equal(VaultFile.ReadHeader(newer).Revision, VaultFile.ReadHeader(found.Vault).Revision);
    }

    [Fact]
    public void A_damaged_stick_does_not_stop_recovery_from_the_spare_copy()
    {
        File.WriteAllBytes(VaultStorage.VaultPathOn(_oldStick), new byte[900]);

        var found = Recovery.FindVault(_pc, _oldStick);

        Assert.False(found.FromStick);
        Assert.NotNull(VaultFile.UnlockWithPhrase(found.Vault, _phrase));
    }

    [Fact]
    public void Nothing_anywhere_says_so_plainly()
    {
        var emptyPc = NewFolder("bare");
        try
        {
            var error = Assert.Throws<UnlockException>(() => Recovery.FindVault(emptyPc, stickRoot: null));
            Assert.Contains("nothing to recover from", error.Message);
        }
        finally
        {
            Directory.Delete(emptyPc, recursive: true);
        }
    }

    // ---- replacing a stolen stick ----

    [Fact]
    public void Replacing_a_stick_keeps_the_credentials()
    {
        var vault = Recovery.FindVault(_pc, _oldStick).Vault;

        var (fresh, newPhrase, toChange) = Recovery.ReplaceStick(vault, _phrase, _newStick);

        Assert.True(VaultStorage.IsSetUp(_newStick));
        var entries = VaultFile.ReadEntries(fresh, VaultFile.UnlockWithPhrase(fresh, newPhrase));
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Name == "bank.com");
        Assert.Equal(2, toChange.Count);
    }

    [Fact]
    public void Replacing_a_stick_kills_the_old_phrase_and_the_old_stick()
    {
        var vault = Recovery.FindVault(_pc, _oldStick).Vault;
        var oldKeyfile = VaultStorage.ReadKeyfile(_oldStick);

        var (fresh, newPhrase, _) = Recovery.ReplaceStick(vault, _phrase, _newStick);

        Assert.NotEqual(_phrase, newPhrase);
        Assert.ThrowsAny<CryptographicException>(() => VaultFile.UnlockWithPhrase(fresh, _phrase));

        // The new stick carries a brand new USB secret, so the old stick is a dead key.
        var newKeyfile = VaultStorage.ReadKeyfile(_newStick);
        Assert.False(oldKeyfile.IsSameStick(newKeyfile));
        Assert.NotEqual(oldKeyfile.UsbSecret, newKeyfile.UsbSecret);
    }

    [Fact]
    public void Replacing_a_stick_moves_to_the_next_epoch()
    {
        var vault = Recovery.FindVault(_pc, _oldStick).Vault;
        var before = VaultFile.ReadHeader(vault).Epoch;

        var (fresh, _, _) = Recovery.ReplaceStick(vault, _phrase, _newStick);

        Assert.Equal(before + 1, VaultFile.ReadHeader(fresh).Epoch);
    }

    [Fact]
    public void The_old_pin_stops_working_until_the_pc_is_set_up_again()
    {
        var vault = Recovery.FindVault(_pc, _oldStick).Vault;
        var (_, newPhrase, _) = Recovery.ReplaceStick(vault, _phrase, _newStick);

        // The PC still holds the old epoch's PIN slot, so it must refuse and say why.
        var error = Assert.Throws<UnlockException>(
            () => Enrolment.UnlockWithPin(_pc, _newStick, Pin, _deviceSecret));
        Assert.Contains("re-keyed", error.Message);

        // Setting the PC up again against the new stick fixes it.
        Enrolment.Enrol(_pc, _newStick, newPhrase, "1357", _deviceSecret, DeviceProtection.Tpm);
        Assert.NotNull(Enrolment.UnlockWithPin(_pc, _newStick, "1357", _deviceSecret).MasterKey);
    }

    [Fact]
    public void It_refuses_to_write_over_somebody_elses_vault()
    {
        VaultStorage.Initialise(_newStick);   // a different vault already living there
        var vault = Recovery.FindVault(_pc, _oldStick).Vault;

        var error = Assert.Throws<UnlockException>(() => Recovery.ReplaceStick(vault, _phrase, _newStick));

        Assert.Contains("already holds a different PassVault", error.Message);
    }

    [Fact]
    public void Re_keying_onto_the_same_stick_is_allowed()
    {
        var vault = Recovery.FindVault(_pc, _oldStick).Vault;

        var (fresh, newPhrase, _) = Recovery.ReplaceStick(vault, _phrase, _oldStick);

        Assert.NotNull(VaultFile.UnlockWithPhrase(VaultStorage.ReadVault(_oldStick), newPhrase));
        Assert.Equal(2, VaultFile.ReadHeader(fresh).Epoch);
    }

    [Fact]
    public void The_wrong_phrase_changes_nothing()
    {
        var vault = Recovery.FindVault(_pc, _oldStick).Vault;

        Assert.Throws<UnlockException>(
            () => Recovery.ReplaceStick(vault, PassVault.Core.Crypto.Mnemonic.Generate(), _newStick));

        Assert.False(VaultStorage.IsSetUp(_newStick));
    }

    // ---- backup files ----

    [Fact]
    public void A_backup_file_plus_the_phrase_is_a_full_recovery()
    {
        var vault = Recovery.FindVault(_pc, _oldStick).Vault;
        var path = Path.Combine(_pc, "backup.pv");

        Recovery.ExportBackup(vault, path);
        var restored = Recovery.OpenBackup(path, _phrase);

        var entries = VaultFile.ReadEntries(restored, VaultFile.UnlockWithPhrase(restored, _phrase));
        Assert.Contains(entries, e => e.Name == "bank.com");
    }

    [Fact]
    public void A_backup_can_be_put_onto_a_fresh_stick()
    {
        var path = Path.Combine(_pc, "backup.pv");
        Recovery.ExportBackup(Recovery.FindVault(_pc, _oldStick).Vault, path);

        // Pretend the stick and this PC are both gone.
        var restored = Recovery.OpenBackup(path, _phrase);
        var (_, newPhrase, toChange) = Recovery.ReplaceStick(restored, _phrase, _newStick);

        Assert.True(VaultStorage.IsSetUp(_newStick));
        Assert.Equal(2, toChange.Count);
        Assert.NotNull(VaultFile.UnlockWithPhrase(VaultStorage.ReadVault(_newStick), newPhrase));
    }

    [Fact]
    public void A_backup_holds_no_usb_secret()
    {
        var path = Path.Combine(_pc, "backup.pv");
        Recovery.ExportBackup(Recovery.FindVault(_pc, _oldStick).Vault, path);

        var bytes = File.ReadAllBytes(path);
        var usbSecret = VaultStorage.ReadKeyfile(_oldStick).UsbSecret;

        Assert.DoesNotContain(Convert.ToHexString(usbSecret), Convert.ToHexString(bytes));
    }

    [Fact]
    public void The_wrong_phrase_will_not_open_a_backup()
    {
        var path = Path.Combine(_pc, "backup.pv");
        Recovery.ExportBackup(Recovery.FindVault(_pc, _oldStick).Vault, path);

        Assert.Throws<UnlockException>(() => Recovery.OpenBackup(path, PassVault.Core.Crypto.Mnemonic.Generate()));
    }

    [Fact]
    public void A_file_that_is_not_a_backup_says_so()
    {
        var path = Path.Combine(_pc, "holiday-photo.jpg");
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(500));

        var error = Assert.Throws<UnlockException>(() => Recovery.OpenBackup(path, _phrase));
        Assert.Contains("not a PassVault backup", error.Message);
    }

    [Fact]
    public void The_suggested_backup_name_says_when_and_which_version()
    {
        var name = Recovery.SuggestBackupName(Recovery.FindVault(_pc, _oldStick).Vault, new DateTime(2026, 9, 19));

        Assert.Equal("passvault-backup-2026-09-19-epoch1-rev2.pv", name);
    }
}
