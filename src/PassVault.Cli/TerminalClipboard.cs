using System.Diagnostics;

namespace PassVault.Cli;

/// <summary>
/// The clipboard for the terminal app on Linux, through the desktop's own tools: wl-copy and
/// wl-paste on Wayland, xclip on X11. Terminal.Gui's clipboard copied, but its wipe never
/// happened - what it read back never matched what it had put there - so a copied password
/// stayed on the clipboard for good.
/// </summary>
internal static class TerminalClipboard
{
    private static bool Wayland => Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 };
    private static bool X11 => Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 };

    /// <summary>False with no desktop to talk to (over SSH, say) or with neither tool installed.</summary>
    public static bool Put(string text)
        => Wayland ? Run("exec wl-copy --type text/plain", text) is not null
         : X11 ? Run("exec xclip -selection clipboard -in", text) is not null
         : false;

    /// <summary>
    /// Clear it - only if it still holds exactly this. Anything copied since is left alone. Cleared by
    /// copying nothing: "wl-copy --clear" did nothing under WSL, whose bridge to the Windows clipboard
    /// kept the text - and an empty copy works on every desktop.
    /// </summary>
    public static void WipeIf(string ours)
    {
        var now = Wayland ? Run("exec wl-paste --no-newline", null) : X11 ? Run("exec xclip -selection clipboard -out", null) : null;
        if (now == ours) Put("");
    }

    /// <summary>
    /// Through sh, so wl-copy's background copy - it stays running to hand the text over - does not
    /// hold on to a pipe this process is waiting to read to the end. Two seconds at most.
    /// </summary>
    private static string? Run(string command, string? input)
    {
        bool reading = input is null;
        var start = new ProcessStartInfo("sh") { RedirectStandardInput = true, RedirectStandardOutput = reading };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(reading ? command + " 2>/dev/null" : command + " >/dev/null 2>&1");

        try
        {
            using var process = Process.Start(start)!;
            if (input is not null) process.StandardInput.Write(input);
            process.StandardInput.Close();
            var output = reading ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
            if (!process.WaitForExit(2000) || !output.Wait(2000)) { process.Kill(); return null; }
            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
