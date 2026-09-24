using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Draws the PassVault logo and writes it out as PNGs and a multi-size .ico.
//
// The logo lives here as real geometry rather than as a picture, so every size is drawn
// fresh rather than scaled from one bitmap. Run this again whenever the mark changes:
//     dotnet run --project tools/IconMaker
//
// THE MARK - "Slot". A brass slot with a keyway cut out of it: the port you push your stick
// into, and the keyhole it opens, as one shape. Drawn in a 64x64 box.
//
// It is one solid brass shape with a dark cut-out on purpose. An outlined version looked
// better at 256px but the outline thins to under a pixel at 16px and disappears, which would
// have meant shipping two different logos. A bold silhouette survives the taskbar.

const double Box = 64;

var tile = new Color[] { Color.FromRgb(0x15, 0x17, 0x1B) }[0];
var brass = Color.FromRgb(0xD8, 0xA2, 0x49);

var outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "brand");
Directory.CreateDirectory(outDir);
outDir = Path.GetFullPath(outDir);

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var pngs = new Dictionary<int, byte[]>();

foreach (var size in sizes)
    pngs[size] = Render(size);

foreach (var size in new[] { 16, 32, 48, 256 })
    File.WriteAllBytes(Path.Combine(outDir, $"passvault-{size}.png"), pngs[size]);

// The installer's artwork. Inno Setup wants BMP, not PNG.
File.WriteAllBytes(Path.Combine(outDir, "passvault-wordmark.png"), RenderWordmark(314, 164, png: true));

// Inno Setup picks the variant matching the screen's scaling. Without these the artwork is
// drawn at 100% into a slot sized for 125% or 150% and gets clipped.
foreach (var (suffix, scale) in new[] { ("", 1.0), ("-125", 1.25), ("-150", 1.5), ("-200", 2.0) })
{
    File.WriteAllBytes(Path.Combine(outDir, $"installer-large{suffix}.bmp"),
        RenderWordmark((int)(314 * scale), (int)(164 * scale), png: false));
    File.WriteAllBytes(Path.Combine(outDir, $"installer-small{suffix}.bmp"),
        RenderTile((int)(138 * scale), (int)(140 * scale)));
}

WriteIco(Path.Combine(outDir, "passvault.ico"), sizes.Select(s => (s, pngs[s])).ToList());

Console.WriteLine($"wrote {outDir}");
foreach (var f in Directory.GetFiles(outDir)) Console.WriteLine($"  {Path.GetFileName(f)}  {new FileInfo(f).Length:N0} bytes");

// ---------------------------------------------------------------------------------------

Geometry Mark()
{
    // The port: a rounded slot.
    var port = new RectangleGeometry(new Rect(19, 10, 26, 44), 7, 7);

    // The keyway, cut out of it: a round head and a long tapered stem. The stem runs about
    // one and a half head-widths, which is what makes it read as a keyhole rather than a
    // blob with a tail.
    var head = new EllipseGeometry(new Point(32, 25), 5.9, 5.9);

    var stem = new PathGeometry();
    var figure = new PathFigure { StartPoint = new Point(30.1, 29.6), IsClosed = true, IsFilled = true };
    figure.Segments.Add(new LineSegment(new Point(27.5, 46.5), true));
    figure.Segments.Add(new LineSegment(new Point(36.5, 46.5), true));
    figure.Segments.Add(new LineSegment(new Point(33.9, 29.6), true));
    stem.Figures.Add(figure);

    var keyway = new CombinedGeometry(GeometryCombineMode.Union, head, stem);
    return new CombinedGeometry(GeometryCombineMode.Exclude, port, keyway);
}

byte[] Render(int size)
{
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        var scale = size / Box;
        dc.PushTransform(new ScaleTransform(scale, scale));

        // Windows 11 icons are full-bleed rounded squares.
        dc.DrawRoundedRectangle(new SolidColorBrush(tile), null, new Rect(0, 0, Box, Box), 14, 14);
        dc.DrawGeometry(new SolidColorBrush(brass), null, Mark());

        dc.Pop();
    }

    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);

    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));

    using var stream = new MemoryStream();
    encoder.Save(stream);
    return stream.ToArray();
}

byte[] RenderTile(int width, int height)
{
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0E, 0x0F, 0x12)), null, new Rect(0, 0, width, height));

        var side = Math.Min(width, height) * 0.62;
        var scale = side / Box;
        dc.PushTransform(new TranslateTransform((width - side) / 2.0, (height - side) / 2.0));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawRoundedRectangle(new SolidColorBrush(tile), null, new Rect(0, 0, Box, Box), 14, 14);
        dc.DrawGeometry(new SolidColorBrush(brass), null, Mark());
        dc.Pop();
        dc.Pop();
    }

    var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(visual);

    var enc = new BmpBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var ms = new MemoryStream();
    enc.Save(ms);
    return ms.ToArray();
}

byte[] RenderWordmark(int height, int width, bool png)
{
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0E, 0x0F, 0x12)), null, new Rect(0, 0, width, height));

        var scale = (width * 0.44) / Box;
        var badge = width * 0.44;
        dc.PushTransform(new TranslateTransform((width - badge) / 2.0, height * 0.19));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawRoundedRectangle(new SolidColorBrush(tile), null, new Rect(0, 0, Box, Box), 14, 14);
        dc.DrawGeometry(new SolidColorBrush(brass), null, Mark());
        dc.Pop();
        dc.Pop();

        var text = new FormattedText(
            "PassVault", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"),
                         FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            width * 0.122, new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED)), 96);

        dc.DrawText(text, new Point((width - text.Width) / 2.0, height * 0.37 + badge * 0.5));
    }

    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);

    BitmapEncoder encoder = png ? new PngBitmapEncoder() : new BmpBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));

    using var stream = new MemoryStream();
    encoder.Save(stream);
    return stream.ToArray();
}

// An .ico is a tiny header, one 16-byte directory entry per size, then the image data.
// Vista and later accept PNG data directly, which is how 256px icons are stored.
void WriteIco(string path, List<(int Size, byte[] Png)> images)
{
    using var file = File.Create(path);
    using var w = new BinaryWriter(file);

    w.Write((ushort)0);                      // reserved
    w.Write((ushort)1);                      // 1 = icon
    w.Write((ushort)images.Count);

    var offset = 6 + images.Count * 16;
    foreach (var (size, png) in images)
    {
        w.Write((byte)(size >= 256 ? 0 : size));   // 0 means 256
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);                    // palette size
        w.Write((byte)0);                    // reserved
        w.Write((ushort)1);                  // colour planes
        w.Write((ushort)32);                 // bits per pixel
        w.Write(png.Length);
        w.Write(offset);
        offset += png.Length;
    }

    foreach (var (_, png) in images) w.Write(png);
}
