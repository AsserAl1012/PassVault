using System.Text.Json;
using System.Text.Json.Serialization;
using PassVault.Core.Crypto;
using PassVault.Core.Usb;

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

    public static bool IsEnrolled(string folder) => File.Exists(PathIn(folder));

    public static MachineFile Read(string folder)
    {
        var path = PathIn(folder);
        if (!File.Exists(path))
            throw new FileNotFoundException("This machine is not set up for PassVault.", path);

        return JsonSerializer.Deserialize<MachineFile>(File.ReadAllText(path), Json)
               ?? throw new InvalidDataException("This machine's file is damaged.");
    }

    public static void Write(string folder, MachineFile machine)
    {
        Directory.CreateDirectory(folder);
        VaultStorage.WriteFileSafely(PathIn(folder), JsonSerializer.SerializeToUtf8Bytes(machine, Json));
    }

    /// <summary>The spare copy of the encrypted vault kept on this PC, so a dead stick is not the end.</summary>
    public static void WriteMirrorVault(string folder, byte[] vault)
    {
        Directory.CreateDirectory(folder);
        VaultStorage.WriteFileSafely(MirrorVaultPathIn(folder), vault);
    }

    public static bool HasMirrorVault(string folder)
        => File.Exists(MirrorVaultPathIn(folder))
           || File.Exists(MirrorVaultPathIn(folder) + VaultStorage.BackupSuffix);

    public static byte[] ReadMirrorVault(string folder)
    {
        var path = MirrorVaultPathIn(folder);
        if (File.Exists(path)) return File.ReadAllBytes(path);

        var backup = path + VaultStorage.BackupSuffix;
        if (File.Exists(backup)) return File.ReadAllBytes(backup);

        throw new FileNotFoundException("There is no spare copy of the vault on this machine.", path);
    }

    /// <summary>Undo the setup on this machine. The vault and the stick are untouched.</summary>
    public static void Forget(string folder)
    {
        foreach (var path in new[] { PathIn(folder), PathIn(folder) + VaultStorage.BackupSuffix })
            if (File.Exists(path)) File.Delete(path);
    }
}
