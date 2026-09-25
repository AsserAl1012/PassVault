using PassVault.Core.Import;
using PassVault.Core.Model;

namespace PassVault.Tests;

/// <summary>
/// One test per product, using the header row each one really writes. The headers and sample
/// rows come from the products' own source code or from real exports kept as test files by
/// Bitwarden, Proton Pass, 2FAS and AliasVault - not written from memory.
/// </summary>
public class CsvImportFormatTests
{
    /// <summary>A CSV line for the named columns of a header, quoted where needed - so long headers cannot be miscounted.</summary>
    private static string Line(string header, params (string Column, string Value)[] cells)
    {
        var columns = header.Split(',');
        return string.Join(",", columns.Select(c =>
        {
            var value = cells.FirstOrDefault(x => x.Column == c).Value ?? "";
            return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
        }));
    }

    [Fact]
    public void Chrome_Edge_Brave_and_Opera()
    {
        var e = Assert.Single(CsvImport.Read(
            "name,url,username,password,note\r\n"
            + "example.com,https://example.com/,usernamegoogle,passwordgoogle,Note for example password from Google\r\n").Entries);

        Assert.Equal(("example.com", "usernamegoogle", "passwordgoogle"), (e.Name, e.Username, e.Password));
        Assert.Equal("Website: https://example.com/\n\nNote for example password from Google", e.Notes);
    }

    [Fact]
    public void Chrome_before_2024_had_no_note_column()
    {
        var e = Assert.Single(CsvImport.Read("name,url,username,password\nexample.com,https://example.com/,me@example.com,pw\n").Entries);
        Assert.Equal(("me@example.com", "pw"), (e.Email, e.Password));
    }

    [Fact]
    public void Firefox_quotes_everything_has_no_names_and_hides_its_own_sync_secret_in_the_export()
    {
        var file = CsvImport.Read(
            "\"url\",\"username\",\"password\",\"httpRealm\",\"formActionOrigin\",\"guid\",\"timeCreated\",\"timeLastUsed\",\"timePasswordChanged\"\r\n"
            + "\"https://example.com\",\"joe@example.com\",\"qwerty\",,\"https://action.example.com\",\"{5ec0d12f-e194-4279-ae1b-d7d281bb46f0}\",\"1589617814635\",\"1589710449871\",\"1589617846802\"\r\n"
            + "\"chrome://FirefoxAccounts\",\"abc\",\"{\"\"kSync\"\":\"\"x\"\"}\",\"Firefox Accounts credentials\",,\"{2}\",\"1\",\"2\",\"3\"");

        var e = Assert.Single(file.Entries);
        Assert.Equal(("example.com", "joe@example.com", "qwerty"), (e.Name, e.Email, e.Password));
        Assert.Equal(1, file.LeftOut);
    }

    [Fact]
    public void Safari_and_Apple_Passwords_keep_the_two_factor_secret()
    {
        var e = Assert.Single(CsvImport.Read(
            "Title,URL,Username,Password,Notes,OTPAuth\n"
            + "2fa.example.com (2fa-scanned),https://2fa.example.com/,2fa-scanned,pass,,otpauth://totp/db@example.com?secret=OTDED5QZA64L6YRUWJLD65QQ3Z6PZ3A3&issuer=Proton&algorithm=SHA1&digits=6&period=30\n").Entries);

        Assert.Equal(("2fa.example.com (2fa-scanned)", "2fa-scanned", "pass"), (e.Name, e.Username, e.Password));
        Assert.Contains("Two-factor secret: otpauth://totp/db@example.com?secret=OTDED5QZA64L6YRUWJLD65QQ3Z6PZ3A3", e.Notes);
    }

    [Fact]
    public void Bitwarden_logins_notes_and_custom_fields()
    {
        const string header = "folder,favorite,type,name,notes,fields,reprompt,archivedDate,login_uri,login_username,login_password,login_totp";
        var file = CsvImport.Read(header + "\r\n"
            + "Social,1,login,Twitter,,,0,,twitter.com,me@example.com,password123,\r\n"
            + Line(header, ("type", "note"), ("name", "My note"), ("notes", "secret note text")) + "\r\n"
            + Line(header, ("type", "login"), ("name", "Bank"), ("fields", "Security question: blue"),
                           ("login_uri", "https://bank.example,https://app.bank.example"), ("login_password", "pw")) + "\r\n");

        Assert.Equal(3, file.Entries.Count);
        var twitter = file.Entries[0];
        Assert.Equal(("Twitter", "me@example.com", "password123", "Website: twitter.com"),
                     (twitter.Name, twitter.Email, twitter.Password, twitter.Notes));
        Assert.Equal(("My note", null, "secret note text"), (file.Entries[1].Name, file.Entries[1].Password, file.Entries[1].Notes));
        Assert.Contains("Other fields: Security question: blue", file.Entries[2].Notes);
        Assert.Contains("https://app.bank.example", file.Entries[2].Notes);
    }

    [Fact]
    public void LastPass_notes_2FA_the_ampersand_bug_and_the_rows_that_are_not_logins()
    {
        var file = CsvImport.Read(
            "url,username,password,totp,extra,name,grouping,fav\n"
            + "http://example.com,someUser,myPassword,Y64VEVMBTSXCYIWRSHRNDZW62MPGVU2G,super secure notes,example.com,,0\n"
            + "http://sn,,,,\"NoteType:Credit Card\nNumber:4111111111111111\",Visa,,0\n"
            + "http://sn,,,,just a plain note,Plain note,,0\n"
            + "http://example.org,bob,a&amp;b&lt;c,,,example.org,,0\n"
            + "http://group,,,,,Empty folder,Folder,0\n");

        Assert.Equal(new[] { "example.com", "Plain note", "example.org" }, file.Entries.Select(e => e.Name));
        Assert.Equal(2, file.LeftOut);

        Assert.Equal("Website: http://example.com\n\nsuper secure notes\n\nTwo-factor secret: Y64VEVMBTSXCYIWRSHRNDZW62MPGVU2G",
                     file.Entries[0].Notes);
        Assert.Equal("just a plain note", file.Entries[1].Notes);
        Assert.Equal("a&b<c", file.Entries[2].Password);
        Assert.DoesNotContain(file.Entries, e => (e.Notes ?? "").Contains("4111"));
    }

    [Fact]
    public void OnePassword_8()
    {
        var e = Assert.Single(CsvImport.Read(
            "Title,Url,Username,Password,OTPAuth,Favorite,Archived,Tags,Notes\n"
            + "Password,https://2fas.com,rafols,y8gL676v7iyNrQWL7shE,,false,false,Rafael,Notka password\n").Entries);

        Assert.Equal(("Password", "rafols", "y8gL676v7iyNrQWL7shE"), (e.Name, e.Username, e.Password));
        Assert.Equal("Website: https://2fas.com\n\nNotka password", e.Notes);
    }

    [Fact]
    public void KeePassXC_doubles_quotes_keeps_backslashes_and_exports_its_recycle_bin()
    {
        var file = CsvImport.Read(
            "\"Group\",\"Title\",\"Username\",\"Password\",\"URL\",\"Notes\",\"TOTP\",\"Icon\",\"Last Modified\",\"Created\"\n"
            + "\"Root\",\"Drugi element\",\"Morgan\",\"~M\"\"e5iS**h*U\\[\"\"7aT%\\\",\"https://2fas.com/\",\"Notka do drugiego elementu\",\"\",\"0\",\"2025-12-22T19:33:57Z\",\"2025-12-22T19:33:19Z\"\n"
            + "\"Root/Recycle Bin\",\"Old one\",\"x\",\"y\",\"\",\"\",\"\",\"0\",\"\",\"\"\n");

        var e = Assert.Single(file.Entries);
        Assert.Equal("~M\"e5iS**h*U\\[\"7aT%\\", e.Password);
        Assert.Equal(1, file.LeftOut);
    }

    /// <summary>The one exporter that escapes with backslashes. Read as ordinary CSV, this password would come in wrong.</summary>
    [Fact]
    public void KeePass_2_escapes_with_backslashes()
    {
        var e = Assert.Single(CsvImport.Read(
            "\"Account\",\"Login Name\",\"Password\",\"Web Site\",\"Comments\"\r\n"
            + "\"Sample Entry\",\"User Name\",\"a\\\"b\\\\c\",\"https://keepass.info/\",\"Notes\"\r\n").Entries);

        Assert.Equal(("Sample Entry", "User Name", "a\"b\\c"), (e.Name, e.Username, e.Password));
        Assert.Equal("Website: https://keepass.info/\n\nNotes", e.Notes);
    }

    [Fact]
    public void Proton_Pass_keeps_email_and_username_apart_and_never_brings_a_card_in_as_a_note()
    {
        const string header = "type,name,url,autofillUrls,email,username,password,note,totp,createTime,modifyTime,vault";
        var file = CsvImport.Read(header + "\r\n"
            + "login,wellcome-home.com,https://wellcome-home.com,\"[{\"\"url\"\":\"\"https://wellcome-home.com\"\",\"\"mode\"\":0}]\",,mark,Washout2-Professor5-Antibody1-Lustfully9-Barrel5,,,1740854486,1740854486,Personal\r\n"
            + Line(header, ("type", "creditCard"), ("name", "My Visa"), ("note", "{\"number\":\"4111111111111111\",\"verificationNumber\":\"123\"}")) + "\r\n"
            + Line(header, ("type", "login"), ("name", "both.example"), ("email", "me@proton.me"), ("username", "myhandle"), ("password", "pw")) + "\r\n");

        Assert.Equal(2, file.Entries.Count);
        Assert.Equal(1, file.LeftOut);
        Assert.Equal(("mark", "Washout2-Professor5-Antibody1-Lustfully9-Barrel5"), (file.Entries[0].Username, file.Entries[0].Password));
        Assert.Equal(("me@proton.me", "myhandle"), (file.Entries[1].Email, file.Entries[1].Username));
        Assert.DoesNotContain(file.Entries, e => (e.Notes ?? "").Contains("4111"));
    }

    [Fact]
    public void Proton_Pass_before_autofillUrls()
    {
        var e = Assert.Single(CsvImport.Read(
            "type,name,url,email,username,password,note,totp,createTime,modifyTime,vault\r\n"
            + "login,wellcome-home.com,https://wellcome-home.com,,mark,Washout2-Professor5-Antibody1-Lustfully9-Barrel5,,,1740854486,1740854486,Personal\r\n").Entries);
        Assert.Equal("mark", e.Username);
    }

    [Fact]
    public void Dashlane_credentials_file_keeps_the_extra_logins_and_2FA()
    {
        var file = CsvImport.Read(
            "username,username2,username3,title,password,note,url,category,otpUrl\r\n"
            + "test@example.com,,,test,ndnndc,,https://test,,\r\n"
            + "me@example.com,handle2,,Site,pw,,https://site.example,,otpauth://totp/?secret=ABC\r\n");

        Assert.Equal(("test", "test@example.com", "ndnndc"), (file.Entries[0].Name, file.Entries[0].Email, file.Entries[0].Password));
        Assert.Contains("Also signs in as: handle2", file.Entries[1].Notes);
        Assert.Contains("Two-factor secret: otpauth://totp/?secret=ABC", file.Entries[1].Notes);
    }

    [Fact]
    public void NordPass_mixes_every_item_type_into_one_file()
    {
        const string header = "name,url,additional_urls,username,password,note,cardholdername,cardnumber,cvc,pin,expirydate,zipcode,folder,full_name,phone_number,email,address1,address2,city,country,state,type,custom_fields";
        var file = CsvImport.Read(header + "\n"
            + "Password,https://2fas.com,\"[\"\"https://www.youtube.com\"\",\"\"https://www.nexusmods.com\"\"]\",rafols,y8gL676v7iyNrQWL7shE,Notka password,,,,,,,Folder 1,,,,,,,,,password,\"[{\"\"type\"\":\"\"text\"\",\"\"label\"\":\"\"Pole tekstowe 1\"\",\"\"value\"\":\"\"Rafael\"\"}]\"\n"
            + Line(header, ("name", "Folder 1"), ("type", "folder")) + "\n"
            + Line(header, ("name", "My card"), ("cardholdername", "Jan Kowalski"), ("cardnumber", "4111111111111111"), ("cvc", "123"), ("type", "credit_card")) + "\n");

        var e = Assert.Single(file.Entries);
        Assert.Equal(2, file.LeftOut);
        Assert.Equal(("Password", "rafols", "y8gL676v7iyNrQWL7shE"), (e.Name, e.Username, e.Password));
        Assert.Contains("Other websites: [\"https://www.youtube.com\",\"https://www.nexusmods.com\"]", e.Notes);
        Assert.Contains("Other fields: [{\"type\":\"text\"", e.Notes);
    }
}
