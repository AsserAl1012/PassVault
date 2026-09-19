using System.Text;
using Konscious.Security.Cryptography;

namespace PassVault.Core.Crypto;

/// <summary>
/// Argon2id settings. These are stored in the vault header, not hard-coded, so a vault
/// written today still opens if we ever raise the cost for new vaults.
/// </summary>
/// <param name="MemoryKiB">
/// Memory in KiB. Konscious wants KiB (65536). libsodium on iOS wants BYTES (67108864).
/// Both sides still "work" if you mix these up, but one is 1024x weaker - so the iOS code
/// must multiply this by 1024.
/// </param>
public sealed record KdfSettings(
    string Algo,
    int Iterations,
    int MemoryKiB,
    int Parallelism,
    int OutputBytes)
{
    public const int SaltSize = 16;

    /// <summary>64 MiB, 3 passes, single-threaded. Parallelism must stay 1 for libsodium.</summary>
    public static KdfSettings Default => new("argon2id", 3, 65536, 1, 32);
}

public static class Kdf
{
    public static byte[] Derive(byte[] secret, byte[] salt, KdfSettings s)
    {
        if (s.Algo != "argon2id")
            throw new NotSupportedException($"Unknown key derivation algorithm: {s.Algo}");
        if (salt.Length != KdfSettings.SaltSize)
            throw new ArgumentException($"Salt must be {KdfSettings.SaltSize} bytes.", nameof(salt));
        if (s.Parallelism != 1)
            throw new NotSupportedException("Parallelism must be 1 so iOS can reproduce the key.");

        using var argon = new Argon2id(secret)
        {
            Salt = salt,
            Iterations = s.Iterations,
            MemorySize = s.MemoryKiB,
            DegreeOfParallelism = s.Parallelism,
        };
        return argon.GetBytes(s.OutputBytes);
    }

    public static byte[] Derive(string password, byte[] salt, KdfSettings s)
        => Derive(Encoding.UTF8.GetBytes(password), salt, s);
}
