using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PassVault.App;

/// <summary>
/// The little square next to a credential. A real brand mark where we have one, and a
/// generated letter mark where we do not - so a list of forty can be scanned by shape
/// instead of read line by line.
/// </summary>
public partial class SiteMark : UserControl
{
    public SiteMark()
    {
        InitializeComponent();
        Resize(Size);
    }

    public static readonly DependencyProperty SiteProperty = DependencyProperty.Register(
        nameof(Site), typeof(string), typeof(SiteMark),
        new PropertyMetadata("", (d, e) => ((SiteMark)d).Draw((string?)e.NewValue)));

    public string Site
    {
        get => (string)GetValue(SiteProperty);
        set => SetValue(SiteProperty, value);
    }

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(SiteMark),
        new PropertyMetadata(28.0, (d, e) => ((SiteMark)d).Resize((double)e.NewValue)));

    /// <summary>Tile size. The artwork and the letter scale with it.</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    private void Resize(double size)
    {
        Tile.Width = size;
        Tile.Height = size;
        Art.Width = size * 0.54;
        Art.Height = size * 0.54;
        Letter.FontSize = size * 0.46;
    }

    private void Draw(string? site)
    {
        var mark = SiteIcons.For(site);

        if (mark is not null)
        {
            Brand.Data = Geometry.Parse(mark.Path);
            Brand.Fill = new SolidColorBrush(Readable(mark.Hex));
            Art.Visibility = Visibility.Visible;
            Letter.Text = "";
            return;
        }

        Art.Visibility = Visibility.Collapsed;

        var token = SiteIcons.Token(site);
        Letter.Text = token.Length > 0 ? token[..1].ToUpperInvariant() : "?";
        Letter.Foreground = new SolidColorBrush(FromToken(token));
    }

    /// <summary>
    /// Plenty of brands are black or near-black - GitHub, Apple, X. Painted as-is they are
    /// invisible on a dark tile, so anything too dark is lifted to a light neutral. Those
    /// brands are monochrome anyway, so nothing recognisable is lost.
    /// </summary>
    private static Color Readable(string hex)
    {
        var colour = (Color)ColorConverter.ConvertFromString("#" + hex);

        double Channel(byte c)
        {
            var v = c / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        var luminance = 0.2126 * Channel(colour.R) + 0.7152 * Channel(colour.G) + 0.0722 * Channel(colour.B);
        return luminance < 0.10 ? Color.FromRgb(0xE8, 0xEA, 0xED) : colour;
    }

    /// <summary>
    /// A stable colour per name, so the same site always looks the same. Fixed saturation and
    /// lightness keep every letter mark at the same weight as its neighbours - only the hue
    /// moves, which is what makes them tellable apart without any one shouting.
    /// </summary>
    private static Color FromToken(string token)
    {
        if (token.Length == 0) return Color.FromRgb(0x9A, 0xA1, 0xAB);

        int hash = 17;
        foreach (var c in token) hash = hash * 31 + c;

        return FromHsl(Math.Abs(hash) % 360, 0.52, 0.66);
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        double c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double x = c * (1 - Math.Abs(hue / 60.0 % 2 - 1));
        double m = lightness - c / 2;

        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromRgb(Byte(r + m), Byte(g + m), Byte(b + m));
    }

    private static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
}
