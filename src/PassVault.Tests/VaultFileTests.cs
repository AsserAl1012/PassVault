using System.Security.Cryptography;
using System.Text;
using PassVault.Core.Crypto;
using PassVault.Core.Model;
using PassVault.Core.Vault;

namespace PassVault.Tests;

public class VaultFileTests
{
    private static Entry Sample(string name = "github.com") => new()
    {
        Name = name,
        Username = "asser",
        Email = "asser@example.com",
        Password = "correct horse battery staple",
    };

    [Fact]
    public void A_new_vault_opens_with_its_phrase_and_is_empty()
    {
        var (file, masterKey, phrase) = VaultFile.Create();

        Assert.Equal(masterKey, VaultFile.UnlockWithPhrase(file, phrase));
        Assert.Empty(VaultFile.ReadEntries(file, masterKey));
    }

    [Fact]
    public void Entries_survive_a_save_and_reload()
    {
        var (file, masterKey, phrase) = VaultFile.Create();
        var saved = VaultFile.Save(file, masterKey, [Sample(), Sample("netflix.com")]);

        var reloaded = VaultFile.ReadEntries(saved, VaultFile.UnlockWithPhrase(saved, phrase));

        Assert.Equal(2, reloaded.Count);
        Assert.Equal("github.com", reloaded[0].Name);
        Assert.Equal("correct horse battery staple", reloaded[0].Password);
        Assert.Equal("asser@example.com", reloaded[0].Email);
    }

    [Fact]
    public void Every_save_bumps_the_revision()
    {
        var (file, masterKey, _) = VaultFile.Create();
        Assert.Equal(1, VaultFile.ReadHeader(file).Revision);

        var once = VaultFile.Save(file, masterKey, [Sample()]);
        var twice = VaultFile.Save(once, masterKey, [Sample()]);

        Assert.Equal(2, VaultFile.ReadHeader(once).Revision);
        Assert.Equal(3, VaultFile.ReadHeader(twice).Revision);
    }

    [Fact]
    public void A_save_lands_above_the_newest_revision_already_seen()
    {
        var (file, masterKey, _) = VaultFile.Create();

        Assert.Equal(11, VaultFile.ReadHeader(VaultFile.Save(file, masterKey, [], revisionSeen: 10)).Revision);
        Assert.Equal(2, VaultFile.ReadHeader(VaultFile.Save(file, masterKey, [], revisionSeen: 0)).Revision);
    }

    [Fact]
    public void A_save_never_seals_a_changed_header_again()
    {
        // Sealing it again would make the change - a damaged recovery slot, say - a valid vault
        // that the 12 words no longer open.
        var (file, masterKey, _) = VaultFile.Create();
        var changed = Tamper(file, "\"revision\":1", "\"revision\":7");

        Assert.ThrowsAny<CryptographicException>(() => VaultFile.Save(changed, masterKey, [Sample()]));
    }

    [Fact]
    public void Only_the_right_key_and_the_untouched_header_are_authentic()
    {
        var (file, masterKey, _) = VaultFile.Create();

        Assert.True(VaultFile.IsAuthentic(file, masterKey));
        Assert.False(VaultFile.IsAuthentic(file, RandomNumberGenerator.GetBytes(32)));
        Assert.False(VaultFile.IsAuthentic(Tamper(file, "\"revision\":1", "\"revision\":7"), masterKey));
    }

    [Fact]
    public void The_wrong_phrase_does_not_open_it()
    {
        var (file, _, _) = VaultFile.Create();

        Assert.ThrowsAny<CryptographicException>(() => VaultFile.UnlockWithPhrase(file, Mnemonic.Generate()));
    }

    [Fact]
    public void Editing_the_header_breaks_the_file()
    {
        // The header is authenticated, so changing anything in it - the epoch here, but just
        // as easily the Argon2 cost - makes the vault refuse to open instead of quietly
        // accepting weaker settings.
        var (file, masterKey, _) = VaultFile.Create();
        var tampered = Tamper(file, "\"epoch\":1", "\"epoch\":9");

        Assert.ThrowsAny<CryptographicException>(() => VaultFile.ReadEntries(tampered, masterKey));
    }

    [Fact]
    public void Random_bytes_are_not_mistaken_for_a_vault()
    {
        Assert.Throws<InvalidDataException>(() => VaultFile.ReadHeader(RandomNumberGenerator.GetBytes(200)));
        Assert.Throws<InvalidDataException>(() => VaultFile.ReadHeader([1, 2, 3]));
    }

    [Fact]
    public void A_silly_header_length_is_caught()
    {
        var (file, _, _) = VaultFile.Create();
        var broken = (byte[])file.Clone();
        broken[6] = 0xFF; broken[7] = 0xFF; broken[8] = 0xFF; broken[9] = 0x7F;

        Assert.Throws<InvalidDataException>(() => VaultFile.ReadHeader(broken));
    }

    // ---- re-keying after a stick is stolen ----

    [Fact]
    public void Rekey_keeps_the_entries_and_kills_the_old_phrase()
    {
        var (file, masterKey, oldPhrase) = VaultFile.Create();
        var saved = VaultFile.Save(file, masterKey, [Sample()]);

        var (fresh, newKey, newPhrase) = VaultFile.ReKey(saved, masterKey);

        Assert.Single(VaultFile.ReadEntries(fresh, newKey));
        Assert.Equal("github.com", VaultFile.ReadEntries(fresh, newKey)[0].Name);
        Assert.Equal(newKey, VaultFile.UnlockWithPhrase(fresh, newPhrase));
        Assert.NotEqual(oldPhrase, newPhrase);
        Assert.NotEqual(masterKey, newKey);
        Assert.ThrowsAny<CryptographicException>(() => VaultFile.UnlockWithPhrase(fresh, oldPhrase));
    }

    [Fact]
    public void Rekey_moves_the_vault_to_a_new_epoch()
    {
        var (file, masterKey, _) = VaultFile.Create();
        var (fresh, _, _) = VaultFile.ReKey(file, masterKey);

        Assert.Equal(1, VaultFile.ReadHeader(file).Epoch);
        Assert.Equal(2, VaultFile.ReadHeader(fresh).Epoch);
        Assert.Equal(VaultFile.ReadHeader(file).VaultId, VaultFile.ReadHeader(fresh).VaultId);
    }

    // ---- putting an old vault back ----

    [Theory]
    [InlineData(1, 5, 1, 5, false)]   // exactly what we saw before
    [InlineData(1, 6, 1, 5, false)]   // newer, fine
    [InlineData(2, 1, 1, 9, false)]   // new epoch, revision restarts - still fine
    [InlineData(1, 4, 1, 5, true)]    // older save put back
    [InlineData(1, 9, 2, 1, true)]    // pre-rekey vault put back
    public void An_older_vault_being_put_back_is_spotted(
        int epoch, long revision, int seenEpoch, long seenRevision, bool expected)
    {
        var header = new VaultHeader { Epoch = epoch, Revision = revision };

        Assert.Equal(expected, VaultFile.IsRollback(header, seenEpoch, seenRevision));
    }

    private static byte[] Tamper(byte[] file, string find, string replaceWith)
    {
        var needle = Encoding.UTF8.GetBytes(find);
        var replacement = Encoding.UTF8.GetBytes(replaceWith);
        if (needle.Length != replacement.Length)
            throw new ArgumentException("Replacement must be the same length so the header length stays right.");

        int at = IndexOf(file, needle);
        Assert.True(at >= 0, $"Could not find {find} in the header.");

        var copy = (byte[])file.Clone();
        replacement.CopyTo(copy, at);
        return copy;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }
}
