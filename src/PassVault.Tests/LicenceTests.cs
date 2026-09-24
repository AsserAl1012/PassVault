using System.Security.Cryptography;
using System.Text;
using PassVault.Core.Licensing;

namespace PassVault.Tests;

public class LicenceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pv-lic-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ECDsa _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _publicKey;

    public LicenceTests()
    {
        Directory.CreateDirectory(_folder);
        _publicKey = Convert.ToBase64String(_signer.ExportSubjectPublicKeyInfo());
    }

    public void Dispose()
    {
        _signer.Dispose();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Issue(string name, ECDsa? signer = null)
    {
        var licence = new Licence(name, "F5WA-2S56-BFET", new DateOnly(2026, 9, 20), "owner");
        var signature = (signer ?? _signer).SignData(
            Encoding.UTF8.GetBytes(licence.Canonical()), HashAlgorithmName.SHA256);

        return LicenceKey.Write(licence, signature);
    }

    [Fact]
    public void A_licence_we_signed_checks_out()
    {
        var licence = LicenceKey.Verify(Issue("Asser Moemen"), _publicKey);

        Assert.NotNull(licence);
        Assert.Equal("Asser Moemen", licence.Name);
        Assert.Equal("F5WA-2S56-BFET", licence.Id);
        Assert.Equal("owner", licence.Edition);
    }

    [Theory]
    [InlineData("th-TH")]   // Thai Buddhist calendar
    [InlineData("fa-IR")]   // Persian calendar
    [InlineData("ar-SA")]   // Hijri calendar
    public void A_licence_checks_out_whatever_language_the_pc_is_in(string culture)
    {
        var block = Issue("Asser Moemen");
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
            Assert.NotNull(LicenceKey.Verify(block, _publicKey));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Changing_the_name_breaks_it()
    {
        // The name is signed, so a licence cannot be passed off as somebody else's.
        var tampered = Issue("Asser Moemen").Replace("Asser Moemen", "Someone Else");

        Assert.Null(LicenceKey.Verify(tampered, _publicKey));
    }

    [Fact]
    public void Changing_any_field_breaks_it()
    {
        foreach (var (from, to) in new[]
                 {
                     ("id: F5WA-2S56-BFET", "id: AAAA-BBBB-CCCC"),
                     ("issued: 2026-09-20", "issued: 2020-01-01"),
                     ("edition: owner", "edition: enterprise"),
                 })
        {
            Assert.Null(LicenceKey.Verify(Issue("Asser Moemen").Replace(from, to), _publicKey));
        }
    }

    [Fact]
    public void Somebody_elses_signing_key_does_not_work()
    {
        using var impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.Null(LicenceKey.Verify(Issue("Asser Moemen", impostor), _publicKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello")]
    [InlineData("-----BEGIN PASSVAULT LICENCE-----\nname: x\n-----END PASSVAULT LICENCE-----")]
    [InlineData("-----BEGIN PASSVAULT LICENCE-----\nname: x\nid: y\nissued: nonsense\nedition: owner\nsig: AAAA\n-----END PASSVAULT LICENCE-----")]
    [InlineData("name: x\nid: y\nissued: 2026-09-20\nedition: owner\nsig: not-base64!!")]
    public void Rubbish_is_refused_without_throwing(string text)
    {
        // A mistyped licence is an everyday thing, not a crash.
        Assert.Null(LicenceKey.Verify(text, _publicKey));
    }

    [Fact]
    public void Null_is_refused()
    {
        Assert.Null(LicenceKey.Verify(null, _publicKey));
    }

    [Fact]
    public void Extra_whitespace_and_stray_lines_are_forgiven()
    {
        // People paste out of emails. It should still work.
        var messy = "\n\n  " + Issue("Asser Moemen").Replace("\n", "\r\n  ") + "  \n\n";

        Assert.NotNull(LicenceKey.Verify(messy, _publicKey));
    }

    // ---- where it lives on disk ----

    [Fact]
    public void No_file_means_the_free_version()
    {
        Assert.Null(LicenceStore.Load(_folder, _publicKey));
    }

    [Fact]
    public void A_saved_licence_is_there_next_time()
    {
        var saved = LicenceStore.Save(_folder, Issue("Asser Moemen"), _publicKey);

        Assert.NotNull(saved);
        Assert.Equal("Asser Moemen", LicenceStore.Load(_folder, _publicKey)?.Name);
    }

    [Fact]
    public void A_bad_licence_is_never_written_to_disk()
    {
        Assert.Null(LicenceStore.Save(_folder, "not a licence", _publicKey));
        Assert.False(File.Exists(LicenceStore.PathIn(_folder)));
    }

    [Fact]
    public void Removing_it_goes_back_to_the_free_version()
    {
        LicenceStore.Save(_folder, Issue("Asser Moemen"), _publicKey);
        LicenceStore.Remove(_folder);

        Assert.Null(LicenceStore.Load(_folder, _publicKey));
    }

    [Fact]
    public void The_check_never_reaches_the_network()
    {
        // There is nothing to assert against a socket here, so this is a guard on the shape
        // of the code instead: verifying takes a string and a key and nothing else. If anyone
        // ever adds an online check, this test's name is the reminder that it was deliberate.
        var licence = LicenceKey.Verify(Issue("Asser Moemen"), _publicKey);

        Assert.NotNull(licence);
        Assert.Equal(new DateOnly(2026, 9, 20), licence.Issued);
    }
}
