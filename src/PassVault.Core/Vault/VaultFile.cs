using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PassVault.Core.Crypto;
using PassVault.Core.Model;

namespace PassVault.Core.Vault;

/// <summary>What the vault file says about itself. Readable without unlocking anything.</summary>
public sealed class VaultHeader
{
    public int FormatVersion { get; set; } = VaultFile.Format;

    /// <summary>Base64, 16 bytes. Set once when the vault is created, never changes.</summary>
    public string VaultId { get; set; } = "";

    /// <summary>Goes up by one on every re-key after a theft.</summary>
    public int Epoch { get; set; } = 1;

    /// <summary>Goes up by one on every save. Used to spot an old vault being put back.</summary>
    public long Revision { get; set; } = 1;

    public KdfSettings Kdf { get; set; } = KdfSettings.Default;

    public List<KeySlot> Slots { get; set; } = [];

    public KeySlot Slot(string name)
        => Slots.SingleOrDefault(s => s.Name == name)
           ?? throw new InvalidOperationException($"This vault has no \"{name}\" way in.");

    public bool HasSlot(string name) => Slots.Any(s => s.Name == name);
}

/// <summary>
/// Reading and writing vault.pv. Layout is in DESIGN.md and is frozen:
/// "PVLT" || formatVersion(2) || headerLength(4) || header || nonce(12) || ciphertext || tag(16)
/// </summary>
public static class VaultFile
{
    public const int Format = 1;
    private const int PrefixSize = 4 + 2 + 4;

    private static readonly byte[] Magic = "PVLT"u8.ToArray();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Make a brand new empty vault. The phrase is shown to the user once and never stored.</summary>
    public static (byte[] File, byte[] MasterKey, string Phrase) Create()
    {
        var masterKey = RandomNumberGenerator.GetBytes(Aead.KeySize);
        var phrase = Mnemonic.Generate();
        var vaultId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var kdf = KdfSettings.Default;

        var header = new VaultHeader { VaultId = vaultId, Kdf = kdf };
        header.Slots.Add(KeyWrap.WrapWithPhrase(masterKey, phrase, vaultId, kdf));

        return (Pack(header, masterKey, []), masterKey, phrase);
    }

    public static VaultHeader ReadHeader(byte[] file) => Unpack(file).Header;

    /// <summary>Open the vault with the 12-word phrase. Returns the master key.</summary>
    public static byte[] UnlockWithPhrase(byte[] file, string phrase)
    {
        var header = ReadHeader(file);
        return KeyWrap.UnwrapWithPhrase(header.Slot(KeyWrap.RecoverySlot), phrase, header.VaultId, header.Kdf);
    }

    public static List<Entry> ReadEntries(byte[] file, byte[] masterKey)
    {
        var json = OpenBody(file, masterKey);
        return JsonSerializer.Deserialize<List<Entry>>(json, Json) ?? [];
    }

    /// <summary>
    /// True only if the body was sealed with this master key under these exact header bytes.
    ///
    /// Until this passes, nothing in the header is authenticated - the epoch and revision are
    /// plain text anyone holding the stick can edit. Check this before recording them or
    /// copying the file anywhere.
    /// </summary>
    public static bool IsAuthentic(byte[] file, byte[] masterKey)
    {
        try
        {
            CryptographicOperations.ZeroMemory(OpenBody(file, masterKey));
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Write the entries back. The revision goes up by one every save, and always lands above
    /// <paramref name="revisionSeen"/> - the newest revision of this vault the device has
    /// already seen. Counting up from an older copy (one restored with the 12 words, or put
    /// back while the vault was open) otherwise produced a save the rollback check then
    /// refused as "older than the one this machine last saw".
    /// </summary>
    public static byte[] Save(byte[] file, byte[] masterKey, IEnumerable<Entry> entries, long revisionSeen = 0)
    {
        // The save carries this file's header forward - its recovery slot, salt and Argon2
        // settings - and seals it again. Unless the header still matches its own seal, that
        // turns a damaged or doctored recovery slot into a perfectly valid vault the 12 words
        // no longer open, on the stick and the spare copy alike.
        if (!IsAuthentic(file, masterKey))
            throw new CryptographicException("The vault there no longer matches its own seal.");

        var header = ReadHeader(file);
        header.Revision = Math.Max(header.Revision, revisionSeen) + 1;
        return Pack(header, masterKey, entries);
    }

    /// <summary>
    /// Start a fresh epoch after a stick is stolen: brand new master key, brand new phrase,
    /// and every old copy of the vault is now out of date. The caller writes a new keyfile
    /// with a new USB secret and re-enrols each machine's PIN.
    /// </summary>
    public static (byte[] File, byte[] MasterKey, string Phrase) ReKey(byte[] file, byte[] oldMasterKey)
    {
        var old = ReadHeader(file);
        var entries = ReadEntries(file, oldMasterKey);

        var masterKey = RandomNumberGenerator.GetBytes(Aead.KeySize);
        var phrase = Mnemonic.Generate();
        var kdf = KdfSettings.Default;

        var header = new VaultHeader
        {
            VaultId = old.VaultId,
            Epoch = old.Epoch + 1,
            Revision = old.Revision + 1,
            Kdf = kdf,
        };
        header.Slots.Add(KeyWrap.WrapWithPhrase(masterKey, phrase, header.VaultId, kdf));

        return (Pack(header, masterKey, entries), masterKey, phrase);
    }

    /// <summary>
    /// True if <paramref name="candidate"/> is older than what this device has already seen -
    /// someone putting an old vault back to undo a password change.
    /// </summary>
    public static bool IsRollback(VaultHeader candidate, int highestEpochSeen, long highestRevisionSeen)
        => candidate.Epoch < highestEpochSeen
           || (candidate.Epoch == highestEpochSeen && candidate.Revision < highestRevisionSeen);

    private static byte[] Pack(VaultHeader header, byte[] masterKey, IEnumerable<Entry> entries)
    {
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, Json);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(entries.ToList(), Json);
        var body = Aead.Seal(masterKey, plaintext, headerBytes);
        CryptographicOperations.ZeroMemory(plaintext);

        var file = new byte[PrefixSize + headerBytes.Length + body.Length];
        Magic.CopyTo(file, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), Format);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(6), (uint)headerBytes.Length);
        headerBytes.CopyTo(file, PrefixSize);
        body.CopyTo(file, PrefixSize + headerBytes.Length);
        return file;
    }

    private static byte[] OpenBody(byte[] file, byte[] masterKey)
    {
        var (_, headerBytes, body) = Unpack(file);
        return Aead.Open(masterKey, body, headerBytes);
    }

    private static (VaultHeader Header, byte[] HeaderBytes, byte[] Body) Unpack(byte[] file)
    {
        if (file.Length < PrefixSize || !file.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("That is not a PassVault file.");

        int version = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4));
        if (version != Format)
            throw new InvalidDataException($"This vault was written by a newer version of the app (format {version}).");

        uint headerLength = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(6));
        if (PrefixSize + (long)headerLength > file.Length)
            throw new InvalidDataException("This vault file is damaged - the header length does not fit.");

        // The header is authenticated as the exact bytes on disk. Never re-encode it first:
        // .NET and Swift do not write identical JSON.
        var headerBytes = file[PrefixSize..(PrefixSize + (int)headerLength)];
        var header = JsonSerializer.Deserialize<VaultHeader>(headerBytes, Json)
            ?? throw new InvalidDataException("This vault file is damaged - the header is empty.");

        return (header, headerBytes, file[(PrefixSize + (int)headerLength)..]);
    }
}
