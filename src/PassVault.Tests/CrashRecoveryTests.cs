using System.Security.Cryptography;
using PassVault.Core.Crypto;
using PassVault.Core.Machine;
using PassVault.Core.Usb;
using PassVault.Core.Vault;

namespace PassVault.Tests;

/// <summary>
/// What is left on disk when a save is interrupted, and whether the app can pick it up.
///
/// Every one of these came out of a QA pass, and every one of them is a state a real user
/// reaches by pulling the stick out at the wrong moment - not a contrived one.
/// </summary>
public class CrashRecoveryTests : IDisposable
{
    private readonly string _stick = NewFolder("stick");
    private readonly string _pc = NewFolder("pc");
    private readonly byte[] _deviceSecret = RandomNumberGenerator.GetBytes(32);
    private readonly string _phrase;

    private const string Pin = "4821";

    public CrashRecoveryTests()
    {
        (_phrase, _) = VaultStorage.Initialise(_stick);
        Enrolment.Enrol(_pc, _stick, _phrase, Pin, _deviceSecret, DeviceProtection.Dpapi);
    }

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

    /// <summary>
    /// The exact state WriteFileSafely leaves behind if the stick is pulled between its two
    /// moves: the newest save sitting under .tmp, the previous one under .bak, and no
    /// vault.pv at all.
    /// </summary>
    private void InterruptTheNextSave(byte[] vault)
    {
        var path = VaultStorage.VaultPathOn(_stick);
        File.WriteAllBytes(path + ".tmp", vault);
        if (File.Exists(path)) { File.Move(path, path + ".bak", overwrite: true); }
    }

    [Fact]
    public void An_interrupted_save_is_finished_rather_than_falling_back_a_revision()
    {
        var masterKey = Enrolment.UnlockWithPin(_pc, _stick, Pin, _deviceSecret).MasterKey;
        var saved = VaultFile.Save(VaultStorage.ReadVault(_stick), masterKey, []);

        InterruptTheNextSave(saved);
        Assert.False(File.Exists(VaultStorage.VaultPathOn(_stick)));

        var read = VaultStorage.ReadVault(_stick);

        Assert.Equal(VaultFile.ReadHeader(saved).Revision, VaultFile.ReadHeader(read).Revision);
        Assert.True(File.Exists(VaultStorage.VaultPathOn(_stick)), "the .tmp should have been put in place");
    }

    /// <summary>
    /// The bug this closes: the .bak is always exactly one save behind, so falling back to it
    /// handed the rollback check a vault older than the machine had already seen - and the
    /// app accused the user of putting an old copy back after their own stick was pulled out.
    /// </summary>
    [Fact]
    public void Pulling_the_stick_mid_save_does_not_get_the_user_accused_of_a_rollback()
    {
        var masterKey = Enrolment.UnlockWithPin(_pc, _stick, Pin, _deviceSecret).MasterKey;
        var saved = VaultFile.Save(VaultStorage.ReadVault(_stick), masterKey, []);
        Enrolment.SaveVault(_pc, _stick, saved);

        var next = VaultFile.Save(VaultStorage.ReadVault(_stick), masterKey, []);
        Enrolment.NoteHowNew(_pc, VaultFile.ReadHeader(next));   // the machine saw it go out
        InterruptTheNextSave(next);

        var reopened = Enrolment.UnlockWithPin(_pc, _stick, Pin, _deviceSecret);
        Assert.NotEmpty(reopened.MasterKey);
    }

    /// <summary>
    /// The spare copy on this PC goes through the same write, so an interruption there leaves the
    /// same state - and the spare copy is read exactly when the stick is gone.
    /// </summary>
    [Fact]
    public void An_interrupted_write_of_the_spare_copy_is_finished_too()
    {
        var masterKey = Enrolment.UnlockWithPin(_pc, _stick, Pin, _deviceSecret).MasterKey;
        var saved = VaultFile.Save(VaultStorage.ReadVault(_stick), masterKey, []);

        var mirror = MachineStore.MirrorVaultPathIn(_pc);
        File.WriteAllBytes(mirror + ".tmp", saved);
        File.Move(mirror, mirror + ".bak", overwrite: true);

        Assert.True(MachineStore.HasMirrorVault(_pc));
        Assert.Equal(VaultFile.ReadHeader(saved).Revision, VaultFile.ReadHeader(MachineStore.ReadMirrorVault(_pc)).Revision);
        Assert.Equal(VaultFile.ReadHeader(saved).Revision,
                     VaultFile.ReadHeader(Recovery.FindVault(_pc, stickRoot: null).Vault).Revision);
    }

    /// <summary>
    /// machine.pvm is rewritten twice on every unlock. Dying mid-write used to leave the PC
    /// claiming it had never been set up, with a good copy one rename away.
    /// </summary>
    [Fact]
    public void A_PC_still_knows_it_is_set_up_if_only_the_backup_machine_file_survives()
    {
        var path = MachineStore.PathIn(_pc);
        File.Move(path, path + ".bak", overwrite: true);

        Assert.True(MachineStore.IsEnrolled(_pc));
        Assert.NotEmpty(MachineStore.Read(_pc).VaultId);
        Assert.NotEmpty(Enrolment.UnlockWithPin(_pc, _stick, Pin, _deviceSecret).MasterKey);
    }

    [Fact]
    public void Forget_clears_every_copy_of_the_machine_file()
    {
        var path = MachineStore.PathIn(_pc);
        File.Copy(path, path + ".bak", overwrite: true);
        File.Copy(path, path + ".tmp", overwrite: true);

        MachineStore.Forget(_pc);

        Assert.False(MachineStore.IsEnrolled(_pc));
    }
}

/// <summary>
/// A phrase copied out of a web page, a PDF or a Word document arrives with invisible
/// characters glued to the words. Rejecting it names a word that is spelled perfectly, and
/// a user in that position concludes their vault is dead.
/// </summary>
public class PhrasePastingTests
{
    private const string Known =
        "legal winner thank year wave sausage worth useful legal winner thank yellow";

    [Theory]
    [InlineData("​")]   // zero width space
    [InlineData("‌")]   // zero width non-joiner
    [InlineData("­")]   // soft hyphen
    [InlineData("﻿")]   // byte order mark
    public void Invisible_characters_inside_the_words_do_not_break_a_good_phrase(string invisible)
    {
        var words = Known.Split(' ');
        words[2] = words[2][..3] + invisible + words[2][3..];

        Assert.True(Mnemonic.TryParse(string.Join(' ', words), out _, out var problem), problem);
    }

    [Theory]
    [InlineData(" ")]   // non-breaking space
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void Any_kind_of_gap_between_the_words_is_treated_as_a_space(string gap)
        => Assert.True(Mnemonic.TryParse(Known.Replace(" ", gap), out _, out var problem), problem);

    [Fact]
    public void A_phrase_wrapped_in_a_byte_order_mark_still_parses()
        => Assert.True(Mnemonic.TryParse("﻿" + Known + "﻿", out _, out var problem), problem);

    /// <summary>
    /// Stripping invisible characters must not turn into accepting anything. "gravity" is a
    /// real BIP-39 word but the wrong one here, and "sausege" is not a word at all.
    /// </summary>
    [Fact]
    public void A_genuinely_wrong_word_is_still_refused()
    {
        Assert.False(Mnemonic.TryParse(Known.Replace("sausage", "sausege"), out _, out var problem));
        Assert.Contains("sausege", problem);

        Assert.False(Mnemonic.TryParse(Known.Replace("sausage", "gravity"), out _, out _));
    }
}
