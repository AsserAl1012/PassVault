using System.Text.Json;
using PassVault.Core.Vault;

namespace PassVault.Tests;

/// <summary>
/// Opens tests/fixture-vault.pv - the same real vault file the Swift tests open. If the two
/// apps ever disagree about a byte of the format, one of these two test files goes red.
///
/// The data in it is made up and its phrase is public on purpose.
/// </summary>
public class FixtureVaultTests
{
    private static readonly byte[] Vault = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixture-vault.pv"));
    private static readonly JsonElement Info = JsonDocument
        .Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixture.json")))
        .RootElement.Clone();

    private static string Phrase => Info.GetProperty("phrase").GetString()!;

    [Fact]
    public void The_header_says_what_the_fixture_file_says_it_should()
    {
        var header = VaultFile.ReadHeader(Vault);

        Assert.Equal(VaultFile.Format, header.FormatVersion);
        Assert.Equal(Info.GetProperty("vaultId").GetString(), header.VaultId);
        Assert.Equal(Info.GetProperty("epoch").GetInt32(), header.Epoch);
        Assert.Equal(Info.GetProperty("revision").GetInt64(), header.Revision);
    }

    [Fact]
    public void Every_credential_comes_back_exactly()
    {
        var entries = VaultFile.ReadEntries(Vault, VaultFile.UnlockWithPhrase(Vault, Phrase));
        var expected = Info.GetProperty("entries").EnumerateArray().ToList();

        Assert.Equal(expected.Count, entries.Count);

        for (int i = 0; i < entries.Count; i++)
        {
            Assert.Equal(expected[i].GetProperty("id").GetString(), entries[i].Id);
            Assert.Equal(expected[i].GetProperty("name").GetString(), entries[i].Name);
            Assert.Equal(Text(expected[i], "username"), entries[i].Username);
            Assert.Equal(Text(expected[i], "email"), entries[i].Email);
            Assert.Equal(Text(expected[i], "password"), entries[i].Password);
            Assert.Equal(Text(expected[i], "notes"), entries[i].Notes);
            Assert.Equal(expected[i].GetProperty("updatedAt").GetInt64(), entries[i].UpdatedAt);
            Assert.Equal(expected[i].GetProperty("deleted").GetBoolean(), entries[i].Deleted);
        }
    }

    [Fact]
    public void A_newline_inside_a_field_survives()
    {
        var entries = VaultFile.ReadEntries(Vault, VaultFile.UnlockWithPhrase(Vault, Phrase));
        var withNotes = entries.Single(e => e.Name == "bank.example");

        Assert.Equal("second line\nwith a newline", withNotes.Notes);
    }

    [Fact]
    public void Deleted_entries_are_kept_as_tombstones()
    {
        var entries = VaultFile.ReadEntries(Vault, VaultFile.UnlockWithPhrase(Vault, Phrase));

        Assert.Single(entries, e => e.Deleted);
        Assert.Equal(2, entries.Count(e => !e.Deleted));
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;
}
