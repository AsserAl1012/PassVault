using PassVault.Core.Model;
using PassVault.Core.Usb;
using PassVault.Core.Vault;

// A small console tool to drive the core while there is no window yet.
// The PIN is not here - that arrives in step 3, along with the machine file and the TPM.
// For now the only way in is the 12-word phrase.

return (args.Length == 0 ? "help" : args[0].ToLowerInvariant()) switch
{
    "find" => Find(),
    "init" => Init(Drive(args)),
    "status" => Status(Drive(args)),
    "list" => List(Drive(args)),
    "add" => Add(Drive(args)),
    _ => Help(),
};

static int Help()
{
    Console.WriteLine("""
        PassVault - step 2 (no window yet)

          find              show every USB stick Windows can read
          init   <drive>    set up a fresh stick, prints your 12 words
          status <drive>    what is on that stick
          list   <drive>    unlock with your 12 words and show your credentials
          add    <drive>    unlock and add one credential

        Example:  PassVault.Cli init E:\
        """);
    return 0;
}

static int Find()
{
    var sticks = UsbDetect.Find();
    if (sticks.Count == 0)
    {
        Console.WriteLine("No USB stick found that Windows can read.");
        Console.WriteLine("If one is plugged in, Windows may not understand its filesystem");
        Console.WriteLine("(a Linux drive, for example) - it gets no drive letter and cannot be used.");
        return 1;
    }

    foreach (var stick in sticks)
        Console.WriteLine($"  {stick}{(stick.IsSetUp ? "   <- PassVault is set up here" : "")}");

    return 0;
}

static int Init(string root)
{
    if (VaultStorage.IsSetUp(root))
    {
        Console.WriteLine($"{root} is already set up. Setting it up again would throw away the vault on it.");
        return 1;
    }

    var (phrase, keyfile) = VaultStorage.Initialise(root);

    Console.WriteLine($"Set up {root}  (stick {keyfile.ShortId})\n");
    Console.WriteLine("YOUR 12-WORD RECOVERY PHRASE - write it down now, on paper:\n");

    var words = phrase.Split(' ');
    for (int i = 0; i < words.Length; i += 4)
        Console.WriteLine("   " + string.Join("  ", words.Skip(i).Take(4).Select((w, j) => $"{i + j + 1,2}. {w,-9}")));

    Console.WriteLine("""

        This is shown once and is not stored anywhere. It is the only way back in
        if the stick is lost or broken. Anyone who reads it owns your passwords.
        """);
    return 0;
}

static int Status(string root)
{
    if (!VaultStorage.IsSetUp(root))
    {
        Console.WriteLine($"{root} has no PassVault on it. Run:  PassVault.Cli init {root}");
        return 1;
    }

    var keyfile = VaultStorage.ReadKeyfile(root);
    var header = VaultFile.ReadHeader(VaultStorage.ReadVault(root));

    Console.WriteLine($"  stick      {keyfile.ShortId}");
    Console.WriteLine($"  vault      {header.VaultId}");
    Console.WriteLine($"  epoch      {header.Epoch}   (goes up when you re-key after a theft)");
    Console.WriteLine($"  revision   {header.Revision}   (goes up on every save)");
    Console.WriteLine($"  ways in    {string.Join(", ", header.Slots.Select(s => s.Name))}");
    return 0;
}

static int List(string root)
{
    var (vault, masterKey) = Unlock(root);
    var entries = VaultFile.ReadEntries(vault, masterKey).Where(e => !e.Deleted).ToList();

    if (entries.Count == 0)
    {
        Console.WriteLine("\nNothing saved yet.");
        return 0;
    }

    Console.WriteLine();
    foreach (var e in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"  {e.Name}");
        if (!string.IsNullOrWhiteSpace(e.Username)) Console.WriteLine($"      username  {e.Username}");
        if (!string.IsNullOrWhiteSpace(e.Email)) Console.WriteLine($"      email     {e.Email}");
        if (!string.IsNullOrWhiteSpace(e.Password)) Console.WriteLine($"      password  {e.Password}");
        if (!string.IsNullOrWhiteSpace(e.Notes)) Console.WriteLine($"      notes     {e.Notes}");
    }
    return 0;
}

static int Add(string root)
{
    var (vault, masterKey) = Unlock(root);
    var entries = VaultFile.ReadEntries(vault, masterKey);

    Console.WriteLine();
    var entry = new Entry
    {
        Name = Required("Website or app"),
        Username = Optional("Username"),
        Email = Optional("Email"),
        Password = Optional("Password"),
        Notes = Optional("Notes"),
    };
    entries.Add(entry);

    VaultStorage.WriteVault(root, VaultFile.Save(vault, masterKey, entries));
    Console.WriteLine($"\nSaved. {entries.Count(e => !e.Deleted)} credential(s) on this stick.");
    return 0;
}

// ---- helpers ----

static (byte[] Vault, byte[] MasterKey) Unlock(string root)
{
    if (!VaultStorage.IsSetUp(root))
        throw new InvalidOperationException($"{root} has no PassVault on it.");

    var vault = VaultStorage.ReadVault(root);

    Console.Write("Your 12 words: ");
    var phrase = Console.ReadLine() ?? "";

    return (vault, VaultFile.UnlockWithPhrase(vault, phrase));
}

static string Drive(string[] args)
    => args.Length > 1 ? args[1] : throw new ArgumentException("Which drive? For example:  E:\\");

static string Required(string label)
{
    while (true)
    {
        Console.Write($"  {label}: ");
        var value = Console.ReadLine();
        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
    }
}

static string? Optional(string label)
{
    Console.Write($"  {label} (leave blank to skip): ");
    var value = Console.ReadLine();
    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
