using System.Security.Cryptography;
using PassVault.Core.Machine;
using PassVault.Core.Model;
using PassVault.Core.Usb;

namespace PassVault.Core.Vault;

/// <summary>Nothing was written, for the reason in the message - a sentence for the user.</summary>
public sealed class SaveRefusedException(string message) : Exception(message);

/// <summary>
/// An unlocked vault as a front end holds it: the credentials, and the one safe way to change
/// them. The terminal app and the Linux window app both use this, so every way into PassVault
/// saves by the same rules. (The Windows window still has its own copy of these rules.)
///
/// Each change is all or nothing: if the save fails, the credential is put back exactly as it
/// was. A half-applied change left in memory used to be committed by the next save of
/// something else.
/// </summary>
public sealed class OpenVault
{
    private readonly string _folder;
    private readonly byte[] _masterKey;

    // Which vault was opened, and how new it was - checked again before every save. A save
    // replaces the whole file, so writing over a different, re-keyed or newer vault destroys it.
    private readonly string _vaultId;
    private readonly int _epoch;
    private long _revision;

    public string StickRoot { get; }

    /// <summary>Every credential, deleted ones included: they stay as tombstones (see Entry).</summary>
    public List<Entry> All { get; }

    public IEnumerable<Entry> Living => All.Where(e => !e.Deleted);

    public OpenVault(string folder, string stickRoot, byte[] masterKey)
    {
        _folder = folder;
        _masterKey = masterKey;
        StickRoot = stickRoot;

        var vault = VaultStorage.ReadVault(stickRoot);
        var header = VaultFile.ReadHeader(vault);
        _vaultId = header.VaultId;
        _epoch = header.Epoch;
        _revision = header.Revision;
        All = VaultFile.ReadEntries(vault, masterKey);
    }

    /// <summary>What to show, alphabetically.</summary>
    public List<Entry> Find(string search)
        => Living.Where(e => e.Matches(search)).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <returns>A warning when the stick took the save but this PC's spare copy did not; otherwise null.</returns>
    public string? Add(Entry entry)
    {
        All.Add(entry);
        try
        {
            return Save();
        }
        catch
        {
            All.Remove(entry);
            throw;
        }
    }

    /// <summary>Apply an edit and save it, or - if the save fails - put the credential back as it was.</summary>
    public string? Change(Entry entry, Action<Entry> edit)
    {
        var before = Copy(entry);
        edit(entry);
        entry.UpdatedAt = Entry.Now();
        try
        {
            return Save();
        }
        catch
        {
            Restore(entry, before);
            throw;
        }
    }

    /// <summary>Kept as an empty tombstone, so a deleted credential cannot come back from another device once sync exists.</summary>
    public string? Delete(Entry entry) => Change(entry, e =>
    {
        e.Deleted = true;
        e.Name = "";
        e.Username = e.Email = e.Password = e.Notes = null;
    });

    /// <summary>Forget the key. Nothing can be read or saved after this.</summary>
    public void Close() => CryptographicOperations.ZeroMemory(_masterKey);

    private string? Save()
    {
        byte[] onDisk;
        VaultHeader header;
        try
        {
            onDisk = VaultStorage.ReadVault(StickRoot);
            header = VaultFile.ReadHeader(onDisk);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new SaveRefusedException($"Could not reach your stick - {Plain(ex)}. Nothing was written.");
        }

        if (header.VaultId != _vaultId)
            throw new SaveRefusedException("The stick holds a different vault from the one you opened. Nothing was written.");
        if (header.Epoch != _epoch)
            throw new SaveRefusedException("This stick has been re-keyed since you opened it. Lock and unlock, then try again.");
        if (header.Revision > _revision)
            throw new SaveRefusedException(
                "Something else has changed this vault since you opened it. Lock and unlock, then make your change again.");

        byte[] saved;
        try
        {
            // Land above everything this PC has seen, not just above the copy on the stick - after a
            // restore with the 12 words, counting on from the stick gave saves the next PIN unlock
            // refused as a rollback.
            saved = VaultFile.Save(onDisk, _masterKey, All, Enrolment.RevisionSeen(_folder, header));
        }
        catch (CryptographicException)
        {
            throw new SaveRefusedException("The vault on your stick has been changed or damaged since it was opened - "
                                           + "it no longer matches its own seal. Nothing was written.");
        }

        string? warning = null;
        try
        {
            Enrolment.SaveVault(_folder, StickRoot, saved);
        }
        catch (SpareCopyException behind)
        {
            warning = behind.Message;   // saved: the stick has it, only this PC's copy is behind
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SaveRefusedException($"Could not save - {Plain(ex)}. Is your stick still plugged in?");
        }

        _revision = VaultFile.ReadHeader(saved).Revision;
        return warning;
    }

    /// <summary>A plain sentence instead of a .NET message with file paths in it.</summary>
    private static string Plain(Exception ex) => ex switch
    {
        FileNotFoundException => "there is no vault there",
        DirectoryNotFoundException => "the stick is not there any more",
        UnauthorizedAccessException => "this PC would not let PassVault write to it",
        InvalidDataException => "the file there is damaged",
        _ => "the stick stopped responding",
    };

    private static Entry Copy(Entry e) => new()
    {
        Id = e.Id, Name = e.Name, Username = e.Username, Email = e.Email,
        Password = e.Password, Notes = e.Notes, UpdatedAt = e.UpdatedAt, Deleted = e.Deleted,
    };

    private static void Restore(Entry into, Entry from)
    {
        into.Name = from.Name;
        into.Username = from.Username;
        into.Email = from.Email;
        into.Password = from.Password;
        into.Notes = from.Notes;
        into.UpdatedAt = from.UpdatedAt;
        into.Deleted = from.Deleted;
    }
}
