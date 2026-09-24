using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace PassVault.Core.Machine;

/// <summary>How the device secret is protected. Worth telling the user honestly.</summary>
public enum DeviceProtection
{
    /// <summary>Sealed by the TPM chip. The secret cannot leave this physical machine.</summary>
    Tpm,

    /// <summary>
    /// Fallback when there is no usable TPM. Tied to this Windows account rather than the
    /// hardware, so someone who can log in as you - or who has your Windows password and
    /// your disk - can get it back. Weaker, and the app says so.
    /// </summary>
    Dpapi,
}

/// <summary>
/// A 32-byte secret that belongs to this machine and nowhere else. It is the second of the
/// three ingredients in the daily unlock, and it is what stops a stolen laptop bag - stick
/// and PC together - from being just 10,000 PIN guesses.
///
/// The secret itself is stored encrypted. On a TPM machine the key that decrypts it lives
/// inside the chip and cannot be exported, so copying the folder or imaging the disk gets
/// an attacker nothing.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DeviceSecret
{
    public const int Size = 32;
    public const string FileName = "device.bin";
    public const string TpmKeyName = "PassVault.DeviceKey";

    private static readonly byte[] Magic = "PVDS"u8.ToArray();
    private static readonly CngProvider TpmProvider = new("Microsoft Platform Crypto Provider");

    /// <summary>
    /// Load this machine's secret, creating it the first time. The TPM is used when it is
    /// there; otherwise we fall back and report that we did.
    /// </summary>
    public static (byte[] Secret, DeviceProtection Protection) Load(string folder, string tpmKeyName = TpmKeyName)
    {
        var path = Path.Combine(folder, FileName);
        if (File.Exists(path)) return Open(File.ReadAllBytes(path), tpmKeyName);

        Directory.CreateDirectory(folder);
        var secret = RandomNumberGenerator.GetBytes(Size);
        var protection = TpmIsUsable() ? DeviceProtection.Tpm : DeviceProtection.Dpapi;

        File.WriteAllBytes(path, Seal(secret, protection, tpmKeyName));
        return (secret, protection);
    }

    public static bool Exists(string folder) => File.Exists(Path.Combine(folder, FileName));

    /// <summary>
    /// Throw the secret away. The vault is untouched - but every machine file that relied on
    /// this secret is now dead and needs setting up again with the 12 words.
    /// </summary>
    public static void Forget(string folder, string tpmKeyName = TpmKeyName)
    {
        var path = Path.Combine(folder, FileName);
        if (File.Exists(path)) File.Delete(path);

        if (CngKey.Exists(tpmKeyName, TpmProvider))
            CngKey.Open(tpmKeyName, TpmProvider).Delete();
    }

    public static bool TpmIsUsable()
    {
        try
        {
            using var key = OpenOrCreateTpmKey(TpmKeyName + ".probe");
            using var rsa = new RSACng(key);
            var check = RandomNumberGenerator.GetBytes(16);
            var back = rsa.Decrypt(rsa.Encrypt(check, RSAEncryptionPadding.OaepSHA256), RSAEncryptionPadding.OaepSHA256);
            key.Delete();
            return check.AsSpan().SequenceEqual(back);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    // ---- file format: "PVDS" || protection(1) || sealed bytes ----

    private static byte[] Seal(byte[] secret, DeviceProtection protection, string tpmKeyName)
    {
        var sealedBytes = protection switch
        {
            DeviceProtection.Tpm => SealWithTpm(secret, tpmKeyName),
            _ => ProtectedData.Protect(secret, null, DataProtectionScope.CurrentUser),
        };

        var file = new byte[Magic.Length + 1 + sealedBytes.Length];
        Magic.CopyTo(file, 0);
        file[Magic.Length] = (byte)protection;
        sealedBytes.CopyTo(file, Magic.Length + 1);
        return file;
    }

    private static (byte[] Secret, DeviceProtection Protection) Open(byte[] file, string tpmKeyName)
    {
        if (file.Length < Magic.Length + 1 || !file.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("This machine's device file is damaged.");

        var protection = (DeviceProtection)file[Magic.Length];
        var sealedBytes = file[(Magic.Length + 1)..];

        var secret = protection switch
        {
            DeviceProtection.Tpm => OpenWithTpm(sealedBytes, tpmKeyName),
            DeviceProtection.Dpapi => ProtectedData.Unprotect(sealedBytes, null, DataProtectionScope.CurrentUser),
            _ => throw new InvalidDataException("This machine's device file uses a protection we do not know."),
        };

        return (secret, protection);
    }

    private static byte[] SealWithTpm(byte[] secret, string keyName)
    {
        using var key = OpenOrCreateTpmKey(keyName);
        using var rsa = new RSACng(key);
        return rsa.Encrypt(secret, RSAEncryptionPadding.OaepSHA256);
    }

    private static byte[] OpenWithTpm(byte[] sealedBytes, string keyName)
    {
        if (!CngKey.Exists(keyName, TpmProvider))
            throw new InvalidOperationException(
                "This machine's TPM key is gone - the chip was cleared, or Windows was reinstalled. " +
                "Set this machine up again with your 12 words.");

        using var key = CngKey.Open(keyName, TpmProvider);
        using var rsa = new RSACng(key);
        return rsa.Decrypt(sealedBytes, RSAEncryptionPadding.OaepSHA256);
    }

    private static CngKey OpenOrCreateTpmKey(string keyName)
    {
        if (CngKey.Exists(keyName, TpmProvider)) return CngKey.Open(keyName, TpmProvider);

        var parameters = new CngKeyCreationParameters
        {
            Provider = TpmProvider,
            ExportPolicy = CngExportPolicies.None,   // the private key must never leave the chip
        };
        parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));

        return CngKey.Create(CngAlgorithm.Rsa, keyName, parameters);
    }
}
