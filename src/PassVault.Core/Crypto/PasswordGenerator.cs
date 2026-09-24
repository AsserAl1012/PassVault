using System.Security.Cryptography;

namespace PassVault.Core.Crypto;

/// <summary>
/// Makes new passwords: 20 characters from 69, about 120 bits. Far past anything that can be
/// guessed, and still short enough for the sites that cap the length.
/// </summary>
public static class PasswordGenerator
{
    public const int Length = 20;

    // Characters that look alike are left out - no 0/O, no 1/l/I - because sooner or later
    // someone reads one of these off the screen and types it into a phone.
    public const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    public const string Lower = "abcdefghijkmnopqrstuvwxyz";
    public const string Digits = "23456789";

    // Symbols almost every site accepts. Spaces, quotes, backslashes and angle brackets are
    // the ones sites most often reject or quietly mangle.
    public const string Symbols = "!#$%&*+-=?@_";

    public static string Make()
    {
        string[] groups = [Upper, Lower, Digits, Symbols];
        var all = string.Concat(groups);

        // One from every group first, because many sites insist on it. The rest come from
        // everything, then the whole lot is shuffled so those four are not always in front.
        var chars = new char[Length];
        for (int i = 0; i < groups.Length; i++) chars[i] = Pick(groups[i]);
        for (int i = groups.Length; i < Length; i++) chars[i] = Pick(all);
        RandomNumberGenerator.Shuffle(chars.AsSpan());

        return new string(chars);
    }

    /// <summary>GetInt32 is unbiased - a plain random byte modulo the length would favour the early characters.</summary>
    private static char Pick(string from) => from[RandomNumberGenerator.GetInt32(from.Length)];
}
