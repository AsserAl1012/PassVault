using PassVault.Core.Crypto;

namespace PassVault.Tests;

public class PasswordGeneratorTests
{
    private const string Allowed =
        PasswordGenerator.Upper + PasswordGenerator.Lower + PasswordGenerator.Digits + PasswordGenerator.Symbols;

    private static readonly List<string> Many = Enumerable.Range(0, 2000).Select(_ => PasswordGenerator.Make()).ToList();

    [Fact]
    public void Every_password_is_twenty_characters()
        => Assert.All(Many, p => Assert.Equal(20, p.Length));

    [Fact]
    public void Only_uses_characters_that_are_easy_to_read_and_that_sites_accept()
    {
        Assert.All(Many, p => Assert.All(p, c => Assert.Contains(c, Allowed)));

        foreach (var avoided in "0O1lI \"'`\\<>")
            Assert.DoesNotContain(avoided, Allowed);
    }

    [Fact]
    public void Every_password_has_a_capital_a_small_letter_a_digit_and_a_symbol()
        => Assert.All(Many, p =>
        {
            Assert.Contains(p, c => PasswordGenerator.Upper.Contains(c));
            Assert.Contains(p, c => PasswordGenerator.Lower.Contains(c));
            Assert.Contains(p, c => PasswordGenerator.Digits.Contains(c));
            Assert.Contains(p, c => PasswordGenerator.Symbols.Contains(c));
        });

    [Fact]
    public void No_two_are_the_same()
        => Assert.Equal(Many.Count, Many.Distinct().Count());

    /// <summary>
    /// Catches a pick that never reaches the end of the alphabet. With 40,000 characters drawn
    /// from 69, one that never turns up would mean something is wrong, not bad luck.
    /// </summary>
    [Fact]
    public void Every_allowed_character_turns_up()
    {
        var seen = new HashSet<char>(Many.SelectMany(p => p));
        Assert.All(Allowed, c => Assert.Contains(c, seen));
    }

    /// <summary>
    /// The four guaranteed characters go in first and are then shuffled. If the shuffle broke,
    /// the first character would always be a capital.
    /// </summary>
    [Fact]
    public void The_guaranteed_characters_are_not_stuck_at_the_front()
        => Assert.Contains(Many, p => !PasswordGenerator.Upper.Contains(p[0]));
}
