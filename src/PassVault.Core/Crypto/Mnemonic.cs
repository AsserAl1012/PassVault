using System.Security.Cryptography;
using System.Text;

namespace PassVault.Core.Crypto;

/// <summary>
/// The 12-word recovery phrase. Standard BIP-39, English word list.
/// The app always generates the phrase - the user never picks their own words.
/// </summary>
public static class Mnemonic
{
    public const int DefaultWordCount = 12;

    private static readonly int[] AllowedWordCounts = [12, 15, 18, 21, 24];
    private static readonly string[] Words = LoadWordList();

    /// <summary>The 2048 words, for a "did you mean..." box in the app later.</summary>
    public static IReadOnlyList<string> WordList => Words;

    /// <summary>Make a fresh phrase from the system random number generator.</summary>
    public static string Generate(int wordCount = DefaultWordCount)
        => FromEntropy(RandomNumberGenerator.GetBytes(EntropyBytesFor(wordCount)));

    /// <summary>Turn raw entropy into words. Used by the tests to check against known vectors.</summary>
    public static string FromEntropy(byte[] entropy)
    {
        if (entropy.Length % 4 != 0 || entropy.Length < 16 || entropy.Length > 32)
            throw new ArgumentException("Entropy must be 16, 20, 24, 28 or 32 bytes.", nameof(entropy));

        var hash = SHA256.HashData(entropy);
        int entropyBits = entropy.Length * 8;
        int totalBits = entropyBits + entropyBits / 32;

        bool BitAt(int i) => i < entropyBits
            ? ((entropy[i / 8] >> (7 - i % 8)) & 1) == 1
            : ((hash[(i - entropyBits) / 8] >> (7 - (i - entropyBits) % 8)) & 1) == 1;

        var words = new List<string>(totalBits / 11);
        for (int i = 0; i < totalBits; i += 11)
        {
            int index = 0;
            for (int b = 0; b < 11; b++) index = (index << 1) | (BitAt(i + b) ? 1 : 0);
            words.Add(Words[index]);
        }
        return string.Join(' ', words);
    }

    /// <summary>
    /// Check a typed phrase. On failure <paramref name="error"/> says exactly what is wrong,
    /// so the user can fix a typo instead of giving up on a vault that was fine.
    /// </summary>
    public static bool TryParse(string phrase, out byte[] entropy, out string error)
    {
        entropy = [];
        error = "";

        var words = Normalise(phrase).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!AllowedWordCounts.Contains(words.Length))
        {
            error = $"A recovery phrase is {DefaultWordCount} words. You typed {words.Length}.";
            return false;
        }

        var indices = new int[words.Length];
        for (int i = 0; i < words.Length; i++)
        {
            int index = Array.IndexOf(Words, words[i]);
            if (index < 0)
            {
                error = $"Word {i + 1} (\"{words[i]}\") is not one of the 2048 words.";
                return false;
            }
            indices[i] = index;
        }

        int totalBits = words.Length * 11;
        int checksumBits = totalBits / 33;
        int entropyBits = totalBits - checksumBits;

        var bits = new bool[totalBits];
        for (int i = 0; i < indices.Length; i++)
            for (int b = 0; b < 11; b++)
                bits[i * 11 + b] = ((indices[i] >> (10 - b)) & 1) == 1;

        var result = new byte[entropyBits / 8];
        for (int i = 0; i < entropyBits; i++)
            if (bits[i]) result[i / 8] |= (byte)(1 << (7 - i % 8));

        var hash = SHA256.HashData(result);
        for (int i = 0; i < checksumBits; i++)
        {
            bool expected = ((hash[i / 8] >> (7 - i % 8)) & 1) == 1;
            if (bits[entropyBits + i] != expected)
            {
                error = "Every word is real, but they don't add up - one of them is the wrong word.";
                return false;
            }
        }

        entropy = result;
        return true;
    }

    /// <summary>
    /// The exact text the key is derived from: lowercase, single spaces, no leading or
    /// trailing space, NFKD. The iOS app must produce byte-identical output here.
    /// </summary>
    public static string Normalise(string phrase)
    {
        var parts = phrase.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts).Normalize(NormalizationForm.FormKD);
    }

    private static int EntropyBytesFor(int wordCount)
    {
        if (!AllowedWordCounts.Contains(wordCount))
            throw new ArgumentException("Word count must be 12, 15, 18, 21 or 24.", nameof(wordCount));
        return wordCount * 11 / 33 * 4;
    }

    private static string[] LoadWordList()
    {
        using var stream = typeof(Mnemonic).Assembly
            .GetManifestResourceStream("PassVault.Core.Resources.english.txt")
            ?? throw new InvalidOperationException("The BIP-39 word list is missing from the build.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var words = reader.ReadToEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim())
            .Where(w => w.Length > 0)
            .ToArray();

        if (words.Length != 2048)
            throw new InvalidOperationException($"The word list should have 2048 words, it has {words.Length}.");

        return words;
    }
}
