using System.Security.Cryptography;
using System.Text;

namespace PassVault.Core.Licensing;

/// <summary>Who owns this copy.</summary>
public sealed record Licence(string Name, string Id, DateOnly Issued, string Edition)
{
    /// <summary>The exact bytes that get signed. Both sides must build this identically.</summary>
    public string Canonical() =>
        $"name: {Name}\nid: {Id}\nissued: {Issued:yyyy-MM-dd}\nedition: {Edition}";
}

/// <summary>
/// Reading and checking a licence key.
///
/// The check happens entirely on this machine, against a public key built into the app. There
/// is no server to contact, no list to be on, and nothing anybody can switch off afterwards -
/// including whoever sold it. That is the whole point: you bought it, so it is yours, and it
/// keeps working even if the person who wrote it disappears.
///
/// It is signed, not copy-protected. A determined person could get around it in an afternoon,
/// and that is deliberate. It exists to be clear about what is yours, not to fight you.
/// </summary>
public static class LicenceKey
{
    public const string Begin = "-----BEGIN PASSVAULT LICENCE-----";
    public const string End = "-----END PASSVAULT LICENCE-----";

    /// <summary>
    /// The public half of the signing key, as base64 SubjectPublicKeyInfo. The private half
    /// lives only on the developer's machine and is never shipped.
    ///
    /// Generate a pair with:  dotnet run --project tools/LicenceMaker -- keygen
    /// </summary>
    public const string PublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEYhgIxG7stOA+LXc4hnnQFo+abWmZbECCClB85ijQHMEfAULJ8gbXXG3ft3vH1xRKrhEjtbSl6V2nJ1GNBegrJg==";

    /// <summary>
    /// Check a pasted licence. Returns null if it is not valid - never throws over bad input,
    /// because a mistyped licence is an everyday thing, not an error.
    /// </summary>
    public static Licence? Verify(string? text, string? publicKey = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var (licence, signature) = Parse(text);
        if (licence is null || signature is null) return null;

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey ?? PublicKey), out _);

            return ecdsa.VerifyData(Encoding.UTF8.GetBytes(licence.Canonical()), signature, HashAlgorithmName.SHA256)
                ? licence
                : null;
        }
        catch (Exception)
        {
            // A damaged key, a placeholder public key, anything at all: not licensed.
            return null;
        }
    }

    /// <summary>Wrap a licence and its signature into the block a buyer pastes in.</summary>
    public static string Write(Licence licence, byte[] signature) =>
        $"""
        {Begin}
        {licence.Canonical()}
        sig: {Convert.ToBase64String(signature)}
        {End}
        """;

    private static (Licence? Licence, byte[]? Signature) Parse(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("-----")) continue;

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        if (!fields.TryGetValue("name", out var name)
            || !fields.TryGetValue("id", out var id)
            || !fields.TryGetValue("issued", out var issued)
            || !fields.TryGetValue("edition", out var edition)
            || !fields.TryGetValue("sig", out var sig))
            return (null, null);

        if (!DateOnly.TryParse(issued, System.Globalization.CultureInfo.InvariantCulture, out var issuedOn))
            return (null, null);

        byte[] signature;
        try { signature = Convert.FromBase64String(sig); }
        catch (FormatException) { return (null, null); }

        return (new Licence(name, id, issuedOn, edition), signature);
    }
}

/// <summary>Where the licence lives on this machine, and whether there is one.</summary>
public static class LicenceStore
{
    public const string FileName = "licence.txt";

    public static string PathIn(string folder) => Path.Combine(folder, FileName);

    /// <summary>The licence on this machine, or null if this is the free version.</summary>
    public static Licence? Load(string folder, string? publicKey = null)
    {
        var path = PathIn(folder);
        if (!File.Exists(path)) return null;

        try { return LicenceKey.Verify(File.ReadAllText(path), publicKey); }
        catch (IOException) { return null; }
    }

    /// <summary>Save a pasted licence, but only if it actually checks out.</summary>
    public static Licence? Save(string folder, string text, string? publicKey = null)
    {
        var licence = LicenceKey.Verify(text, publicKey);
        if (licence is null) return null;

        Directory.CreateDirectory(folder);
        File.WriteAllText(PathIn(folder), text.Trim());
        return licence;
    }

    public static void Remove(string folder)
    {
        var path = PathIn(folder);
        if (File.Exists(path)) File.Delete(path);
    }
}
