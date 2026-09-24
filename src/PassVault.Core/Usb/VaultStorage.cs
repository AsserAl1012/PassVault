using PassVault.Core.Vault;

namespace PassVault.Core.Usb;

/// <summary>
/// Reading and writing the two files that live on the stick:
/// <c>&lt;drive&gt;\PassVault\keyfile.pvk</c> and <c>&lt;drive&gt;\PassVault\vault.pv</c>
///
/// Everything here works on any folder, so the same code handles a real stick, a stand-in
/// folder in a test, and later the mirror copy kept on each PC.
/// </summary>
public static class VaultStorage
{
    public const string FolderName = "PassVault";
    public const string KeyfileName = "keyfile.pvk";
    public const string VaultName = "vault.pv";

    public static string FolderOn(string root) => Path.Combine(root, FolderName);
    public static string KeyfilePathOn(string root) => Path.Combine(FolderOn(root), KeyfileName);
    public static string VaultPathOn(string root) => Path.Combine(FolderOn(root), VaultName);

    public static bool IsSetUp(string root)
        => File.Exists(KeyfilePathOn(root)) && VaultExists(root);

    /// <summary>
    /// Set up a fresh stick: a new vault, a new keyfile, a new 12-word phrase.
    /// The phrase is returned once and never stored - write it down or lose it.
    /// Refuses to run if this stick is already set up.
    /// </summary>
    public static (string Phrase, Keyfile Keyfile) Initialise(string root)
    {
        if (IsSetUp(root))
            throw new InvalidOperationException(
                "This stick is already set up. Setting it up again would throw away the vault on it.");

        Directory.CreateDirectory(FolderOn(root));

        var (file, masterKey, phrase) = VaultFile.Create();
        var keyfile = Keyfile.Create();

        WriteFileSafely(VaultPathOn(root), file);
        WriteFileSafely(KeyfilePathOn(root), keyfile.ToBytes());

        Array.Clear(masterKey);
        return (phrase, keyfile);
    }

    public static Keyfile ReadKeyfile(string root)
    {
        var path = KeyfilePathOn(root);
        if (!File.Exists(path))
            throw new FileNotFoundException("There is no PassVault keyfile on this drive.", path);

        return Keyfile.FromBytes(File.ReadAllBytes(path));
    }

    public static bool VaultExists(string root)
        => File.Exists(VaultPathOn(root)) || File.Exists(VaultPathOn(root) + BackupSuffix);

    /// <summary>
    /// Read the vault. If the stick was pulled out mid-save, the main file can be missing
    /// while the previous copy is still there - so fall back to it rather than failing.
    /// </summary>
    public static byte[] ReadVault(string root)
    {
        var path = VaultPathOn(root);
        FinishInterruptedWrite(path);

        if (File.Exists(path)) return File.ReadAllBytes(path);

        var backup = path + BackupSuffix;
        if (File.Exists(backup)) return File.ReadAllBytes(backup);

        throw new FileNotFoundException("There is no vault on this drive.", path);
    }

    /// <summary>
    /// Pick up where WriteFileSafely was interrupted.
    ///
    /// If the stick was pulled between moving the old file aside and moving the new one into
    /// place, the newest save is sitting there complete, under .tmp. Putting it back is not
    /// just tidiness: falling through to the .bak instead hands back a vault one save older
    /// than this machine has already seen, and the rollback check then accuses the user of
    /// putting an old copy back. Two safety features were fighting each other.
    ///
    /// The .tmp is only ever consulted when the real file is missing, and it is written in
    /// full before either move happens - so it cannot be a half-written file.
    /// </summary>
    private static void FinishInterruptedWrite(string path)
    {
        var temp = path + TempSuffix;
        if (File.Exists(path) || !File.Exists(temp)) return;

        try { File.Move(temp, path); }
        catch (IOException) { /* Read-only stick. The .bak below still opens. */ }
        catch (UnauthorizedAccessException) { }
    }

    public static void WriteVault(string root, byte[] file) => WriteFileSafely(VaultPathOn(root), file);

    public const string BackupSuffix = ".bak";
    public const string TempSuffix = ".tmp";

    /// <summary>
    /// Never write straight over the vault. Write a new file first, move the old one aside,
    /// then put the new one in place - so pulling the stick out at the wrong moment leaves
    /// you with one good copy instead of half a file.
    /// </summary>
    public static void WriteFileSafely(string path, byte[] bytes)
    {
        var temp = path + TempSuffix;
        var backup = path + BackupSuffix;

        File.WriteAllBytes(temp, bytes);

        if (File.Exists(path))
        {
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(path, backup);
        }

        File.Move(temp, path);
    }
}
