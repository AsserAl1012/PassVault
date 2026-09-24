using System.Security.Cryptography;

namespace PassVault.Core.Crypto;

/// <summary>
/// AES-256-GCM. Every sealed blob is laid out as: nonce(12) || ciphertext || tag(16).
/// That layout is what Apple's CryptoKit calls "combined", so the iOS app can read
/// these bytes with no shuffling.
/// </summary>
public static class Aead
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    /// <summary>Encrypt. A fresh random nonce is generated every call - never a counter.</summary>
    public static byte[] Seal(byte[] key, byte[] plaintext, byte[] aad)
    {
        RequireKey(key);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);

        var blob = new byte[NonceSize + plaintext.Length + TagSize];
        nonce.CopyTo(blob, 0);

        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(
            nonce,
            plaintext,
            blob.AsSpan(NonceSize, plaintext.Length),
            blob.AsSpan(NonceSize + plaintext.Length, TagSize),
            aad);

        return blob;
    }

    /// <summary>Decrypt. Throws CryptographicException if the key, the blob or the aad is wrong.</summary>
    public static byte[] Open(byte[] key, byte[] blob, byte[] aad)
    {
        RequireKey(key);
        if (blob.Length < NonceSize + TagSize)
            throw new CryptographicException("Sealed blob is too short to be valid.");

        var plaintext = new byte[blob.Length - NonceSize - TagSize];

        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(
            blob.AsSpan(0, NonceSize),
            blob.AsSpan(NonceSize, plaintext.Length),
            blob.AsSpan(NonceSize + plaintext.Length, TagSize),
            plaintext,
            aad);

        return plaintext;
    }

    private static void RequireKey(byte[] key)
    {
        if (key.Length != KeySize)
            throw new ArgumentException($"Key must be {KeySize} bytes, got {key.Length}.", nameof(key));
    }
}
