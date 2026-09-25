using System.Text;
using PassVault.Core.Model;

namespace PassVault.Core.Import;

/// <summary>What was in an export: the credentials to bring in, and how many rows were not credentials.</summary>
public sealed record ImportFile(List<Entry> Entries, int LeftOut);

/// <summary>
/// Reads the CSV file a browser or another password manager exports, and turns each row into a
/// credential. Covers Chrome, Edge, Brave, Opera, Firefox, Safari, Bitwarden, LastPass,
/// 1Password, KeePassXC, KeePass 2, Proton Pass, Dashlane and NordPass - each checked against
/// the product's own source or a real export (see CsvImportFormatTests).
///
/// Columns are found by header name, not position. Every product names the same things a little
/// differently ("name", "title", "account"), and these lists are nearly all of the difference.
/// </summary>
public static class CsvImport
{
    private static readonly string[] NameColumns = ["name", "title", "account"];
    private static readonly string[] UrlColumns = ["url", "login_uri", "web site", "website"];
    private static readonly string[] LoginColumns = ["username", "login_username", "login name"];
    private static readonly string[] EmailColumns = ["email"];
    private static readonly string[] PasswordColumns = ["password", "login_password"];
    private static readonly string[] NotesColumns = ["notes", "note", "extra", "comments"];

    /// <summary>
    /// Things PassVault has no field for, kept at the end of the notes rather than dropped. The
    /// two-factor secret matters most: lose it and close the old manager, and you are locked
    /// out of those accounts.
    /// </summary>
    private static readonly (string Label, string[] Columns)[] KeptInNotes =
    [
        ("Two-factor secret", ["otpauth", "login_totp", "totp", "otpurl", "otpsecret"]),
        ("Also signs in as", ["username2", "username3"]),
        ("Other websites", ["additional_urls"]),
        ("Other fields", ["fields", "custom_fields"]),
    ];

    /// <summary>
    /// Where a file has a type column, only these rows are credentials. The rest are cards,
    /// identities, folders and so on - and Proton Pass puts a card's number and CVV in its note
    /// column, so bringing them in as notes would be worse than leaving them out.
    /// </summary>
    private static readonly string[] CredentialTypes = ["", "login", "password", "note"];

    /// <summary>
    /// KeePass 2's "KeePass CSV (1.x)" export escapes with backslashes (\" and \\) instead of
    /// doubling quotes. Read as ordinary CSV, any password holding a quote or a backslash would
    /// come in wrong without a word said - so that one exact header switches the rule.
    /// </summary>
    private const string KeePass1Header = "\"Account\",\"Login Name\",\"Password\",\"Web Site\",\"Comments\"";

    /// <summary>The credentials in a CSV export. Throws <see cref="InvalidDataException"/> with a plain reason if it is not one.</summary>
    public static ImportFile Read(string text)
    {
        text = text.TrimStart('﻿');

        if (text.StartsWith("PK\u0003\u0004"))
            throw new InvalidDataException(
                "That is a ZIP file. Open it and pick the CSV file inside - for Dashlane, that is credentials.csv.");

        var rows = Rows(text, backslashEscapes: text.StartsWith(KeePass1Header));
        if (rows.Count == 0) throw new InvalidDataException("That file is empty.");

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToArray();
        int Find(string[] names) => names.Select(n => Array.IndexOf(header, n)).FirstOrDefault(i => i >= 0, -1);

        int name = Find(NameColumns), url = Find(UrlColumns), login = Find(LoginColumns), email = Find(EmailColumns),
            password = Find(PasswordColumns), notes = Find(NotesColumns), type = Find(["type"]), group = Find(["group"]);
        var kept = KeptInNotes.Select(k => (k.Label, Columns: k.Columns.Select(c => Array.IndexOf(header, c)).Where(i => i >= 0).ToArray())).ToArray();

        if (password < 0)
            throw new InvalidDataException(
                "That is not a password export. Pick the CSV your browser or old password manager saved.");

        // The LastPass Chrome extension writes & < > as &amp; &lt; &gt; inside its export. Left
        // alone, a password containing & would come in as a different password.
        bool lastPass = header.Contains("grouping") && header.Contains("extra");

        var entries = new List<Entry>();
        int leftOut = 0;

        foreach (var row in rows.Skip(1))
        {
            string Cell(int i)
            {
                var v = i >= 0 && i < row.Length ? row[i] : "";
                return lastPass ? v.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&") : v;
            }

            if (!IsCredential(Cell(type), Cell(url), Cell(notes), Cell(group))) { leftOut++; continue; }

            var extras = kept
                .Select(k => (k.Label, Value: string.Join("\n", k.Columns.Select(Cell).Where(v => v.Length > 0))))
                .Where(k => k.Value.Length > 0)
                .Select(k => $"{k.Label}: {k.Value}");

            var entry = ToEntry(Cell(name), Cell(url), Cell(login), Cell(email), Cell(password), Cell(notes), extras);
            if (entry is not null) entries.Add(entry);
        }

        return new ImportFile(entries, leftOut);
    }

    private static bool IsCredential(string type, string url, string notes, string group)
    {
        if (!CredentialTypes.Contains(type.Trim().ToLowerInvariant())) return false;

        // Firefox exports its own account-sync secret as a "login" - a JSON blob, not a website.
        if (url.Equals("chrome://FirefoxAccounts", StringComparison.OrdinalIgnoreCase)) return false;

        // LastPass: empty folders are rows at http://group, and cards, bank accounts and
        // addresses are "secure notes" whose text starts NoteType:.
        if (url.Equals("http://group", StringComparison.OrdinalIgnoreCase)) return false;
        if (notes.StartsWith("NoteType:", StringComparison.Ordinal)) return false;

        // KeePassXC exports its Recycle Bin too. Deleted things should stay deleted.
        return !group.Split('/').Contains("Recycle Bin");
    }

    /// <summary>
    /// Leaves out anything already in the vault, so importing the same file twice - which people
    /// do, to be sure - does not double everything up.
    /// </summary>
    public static List<Entry> NotAlreadyIn(IEnumerable<Entry> incoming, IEnumerable<Entry> existing)
    {
        var have = existing.Where(e => !e.Deleted).Select(Key).ToHashSet();
        var fresh = new List<Entry>();
        foreach (var e in incoming)
            if (have.Add(Key(e))) fresh.Add(e);   // Add is false for a repeat, inside the file too
        return fresh;
    }

    private static (string, string, string, string) Key(Entry e)
        => (e.Name.ToLowerInvariant(), e.Username ?? "", e.Email ?? "", e.Password ?? "");

    private static Entry? ToEntry(string name, string url, string login, string email, string password,
                                  string notes, IEnumerable<string> extras)
    {
        if (url.Equals("http://sn", StringComparison.OrdinalIgnoreCase)) url = "";   // a LastPass note, not a site

        var kept = extras.ToList();
        if (name.Length + url.Length + login.Length + email.Length + password.Length + notes.Length == 0 && kept.Count == 0)
            return null;   // a blank line

        // One login column goes where it belongs: an email address in Email, anything else in
        // Username. Most exports have a single column holding either.
        if (email.Length == 0 && LooksLikeEmail(login)) (email, login) = (login, "");

        // PassVault has no website field, so the address goes at the top of the notes, and the
        // rest at the bottom. Nothing in the export is thrown away.
        var parts = new List<string>();
        if (url.Length > 0) parts.Add($"Website: {url}");
        if (notes.Length > 0) parts.Add(notes);
        parts.AddRange(kept);

        return new Entry
        {
            Name = name.Trim().Length > 0 ? name.Trim() : SiteOf(url) ?? "Imported",
            Username = Nothing(login),
            Email = Nothing(email),
            Password = Nothing(password),   // exactly as exported - never trimmed
            Notes = Nothing(string.Join("\n\n", parts)),
        };
    }

    private static string? Nothing(string value) => value.Length == 0 ? null : value;

    private static bool LooksLikeEmail(string value)
    {
        int at = value.IndexOf('@');
        return at > 0 && value.IndexOf('.', at) > at + 1 && !value.Contains(' ');
    }

    /// <summary>"https://www.github.com/login" becomes "github.com". Firefox exports no name at all.</summary>
    private static string? SiteOf(string url)
    {
        url = url.Split(',')[0].Trim();   // Bitwarden and Proton put several addresses in one cell
        if (url.Length == 0) return null;
        if (!url.Contains("://")) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return null;
        return uri.Host.StartsWith("www.") ? uri.Host[4..] : uri.Host;
    }

    /// <summary>
    /// CSV as exporters write it (RFC 4180): commas between fields, and a field in double quotes
    /// may hold commas, line breaks and doubled "" quote marks. Notes are where the line breaks
    /// turn up, and splitting on newlines first would cut those rows in half.
    /// </summary>
    private static List<string[]> Rows(string text, bool backslashEscapes)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (quoted)
            {
                if (backslashEscapes && c == '\\' && i + 1 < text.Length) field.Append(text[++i]);
                else if (c != '"') field.Append(c);
                else if (!backslashEscapes && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row.ToArray());
                row.Clear();
            }
            else field.Append(c);
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }

        return rows;
    }
}
