using System.Security.Cryptography;
using PassVault.Core.Machine;
using PassVault.Core.Model;
using PassVault.Core.Usb;
using PassVault.Core.Vault;

namespace PassVault.Tests;

/// <summary>The one safe way every front end changes a vault.</summary>
public class OpenVaultTests : IDisposable
{
    private readonly string _stick = NewFolder("stick");
    private readonly string _pc = NewFolder("pc");
    private readonly byte[] _masterKey;

    public OpenVaultTests()
    {
        var (phrase, _) = VaultStorage.Initialise(_stick);
        _masterKey = Enrolment.Enrol(_pc, _stick, phrase, "4821", RandomNumberGenerator.GetBytes(32), DeviceProtection.Tpm).MasterKey;
    }

    private static string NewFolder(string tag)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pv-{tag}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var folder in new[] { _stick, _pc })
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private OpenVault Open() => new(_pc, _stick, (byte[])_masterKey.Clone());

    [Fact]
    public void Added_changed_and_deleted_credentials_are_on_the_stick_next_time()
    {
        var vault = Open();
        var github = new Entry { Name = "github.com", Username = "asser", Password = "one" };
        vault.Add(github);
        vault.Add(new Entry { Name = "bank", Password = "two" });
        vault.Change(github, e => e.Password = "three");
        vault.Delete(vault.Find("bank")[0]);

        var again = Open();
        Assert.Equal(["github.com"], again.Living.Select(e => e.Name));
        Assert.Equal("three", again.Find("git")[0].Password);
        Assert.Single(again.All, e => e.Deleted && e.Password is null);   // a tombstone, emptied
    }

    /// <summary>A change that did not reach the stick must not stay in memory, where the next save would commit it.</summary>
    [Fact]
    public void A_change_that_could_not_be_saved_is_put_back_exactly()
    {
        var vault = Open();
        var entry = new Entry { Name = "github.com", Password = "before" };
        vault.Add(entry);
        Directory.Move(_stick, _stick + "-pulled-out");

        try
        {
            Assert.Throws<SaveRefusedException>(() => vault.Change(entry, e => e.Password = "after"));
            Assert.Throws<SaveRefusedException>(() => vault.Delete(entry));
            Assert.Throws<SaveRefusedException>(() => vault.Add(new Entry { Name = "new" }));

            Assert.Equal("before", entry.Password);
            Assert.False(entry.Deleted);
            Assert.Equal(["github.com"], vault.Living.Select(e => e.Name));
        }
        finally
        {
            Directory.Move(_stick + "-pulled-out", _stick);
        }
    }

    /// <summary>Two windows open on one vault: the second save would silently undo the first.</summary>
    [Fact]
    public void A_vault_something_else_saved_since_is_not_written_over()
    {
        var first = Open();
        var second = Open();
        first.Add(new Entry { Name = "from the first" });

        var refused = Assert.Throws<SaveRefusedException>(() => second.Add(new Entry { Name = "from the second" }));

        Assert.Contains("Lock and unlock", refused.Message);
        Assert.Equal(["from the first"], Open().Living.Select(e => e.Name));
    }

    [Fact]
    public void A_different_vault_on_the_stick_is_never_written_over()
    {
        var vault = Open();
        foreach (var file in Directory.GetFiles(VaultStorage.FolderOn(_stick))) File.Delete(file);
        VaultStorage.Initialise(_stick);
        var theirs = File.ReadAllBytes(VaultStorage.VaultPathOn(_stick));

        Assert.Throws<SaveRefusedException>(() => vault.Add(new Entry { Name = "mine" }));
        Assert.Equal(theirs, File.ReadAllBytes(VaultStorage.VaultPathOn(_stick)));
    }

    [Fact]
    public void Search_finds_by_any_field_in_name_order_and_skips_deleted_ones()
    {
        var vault = Open();
        vault.Add(new Entry { Name = "Zebra mail", Email = "me@example.com" });
        vault.Add(new Entry { Name = "apple", Notes = "example account" });
        vault.Add(new Entry { Name = "example gone" });
        vault.Delete(vault.Find("gone")[0]);

        Assert.Equal(["apple", "Zebra mail"], vault.Find("EXAMPLE").Select(e => e.Name));
    }
}
