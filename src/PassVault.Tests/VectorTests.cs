using System.Text;
using System.Text.Json;
using PassVault.Core.Crypto;

namespace PassVault.Tests;

/// <summary>
/// These read tests/vectors.json - the same file the Swift tests will read. They are what
/// catches the two apps drifting apart, which you would otherwise only find out during
/// someone's real recovery.
/// </summary>
public class VectorTests
{
    private static readonly JsonElement V = LoadVectors();

    private static JsonElement LoadVectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vectors.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static byte[] Hex(string h) => Convert.FromHexString(h);

    [Fact]
    public void Kdf_settings_still_match_the_frozen_ones()
    {
        var k = V.GetProperty("kdf");
        var d = KdfSettings.Default;

        Assert.Equal(k.GetProperty("algo").GetString(), d.Algo);
        Assert.Equal(k.GetProperty("iterations").GetInt32(), d.Iterations);
        Assert.Equal(k.GetProperty("memoryKiB").GetInt32(), d.MemoryKiB);
        Assert.Equal(k.GetProperty("parallelism").GetInt32(), d.Parallelism);
        Assert.Equal(k.GetProperty("outputBytes").GetInt32(), d.OutputBytes);

        // The number the iOS side must pass to libsodium, which wants bytes not KiB.
        Assert.Equal(k.GetProperty("memoryBytesForLibsodium").GetInt64(), d.MemoryKiB * 1024L);
    }

    [Fact]
    public void Official_bip39_vectors_still_pass()
    {
        foreach (var v in V.GetProperty("mnemonic").EnumerateArray())
        {
            var entropy = Hex(v.GetProperty("entropyHex").GetString()!);
            var phrase = v.GetProperty("phrase").GetString()!;

            Assert.Equal(phrase, Mnemonic.FromEntropy(entropy));
            Assert.True(Mnemonic.TryParse(phrase, out var back, out var error), error);
            Assert.Equal(entropy, back);
        }
    }

    [Fact]
    public void Phrase_is_cleaned_up_the_same_way_on_both_platforms()
    {
        foreach (var v in V.GetProperty("normalise").EnumerateArray())
        {
            Assert.Equal(
                v.GetProperty("output").GetString(),
                Mnemonic.Normalise(v.GetProperty("input").GetString()!));
        }
    }

    [Fact]
    public void Argon2id_gives_the_same_key_as_the_frozen_vector()
    {
        var a = V.GetProperty("argon2id");
        var key = Kdf.Derive(Hex(a.GetProperty("secretHex").GetString()!),
                             Hex(a.GetProperty("saltHex").GetString()!),
                             KdfSettings.Default);

        Assert.Equal(a.GetProperty("expectedKeyHex").GetString(), Convert.ToHexString(key).ToLowerInvariant());
    }

    [Fact]
    public void A_blob_sealed_by_the_frozen_vector_still_opens()
    {
        var a = V.GetProperty("aead");
        var plaintext = Aead.Open(
            Hex(a.GetProperty("keyHex").GetString()!),
            Hex(a.GetProperty("blobHex").GetString()!),
            Encoding.UTF8.GetBytes(a.GetProperty("aadUtf8").GetString()!));

        Assert.Equal(a.GetProperty("plaintextUtf8").GetString(), Encoding.UTF8.GetString(plaintext));
    }

    [Fact]
    public void Pin_input_and_key_match_the_frozen_vector()
    {
        var p = V.GetProperty("pinSlot");
        var input = KeyWrap.PinInput(
            Hex(p.GetProperty("usbSecretHex").GetString()!),
            Hex(p.GetProperty("deviceSecretHex").GetString()!),
            p.GetProperty("pin").GetString()!);

        Assert.Equal(p.GetProperty("pinInputHex").GetString(), Convert.ToHexString(input).ToLowerInvariant());

        var key = Kdf.Derive(input, Hex(p.GetProperty("saltHex").GetString()!), KdfSettings.Default);
        Assert.Equal(p.GetProperty("expectedKeyHex").GetString(), Convert.ToHexString(key).ToLowerInvariant());
    }
}
