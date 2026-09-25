using PassVault.Core.Import;
using PassVault.Core.Model;

namespace PassVault.Tests;

/// <summary>What any export file can throw at the reader, whichever product wrote it.</summary>
public class CsvImportTests
{
    private static List<Entry> Read(string csv) => CsvImport.Read(csv).Entries;

    [Fact]
    public void Quoted_fields_keep_their_commas_quote_marks_and_line_breaks()
    {
        var e = Assert.Single(Read(
            "name,url,username,password,note\r\n"
            + "\"Bank, main\",https://bank.example,alice,\"pa,ss\"\"word\",\"line one\nline two\"\r\n"));

        Assert.Equal("Bank, main", e.Name);
        Assert.Equal("pa,ss\"word", e.Password);
        Assert.Equal("Website: https://bank.example\n\nline one\nline two", e.Notes);
    }

    [Fact]
    public void Passwords_come_in_exactly_as_exported_spaces_and_all()
    {
        var e = Assert.Single(Read("name,url,username,password\nsite,,bob,\" padded \"\n"));
        Assert.Equal(" padded ", e.Password);
    }

    [Fact]
    public void An_email_in_the_login_column_goes_in_Email_and_anything_else_in_Username()
    {
        var entries = Read(
            "name,url,username,password\n"
            + "a,,alice@example.com,p1\n"
            + "b,,alice,p2\n"
            + "c,,not an@email.com,p3\n");

        Assert.Equal((null, "alice@example.com"), (entries[0].Username, entries[0].Email));
        Assert.Equal(("alice", null), (entries[1].Username, entries[1].Email));
        Assert.Equal("not an@email.com", entries[2].Username);
    }

    [Theory]
    [InlineData("https://www.github.com/login", "github.com")]
    [InlineData("https://accounts.google.com/signin", "accounts.google.com")]
    [InlineData("github.com/login", "github.com")]
    [InlineData("\"https://a.example,https://b.example\"", "a.example")]
    [InlineData("", "Imported")]
    public void With_no_name_the_site_comes_from_the_address(string url, string expected)
    {
        var e = Assert.Single(Read($"url,username,password\n{url},bob,pw\n"));
        Assert.Equal(expected, e.Name);
    }

    [Fact]
    public void Columns_are_found_by_name_in_any_order_and_any_case()
    {
        var e = Assert.Single(Read("PASSWORD,Notes,Title\npw,hello,Site\n"));
        Assert.Equal(("Site", "pw", "hello"), (e.Name, e.Password, e.Notes));
    }

    [Fact]
    public void A_byte_order_mark_blank_lines_and_a_missing_last_newline_are_all_fine()
        => Assert.Equal(new[] { "first", "second" }, Read("﻿name,password\n\nfirst,1\n\nsecond,2").Select(e => e.Name));

    [Fact]
    public void A_row_shorter_than_the_header_does_not_throw()
    {
        var e = Assert.Single(Read("name,url,username,password,note\nsite,,bob,pw\n"));
        Assert.Null(e.Notes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("name,url,username\nsite,https://x.example,bob\n")]
    [InlineData("just some text that is not a csv at all")]
    [InlineData("PK\u0003\u0004 binary zip content")]
    public void Something_that_is_not_a_password_export_is_refused_with_a_reason(string text)
    {
        var ex = Assert.Throws<InvalidDataException>(() => CsvImport.Read(text));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void A_zip_gets_told_to_open_it_first()
        => Assert.Contains("ZIP", Assert.Throws<InvalidDataException>(() => CsvImport.Read("PK\u0003\u0004...")).Message);

    [Fact]
    public void Importing_the_same_file_twice_adds_nothing_the_second_time()
    {
        const string csv = "name,username,password\nsite,bob,pw\nother,amy,pw2\n";
        var vault = CsvImport.NotAlreadyIn(Read(csv), []);

        Assert.Equal(2, vault.Count);
        Assert.Empty(CsvImport.NotAlreadyIn(Read(csv), vault));
    }

    [Fact]
    public void A_changed_password_is_not_a_duplicate_and_repeats_inside_one_file_count_once()
    {
        var existing = new List<Entry> { new() { Name = "Site", Username = "bob", Password = "old" } };
        var fresh = CsvImport.NotAlreadyIn(Read("name,username,password\nsite,bob,new\nsite,bob,new\n"), existing);

        Assert.Equal("new", Assert.Single(fresh).Password);
    }

    [Fact]
    public void A_deleted_credential_does_not_block_bringing_it_back()
    {
        var existing = new List<Entry> { new() { Name = "site", Username = "bob", Password = "pw", Deleted = true } };
        Assert.Single(CsvImport.NotAlreadyIn(Read("name,username,password\nsite,bob,pw\n"), existing));
    }
}
