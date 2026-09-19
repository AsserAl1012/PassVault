using System.Security.Cryptography;
using System.Text;
using PassVault.Core.Crypto;

namespace PassVault.Tests;

public class AeadTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] Aad = "pv1:test"u8.ToArray();
    private static readonly byte[] Plain = "hunter2 is a terrible password"u8.ToArray();

    [Fact]
    public void Sealed_then_opened_gives_the_same_bytes_back()
    {
        var blob = Aead.Seal(Key, Plain, Aad);
        Assert.Equal(Plain, Aead.Open(Key, blob, Aad));
    }

    [Fact]
    public void Blob_is_nonce_plus_ciphertext_plus_tag()
    {
        var blob = Aead.Seal(Key, Plain, Aad);
        Assert.Equal(Aead.NonceSize + Plain.Length + Aead.TagSize, blob.Length);
    }

    [Fact]
    public void Same_input_twice_gives_different_bytes()
    {
        // If this ever fails, the nonce is not random and two saved vaults leak each other.
        Assert.NotEqual(Aead.Seal(Key, Plain, Aad), Aead.Seal(Key, Plain, Aad));
    }

    [Fact]
    public void Wrong_key_is_rejected()
    {
        var blob = Aead.Seal(Key, Plain, Aad);
        Assert.ThrowsAny<CryptographicException>(() => Aead.Open(RandomNumberGenerator.GetBytes(32), blob, Aad));
    }

    [Fact]
    public void Wrong_aad_is_rejected()
    {
        var blob = Aead.Seal(Key, Plain, Aad);
        Assert.ThrowsAny<CryptographicException>(() => Aead.Open(Key, blob, "pv1:something-else"u8.ToArray()));
    }

    [Fact]
    public void Changing_one_bit_anywhere_is_rejected()
    {
        var blob = Aead.Seal(Key, Plain, Aad);
        for (int i = 0; i < blob.Length; i++)
        {
            var tampered = (byte[])blob.Clone();
            tampered[i] ^= 0x01;
            Assert.ThrowsAny<CryptographicException>(() => Aead.Open(Key, tampered, Aad));
        }
    }

    [Fact]
    public void A_truncated_blob_gives_a_clear_error()
    {
        Assert.ThrowsAny<CryptographicException>(() => Aead.Open(Key, new byte[10], Aad));
    }

    [Fact]
    public void Wrong_key_size_is_refused_up_front()
    {
        Assert.Throws<ArgumentException>(() => Aead.Seal(new byte[16], Plain, Aad));
    }

    [Fact]
    public void Empty_plaintext_works()
    {
        var blob = Aead.Seal(Key, [], Aad);
        Assert.Empty(Aead.Open(Key, blob, Aad));
    }
}
