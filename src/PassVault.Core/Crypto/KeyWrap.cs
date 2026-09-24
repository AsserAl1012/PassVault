using System.Security.Cryptography;
using System.Text;

namespace PassVault.Core.Crypto;

/// <summary>
/// One locked-away copy of the master key - one per way in. The name is stored in the
/// vault header and is authenticated, so each unlock opens exactly the slot it expects.
/// We never loop over slots trying each one until something works.
/// </summary>
public sealed class KeySlot
{
    public string Name { get; set; } = "";

    /// <summary>Base64, 16 bytes.</summary>
    public string Salt { get; set; } = "";

    /// <summary>Base64 of nonce(12) || ciphertext || tag(16).</summary>
    public string Blob { get; set; } = "";
}

public static class KeyWrap
{
    public const string RecoverySlot = "recovery";
    public const string PinSlot = "pin";
    public const int PinLength = 4;
    public const int SecretSize = 32;

    // ---- recovery phrase ----

    public static KeySlot WrapWithPhrase(byte[] masterKey, string phrase, string vaultId, KdfSettings kdf)
    {
        var salt = RandomNumberGenerator.GetBytes(KdfSettings.SaltSize);
        var key = Kdf.Derive(Mnemonic.Normalise(phrase), salt, kdf);
        var blob = Aead.Seal(key, masterKey, PhraseAad(vaultId));
        CryptographicOperations.ZeroMemory(key);

        return new KeySlot
        {
            Name = RecoverySlot,
            Salt = Convert.ToBase64String(salt),
            Blob = Convert.ToBase64String(blob),
        };
    }

    public static byte[] UnwrapWithPhrase(KeySlot slot, string phrase, string vaultId, KdfSettings kdf)
    {
        Require(slot, RecoverySlot);
        var key = Kdf.Derive(Mnemonic.Normalise(phrase), Convert.FromBase64String(slot.Salt), kdf);
        try
        {
            return Aead.Open(key, Convert.FromBase64String(slot.Blob), PhraseAad(vaultId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    // ---- USB stick + this machine's chip + PIN ----

    public static KeySlot WrapWithPin(
        byte[] masterKey, byte[] usbSecret, byte[] deviceSecret, string pin,
        string vaultId, string machineId, KdfSettings kdf)
    {
        var salt = RandomNumberGenerator.GetBytes(KdfSettings.SaltSize);
        var key = Kdf.Derive(PinInput(usbSecret, deviceSecret, pin), salt, kdf);
        var blob = Aead.Seal(key, masterKey, PinAad(vaultId, machineId));
        CryptographicOperations.ZeroMemory(key);

        return new KeySlot
        {
            Name = PinSlot,
            Salt = Convert.ToBase64String(salt),
            Blob = Convert.ToBase64String(blob),
        };
    }

    public static byte[] UnwrapWithPin(
        KeySlot slot, byte[] usbSecret, byte[] deviceSecret, string pin,
        string vaultId, string machineId, KdfSettings kdf)
    {
        Require(slot, PinSlot);
        var key = Kdf.Derive(PinInput(usbSecret, deviceSecret, pin), Convert.FromBase64String(slot.Salt), kdf);
        try
        {
            return Aead.Open(key, Convert.FromBase64String(slot.Blob), PinAad(vaultId, machineId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// The 68 bytes fed to Argon2id: usbSecret(32) || deviceSecret(32) || PIN as 4 ASCII digits.
    /// Plain concatenation of fixed-length parts, because Argon2's own "secret" parameter
    /// does not exist in libsodium on iOS.
    /// </summary>
    public static byte[] PinInput(byte[] usbSecret, byte[] deviceSecret, string pin)
    {
        if (usbSecret.Length != SecretSize)
            throw new ArgumentException($"USB secret must be {SecretSize} bytes.", nameof(usbSecret));
        if (deviceSecret.Length != SecretSize)
            throw new ArgumentException($"Device secret must be {SecretSize} bytes.", nameof(deviceSecret));
        if (pin.Length != PinLength || !pin.All(char.IsAsciiDigit))
            throw new ArgumentException($"The PIN must be {PinLength} digits.", nameof(pin));

        var input = new byte[SecretSize * 2 + PinLength];
        usbSecret.CopyTo(input, 0);
        deviceSecret.CopyTo(input, SecretSize);
        Encoding.ASCII.GetBytes(pin).CopyTo(input, SecretSize * 2);
        return input;
    }

    private static byte[] PhraseAad(string vaultId) => Encoding.UTF8.GetBytes($"pv1:slot:recovery:{vaultId}");

    private static byte[] PinAad(string vaultId, string machineId) => Encoding.UTF8.GetBytes($"pv1:slot:pin:{vaultId}:{machineId}");

    private static void Require(KeySlot slot, string expected)
    {
        if (slot.Name != expected)
            throw new InvalidOperationException($"Expected a \"{expected}\" slot but got \"{slot.Name}\".");
    }
}
