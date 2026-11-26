using PassVault.Core.Config;
using PassVault.Core.Crypto;
using PassVault.Core.Machine;
using PassVault.Core.Model;
using PassVault.Core.Usb;
using PassVault.Core.Vault;

// The console tool: everything the app does, without a window - for scripts, for testing, and
// for people who would rather type.

// "--set key=value" can go anywhere, any number of times; what is left is the command.
var overrides = new List<string>();
var rest = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--set" && i + 1 < args.Length) overrides.Add(args[++i]);
    else if (args[i].StartsWith("--set=")) overrides.Add(args[i][6..]);
    else rest.Add(args[i]);
}
args = [.. rest];

try
{
    MachineStore.MoveFromOldPlace();
    Current.Settings = Settings.Load(overrides: overrides);
    foreach (var problem in Current.Settings.Problems) Console.Error.WriteLine($"settings: {problem}");
    UsbDetect.ExtraPlaces.AddRange(Current.Settings.ExtraStickPlaces);
    if (!OperatingSystem.IsWindows()) LinuxChip.Allowed = Current.Settings.UseChip;

    // On its own, in a terminal, passvault opens full screen. Piped or scripted, it explains itself.
    if (args.Length == 0)
        return Console.IsInputRedirected || Console.IsOutputRedirected ? Help() : PassVault.Cli.Tui.Run(Current.Settings);

    return args[0].ToLowerInvariant() switch
    {
        "config" => Config(args.Length > 1 ? args[1] : ""),
        "generate" => Generate(),
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
catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or InvalidDataException or ArgumentException
                               or System.Security.Cryptography.CryptographicException or PinRefusedException)
{
    Console.WriteLine($"\n{ex.Message}");
    return 1;
}

static int Help()
{
    Console.WriteLine("""
        PassVault, in the console

          (nothing)         open PassVault full screen in this terminal

          find              show every USB stick PassVault can see
          init   <drive>    set up a fresh stick, prints your 12 words
          enrol  <drive>    set this PC up: 12 words once, then choose a PIN
          status <drive>    what is on that stick, and how this PC is set up
          list   <drive>    open the vault and show your credentials
          add    <drive>    open the vault and add one credential
          forget            undo this PC's setup (the vault is untouched)
          generate          print a new strong password
          config            show every setting, where the file is, and anything wrong in it
          config init       write a settings file with every setting explained

          --set key=value   change a setting for this run only, e.g. --set generator.length=32

        Once this PC is set up, list and add ask for your PIN.
        Before that, and any time the PIN is locked out, they ask for your 12 words.

        Example:  PassVault.Cli enrol "E:\"      on Linux:  PassVault.Cli enrol "/media/you/STICK"
        """);
    return 0;
}

static int Config(string what)
{
    var path = Settings.DefaultPath;
    if (what == "init")
    {
        if (File.Exists(path))
        {
            Console.WriteLine($"There is a settings file already - left alone:\n  {path}");
            return 1;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Settings.Template.Replace("\r\n", "\n") + "\n");
        Console.WriteLine($"Written, with every setting at its default and explained:\n  {path}");
        return 0;
    }

    var s = Current.Settings;
    Console.WriteLine($"settings file   {path}{(File.Exists(path) ? "" : "   (not there - all defaults; 'config init' writes one)")}");
    Console.WriteLine($"""

        lock.after_minutes            = {s.LockAfterMinutes}
        lock.when_stick_removed       = {Bool(s.LockWhenStickRemoved)}
        lock.when_screen_locks        = {Bool(s.LockWhenScreenLocks)}
        clipboard.clear_after_seconds = {s.ClearClipboardAfterSeconds}
        generator.length              = {s.GeneratorLength}
        generator.symbols             = {Bool(s.GeneratorSymbols)}
        pin.min_length                = {s.MinPinLength}
        sticks.extra_places           = [{string.Join(", ", s.ExtraStickPlaces.Select(p => $"\"{p}\""))}]
        chip.use                      = "{(s.UseChip ? "auto" : "off")}"
        look.theme                    = "{s.Theme}"
        look.font_size                = {s.FontSize}
        """);
    if (s.Problems.Count > 0) Console.WriteLine($"\n{s.Problems.Count} problem(s) - listed above.");
    return s.Problems.Count == 0 ? 0 : 1;

    static string Bool(bool b) => b ? "true" : "false";
}

static int Generate()
{
    Console.WriteLine(PasswordGenerator.Make(Current.Settings.GeneratorLength, Current.Settings.GeneratorSymbols));
    return 0;
}

static int Find()
{
    var sticks = UsbDetect.Find();
    if (sticks.Count == 0)
    {
        Console.WriteLine("No USB stick found that this PC can read.");
        Console.WriteLine("If one is plugged in, this PC may not understand its filesystem");
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

    var device = DeviceSecret.Create(pin);
    var (machine, _) = Enrolment.Enrol(folder, root, phrase, pin, device.Secret, device.Protection, device.Sealed);

    Console.WriteLine($"\nThis PC is set up. From now on: stick in, PIN, done.");
    Console.WriteLine($"  machine secret   {Describe(machine.Protection)}");
    Console.WriteLine($"  spare vault      {MachineStore.MirrorVaultPathIn(folder)}");

    if (machine.Protection != DeviceProtection.Tpm)
        Console.WriteLine($"""

            Heads up: no usable TPM was found, so this PC's secret is {Describe(machine.Protection)}
            instead of sealed in the chip. That is weaker - someone who can log in as you can
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
        // Quoted, and shown whenever the value is not null rather than not blank. This is
        // the tool the app gets checked against, so it has to show exactly what is stored -
        // a password of nothing but spaces used to be invisible here too.
        Console.WriteLine($"  \"{e.Name}\"");
        Show("username", e.Username);
        Show("email", e.Email);
        Show("password", e.Password);
        Show("notes", e.Notes);
    }

    var deleted = VaultFile.ReadEntries(vault, masterKey).Count(e => e.Deleted);
    Console.WriteLine($"\n{entries.Count} shown, {deleted} deleted");
    return 0;

    static void Show(string label, string? value)
    {
        if (value is not null) Console.WriteLine($"      {label,-8}  \"{value}\"");
    }
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

    var folder = MachineStore.DefaultFolder;
    var saved = VaultFile.Save(vault, masterKey, entries, Enrolment.RevisionSeen(folder, VaultFile.ReadHeader(vault)));
    var where = "on the stick and in the spare copy here";
    try
    {
        Enrolment.SaveVault(folder, root, saved);
    }
    catch (SpareCopyException behind)
    {
        Console.WriteLine($"\n{behind.Message}");   // saved all the same - the stick has it
        where = "on the stick";
    }

    Console.WriteLine($"\nSaved. {entries.Count(e => !e.Deleted)} credential(s), {where}.");
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

        var pin = AskPin("PIN");
        var (masterKey, _) = Enrolment.UnlockWithPin(folder, root, pin, typed => DeviceSecret.Open(folder, typed).Secret);
        DeviceSecret.UpgradeIfOld(folder, pin);
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
    DeviceProtection.UserFile => "kept in a file only you can read (no TPM found) - weaker",
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
        int shortest = Current.Settings.MinPinLength;
        var first = AskPin($"Choose a PIN, {shortest} to 12 digits");
        if (first.Length < shortest)
        {
            Console.WriteLine($"  Your settings ask for at least {shortest} digits (pin.min_length).");
            continue;
        }
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
        if (Enrolment.IsPin(pin)) return pin;
        Console.WriteLine("  A PIN is 4 to 12 digits, 0 to 9.");
    }
}

// Hide the PIN as it is typed, unless input is being piped in (a script or a test).
static string ReadHidden()
{
    if (Console.IsInputRedirected) return ReadLineOrStop();

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
    => args.Length > 1 ? args[1] : throw new ArgumentException(OperatingSystem.IsWindows()
        ? "Which drive? For example:  E:\\"
        : "Which stick? For example:  /media/you/STICK");

static string Required(string label)
{
    while (true)
    {
        Console.Write($"  {label}: ");
        var value = ReadLineOrStop();
        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
    }
}

// Piped input can run out. Asking again then looped for ever, printing the question until the disk filled.
static string ReadLineOrStop()
    => Console.ReadLine() ?? throw new InvalidOperationException("The input ended before everything was answered.");

static string? Optional(string label)
{
    Console.Write($"  {label} (leave blank to skip): ");
    var value = Console.ReadLine();
    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>The settings for this run - read once at the start, used by the commands above.</summary>
static class Current
{
    public static Settings Settings { get; set; } = Settings.Load("");
}
