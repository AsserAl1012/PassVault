using PassVault.Core.Config;
using PassVault.Core.Crypto;

namespace PassVault.Tests;

public class SettingsTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"pv-config-{Guid.NewGuid().ToString("N")[..8]}.toml");

    public void Dispose()
    {
        if (File.Exists(_file)) File.Delete(_file);
        GC.SuppressFinalize(this);
    }

    private Settings Load(string text, params string[] overrides)
    {
        File.WriteAllText(_file, text);
        return Settings.Load(_file, overrides);
    }

    [Fact]
    public void With_no_file_everything_is_the_default()
    {
        var s = Settings.Load(_file);

        Assert.Empty(s.Problems);
        Assert.Equal(5, s.LockAfterMinutes);
        Assert.Equal(20, s.ClearClipboardAfterSeconds);
        Assert.True(s.UseChip);
    }

    /// <summary>The file "config init" writes documents every setting. It must say exactly what the code does.</summary>
    [Fact]
    public void The_template_is_exactly_the_defaults_with_nothing_to_complain_about()
    {
        var fromTemplate = Load(Settings.Template);
        var defaults = Settings.Load(_file + ".none");

        Assert.Empty(fromTemplate.Problems);
        foreach (var p in typeof(Settings).GetProperties().Where(p => p.Name != nameof(Settings.Problems)))
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(p.GetValue(defaults)),
                         System.Text.Json.JsonSerializer.Serialize(p.GetValue(fromTemplate)));
    }

    /// <summary>Every key the template names is one the code reads - a changed value for each comes through.</summary>
    [Fact]
    public void Every_setting_can_be_changed()
    {
        var s = Load("""
            [lock]
            after_minutes = 30
            when_stick_removed = false
            when_screen_locks = false
            [clipboard]
            clear_after_seconds = 45
            [generator]
            length = 32
            symbols = false
            [pin]
            min_length = 8
            [sticks]
            extra_places = ["/mnt/usb", '/media/C:\odd']
            [chip]
            use = "off"
            [look]
            theme = "light"
            font_size = 16
            """);

        Assert.Empty(s.Problems);
        Assert.Equal((30, false, false, 45, 32, false, 8, false, "light", 16),
                     (s.LockAfterMinutes, s.LockWhenStickRemoved, s.LockWhenScreenLocks, s.ClearClipboardAfterSeconds,
                      s.GeneratorLength, s.GeneratorSymbols, s.MinPinLength, s.UseChip, s.Theme, s.FontSize));
        Assert.Equal(["/mnt/usb", "/media/C:\\odd"], s.ExtraStickPlaces);
    }

    /// <summary>A setting can make things stricter or looser within its range, never past a safe floor.</summary>
    [Fact]
    public void A_value_outside_its_range_is_brought_back_inside_and_the_line_is_named()
    {
        var s = Load("[clipboard]\nclear_after_seconds = 0\n[pin]\nmin_length = 2\n");

        Assert.Equal(5, s.ClearClipboardAfterSeconds);
        Assert.Equal(4, s.MinPinLength);
        Assert.Contains(s.Problems, p => p.Contains("line 2") && p.Contains("clipboard.clear_after_seconds") && p.Contains("Using 5"));
    }

    [Fact]
    public void A_typo_is_pointed_out_and_ignored_and_the_rest_still_counts()
    {
        var s = Load("[lock]\naftr_minutes = 30\nafter_minutes = 10\n");

        Assert.Equal(10, s.LockAfterMinutes);
        Assert.Contains(s.Problems, p => p.Contains("\"lock.aftr_minutes\"") && p.Contains("typo"));
    }

    [Theory]
    [InlineData("[lock]\nafter_minutes = soon\n")]
    [InlineData("[lock]\nwhen_stick_removed = yes\n")]
    [InlineData("[look]\ntheme = \"blue\"\n")]
    [InlineData("[sticks]\nextra_places = \"/mnt/usb\"\n")]
    [InlineData("this is not a setting\n")]
    public void A_value_of_the_wrong_kind_keeps_the_default_and_says_so(string text)
    {
        var s = Load(text);

        Assert.Single(s.Problems);
        Assert.Equal(5, s.LockAfterMinutes);
        Assert.Equal("system", s.Theme);
    }

    [Fact]
    public void Comments_are_skipped_but_a_hash_inside_quotes_is_kept()
    {
        var s = Load("# my settings\n[sticks]   # where\nextra_places = [\"/mnt/usb#2\"]  # the second one\n");

        Assert.Empty(s.Problems);
        Assert.Equal(["/mnt/usb#2"], s.ExtraStickPlaces);
    }

    [Fact]
    public void A_set_on_the_command_line_beats_the_file()
    {
        var s = Load("[lock]\nafter_minutes = 30\n", "lock.after_minutes=2", "look.theme=\"dark\"", "nonsense");

        Assert.Equal(2, s.LockAfterMinutes);
        Assert.Equal("dark", s.Theme);
        Assert.Contains(s.Problems, p => p.StartsWith("--set nonsense"));
    }

    [Theory]
    [InlineData(12, true)]
    [InlineData(64, false)]
    public void The_generator_follows_its_settings(int length, bool symbols)
    {
        var made = PasswordGenerator.Make(length, symbols);

        Assert.Equal(length, made.Length);
        Assert.Equal(symbols, made.Any(c => PasswordGenerator.Symbols.Contains(c)));   // with symbols on, there is always one
    }
}
