using PassVault.Core.Crypto;

namespace PassVault.Tests;

public class MnemonicTests
{
    [Fact]
    public void Generated_phrase_is_twelve_real_words()
    {
        var words = Mnemonic.Generate().Split(' ');

        Assert.Equal(12, words.Length);
        Assert.All(words, w => Assert.Contains(w, Mnemonic.WordList));
    }

    [Fact]
    public void Two_generated_phrases_are_different()
    {
        Assert.NotEqual(Mnemonic.Generate(), Mnemonic.Generate());
    }

    [Fact]
    public void Phrase_survives_a_round_trip()
    {
        var entropy = Convert.FromHexString("0f1e2d3c4b5a69788796a5b4c3d2e1f0");
        var phrase = Mnemonic.FromEntropy(entropy);

        Assert.True(Mnemonic.TryParse(phrase, out var back, out var error), error);
        Assert.Equal(entropy, back);
    }

    [Fact]
    public void A_made_up_word_is_reported_by_position()
    {
        var phrase = Mnemonic.Generate();
        var words = phrase.Split(' ');
        words[6] = "banananana";

        Assert.False(Mnemonic.TryParse(string.Join(' ', words), out _, out var error));
        Assert.Contains("Word 7", error);
        Assert.Contains("banananana", error);
    }

    [Fact]
    public void A_real_word_in_the_wrong_place_fails_the_checksum()
    {
        // Every word is in the list, so only the checksum can catch this.
        var words = Mnemonic.FromEntropy(new byte[16]).Split(' ');
        words[3] = words[3] == "abandon" ? "ability" : "abandon";

        Assert.False(Mnemonic.TryParse(string.Join(' ', words), out _, out var error));
        Assert.Contains("don't add up", error);
    }

    [Fact]
    public void Wrong_number_of_words_says_so()
    {
        Assert.False(Mnemonic.TryParse("abandon abandon about", out _, out var error));
        Assert.Contains("12 words", error);
        Assert.Contains("You typed 3", error);
    }

    [Theory]
    [InlineData("  ABANDON   abandon\tAbout ", "abandon abandon about")]
    [InlineData("Zoo\r\nzoo  ZOO", "zoo zoo zoo")]
    [InlineData("legal winner thank", "legal winner thank")]
    public void Messy_typing_is_cleaned_up_the_same_way_every_time(string input, string expected)
    {
        Assert.Equal(expected, Mnemonic.Normalise(input));
    }

    [Fact]
    public void Messy_typing_still_opens_the_vault()
    {
        var phrase = Mnemonic.Generate();
        var messy = "  " + phrase.ToUpperInvariant().Replace(" ", "   ") + "\t";

        Assert.True(Mnemonic.TryParse(messy, out _, out var error), error);
    }

    [Fact]
    public void Only_standard_word_counts_are_allowed()
    {
        Assert.Throws<ArgumentException>(() => Mnemonic.Generate(10));
        Assert.Throws<ArgumentException>(() => Mnemonic.Generate(13));
    }
}
