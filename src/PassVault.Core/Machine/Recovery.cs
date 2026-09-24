using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using PassVault.Core.Model;
using PassVault.Core.Usb;
using PassVault.Core.Vault;

namespace PassVault.Core.Machine;

/// <summary>Where a vault was found, and whether it came off a stick.</summary>
public sealed record VaultSource(byte[] Vault, string Where, bool FromStick);

/// <summary>
/// The things you do when something has gone wrong: your stick was stolen, lost or broken.
///
/// All three end the same way - a brand new stick with brand new keys - so there is one
/// flow, not three. The old stick becomes a dead key the moment this finishes.
/// </summary>
public static class Recovery
{
    /// <summary>
    /// Get the newest copy of the vault we can reach: the stick if it is plugged in,
    /// otherwise the spare copy kept on this PC. If both are here, take the newer one.
    /// </summary>
    public static VaultSource FindVault(string folder, string? stickRoot)
    {
        // Either copy can be the broken one - a broken stick is half the reason anyone is here.
        // One that cannot be read is passed over; it used to fail the whole search, and with it
        // a recovery the perfectly good other copy could have done.
        bool damaged = false;

        byte[]? onStick = stickRoot is not null && VaultStorage.IsSetUp(stickRoot)
            ? ReadableOrNull(() => VaultStorage.ReadVault(stickRoot), ref damaged)
            : null;

        byte[]? onPc = MachineStore.HasMirrorVault(folder)
            ? ReadableOrNull(() => MachineStore.ReadMirrorVault(folder), ref damaged)
            : null;

        if (onStick is null && onPc is null)
            throw new UnlockException(damaged
                ? "The vault on the stick and the spare copy on this PC are both damaged. "
                  + "If you have a backup file, restore from that instead."
                : "There is nothing to recover from - no vault on a stick, and no spare copy on this PC. "
                  + "If you have a backup file, restore from that instead.");

        if (onStick is null) return new VaultSource(onPc!, "the spare copy on this PC", false);
        if (onPc is null) return new VaultSource(onStick, $"your stick ({stickRoot})", true);

        var stick = VaultFile.ReadHeader(onStick);
        var pc = VaultFile.ReadHeader(onPc);
        bool stickIsNewer = stick.Epoch > pc.Epoch || (stick.Epoch == pc.Epoch && stick.Revision >= pc.Revision);

        return stickIsNewer
            ? new VaultSource(onStick, $"your stick ({stickRoot})", true)
            : new VaultSource(onPc, "the spare copy on this PC, which is newer than the stick", false);
    }

    private static byte[]? ReadableOrNull(Func<byte[]> read, ref bool damaged)
    {
        try
        {
            var bytes = read();
            VaultFile.ReadHeader(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;   // Gone since we looked - unplugged, say. Not damage.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            damaged = true;
            return null;
        }
    }

    /// <summary>
    /// Mint a brand new master key, USB secret and 12-word phrase onto a stick, and move the
    /// vault to the next epoch. Everything that knew the old keys - the old stick, every PC
    /// set up against it - is cut off at once.
    ///
    /// Returns the new vault bytes, the new phrase to write down, and everything that was in
    /// the vault when the old stick went missing, because those passwords should be changed.
    /// </summary>
    public static (byte[] Vault, string NewPhrase, List<Entry> ToChange) ReplaceStick(
        byte[] currentVault, string oldPhrase, string newStickRoot)
    {
        if (!Directory.Exists(newStickRoot))
            throw new UnlockException($"{newStickRoot} is not there. Plug the new stick in first.");

        var header = VaultFile.ReadHeader(currentVault);

        // Never write over somebody else's vault. Writing onto the same vault's stick is
        // fine - that is just re-keying in place.
        if (VaultStorage.IsSetUp(newStickRoot))
        {
            var alreadyThere = VaultFile.ReadHeader(VaultStorage.ReadVault(newStickRoot));
            if (alreadyThere.VaultId != header.VaultId)
                throw new UnlockException(
                    $"{newStickRoot} already holds a different PassVault. Use an empty stick so nothing is lost.");
        }

        var oldMasterKey = Enrolment.OpenWithPhraseOrExplain(currentVault, oldPhrase);

        try
        {
            var (fresh, newMasterKey, newPhrase) = VaultFile.ReKey(currentVault, oldMasterKey);

            try
            {
                var toChange = VaultFile.ReadEntries(fresh, newMasterKey)
                    .Where(e => !e.Deleted)
                    .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                Directory.CreateDirectory(VaultStorage.FolderOn(newStickRoot));
                VaultStorage.WriteVault(newStickRoot, fresh);
                VaultStorage.WriteFileSafely(VaultStorage.KeyfilePathOn(newStickRoot), Keyfile.Create().ToBytes());

                return (fresh, newPhrase, toChange);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(newMasterKey);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldMasterKey);
        }
    }

    /// <summary>
    /// Write a spare copy of the encrypted vault somewhere the user chooses. This file plus
    /// the 12 words is a complete recovery - it needs no stick and no PC. It is safe to keep
    /// in a cloud folder because it is encrypted, but do NOT copy the whole stick there:
    /// that hands over the USB secret as well.
    /// </summary>
    public static void ExportBackup(byte[] vault, string path) => File.WriteAllBytes(path, vault);

    public static string SuggestBackupName(byte[] vault, DateTime today)
    {
        var header = VaultFile.ReadHeader(vault);
        return string.Create(CultureInfo.InvariantCulture,
            $"passvault-backup-{today:yyyy-MM-dd}-epoch{header.Epoch}-rev{header.Revision}.pv");
    }

    /// <summary>
    /// Read a backup file back. Only checks that it really is a vault and that the phrase
    /// opens it - restoring it onto a stick is <see cref="ReplaceStick"/>.
    /// </summary>
    public static byte[] OpenBackup(string path, string phrase)
    {
        if (!File.Exists(path)) throw new UnlockException($"There is no file at {path}.");

        byte[] vault;
        try
        {
            vault = File.ReadAllBytes(path);
            VaultFile.ReadHeader(vault);
        }
        catch (InvalidDataException)
        {
            throw new UnlockException("That file is not a PassVault backup.");
        }

        CryptographicOperations.ZeroMemory(Enrolment.OpenWithPhraseOrExplain(vault, phrase));
        return vault;
    }
}
