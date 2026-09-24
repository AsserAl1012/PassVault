using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PassVault.Core.Crypto;
using PassVault.Core.Usb;
using PassVault.Core.Vault;

namespace PassVault.Core.Machine;

/// <summary>
/// machine.pvm - what this one PC knows. It NEVER goes on the stick and NEVER goes in the
/// cloud. Keeping it here is what makes a stolen stick safe: the thief has no blob to test
/// PIN guesses against.
/// </summary>
public sealed class MachineFile
{
    public int FormatVersion { get; set; } = 1;

    /// <summary>Base64, 16 random bytes. Part of what the PIN slot is authenticated against.</summary>
    public string MachineId { get; set; } = "";

    /// <summary>Which vault this machine is set up for.</summary>
    public string VaultId { get; set; } = "";

    /// <summary>Only for the friendly "that is not your stick" message.</summary>
    public string StickShortId { get; set; } = "";

    /// <summary>The master key, locked behind USB secret + this machine's secret + the PIN.</summary>
    public KeySlot PinSlot { get; set; } = new();

    /// <summary>Kept here so the stick cannot dictate how hard the PIN is to attack.</summary>
    public KdfSettings Kdf { get; set; } = KdfSettings.Default;

    /// <summary>A re-key makes this machine's PIN slot useless, so we notice and say so.</summary>
    public int EpochAtEnrolment { get; set; } = 1;

    public int FailedAttempts { get; set; }

    public bool PinDisabled { get; set; }

    public int HighestEpochSeen { get; set; } = 1;
    public long HighestRevisionSeen { get; set; } = 1;

    public DeviceProtection Protection { get; set; }
}

public static class MachineStore
{
    public const string FileName = "machine.pvm";
    public const string MirrorVaultName = "vault.pv";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Local app data, not roaming - this must not follow the user onto another machine,
    /// because the whole point is that it belongs to this one.
    /// </summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PassVault");

    public static string PathIn(string folder) => Path.Combine(folder, FileName);
    public static string MirrorVaultPathIn(string folder) => Path.Combine(folder, MirrorVaultName);

    /// <summary>
    /// Is this PC set up? Asking only about machine.pvm was a bug: this file is rewritten
    /// twice on every unlock, and if the app dies mid-write the only copy left is the .bak
    /// sitting right next to it. The PC would then claim it had never been set up and ask
    /// for the 12 words, with a perfectly good file one rename away.
    /// </summary>
    public static bool IsEnrolled(string folder)
        => File.Exists(PathIn(folder))
           || File.Exists(PathIn(folder) + VaultStorage.TempSuffix)
           || File.Exists(PathIn(folder) + VaultStorage.BackupSuffix);

    public static MachineFile Read(string folder)
    {
        var path = PathIn(folder);

        foreach (var candidate in new[] { path, path + VaultStorage.TempSuffix, path + VaultStorage.BackupSuffix })
        {
            if (!File.Exists(candidate)) continue;

            try
            {
                var machine = JsonSerializer.Deserialize<MachineFile>(File.ReadAllText(candidate), Json);
                if (machine is not null) return machine;
            }
            catch (JsonException)
            {
                // Half-written. Try the next copy.
            }
        }

        throw new FileNotFoundException("This machine is not set up for PassVault.", path);
    }

    public static void Write(string folder, MachineFile machine)
    {
        Directory.CreateDirectory(folder);
        VaultStorage.WriteFileSafely(PathIn(folder), JsonSerializer.SerializeToUtf8Bytes(machine, Json));
    }

    /// <summary>
    /// The spare copy of the encrypted vault kept on this PC, so a dead stick is not the end.
    ///
    /// A spare copy of a DIFFERENT vault is never written over, because it can be the last copy
    /// of that vault anywhere: pressing "Set up this stick" on a PC whose own stick had just been
    /// lost replaced it with the new, empty vault, and one unlock later it was gone for good.
    /// It is kept beside this one instead, under a name of its own.
    /// </summary>
    public static void WriteMirrorVault(string folder, byte[] vault)
    {
        Directory.CreateDirectory(folder);
        KeepSpareOfAnotherVault(folder, VaultFile.ReadHeader(vault).VaultId);
        VaultStorage.WriteFileSafely(MirrorVaultPathIn(folder), vault);
    }

    /// <summary>
    /// Where a spare copy is kept once something is about to replace it - the newest copy of each
    /// vault, one file per vault. It is an ordinary vault file: the recovery wizard's "Use a
    /// backup file instead" opens it with that vault's 12 words.
    /// </summary>
    public static string KeptSparePathIn(string folder, string vaultId)
        => Path.Combine(folder, $"vault-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(vaultId)))[..16].ToLowerInvariant()}.pv");

    private static void KeepSpareOfAnotherVault(string folder, string vaultId)
    {
        if (ReadableMirror(folder) is { } mirror && mirror.Header.VaultId != vaultId)
            Keep(folder, mirror.Vault, mirror.Header);
    }

    /// <summary>
    /// Put the spare copy where nothing writes over it, before something is about to. Keeps the
    /// newest copy per vault: an older one never replaces a kept newer one.
    /// </summary>
    public static void KeepSpareCopy(string folder)
    {
        if (ReadableMirror(folder) is { } mirror) Keep(folder, mirror.Vault, mirror.Header);
    }

    private static (byte[] Vault, VaultHeader Header)? ReadableMirror(string folder)
    {
        if (!HasMirrorVault(folder)) return null;

        try
        {
            var vault = ReadMirrorVault(folder);
            return (vault, VaultFile.ReadHeader(vault));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return null;   // Nothing readable to keep.
        }
    }

    private static void Keep(string folder, byte[] current, VaultHeader header)
    {
        var keep = KeptSparePathIn(folder, header.VaultId);
        if (File.Exists(keep))
        {
            try
            {
                var kept = VaultFile.ReadHeader(File.ReadAllBytes(keep));
                if (VaultFile.IsRollback(header, kept.Epoch, kept.Revision)) return;   // what is kept is newer
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            {
                // A damaged kept copy is replaced by a readable one.
            }
        }

        VaultStorage.WriteFileSafely(keep, current);
    }

    public static bool HasMirrorVault(string folder)
    {
        var path = MirrorVaultPathIn(folder);
        return File.Exists(path)
               || File.Exists(path + VaultStorage.TempSuffix)
               || File.Exists(path + VaultStorage.BackupSuffix);
    }

    /// <summary>
    /// Read the spare copy by the same rules as the stick: finish a write that was interrupted
    /// between its two moves rather than falling back to the .bak, which is a save behind.
    /// </summary>
    public static byte[] ReadMirrorVault(string folder)
        => VaultStorage.ReadNewestCopy(MirrorVaultPathIn(folder), "There is no spare copy of the vault on this machine.");

    /// <summary>Undo the setup on this machine. The vault and the stick are untouched.</summary>
    public static void Forget(string folder)
    {
        foreach (var path in new[] { PathIn(folder), PathIn(folder) + VaultStorage.BackupSuffix,
                                     PathIn(folder) + VaultStorage.TempSuffix })
            if (File.Exists(path)) File.Delete(path);
    }
}
