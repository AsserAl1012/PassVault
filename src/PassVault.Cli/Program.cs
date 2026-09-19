using PassVault.Core.Machine;
using PassVault.Core.Model;
using PassVault.Core.Usb;
using PassVault.Core.Vault;

// A small console tool to drive the core while there is no window yet.

try
{
    return (args.Length == 0 ? "help" : args[0].ToLowerInvariant()) switch
    {
        "find" => Find(),
        "init" => Init(Drive(args)),
        "enrol" or "enroll" => Enrol(Drive(args)),
        "status" => Status(Drive(args)),
        "list" => List(Drive(args)),
        "add" => Add(Drive(args)),
        "forget" => Forget(),
        _ => Help(),
    };
}
catch (UnlockException ex)
{
    Console.WriteLine($"\n{ex.Message}");
    return 1;
}
catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or InvalidDataException or ArgumentException)
{
    Console.WriteLine($"\n{ex.Message}");
    return 1;
}

static int Help()
{
    Console.WriteLine("""
        PassVault - steps 1 to 3 (no window yet)

          find              show every USB stick Windows can read
          init   <drive>    set up a fresh stick, prints your 12 words
          enrol  <drive>    set this PC up: 12 words once, then choose a PIN
          status <drive>    what is on that stick, and how this PC is set up
          list   <drive>    open the vault and show your credentials
          add    <drive>    open the vault and add one credential
          forget            undo this PC's setup (the vault is untouched)

        Once this PC is set up, list and add ask for your PIN.
        Before that, and any time the PIN is locked out, they ask for your 12 words.

        Example:  PassVault.Cli enrol "E:\"
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
    PrintPhrase(phrase);
    Console.WriteLine("""

        This is shown once and is not stored anywhere. It is the only way back in
        if the stick is lost or broken. Anyone who reads it owns your passwords.

        Next:  PassVault.Cli enrol <drive>
        """);
    return 0;
}

static int Enrol(string root)
{
    var folder = MachineStore.DefaultFolder;

    if (MachineStore.IsEnrolled(folder))
    {
        Console.WriteLine("This PC is already set up. Run 'forget' first if you want to start again.");
        return 1;
    }
    if (!VaultStorage.IsSetUp(root))
    {
        Console.WriteLine($"{root} has no PassVault on it. Run:  PassVault.Cli init {root}");
        return 1;
    }

    Console.Write("Your 12 words: ");
    var phrase = Console.ReadLine() ?? "";

    var pin = NewPin();

    var (deviceSecret, protection) = DeviceSecret.Load(folder);
    var (machine, _) = Enrolment.Enrol(folder, root, phrase, pin, deviceSecret, protection);

    Console.WriteLine($"\nThis PC is set up. From now on: stick in, PIN, done.");
    Console.WriteLine($"  machine secret   {Describe(machine.Protection)}");
    Console.WriteLine($"  spare vault      {MachineStore.MirrorVaultPathIn(folder)}");

    if (machine.Protection == DeviceProtection.Dpapi)
        Console.WriteLine("""

            Heads up: no usable TPM was found, so this PC's secret is tied to your Windows
            account instead of the chip. That is weaker - someone who can log in as you can
            get it. Everything still works; it just leans more on the PIN.
            """);

    return 0;
}

static int Status(string root)
{
    if (VaultStorage.IsSetUp(root))
    {
        var keyfile = VaultStorage.ReadKeyfile(root);
        var header = VaultFile.ReadHeader(VaultStorage.ReadVault(root));

        Console.WriteLine("STICK");
        Console.WriteLine($"  stick      {keyfile.ShortId}");
        Console.WriteLine($"  vault      {header.VaultId}");
        Console.WriteLine($"  epoch      {header.Epoch}   (goes up when you re-key after a theft)");
        Console.WriteLine($"  revision   {header.Revision}   (goes up on every save)");
        Console.WriteLine($"  ways in    {string.Join(", ", header.Slots.Select(s => s.Name))}");
    }
    else
    {
        Console.WriteLine($"STICK\n  {root} has no PassVault on it.");
    }

    var folder = MachineStore.DefaultFolder;
    Console.WriteLine("\nTHIS PC");

    if (!MachineStore.IsEnrolled(folder))
    {
        Console.WriteLine("  not set up. Run:  PassVault.Cli enrol <drive>");
        return 0;
    }

    var machine = MachineStore.Read(folder);
    Console.WriteLine($"  set up for      vault {machine.VaultId}, stick {machine.StickShortId}");
    Console.WriteLine($"  machine secret  {Describe(machine.Protection)}");
    Console.WriteLine($"  PIN             {(machine.PinDisabled ? "switched off - use your 12 words" : "on")}");
    Console.WriteLine($"  wrong tries     {machine.FailedAttempts} of {Enrolment.MaxAttempts}");
    Console.WriteLine($"  last seen       epoch {machine.HighestEpochSeen}, revision {machine.HighestRevisionSeen}");
    Console.WriteLine($"  spare vault     {(MachineStore.HasMirrorVault(folder) ? "yes" : "MISSING")}");
    return 0;
}

static int List(string root)
{
    var (vault, masterKey) = Open(root);
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
    var (vault, masterKey) = Open(root);
    var entries = VaultFile.ReadEntries(vault, masterKey);

    Console.WriteLine();
    entries.Add(new Entry
    {
        Name = Required("Website or app"),
        Username = Optional("Username"),
        Email = Optional("Email"),
        Password = Optional("Password"),
        Notes = Optional("Notes"),
    });

    var saved = VaultFile.Save(vault, masterKey, entries);
    Enrolment.SaveVault(MachineStore.DefaultFolder, root, saved);

    Console.WriteLine($"\nSaved. {entries.Count(e => !e.Deleted)} credential(s), on the stick and in the spare copy here.");
    return 0;
}

static int Forget()
{
    var folder = MachineStore.DefaultFolder;
    if (!MachineStore.IsEnrolled(folder))
    {
        Console.WriteLine("This PC is not set up.");
        return 0;
    }

    Console.Write("This undoes the PIN setup on this PC. The vault on your stick is untouched. Type YES: ");
    if (Console.ReadLine()?.Trim() != "YES")
    {
        Console.WriteLine("Left alone.");
        return 1;
    }

    MachineStore.Forget(folder);
    DeviceSecret.Forget(folder);
    Console.WriteLine("Done. Set this PC up again whenever you like, with your 12 words.");

    // Deliberately kept: it is a backup, and "undo my PIN" must never throw away a backup.
    if (MachineStore.HasMirrorVault(folder))
        Console.WriteLine($"\nThe spare copy of your vault is still here, and still needs your 12 words:\n  {MachineStore.MirrorVaultPathIn(folder)}");

    return 0;
}

// ---- opening the vault ----

static (byte[] Vault, byte[] MasterKey) Open(string root)
{
    var folder = MachineStore.DefaultFolder;

    if (MachineStore.IsEnrolled(folder) && !MachineStore.Read(folder).PinDisabled)
    {
        var wait = Enrolment.DelayBeforeNextTry(MachineStore.Read(folder).FailedAttempts);
        if (wait > TimeSpan.Zero)
        {
            Console.WriteLine($"Waiting {wait.TotalSeconds:0} seconds after earlier wrong tries...");
            Thread.Sleep(wait);
        }

        var (deviceSecret, _) = DeviceSecret.Load(folder);
        var pin = AskPin("PIN");
        var (masterKey, _) = Enrolment.UnlockWithPin(folder, root, pin, deviceSecret);
        return (VaultStorage.ReadVault(root), masterKey);
    }

    Console.WriteLine("This PC has no working PIN, so we need your 12 words.");
    Console.Write("Your 12 words: ");
    var phrase = Console.ReadLine() ?? "";
    return (VaultStorage.ReadVault(root), Enrolment.UnlockWithPhrase(folder, root, phrase));
}

// ---- little helpers ----

static string Describe(DeviceProtection protection) => protection switch
{
    DeviceProtection.Tpm => "sealed in this PC's TPM chip - it cannot leave this machine",
    _ => "tied to your Windows account (no TPM found) - weaker",
};

static void PrintPhrase(string phrase)
{
    var words = phrase.Split(' ');
    for (int i = 0; i < words.Length; i += 4)
        Console.WriteLine("   " + string.Join("  ", words.Skip(i).Take(4).Select((w, j) => $"{i + j + 1,2}. {w,-9}")));
}

static string NewPin()
{
    while (true)
    {
        var first = AskPin("Choose a 4-digit PIN");
        var again = AskPin("Type it again");
        if (first == again) return first;
        Console.WriteLine("  Those did not match. Try again.");
    }
}

static string AskPin(string label)
{
    while (true)
    {
        Console.Write($"{label}: ");
        var pin = ReadHidden();
        if (pin.Length == 4 && pin.All(char.IsAsciiDigit)) return pin;
        Console.WriteLine("  A PIN is exactly 4 digits.");
    }
}

// Hide the PIN as it is typed, unless input is being piped in (a script or a test).
static string ReadHidden()
{
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";

    var typed = "";
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return typed; }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (typed.Length > 0) { typed = typed[..^1]; Console.Write("\b \b"); }
        }
        else if (!char.IsControl(key.KeyChar))
        {
            typed += key.KeyChar;
            Console.Write('*');
        }
    }
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
