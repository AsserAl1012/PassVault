using System.Security.Cryptography;
using System.Text;
using PassVault.Core.Licensing;

// Issues PassVault licences. This is the seller's tool - it is never shipped to anyone.
//
//   dotnet run --project tools/LicenceMaker -- keygen
//   dotnet run --project tools/LicenceMaker -- issue "Asser Moemen"
//   dotnet run --project tools/LicenceMaker -- check licence.txt
//
// The private key it writes is the one thing in this project that must never be shared or
// committed. Anyone holding it can mint licences. It is written outside the repository for
// that reason, and .gitignore covers the filename too.

var keyPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".passvault-signing-key");

return (args.Length == 0 ? "help" : args[0].ToLowerInvariant()) switch
{
    "keygen" => Keygen(),
    "issue" => Issue(args.Length > 1 ? args[1] : ""),
    "check" => Check(args.Length > 1 ? args[1] : ""),
    _ => Help(),
};

int Help()
{
    Console.WriteLine("""
        PassVault licence maker - the seller's tool, never shipped

          keygen              make the signing key pair, once, and print the public half
          issue "<name>"      make a licence for somebody
          check <file>        verify a licence the way the app would

        The private key lives at ~/.passvault-signing-key and must never be shared.
        """);
    return 0;
}

int Keygen()
{
    if (File.Exists(keyPath))
    {
        Console.WriteLine($"A signing key already exists at {keyPath}");
        Console.WriteLine("Delete it only if you are certain - every licence you have ever issued");
        Console.WriteLine("stops verifying the moment you replace it.");
        return 1;
    }

    using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    File.WriteAllText(keyPath, Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()));
    try { File.SetAttributes(keyPath, FileAttributes.Hidden); } catch (IOException) { }

    Console.WriteLine($"Private key written to {keyPath}");
    Console.WriteLine("Back it up somewhere safe and never share it.\n");
    Console.WriteLine("Now paste this public key into LicenceKey.PublicKey in");
    Console.WriteLine("src/PassVault.Core/Licensing/Licence.cs:\n");
    Console.WriteLine(Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
    return 0;
}

int Issue(string name)
{
    if (string.IsNullOrWhiteSpace(name))
    {
        Console.WriteLine("Who is it for?  issue \"Asser Moemen\"");
        return 1;
    }
    if (!File.Exists(keyPath))
    {
        Console.WriteLine("No signing key yet. Run:  keygen");
        return 1;
    }

    using var ecdsa = ECDsa.Create();
    ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(File.ReadAllText(keyPath)), out _);

    var licence = new Licence(
        Name: name.Trim(),
        Id: NewId(),
        Issued: DateOnly.FromDateTime(DateTime.Now),
        Edition: "owner");

    var signature = ecdsa.SignData(Encoding.UTF8.GetBytes(licence.Canonical()), HashAlgorithmName.SHA256);
    var block = LicenceKey.Write(licence, signature);

    Console.WriteLine(block);
    Console.WriteLine($"\nSend that to {licence.Name}. It never expires and never phones home.");
    return 0;
}

int Check(string path)
{
    if (!File.Exists(path))
    {
        Console.WriteLine($"No file at {path}");
        return 1;
    }

    var licence = LicenceKey.Verify(File.ReadAllText(path));
    if (licence is null)
    {
        Console.WriteLine("Not a valid licence for the public key built into this app.");
        return 1;
    }

    Console.WriteLine($"Valid.  {licence.Name}  ·  {licence.Id}  ·  issued {licence.Issued:d MMMM yyyy}");
    return 0;
}

// Readable, unambiguous: no letters that can be mistaken for digits.
static string NewId()
{
    const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    var bytes = RandomNumberGenerator.GetBytes(12);

    var id = new StringBuilder();
    for (int i = 0; i < 12; i++)
    {
        if (i > 0 && i % 4 == 0) id.Append('-');
        id.Append(alphabet[bytes[i] % alphabet.Length]);
    }
    return id.ToString();
}
