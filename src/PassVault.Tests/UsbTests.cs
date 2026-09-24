using System.Security.Cryptography;
using PassVault.Core.Model;
using PassVault.Core.Usb;
using PassVault.Core.Vault;

namespace PassVault.Tests;

/// <summary>
/// A temp folder stands in for a stick. Everything in VaultStorage works on any folder,
/// so these tests exercise the same code that runs against real hardware.
/// </summary>
public class UsbTests : IDisposable
{
    private readonly string _stick = Path.Combine(Path.GetTempPath(), "pv-test-" + Guid.NewGuid().ToString("N")[..8]);

    public UsbTests() => Directory.CreateDirectory(_stick);

    public void Dispose()
    {
        if (Directory.Exists(_stick)) Directory.Delete(_stick, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ---- the keyfile ----

    [Fact]
    public void Keyfile_survives_a_round_trip()
    {
        var keyfile = Keyfile.Create();
        var back = Keyfile.FromBytes(keyfile.ToBytes());

        Assert.Equal(keyfile.StickId, back.StickId);
        Assert.Equal(keyfile.UsbSecret, back.UsbSecret);
        Assert.True(keyfile.IsSameStick(back));
    }

    [Fact]
    public void Keyfile_is_fifty_two_bytes()
    {
        Assert.Equal(52, Keyfile.Size);
        Assert.Equal(52, Keyfile.Create().ToBytes().Length);
    }

    [Fact]
    public void Two_sticks_are_never_the_same()
    {
        var a = Keyfile.Create();
        var b = Keyfile.Create();

        Assert.NotEqual(a.StickId, b.StickId);
        Assert.NotEqual(a.UsbSecret, b.UsbSecret);
        Assert.False(a.IsSameStick(b));
    }

    [Fact]
    public void Random_bytes_are_not_mistaken_for_a_keyfile()
    {
        Assert.Throws<InvalidDataException>(() => Keyfile.FromBytes(RandomNumberGenerator.GetBytes(52)));
        Assert.Throws<InvalidDataException>(() => Keyfile.FromBytes([1, 2, 3]));
    }

    // ---- setting up a stick ----

    [Fact]
    public void Setting_up_a_stick_writes_both_files()
    {
        var (phrase, keyfile) = VaultStorage.Initialise(_stick);

        Assert.True(File.Exists(VaultStorage.KeyfilePathOn(_stick)));
        Assert.True(File.Exists(VaultStorage.VaultPathOn(_stick)));
        Assert.True(VaultStorage.IsSetUp(_stick));
        Assert.Equal(12, phrase.Split(' ').Length);
        Assert.Equal(8, keyfile.ShortId.Length);
    }

    [Fact]
    public void The_phrase_from_setup_opens_the_vault()
    {
        var (phrase, _) = VaultStorage.Initialise(_stick);

        var masterKey = VaultFile.UnlockWithPhrase(VaultStorage.ReadVault(_stick), phrase);

        Assert.Equal(32, masterKey.Length);
    }

    [Fact]
    public void The_keyfile_written_is_the_keyfile_read_back()
    {
        var (_, keyfile) = VaultStorage.Initialise(_stick);

        Assert.True(keyfile.IsSameStick(VaultStorage.ReadKeyfile(_stick)));
        Assert.Equal(keyfile.UsbSecret, VaultStorage.ReadKeyfile(_stick).UsbSecret);
    }

    [Fact]
    public void Setting_up_a_stick_twice_is_refused()
    {
        VaultStorage.Initialise(_stick);

        // Otherwise a mis-click would throw away every password on the stick.
        Assert.Throws<InvalidOperationException>(() => VaultStorage.Initialise(_stick));
    }

    [Fact]
    public void A_stick_that_still_holds_a_vault_is_not_set_up_over_it()
    {
        VaultStorage.Initialise(_stick);
        var vault = VaultStorage.ReadVault(_stick);
        File.Delete(VaultStorage.KeyfilePathOn(_stick));

        Assert.Throws<InvalidOperationException>(() => VaultStorage.Initialise(_stick));
        Assert.Equal(vault, VaultStorage.ReadVault(_stick));
    }

    [Fact]
    public void An_empty_drive_is_not_set_up()
    {
        Assert.False(VaultStorage.IsSetUp(_stick));
        Assert.Throws<FileNotFoundException>(() => VaultStorage.ReadKeyfile(_stick));
        Assert.Throws<FileNotFoundException>(() => VaultStorage.ReadVault(_stick));
    }

    // ---- saving ----

    [Fact]
    public void Entries_written_to_the_stick_come_back()
    {
        var (phrase, _) = VaultStorage.Initialise(_stick);
        var vault = VaultStorage.ReadVault(_stick);
        var masterKey = VaultFile.UnlockWithPhrase(vault, phrase);

        VaultStorage.WriteVault(_stick, VaultFile.Save(vault, masterKey, [new Entry { Name = "github.com", Username = "asser" }]));

        var reloaded = VaultStorage.ReadVault(_stick);
        var entries = VaultFile.ReadEntries(reloaded, VaultFile.UnlockWithPhrase(reloaded, phrase));

        Assert.Single(entries);
        Assert.Equal("github.com", entries[0].Name);
    }

    [Fact]
    public void The_previous_save_is_kept_as_a_backup()
    {
        var (phrase, _) = VaultStorage.Initialise(_stick);
        var vault = VaultStorage.ReadVault(_stick);
        var masterKey = VaultFile.UnlockWithPhrase(vault, phrase);

        VaultStorage.WriteVault(_stick, VaultFile.Save(vault, masterKey, [new Entry { Name = "one" }]));

        Assert.True(File.Exists(VaultStorage.VaultPathOn(_stick) + ".bak"));
    }

    [Fact]
    public void Pulling_the_stick_out_mid_save_still_leaves_a_readable_vault()
    {
        var (phrase, _) = VaultStorage.Initialise(_stick);
        var vault = VaultStorage.ReadVault(_stick);
        var masterKey = VaultFile.UnlockWithPhrase(vault, phrase);
        VaultStorage.WriteVault(_stick, VaultFile.Save(vault, masterKey, [new Entry { Name = "github.com" }]));

        // The moment between moving the old file aside and the new one landing.
        File.Delete(VaultStorage.VaultPathOn(_stick));

        Assert.True(VaultStorage.VaultExists(_stick));
        var rescued = VaultStorage.ReadVault(_stick);
        Assert.NotNull(VaultFile.UnlockWithPhrase(rescued, phrase));
    }

    [Fact]
    public void No_leftover_temp_file_after_a_save()
    {
        var (phrase, _) = VaultStorage.Initialise(_stick);
        var vault = VaultStorage.ReadVault(_stick);
        var masterKey = VaultFile.UnlockWithPhrase(vault, phrase);

        VaultStorage.WriteVault(_stick, VaultFile.Save(vault, masterKey, [new Entry { Name = "one" }]));

        Assert.False(File.Exists(VaultStorage.VaultPathOn(_stick) + ".tmp"));
    }

    // ---- finding sticks ----

    [Fact]
    public void Finding_sticks_does_not_blow_up_on_this_machine()
    {
        // Whatever is plugged in, this must return a list and not throw.
        var found = UsbDetect.Find();

        Assert.NotNull(found);
        Assert.All(found, s => Assert.True(Directory.Exists(s.Root)));
    }
}
