using Avalonia;
using PassVault.Core.Config;
using PassVault.Core.Machine;
using PassVault.Core.Usb;

namespace PassVault.Linux;

internal static class Program
{
    /// <summary>The settings for this run: config.toml, then any --set key=value given on the command line.</summary>
    public static Settings Settings { get; private set; } = Settings.Load("");

    [STAThread]
    public static void Main(string[] args)
    {
        var overrides = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--set" && i + 1 < args.Length) overrides.Add(args[++i]);
            else if (args[i].StartsWith("--set=")) overrides.Add(args[i][6..]);
        }

        Settings = Settings.Load(overrides: overrides);
        foreach (var problem in Settings.Problems) Console.Error.WriteLine($"settings: {problem}");
        UsbDetect.ExtraPlaces.AddRange(Settings.ExtraStickPlaces);
        if (!OperatingSystem.IsWindows()) LinuxChip.Allowed = Settings.UseChip;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Wayland where there is one - it keeps other programs from reading this window or its keys -
    /// and X11 only where there is not.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().UseWaylandWithFallback().LogToTrace();   // the fallback is set first
}
