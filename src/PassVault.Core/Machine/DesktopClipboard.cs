using System.Diagnostics;

namespace PassVault.Core.Machine;

/// <summary>
/// The clipboard on Linux, through the desktop's own tools: wl-copy and wl-paste on Wayland, xclip
/// on X11. The terminal app copies with these - Terminal.Gui's clipboard copied, but its wipe never
/// happened. The window app wipes with them when it cannot itself: under Wayland a window that is
/// not in front can neither read nor clear the clipboard, and after a copy it never is.
/// </summary>
public static class DesktopClipboard
{
    /// <summary>
    /// A Wayland desktop that can really be reached - its socket is there. Under WSL with systemd the
    /// variable is set while the socket is hidden, and the window app runs on X11 instead.
    /// </summary>
    public static bool Wayland => Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 } display
        && File.Exists(Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "", display));
    private static bool X11 => Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 };

    /// <summary>Whether this desktop's tool is installed.</summary>
    public static bool CanReach => Wayland ? OnPath("wl-paste") && OnPath("wl-copy") : X11 && OnPath("xclip");

    private static bool OnPath(string tool)
        => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(dir => File.Exists(Path.Combine(dir, tool)));

    /// <summary>False with no desktop to talk to (over SSH, say) or with neither tool installed.</summary>
    public static bool Put(string text)
        => Wayland ? Run("exec wl-copy --type text/plain", text) is not null
         : X11 ? Run("exec xclip -selection clipboard -in", text) is not null
         : false;

    /// <summary>
    /// Clear it - only if it still holds exactly this. Anything copied since is left alone. Cleared by
    /// copying nothing: "wl-copy --clear" did nothing under WSL, whose bridge to the Windows clipboard
    /// kept the text - and an empty copy works on every desktop. False when it could not be read at
    /// all (no tool, no desktop, or nothing on it).
    /// </summary>
    public static bool WipeIf(string ours)
    {
        var now = Wayland ? Run("exec wl-paste --no-newline", null) : X11 ? Run("exec xclip -selection clipboard -out", null) : null;
        return now is not null && (now != ours || Put(""));
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
