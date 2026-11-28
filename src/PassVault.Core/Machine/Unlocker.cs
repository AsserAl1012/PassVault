using System.Diagnostics;
using System.Security.Cryptography;
using PassVault.Core.Usb;
using PassVault.Core.Vault;

namespace PassVault.Core.Machine;

/// <summary>What the start screen should offer.</summary>
public enum Situation { NoStick, NewStick, SetUpThisPc, SetupDamaged, Rekeyed, PinOff, Pin }

/// <param name="OtherStick">A second stick with a vault on it - the screen says which one is in use.</param>
public sealed record Arrival(Situation Situation, UsbStick? Stick, UsbStick? OtherStick = null,
                             int PinLength = 4, int FailedAttempts = 0);

/// <summary>
/// This PC's own secret can never be opened here again: the chip was cleared, the board replaced,
/// or the secret is damaged. Only setting the PC up again with the 12 words helps.
/// </summary>
public sealed class ChipLostException(Exception inner) : Exception(
    "This PC's own secret can no longer be opened - its chip was cleared, or this is another PC. "
    + "Type your 12 words to set it up again.", inner);

/// <summary>
/// The way into a vault, worked out once for every front end - the terminal app and the Linux
/// window app. Each had its own copy of these rules, and each copy missed some: the re-keyed
/// stick, the second stick, which failures mean "set this PC up again".
/// </summary>
public static class Unlocker
{
    /// <summary>What is plugged in, and how this PC is set up. Throws IOException when this PC's file is busy - look again shortly.</summary>
    /// <param name="sticks">Tests pass their own, so a real stick plugged into the PC never comes into it.</param>
    public static Arrival Look(string folder, List<UsbStick>? sticks = null)
    {
        sticks ??= UsbDetect.Find();
        var setUp = sticks.Where(s => s.IsSetUp).ToList();
        if (setUp.Count == 0)
            return new(sticks.Count > 0 ? Situation.NewStick : Situation.NoStick, sticks.FirstOrDefault());

        var (stick, other) = (setUp[0], setUp.Count > 1 ? setUp[1] : null);
        if (!MachineStore.IsEnrolled(folder)) return new(Situation.SetUpThisPc, stick, other);

        MachineFile machine;
        try
        {
            machine = MachineStore.Read(folder);
        }
        catch (FileNotFoundException)
        {
            return new(Situation.SetupDamaged, stick, other);   // there, but no copy of it can be read
        }

        if (RekeyedSinceSetUp(stick.Root, machine)) return new(Situation.Rekeyed, stick, other);
        if (machine.PinDisabled) return new(Situation.PinOff, stick, other);
        return new(Situation.Pin, stick, other, machine.PinLength, machine.FailedAttempts);
    }

    /// <summary>
    /// How long to wait before the next PIN try, after earlier wrong ones. Null when this PC's file
    /// cannot be read just now - forgotten from a terminal, or mid-save - so look again instead.
    /// </summary>
    public static TimeSpan? WaitBeforeTry(string folder)
    {
        try
        {
            return Enrolment.DelayBeforeNextTry(MachineStore.Read(folder).FailedAttempts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether wrong PINs have switched the PIN off - false when this PC's file cannot be read.</summary>
    public static bool PinSwitchedOff(string folder)
    {
        try
        {
            return MachineStore.IsEnrolled(folder) && MachineStore.Read(folder).PinDisabled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The daily unlock - the chip checks the PIN. Throws UnlockException for a wrong PIN or a stick
    /// problem (with the sentence to show), ChipUnavailableException when the chip cannot be reached
    /// (no try spent), and ChipLostException only when this PC's own secret will never open again.
    /// </summary>
    public static byte[] WithPin(string folder, string stickRoot, string pin)
    {
        var key = Enrolment.UnlockWithPin(folder, stickRoot, pin, typed =>
        {
            try
            {
                return DeviceSecret.Open(folder, typed).Secret;
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or CryptographicException)
            {
                // Only what the chip's own step throws means the secret is gone. A vault from a newer
                // version, a missing tool - those were once all read as "chip cleared, set up again",
                // and setting up again then failed the same way, round and round.
                throw new ChipLostException(ex);
            }
        }).MasterKey;

        DeviceSecret.UpgradeIfOld(folder, pin);
        return key;
    }

    /// <summary>Set this PC up against a stick that has a vault: the 12 words once, then a PIN.</summary>
    public static (byte[] MasterKey, DeviceProtection Protection) SetUpThisPc(string folder, string stickRoot, string phrase, string pin)
    {
        var device = DeviceSecret.Create(pin);
        return (Enrolment.Enrol(folder, stickRoot, phrase, pin, device.Secret, device.Protection, device.Sealed).MasterKey,
                device.Protection);
    }

    /// <summary>What the user should know about how well this PC guards the PIN - or null when nothing.</summary>
    public static string? Weakness(DeviceProtection protection)
    {
        if (protection != DeviceProtection.Tpm)
            return "No TPM chip checks your PIN on this PC - its secret is only as safe as your files. That is weaker.";
        if (OperatingSystem.IsLinux() && LinuxChip.LockoutCanBeReset())
            return "This PC's chip has no lockout password, so someone who boots another system on this PC could wipe "
                   + "its count of wrong PINs. A PIN of 6 or more digits protects you far better.";
        return null;
    }

    /// <summary>
    /// A re-key on another PC leaves this PC's PIN slot fitting keys that no longer exist. Unnoticed,
    /// the PIN said "set this machine up again" on every try and nothing offered set-up.
    /// Reads the main file and nothing else: ReadVault also finishes a save left half-done, and asked
    /// every two seconds it finished another app's save in the middle of it, which that app then
    /// reported as failed although the stick held it.
    /// </summary>
    private static bool RekeyedSinceSetUp(string stickRoot, MachineFile machine)
    {
        try
        {
            var path = VaultStorage.VaultPathOn(stickRoot);
            if (!File.Exists(path)) return false;
            var header = VaultFile.ReadHeader(File.ReadAllBytes(path));
            return header.VaultId == machine.VaultId && header.Epoch > machine.EpochAtEnrolment;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or System.Text.Json.JsonException)
        {
            return false;   // the unlock itself will say what is wrong with it
        }
    }
}

/// <summary>Whether this desktop session's screen is locked - for lock.when_screen_locks.</summary>
public static class ScreenLock
{
    /// <summary>
    /// GNOME and KDE tell logind when they lock the screen (its LockedHint). Asked of the user's
    /// desktop session - the one with the display - not of whatever session started PassVault: one
    /// started from an SSH or script session watched that, and the vault stayed open behind a locked
    /// screen. With no desktop at all this is false. Asked every couple of seconds while a vault is open.
    /// </summary>
    public static bool IsLocked()
    {
        if (!OperatingSystem.IsLinux()) return false;
        _desktop ??= Loginctl("show-user", Environment.UserName, "-p", "Display", "--value") is { Length: > 0 } display ? display
                   : Environment.GetEnvironmentVariable("XDG_SESSION_ID") ?? "";
        return _desktop.Length > 0 && Loginctl("show-session", _desktop, "-p", "LockedHint", "--value") == "yes";
    }

    private static string? _desktop;

    private static string Loginctl(params string[] args)
    {
        try
        {
            using var loginctl = Process.Start(new ProcessStartInfo("loginctl", args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var said = loginctl.StandardOutput.ReadToEnd();
            loginctl.WaitForExit(2000);
            return said.Trim();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "";   // no loginctl: not a systemd desktop
        }
    }
}
