using System.Security.Cryptography;
using PassVault.Core.Crypto;
using PassVault.Core.Usb;
using PassVault.Core.Vault;

namespace PassVault.Core.Machine;

/// <summary>Something went wrong opening the vault, with a message meant for the user to read.</summary>
public sealed class UnlockException(string message) : Exception(message);

/// <summary>
/// Setting a machine up, and the two ways of opening the vault on it:
/// the daily one (stick + this machine + PIN) and the recovery one (12 words).
/// </summary>
public static class Enrolment
{
    /// <summary>After this many wrong PINs the PIN is switched off until the 12 words are used.</summary>
    public const int MaxAttempts = 10;

    /// <summary>
    /// Set this machine up. Needs the 12 words once, then a PIN of your choosing.
    /// Also drops a spare copy of the encrypted vault here, so a dead stick is not the end.
    /// </summary>
    public static (MachineFile Machine, byte[] MasterKey) Enrol(
        string folder, string stickRoot, string phrase, string pin, byte[] deviceSecret, DeviceProtection protection)
    {
        var vault = VaultStorage.ReadVault(stickRoot);
        var keyfile = VaultStorage.ReadKeyfile(stickRoot);
        var header = VaultFile.ReadHeader(vault);

        var masterKey = OpenWithPhraseOrExplain(vault, phrase);
        var machineId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

        var machine = new MachineFile
        {
            MachineId = machineId,
            VaultId = header.VaultId,
            StickShortId = keyfile.ShortId,
            Kdf = header.Kdf,
            PinSlot = KeyWrap.WrapWithPin(masterKey, keyfile.UsbSecret, deviceSecret, pin, header.VaultId, machineId, header.Kdf),
            EpochAtEnrolment = header.Epoch,
            HighestEpochSeen = header.Epoch,
            HighestRevisionSeen = header.Revision,
            Protection = protection,
        };

        MachineStore.Write(folder, machine);
        MachineStore.WriteMirrorVault(folder, vault);

        return (machine, masterKey);
    }

    /// <summary>
    /// The daily unlock: the stick plugged in, this machine's own secret, and the PIN.
    /// All three or nothing.
    /// </summary>
    public static (byte[] MasterKey, MachineFile Machine) UnlockWithPin(
        string folder, string stickRoot, string pin, byte[] deviceSecret)
    {
        if (!MachineStore.IsEnrolled(folder))
            throw new UnlockException("This machine is not set up yet. Use your 12 words to set it up.");

        var machine = MachineStore.Read(folder);

        if (machine.PinDisabled)
            throw new UnlockException(
                $"The PIN is switched off on this machine after {MaxAttempts} wrong tries. Use your 12 words to switch it back on.");

        RequireStick(stickRoot, machine.StickShortId);

        var vault = VaultStorage.ReadVault(stickRoot);
        var keyfile = VaultStorage.ReadKeyfile(stickRoot);
        var header = VaultFile.ReadHeader(vault);

        if (header.VaultId != machine.VaultId)
            throw new UnlockException(
                $"That is not your stick. This machine is set up for a different vault - yours is stick {machine.StickShortId}.");

        if (VaultFile.IsRollback(header, machine.HighestEpochSeen, machine.HighestRevisionSeen))
            throw new UnlockException(
                "The vault on this stick is older than the one this machine last saw. Someone may have put an old copy back, "
                + "which would quietly undo password changes. Refusing to open it.");

        if (header.Epoch != machine.EpochAtEnrolment)
            throw new UnlockException(
                "This stick was re-keyed after this machine was set up, so the PIN here no longer fits. "
                + "Set this machine up again with your 12 words.");

        // Count the try BEFORE checking it. Otherwise pulling the plug on a wrong guess
        // would rewind the counter and the "tries left" message would be a lie.
        machine.FailedAttempts++;
        if (machine.FailedAttempts >= MaxAttempts) machine.PinDisabled = true;
        MachineStore.Write(folder, machine);

        byte[] masterKey;
        try
        {
            masterKey = KeyWrap.UnwrapWithPin(
                machine.PinSlot, keyfile.UsbSecret, deviceSecret, pin, machine.VaultId, machine.MachineId, machine.Kdf);
        }
        catch (CryptographicException)
        {
            int left = MaxAttempts - machine.FailedAttempts;
            throw new UnlockException(left > 0
                ? $"Wrong PIN. {left} {(left == 1 ? "try" : "tries")} left before the PIN is switched off on this machine."
                : "Wrong PIN. The PIN is now switched off on this machine - use your 12 words to switch it back on.");
        }

        machine.FailedAttempts = 0;
        machine.PinDisabled = false;
        machine.HighestEpochSeen = header.Epoch;
        machine.HighestRevisionSeen = header.Revision;
        MachineStore.Write(folder, machine);
        MachineStore.WriteMirrorVault(folder, vault);

        return (masterKey, machine);
    }

    /// <summary>
    /// The way back in. Works with no machine setup at all, and switches the PIN back on
    /// if it had been locked out.
    /// </summary>
    public static byte[] UnlockWithPhrase(string folder, string stickRoot, string phrase)
    {
        RequireStick(stickRoot, null);

        var vault = VaultStorage.ReadVault(stickRoot);
        var masterKey = OpenWithPhraseOrExplain(vault, phrase);
        var header = VaultFile.ReadHeader(vault);

        bool isOld = false;

        if (MachineStore.IsEnrolled(folder))
        {
            var machine = MachineStore.Read(folder);
            isOld = VaultFile.IsRollback(header, machine.HighestEpochSeen, machine.HighestRevisionSeen);

            machine.FailedAttempts = 0;
            machine.PinDisabled = false;
            if (!isOld)
            {
                machine.HighestEpochSeen = header.Epoch;
                machine.HighestRevisionSeen = header.Revision;
            }
            MachineStore.Write(folder, machine);
        }

        // Never let an older vault overwrite the good spare copy on this PC.
        if (!isOld) MachineStore.WriteMirrorVault(folder, vault);

        return masterKey;
    }

    /// <summary>
    /// Save the vault everywhere it belongs: the stick, the spare copy on this PC, and the
    /// note of how new it is.
    ///
    /// Always save through here. Writing the stick directly leaves this machine still
    /// thinking the newest vault it has seen is the older one, and then putting that older
    /// copy back would slip past the rollback check.
    /// </summary>
    public static void SaveVault(string folder, string stickRoot, byte[] vault)
    {
        VaultStorage.WriteVault(stickRoot, vault);
        MachineStore.WriteMirrorVault(folder, vault);
        NoteHowNew(folder, VaultFile.ReadHeader(vault));
    }

    /// <summary>Remember the newest vault this machine has seen. Never moves backwards.</summary>
    public static void NoteHowNew(string folder, VaultHeader header)
    {
        if (!MachineStore.IsEnrolled(folder)) return;

        var machine = MachineStore.Read(folder);
        bool newer = header.Epoch > machine.HighestEpochSeen
                     || (header.Epoch == machine.HighestEpochSeen && header.Revision > machine.HighestRevisionSeen);
        if (!newer) return;

        machine.HighestEpochSeen = header.Epoch;
        machine.HighestRevisionSeen = header.Revision;
        MachineStore.Write(folder, machine);
    }

    /// <summary>
    /// How long to wait before letting the next PIN try through. The real protection is this
    /// machine's TPM secret, not this counter - the wait is just here to make guessing at
    /// the keyboard pointless.
    /// </summary>
    public static TimeSpan DelayBeforeNextTry(int failedAttempts)
    {
        if (failedAttempts < 3) return TimeSpan.Zero;
        return TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, failedAttempts - 3)));
    }

    /// <summary>
    /// The everyday mistake is the stick not being plugged in, so say that plainly rather
    /// than leaking a filename at the user.
    /// </summary>
    private static void RequireStick(string stickRoot, string? expectedShortId)
    {
        if (VaultStorage.IsSetUp(stickRoot)) return;

        var which = expectedShortId is null ? "Your PassVault stick" : $"Your PassVault stick ({expectedShortId})";
        throw new UnlockException(
            Directory.Exists(stickRoot)
                ? $"{which} is not at {stickRoot} - there is no vault there. Plug the right stick in."
                : $"{which} is not plugged in. Plug it in and try again.");
    }

    internal static byte[] OpenWithPhraseOrExplain(byte[] vault, string phrase)
    {
        if (!Mnemonic.TryParse(phrase, out _, out var problem))
            throw new UnlockException(problem);

        try
        {
            return VaultFile.UnlockWithPhrase(vault, phrase);
        }
        catch (CryptographicException)
        {
            throw new UnlockException("Those 12 words are a valid phrase, but they do not open this vault.");
        }
    }
}
