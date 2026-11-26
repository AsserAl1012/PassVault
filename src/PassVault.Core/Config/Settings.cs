using System.Globalization;
using System.Text;

namespace PassVault.Core.Config;

/// <summary>
/// Everything a user can set, from one plain text file - config.toml - plus --set overrides on
/// the command line. Every setting has a default and a safe range. A value outside the range is
/// brought back inside it, a typo is pointed out, and neither ever stops PassVault starting:
/// each is listed in <see cref="Problems"/> for the front end to show.
///
/// The file is read as simple TOML: [sections], key = value, "text", numbers, true/false and
/// ["lists", "of text"]. That is all these settings need, and it keeps third-party code away
/// from them.
/// </summary>
public sealed class Settings
{
    /// <summary>Lock after this many minutes without use.</summary>
    public int LockAfterMinutes { get; private set; } = 5;
    public bool LockWhenStickRemoved { get; private set; } = true;
    public bool LockWhenScreenLocks { get; private set; } = true;

    /// <summary>What PassVault copies is wiped from the clipboard after this long.</summary>
    public int ClearClipboardAfterSeconds { get; private set; } = 20;

    public int GeneratorLength { get; private set; } = 20;
    public bool GeneratorSymbols { get; private set; } = true;

    /// <summary>The shortest PIN set-up accepts. It can only make PINs longer than the 4 PassVault always allows.</summary>
    public int MinPinLength { get; private set; } = 4;

    /// <summary>Folders to treat as a stick as well as the ones the desktop mounts.</summary>
    public List<string> ExtraStickPlaces { get; private set; } = [];

    /// <summary>False only when the user turned the chip off: then the secret is kept in a file only they can read. Weaker.</summary>
    public bool UseChip { get; private set; } = true;

    public string Theme { get; private set; } = "system";
    public int FontSize { get; private set; } = 13;

    /// <summary>Everything that was wrong in the file or the overrides, and what was done about it.</summary>
    public List<string> Problems { get; } = [];

    /// <summary>~/.config/passvault/config.toml on Linux ($XDG_CONFIG_HOME); next to this PC's set-up on Windows.</summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("PASSVAULT_CONFIG") is { Length: > 0 } chosen ? chosen
        : OperatingSystem.IsWindows() ? Path.Combine(Machine.MachineStore.DefaultFolder, "config.toml")
        : Path.Combine(Machine.MachineStore.Known(Environment.SpecialFolder.ApplicationData), "passvault", "config.toml");

    /// <summary>Read the file if there is one, then apply overrides like "lock.after_minutes=10".</summary>
    public static Settings Load(string? path = null, IEnumerable<string>? overrides = null)
    {
        var settings = new Settings();
        var values = new Dictionary<string, (string Raw, string Where)>();
        path ??= DefaultPath;

        if (File.Exists(path))
            Parse(File.ReadAllLines(path), Path.GetFileName(path), values, settings.Problems);

        foreach (var o in overrides ?? [])
        {
            int eq = o.IndexOf('=');
            if (eq < 1) settings.Problems.Add($"--set {o}: write it as key=value, for example lock.after_minutes=10");
            else values[o[..eq].Trim()] = (o[(eq + 1)..].Trim(), $"--set {o}");
        }

        settings.Apply(values);
        return settings;
    }

    private void Apply(Dictionary<string, (string Raw, string Where)> values)
    {
        LockAfterMinutes = Number(values, "lock.after_minutes", LockAfterMinutes, 1, 240);
        LockWhenStickRemoved = YesNo(values, "lock.when_stick_removed", LockWhenStickRemoved);
        LockWhenScreenLocks = YesNo(values, "lock.when_screen_locks", LockWhenScreenLocks);
        ClearClipboardAfterSeconds = Number(values, "clipboard.clear_after_seconds", ClearClipboardAfterSeconds, 5, 300);
        GeneratorLength = Number(values, "generator.length", GeneratorLength, 12, 128);
        GeneratorSymbols = YesNo(values, "generator.symbols", GeneratorSymbols);
        MinPinLength = Number(values, "pin.min_length", MinPinLength, Crypto.KeyWrap.MinPinLength, Crypto.KeyWrap.MaxPinLength);
        ExtraStickPlaces = Texts(values, "sticks.extra_places") ?? ExtraStickPlaces;
        UseChip = Choice(values, "chip.use", "auto", ["auto", "off"]) == "auto";
        Theme = Choice(values, "look.theme", Theme, ["system", "dark", "light"]);
        FontSize = Number(values, "look.font_size", FontSize, 9, 32);

        foreach (var (key, (_, where)) in values)
            Problems.Add($"{where}: PassVault has no setting called \"{key}\" - a typo? It was ignored.");
    }

    // ---- reading one value: each takes its key out of the dictionary, so what is left over is unknown ----

    private int Number(Dictionary<string, (string Raw, string Where)> values, string key, int fallback, int min, int max)
    {
        if (!values.Remove(key, out var v)) return fallback;
        if (!int.TryParse(v.Raw.Replace("_", ""), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n))
        {
            Problems.Add($"{v.Where}: {key} should be a whole number from {min} to {max}. Using {fallback}.");
            return fallback;
        }
        if (n >= min && n <= max) return n;

        int kept = Math.Clamp(n, min, max);
        Problems.Add($"{v.Where}: {key} = {n} is outside {min} to {max}. Using {kept}.");
        return kept;
    }

    private bool YesNo(Dictionary<string, (string Raw, string Where)> values, string key, bool fallback)
    {
        if (!values.Remove(key, out var v)) return fallback;
        if (v.Raw is "true" or "false") return v.Raw == "true";
        Problems.Add($"{v.Where}: {key} should be true or false. Using {(fallback ? "true" : "false")}.");
        return fallback;
    }

    private string Choice(Dictionary<string, (string Raw, string Where)> values, string key, string fallback, string[] allowed)
    {
        if (!values.Remove(key, out var v)) return fallback;
        if (Text(v.Raw) is { } s && allowed.Contains(s)) return s;
        Problems.Add($"{v.Where}: {key} should be one of {string.Join(", ", allowed.Select(a => $"\"{a}\""))}. Using \"{fallback}\".");
        return fallback;
    }

    private List<string>? Texts(Dictionary<string, (string Raw, string Where)> values, string key)
    {
        if (!values.Remove(key, out var v)) return null;
        var raw = v.Raw.Trim();
        if (raw.StartsWith('[') && raw.EndsWith(']'))
        {
            var items = SplitList(raw[1..^1]).Select(Text).ToList();
            if (items.All(i => i is not null)) return items!;
        }
        Problems.Add($"{v.Where}: {key} should be a list of text, for example [\"/mnt/usb\"]. It was ignored.");
        return null;
    }

    // ---- the file ----

    private static void Parse(string[] lines, string file, Dictionary<string, (string, string)> values, List<string> problems)
    {
        var section = "";
        for (int i = 0; i < lines.Length; i++)
        {
            var line = WithoutComment(lines[i]).Trim();
            var where = $"{file} line {i + 1}";
            if (line.Length == 0) continue;

            if (line.StartsWith('[') && line.EndsWith(']') && !line.StartsWith("[["))
            {
                section = line[1..^1].Trim();
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq < 1)
            {
                problems.Add($"{where}: PassVault reads lines like  key = value  and  [section]. This one was ignored.");
                continue;
            }

            var key = line[..eq].Trim();
            values[section.Length == 0 ? key : $"{section}.{key}"] = (line[(eq + 1)..].Trim(), where);
        }
    }

    /// <summary>"text" with \" \\ \n \t, or 'text' taken exactly as written. Null when it is neither.</summary>
    private static string? Text(string raw)
    {
        raw = raw.Trim();
        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'') return raw[1..^1];
        if (raw.Length < 2 || raw[0] != '"' || raw[^1] != '"') return null;

        var s = new StringBuilder();
        for (int i = 1; i < raw.Length - 1; i++)
        {
            if (raw[i] != '\\' || i + 1 >= raw.Length - 1) { s.Append(raw[i]); continue; }
            s.Append(raw[++i] switch { 'n' => '\n', 't' => '\t', var c => c });
        }
        return s.ToString();
    }

    /// <summary>A # starts a comment, unless it is inside quotes.</summary>
    private static string WithoutComment(string line)
    {
        char? quote = null;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote is null && c == '#') return line[..i];
            if (quote is null && c is '"' or '\'') quote = c;
            else if (c == quote && (c == '\'' || line[i - 1] != '\\')) quote = null;
        }
        return line;
    }

    /// <summary>Split a list's inside on the commas that are not inside quotes.</summary>
    private static IEnumerable<string> SplitList(string inside)
    {
        var item = new StringBuilder();
        char? quote = null;
        foreach (char c in inside)
        {
            if (quote is null && c == ',') { if (item.ToString().Trim().Length > 0) yield return item.ToString(); item.Clear(); continue; }
            if (quote is null && c is '"' or '\'') quote = c;
            else if (c == quote) quote = null;
            item.Append(c);
        }
        if (item.ToString().Trim().Length > 0) yield return item.ToString();
    }

    /// <summary>What "passvault config init" writes: every setting, at its default, explained.</summary>
    public const string Template = """
        # PassVault settings. Every line here is the default - change what you like, delete
        # what you do not. A wrong value is put back to the nearest safe one and PassVault says so.
        # Any setting can also be given once on the command line:  --set lock.after_minutes=10

        [lock]
        after_minutes = 5             # lock after this long without use: 1 to 240
        when_stick_removed = true     # lock the moment the stick is pulled out
        when_screen_locks = true      # lock when your desktop locks the screen

        [clipboard]
        clear_after_seconds = 20      # wipe a copied password after this long: 5 to 300

        [generator]
        length = 20                   # 12 to 128 characters
        symbols = true                # include !#$%&*+-=?@_ (a few sites refuse them)

        [pin]
        min_length = 4                # the shortest PIN set-up accepts: 4 to 12

        [sticks]
        extra_places = []             # folders to treat as a stick too, e.g. ["/mnt/usb"]

        [chip]
        use = "auto"                  # "auto": this PC's TPM chip checks your PIN when there is one
                                      # "off": never use it - your secret is then only as safe as your files

        [look]
        theme = "system"              # "system", "dark" or "light"
        font_size = 13                # 9 to 32
        """;
}
