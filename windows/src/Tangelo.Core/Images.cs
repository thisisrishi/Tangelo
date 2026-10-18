using ImageMagick;
using ImageMagick.Drawing;

namespace Tangelo.Core;

public sealed record ImageAdjustments
{
    public double Exposure { get; init; }          // EV, -2…2
    public double Contrast { get; init; } = 1;     // 0.5…1.5
    public double Saturation { get; init; } = 1;   // 0…2
    public double Warmth { get; init; }            // -1…1
    public double Tint { get; init; }              // -1…1
    public double Highlights { get; init; } = 1;   // 0…1 (1 = unchanged)
    public double Shadows { get; init; }           // -1…1
    public double Sharpness { get; init; }         // 0…2
    public double NoiseReduction { get; init; }    // 0…1
    public double Vignette { get; init; }          // 0…1
    public double Grain { get; init; }             // 0…1
    public bool BlackAndWhite { get; init; }
}

public enum AnnotationKind { Arrow, Rectangle, Ellipse, Line, Pen, Highlight, Text, Magnifier, Number }

/// Points are normalized (0–1, top-left origin); sizes are relative to image width.
public sealed record Annotation(AnnotationKind Kind, IReadOnlyList<(double X, double Y)> Points, string Color = "#FF3B30",
                                double LineWidth = 0.006, string Text = "", double FontSize = 0.04, double Zoom = 2, int Number = 1);

public enum CanvasRatio { Auto, Square, Wide, Classic, Portrait, Story }

public sealed record GradientPreset(string Name, string From, string To)
{
    public static readonly GradientPreset[] All =
    [
        new("Citrus", "#FF8A00", "#FF2E63"), new("Meadow", "#9BE15D", "#00C9A7"), new("Orchid", "#DA22FF", "#9733EE"),
        new("Lagoon", "#43CEA2", "#185A9D"), new("Sunset", "#FF5F6D", "#FFC371"), new("Frost", "#E0EAFC", "#CFDEF3"),
        new("Violet", "#8E2DE2", "#4A00E0"), new("Mint", "#A8FF78", "#78FFD6"), new("Sorbet", "#FBD3E9", "#BB377D"),
        new("Midnight", "#232526", "#414345"), new("Aqua", "#13547A", "#80D0C7"), new("Emerald", "#348F50", "#56B4D3"),
    ];
}

public sealed record FrameOptions
{
    public GradientPreset Preset { get; init; } = GradientPreset.All[0];
    public string? SolidColor { get; init; }
    public string? BackgroundImage { get; init; }
    public bool Transparent { get; init; }
    public double Padding { get; init; } = 0.08;      // fraction of the longer side
    public double CornerRadius { get; init; } = 0.02; // fraction of image width
    public double Shadow { get; init; } = 0.5;        // 0…1
    public CanvasRatio Ratio { get; init; } = CanvasRatio.Auto;
}

public enum CollageStyle { Grid, Row, Column, Featured }

public sealed record CollageOptions
{
    public CollageStyle Style { get; init; } = CollageStyle.Grid;
    public int Spacing { get; init; } = 16;
    public int Padding { get; init; } = 24;
    public int CornerRadius { get; init; } = 12;
    public bool Fill { get; init; } = true;
    public string Background { get; init; } = "#FFFFFF";
    public int CellSize { get; init; } = 800;
}

public enum ImagePreset { High, Balanced, Small, Smallest }

public static class Images
{
    public static MagickImage Load(string path)
    {
        try
        {
            var img = new MagickImage(path);
            img.AutoOrient();
            return img;
        }
        catch (MagickException e) { throw new TangeloException($"Could not read the image {Path.GetFileName(path)}: {e.Message}"); }
    }

    static MagickFormat MagickFormatFor(ConversionFormat f) => f switch
    {
        ConversionFormat.Jpg => MagickFormat.Jpeg,
        ConversionFormat.Png => MagickFormat.Png,
        ConversionFormat.Webp => MagickFormat.WebP,
        ConversionFormat.Heic => MagickFormat.Heic,
        ConversionFormat.Avif => MagickFormat.Avif,
        ConversionFormat.Tiff => MagickFormat.Tiff,
        ConversionFormat.Gif => MagickFormat.Gif,
        ConversionFormat.Bmp => MagickFormat.Bmp,
        ConversionFormat.Ico => MagickFormat.Ico,
        ConversionFormat.Pdf => MagickFormat.Pdf,
        _ => throw new TangeloException($"{f.Label()} is not an image format."),
    };

    public static void Save(MagickImage img, string path, ConversionFormat f, int quality = 85)
    {
        if (f == ConversionFormat.Pdf) { Pdf.CreateFromImages([img], path); return; }
        using var copy = (MagickImage)img.Clone();
        if (f is ConversionFormat.Jpg or ConversionFormat.Bmp && copy.HasAlpha)
        {
            copy.BackgroundColor = MagickColors.White;
            copy.Alpha(AlphaAction.Remove);
        }
        if (f == ConversionFormat.Ico)
        {
            var side = (uint)Math.Min(256, Math.Max(copy.Width, copy.Height));
            copy.Resize(new MagickGeometry(side, side));
            copy.BackgroundColor = MagickColors.Transparent;
            copy.Extent(side, side, Gravity.Center);
        }
        copy.Quality = (uint)Math.Clamp(quality, 1, 100);
        copy.Format = MagickFormatFor(f);
        copy.Write(path);
    }

    /// Converts keeping metadata (unless disabled) and animation for GIF/WebP targets.
    public static void Convert(string input, string output, ConversionFormat f)
    {
        var s = Settings.Current;
        if (f is ConversionFormat.Gif or ConversionFormat.Webp)
        {
            using var frames = new MagickImageCollection(input);
            if (frames.Count > 1)
            {
                frames.Coalesce();
                foreach (var fr in frames) fr.Format = MagickFormatFor(f);
                if (f == ConversionFormat.Gif) frames.Optimize();
                frames.Write(output, MagickFormatFor(f));
                return;
            }
        }
        using var img = Load(input);
        if (!s.PreserveMetadata) img.Strip();
        Save(img, output, f, s.ImageQuality);
    }

    public static string SaveLike(MagickImage img, string source, string suffix, bool forcePng = false)
    {
        var f = Formats.From(source) ?? ConversionFormat.Png;
        if (forcePng || f is not (ConversionFormat.Jpg or ConversionFormat.Png or ConversionFormat.Heic or ConversionFormat.Tiff or ConversionFormat.Webp or ConversionFormat.Avif or ConversionFormat.Bmp))
            f = ConversionFormat.Png;
        var output = Output.PathFor(source, f.Extension(), suffix);
        Save(img, output, f, 92);
        return output;
    }

    // MARK: Compression

    public static string Compress(string input, ImagePreset? preset, long? targetBytes, bool keepFormat)
    {
        using var img = Load(input);
        var f = Formats.From(input) ?? ConversionFormat.Jpg;
        if (!keepFormat || f is not (ConversionFormat.Jpg or ConversionFormat.Heic or ConversionFormat.Webp or ConversionFormat.Avif or ConversionFormat.Png)) f = ConversionFormat.Jpg;
        if (targetBytes != null && f == ConversionFormat.Png) f = ConversionFormat.Jpg;
        int? maxDim = preset switch { ImagePreset.Balanced => 3000, ImagePreset.Small => 2000, ImagePreset.Smallest => 1280, _ => null };
        if (maxDim is { } m && Math.Max(img.Width, img.Height) > m) img.Resize(new MagickGeometry((uint)m, (uint)m));
        img.Strip();
        var output = Output.PathFor(input, f.Extension(), "compressed");
        if (f == ConversionFormat.Png)
        {
            img.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
            Save(img, output, f, 100);
            return output;
        }
        if (targetBytes is { } target)
        {
            byte[]? best = null;
            for (int round = 0; round < 4 && best == null; round++)
            {
                int lo = 5, hi = 95;
                while (lo <= hi)
                {
                    int q = (lo + hi) / 2;
                    var bytes = Encode(img, f, q);
                    if (bytes.Length <= target) { best = bytes; lo = q + 1; } else hi = q - 1;
                }
                if (best == null) img.Resize(new Percentage(70));
            }
            if (best == null) throw new TangeloException("Could not reach the target size. Try a larger size.");
            File.WriteAllBytes(output, best);
        }
        else
        {
            int q = preset switch { ImagePreset.High => 85, ImagePreset.Small => 55, ImagePreset.Smallest => 40, _ => 70 };
            Save(img, output, f, q);
        }
        return output;
    }

    static byte[] Encode(MagickImage img, ConversionFormat f, int quality)
    {
        using var copy = (MagickImage)img.Clone();
        if (copy.HasAlpha && f == ConversionFormat.Jpg) { copy.BackgroundColor = MagickColors.White; copy.Alpha(AlphaAction.Remove); }
        copy.Quality = (uint)quality;
        copy.Format = MagickFormatFor(f);
        return copy.ToByteArray();
    }

    // MARK: Edits

    public static MagickImage Adjust(MagickImage source, ImageAdjustments a)
    {
        var img = (MagickImage)source.Clone();
        if (a.Exposure != 0) img.Evaluate(Channels.RGB, EvaluateOperator.Multiply, Math.Pow(2, a.Exposure));
        if (a.Contrast != 1)
        {
            var strength = Math.Abs(a.Contrast - 1) * 10;
            if (a.Contrast > 1) img.SigmoidalContrast(strength, new Percentage(50)); else img.InverseSigmoidalContrast(strength, new Percentage(50));
        }
        if (a.Saturation != 1 || a.BlackAndWhite) img.Modulate(new Percentage(100), new Percentage(a.BlackAndWhite ? 0 : a.Saturation * 100), new Percentage(100));
        if (a.Warmth != 0)
        {
            img.Evaluate(Channels.Red, EvaluateOperator.Multiply, 1 + a.Warmth * 0.12);
            img.Evaluate(Channels.Blue, EvaluateOperator.Multiply, 1 - a.Warmth * 0.12);
        }
        if (a.Tint != 0) img.Evaluate(Channels.Green, EvaluateOperator.Multiply, 1 - a.Tint * 0.1);
        if (a.Shadows != 0) img.Level(new Percentage(0), new Percentage(100), 1 + a.Shadows * 0.6);
        if (a.Highlights < 1) img.InverseSigmoidalContrast((1 - a.Highlights) * 6, new Percentage(75));
        if (a.NoiseReduction > 0) img.WaveletDenoise(new Percentage(a.NoiseReduction * 15), 0.5);
        if (a.Sharpness > 0) img.UnsharpMask(0, 1 + a.Sharpness, a.Sharpness, 0.02);
        if (a.Vignette > 0)
        {
            using var mask = new MagickImage(MagickColors.White, img.Width, img.Height);
            mask.Vignette(0, Math.Max(img.Width, img.Height) * (0.35 - a.Vignette * 0.2), 0, 0);
            img.Composite(mask, CompositeOperator.Multiply, Channels.RGB);
        }
        if (a.Grain > 0)
        {
            using var noise = new MagickImage(MagickColors.Gray, img.Width, img.Height);
            noise.AddNoise(NoiseType.Gaussian, a.Grain * 2);
            noise.Grayscale();
            img.Composite(noise, CompositeOperator.SoftLight, Channels.RGB);
        }
        return img;
    }

    public static MagickImage Crop(MagickImage source, RectF r)
    {
        var img = (MagickImage)source.Clone();
        var g = new MagickGeometry((int)(r.X * img.Width), (int)(r.Y * img.Height), (uint)Math.Max(1, r.W * img.Width), (uint)Math.Max(1, r.H * img.Height));
        img.Crop(g);
        img.ResetPage();
        return img;
    }

    public static MagickImage Resize(MagickImage source, uint width, uint height)
    {
        var img = (MagickImage)source.Clone();
        img.FilterType = FilterType.Lanczos;
        img.Resize(new MagickGeometry(width, height) { IgnoreAspectRatio = true });
        return img;
    }

    public static MagickImage Annotate(MagickImage source, IReadOnlyList<Annotation> annotations)
    {
        var img = (MagickImage)source.Clone();
        double W = img.Width, H = img.Height;
        foreach (var a in annotations)
        {
            if (a.Points.Count == 0) continue;
            var pts = a.Points.Select(p => new PointD(p.X * W, p.Y * H)).ToArray();
            var (p0, p1) = (pts[0], pts[^1]);
            var lw = Math.Max(1, a.LineWidth * W);
            var color = new MagickColor(a.Color);
            var rect = (X: Math.Min(p0.X, p1.X), Y: Math.Min(p0.Y, p1.Y), W: Math.Abs(p1.X - p0.X), H: Math.Abs(p1.Y - p0.Y));
            var d = new Drawables().StrokeColor(color).StrokeWidth(lw).StrokeLineCap(LineCap.Round).StrokeLineJoin(LineJoin.Round).FillColor(MagickColors.Transparent);
            switch (a.Kind)
            {
                case AnnotationKind.Line:
                    d.Line(p0.X, p0.Y, p1.X, p1.Y).Draw(img); break;
                case AnnotationKind.Arrow:
                {
                    var angle = Math.Atan2(p1.Y - p0.Y, p1.X - p0.X);
                    var head = lw * 4.5;
                    var tip = new PointD(p1.X, p1.Y);
                    var l = new PointD(p1.X - head * Math.Cos(angle - Math.PI / 7), p1.Y - head * Math.Sin(angle - Math.PI / 7));
                    var r = new PointD(p1.X - head * Math.Cos(angle + Math.PI / 7), p1.Y - head * Math.Sin(angle + Math.PI / 7));
                    var shaftEnd = new PointD(p1.X - head * 0.8 * Math.Cos(angle), p1.Y - head * 0.8 * Math.Sin(angle));
                    d.Line(p0.X, p0.Y, shaftEnd.X, shaftEnd.Y).Draw(img);
                    new Drawables().FillColor(color).StrokeColor(color).StrokeWidth(1).Polygon(tip, l, r).Draw(img);
                    break;
                }
                case AnnotationKind.Rectangle:
                    d.RoundRectangle(rect.X, rect.Y, rect.X + rect.W, rect.Y + rect.H, lw, lw).Draw(img); break;
                case AnnotationKind.Ellipse:
                    d.Ellipse(rect.X + rect.W / 2, rect.Y + rect.H / 2, rect.W / 2, rect.H / 2, 0, 360).Draw(img); break;
                case AnnotationKind.Pen:
                    if (pts.Length > 1) d.Polyline(pts).Draw(img); break;
                case AnnotationKind.Highlight:
                {
                    using var layer = new MagickImage(MagickColors.Transparent, img.Width, img.Height);
                    new Drawables().FillColor(new MagickColor(a.Color) { A = (byte)90 }).Rectangle(rect.X, rect.Y, rect.X + rect.W, rect.Y + rect.H).Draw(layer);
                    img.Composite(layer, CompositeOperator.Multiply, Channels.RGB);
                    break;
                }
                case AnnotationKind.Text:
                {
                    var size = a.FontSize * W;
                    new Drawables().Font(DefaultFont).FontPointSize(size).FillColor(color).StrokeColor(MagickColors.Transparent)
                        .TextAlignment(TextAlignment.Left).Text(p0.X, p0.Y + size, string.IsNullOrEmpty(a.Text) ? "Text" : a.Text).Draw(img);
                    break;
                }
                case AnnotationKind.Number:
                {
                    var rad = a.FontSize * W;
                    new Drawables().FillColor(color).StrokeColor(color).Circle(p0.X, p0.Y, p0.X + rad, p0.Y).Draw(img);
                    new Drawables().Font(DefaultFont).FontPointSize(rad * 1.1).FillColor(MagickColors.White).TextAlignment(TextAlignment.Center)
                        .Text(p0.X, p0.Y + rad * 0.38, a.Number.ToString()).Draw(img);
                    break;
                }
                case AnnotationKind.Magnifier:
                {
                    var radius = Math.Max(rect.W, rect.H) / 2;
                    if (radius < 3) break;
                    var src = radius * 2 / a.Zoom;
                    using var piece = (MagickImage)source.Clone();
                    piece.Crop(new MagickGeometry((int)(p0.X - src / 2), (int)(p0.Y - src / 2), (uint)src, (uint)src));
                    piece.ResetPage();
                    piece.Resize(new MagickGeometry((uint)(radius * 2), (uint)(radius * 2)) { IgnoreAspectRatio = true });
                    using var mask = new MagickImage(MagickColors.Black, piece.Width, piece.Height);
                    new Drawables().FillColor(MagickColors.White).Circle(piece.Width / 2.0, piece.Height / 2.0, piece.Width / 2.0, 0).Draw(mask);
                    piece.Alpha(AlphaAction.Set);
                    piece.Composite(mask, CompositeOperator.CopyAlpha);
                    img.Composite(piece, (int)(p0.X - radius), (int)(p0.Y - radius), CompositeOperator.Over);
                    new Drawables().StrokeColor(MagickColors.White).StrokeWidth(lw * 1.2).FillColor(MagickColors.Transparent)
                        .Circle(p0.X, p0.Y, p0.X + radius, p0.Y).Draw(img);
                    break;
                }
            }
        }
        return img;
    }

    /// A font that exists on every machine: Segoe UI on Windows, Helvetica/Arial elsewhere.
    static string DefaultFont => OperatingSystem.IsWindows() ? "Segoe-UI-Bold"
        : File.Exists("/System/Library/Fonts/Supplemental/Arial Bold.ttf") ? "/System/Library/Fonts/Supplemental/Arial Bold.ttf" : "Helvetica";

    static MagickImage Rounded(MagickImage source, double radius)
    {
        var img = (MagickImage)source.Clone();
        if (radius < 1) return img;
        using var mask = new MagickImage(MagickColors.Black, img.Width, img.Height);
        new Drawables().FillColor(MagickColors.White).RoundRectangle(0, 0, img.Width - 1, img.Height - 1, radius, radius).Draw(mask);
        img.Alpha(AlphaAction.Set);
        img.Composite(mask, CompositeOperator.CopyAlpha);
        return img;
    }

    public static MagickImage Frame(MagickImage source, FrameOptions o)
    {
        double iw = source.Width, ih = source.Height;
        var pad = Math.Max(iw, ih) * o.Padding;
        double cw = iw + pad * 2, ch = ih + pad * 2;
        double? ratio = o.Ratio switch { CanvasRatio.Square => 1, CanvasRatio.Wide => 16.0 / 9, CanvasRatio.Classic => 4.0 / 3, CanvasRatio.Portrait => 0.8, CanvasRatio.Story => 9.0 / 16, _ => null };
        if (ratio is { } r) { if (cw / ch < r) cw = ch * r; else ch = cw / r; }
        uint W = (uint)cw, H = (uint)ch;
        MagickImage canvas;
        if (o.Transparent) canvas = new MagickImage(MagickColors.Transparent, W, H);
        else if (o.BackgroundImage is { } bgPath && File.Exists(bgPath))
        {
            canvas = Load(bgPath);
            canvas.Resize(new MagickGeometry(W, H) { FillArea = true });
            canvas.Extent(W, H, Gravity.Center);
        }
        else if (o.SolidColor is { } solid) canvas = new MagickImage(new MagickColor(solid), W, H);
        else
        {
            canvas = new MagickImage();
            canvas.Settings.SetDefine("gradient:angle", "135");
            canvas.Read($"gradient:{o.Preset.From}-{o.Preset.To}", W, H);
        }
        using var rounded = Rounded(source, iw * o.CornerRadius);
        int x = (int)((cw - iw) / 2), y = (int)((ch - ih) / 2);
        if (o.Shadow > 0)
        {
            using var shadow = (MagickImage)rounded.Clone();
            shadow.Shadow(0, (int)(iw * 0.012 * o.Shadow), iw * 0.03 * o.Shadow, new Percentage(55 * o.Shadow), MagickColors.Black);
            int off = (int)(iw * 0.03 * o.Shadow * 2);
            canvas.Composite(shadow, x - off, y - off + (int)(iw * 0.012 * o.Shadow), CompositeOperator.Over);
        }
        canvas.Composite(rounded, x, y, CompositeOperator.Over);
        return canvas;
    }

    public static MagickImage Collage(IReadOnlyList<MagickImage> images, CollageOptions o)
    {
        if (images.Count == 0) throw new TangeloException("Choose images for the collage.");
        int n = images.Count, cell = o.CellSize, sp = o.Spacing, pad = o.Padding;
        var cells = new List<(int X, int Y, int W, int H)>();
        int width, height;
        switch (o.Style)
        {
            case CollageStyle.Row:
            {
                int x = pad;
                foreach (var img in images)
                {
                    int w = o.Fill ? cell : (int)(cell * (double)img.Width / img.Height);
                    cells.Add((x, pad, w, cell)); x += w + sp;
                }
                width = x - sp + pad; height = cell + pad * 2; break;
            }
            case CollageStyle.Column:
            {
                int y = pad;
                foreach (var img in images)
                {
                    int h = o.Fill ? cell : (int)(cell * (double)img.Height / img.Width);
                    cells.Add((pad, y, cell, h)); y += h + sp;
                }
                width = cell + pad * 2; height = y - sp + pad; break;
            }
            case CollageStyle.Featured:
            {
                int big = cell * 2 + sp, rest = n - 1, rows = Math.Max(2, rest);
                int small = rest > 0 ? (big - (rows - 1) * sp) / rows : 0;
                cells.Add((pad, pad, big, big));
                for (int i = 0; i < rest; i++) cells.Add((pad + big + sp, pad + i * (small + sp), small, small));
                width = pad * 2 + big + (rest > 0 ? sp + small : 0); height = pad * 2 + big; break;
            }
            default:
            {
                int cols = (int)Math.Ceiling(Math.Sqrt(n)), rows = (int)Math.Ceiling((double)n / cols);
                for (int i = 0; i < n; i++) cells.Add((pad + i % cols * (cell + sp), pad + i / cols * (cell + sp), cell, cell));
                width = pad * 2 + cols * cell + (cols - 1) * sp; height = pad * 2 + rows * cell + (rows - 1) * sp; break;
            }
        }
        var canvas = new MagickImage(new MagickColor(o.Background), (uint)width, (uint)height);
        for (int i = 0; i < n; i++)
        {
            var (x, y, w, h) = cells[i];
            using var piece = (MagickImage)images[i].Clone();
            if (o.Fill)
            {
                piece.Resize(new MagickGeometry((uint)w, (uint)h) { FillArea = true });
                piece.Extent((uint)w, (uint)h, Gravity.Center);
            }
            else piece.Resize(new MagickGeometry((uint)w, (uint)h));
            using var rounded = Rounded(piece, o.CornerRadius);
            canvas.Composite(rounded, x + (w - (int)rounded.Width) / 2, y + (h - (int)rounded.Height) / 2, CompositeOperator.Over);
        }
        return canvas;
    }

    public static MagickImage Redact(MagickImage source, IEnumerable<RectF> rects, RedactionStyle style)
    {
        var img = (MagickImage)source.Clone();
        foreach (var r in rects)
        {
            int x = (int)(r.X * img.Width), y = (int)(r.Y * img.Height);
            uint w = (uint)Math.Max(1, r.W * img.Width), h = (uint)Math.Max(1, r.H * img.Height);
            using var piece = (MagickImage)img.Clone();
            piece.Crop(new MagickGeometry(x, y, w, h));
            piece.ResetPage();
            switch (style)
            {
                case RedactionStyle.Solid: piece.Evaluate(Channels.RGB, EvaluateOperator.Set, 0); break;
                case RedactionStyle.Pixelate:
                    var block = Math.Max(8, Math.Max(w, h) / 12);
                    piece.Scale(new MagickGeometry(Math.Max(1, w / block), Math.Max(1, h / block)) { IgnoreAspectRatio = true });
                    piece.Scale(new MagickGeometry(w, h) { IgnoreAspectRatio = true });
                    break;
                default: piece.Blur(0, Math.Max(8, Math.Max(w, h) / 8.0)); break;
            }
            img.Composite(piece, x, y, CompositeOperator.Over);
        }
        return img;
    }

    // MARK: Metadata

    public static List<(string Group, string Key, string Value)> Metadata(string path)
    {
        using var img = new MagickImage(path);
        var list = new List<(string, string, string)>
        {
            ("File", "Format", img.Format.ToString()),
            ("File", "Dimensions", $"{img.Width} × {img.Height}"),
            ("File", "Color space", img.ColorSpace.ToString()),
            ("File", "Bit depth", img.Depth.ToString()),
        };
        if (img.GetExifProfile() is { } exif)
            foreach (var v in exif.Values)
            {
                var group = v.Tag.ToString().StartsWith("GPS") ? "GPS" : "EXIF";
                var value = v.GetValue() is Array arr ? string.Join(", ", arr.Cast<object>()) : v.GetValue()?.ToString() ?? "";
                list.Add((group, v.Tag.ToString(), value));
            }
        if (img.GetIptcProfile() is { } iptc) foreach (var v in iptc.Values) list.Add(("IPTC", v.Tag.ToString(), v.Value));
        if (img.GetXmpProfile() != null) list.Add(("XMP", "XMP packet", "present"));
        return list;
    }

    public static string StripMetadata(string path)
    {
        using var img = new MagickImage(path);
        var quality = img.Quality == 0 ? 92u : img.Quality;
        img.Strip();
        var output = Output.PathFor(path, Path.GetExtension(path).TrimStart('.'), "clean");
        img.Quality = quality;
        img.Write(output);
        return output;
    }

    public static string EditMetadata(string path, string? title, string? author, string? copyright, string? description, bool removeGps)
    {
        using var img = new MagickImage(path);
        var quality = img.Quality == 0 ? 92u : img.Quality;
        var exif = img.GetExifProfile() ?? new ExifProfile();
        if (!string.IsNullOrEmpty(author)) exif.SetValue(ExifTag.Artist, author);
        if (!string.IsNullOrEmpty(copyright)) exif.SetValue(ExifTag.Copyright, copyright);
        if (!string.IsNullOrEmpty(description)) exif.SetValue(ExifTag.ImageDescription, description);
        if (!string.IsNullOrEmpty(title)) exif.SetValue(ExifTag.XPTitle, System.Text.Encoding.Unicode.GetBytes(title + "\0"));
        if (removeGps) foreach (var v in exif.Values.Where(v => v.Tag.ToString().StartsWith("GPS")).ToList()) exif.RemoveValue(v.Tag);
        img.SetProfile(exif);
        img.Quality = quality;
        var output = Output.PathFor(path, Path.GetExtension(path).TrimStart('.'), "edited");
        img.Write(output);
        return output;
    }

    // MARK: QR / barcodes

    public static List<string> ReadCodes(MagickImage img)
    {
        using var rgb = (MagickImage)img.Clone();
        rgb.Alpha(AlphaAction.Remove);
        var bytes = rgb.GetPixels().ToByteArray(PixelMapping.RGB)!;
        var source = new ZXing.RGBLuminanceSource(bytes, (int)rgb.Width, (int)rgb.Height, ZXing.RGBLuminanceSource.BitmapFormat.RGB24);
        var reader = new ZXing.BarcodeReaderGeneric { AutoRotate = true, Options = { TryHarder = true, TryInverted = true } };
        return reader.DecodeMultiple(source)?.Select(r => r.Text).Distinct().ToList() ?? [];
    }

    public static List<string> ReadCodes(string path)
    {
        if (Formats.From(path) == ConversionFormat.Pdf)
            return Pdf.RenderPages(path, 150).SelectMany(p => { using (p) return ReadCodes(p); }).Distinct().ToList();
        using var img = Load(path);
        return ReadCodes(img);
    }
}
