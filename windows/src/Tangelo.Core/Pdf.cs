using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace Tangelo.Core;

public static class Pdf
{
    static Pdf() => Fonts.Install();

    public static int PageCount(string path)
    {
        using var doc = Open(path, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    static PdfDocument Open(string path, PdfDocumentOpenMode mode)
    {
        try
        {
            var doc = PdfReader.Open(path, mode);
            if (doc.PageCount == 0) throw new TangeloException("This PDF has no pages.");
            return doc;
        }
        catch (PdfReaderException) { throw new TangeloException("The PDF cannot be read or is password-protected."); }
    }

    public static IEnumerable<MagickImage> RenderPages(string path, int dpi = 144, int? onlyPage = null)
    {
        var bytes = File.ReadAllBytes(path);
        var options = new PDFtoImage.RenderOptions(Dpi: dpi, WithAnnotations: true, BackgroundColor: SKColors.White);
        IEnumerable<SKBitmap> pages = onlyPage is { } p
            ? [PDFtoImage.Conversion.ToImage(bytes, page: p, options: options)]
            : PDFtoImage.Conversion.ToImages(bytes, options: options);
        foreach (var bmp in pages)
        {
            using (bmp)
            using (var data = bmp.Encode(SKEncodedImageFormat.Png, 100))
                yield return new MagickImage(data.ToArray());
        }
    }

    public static void CreateFromImages(IEnumerable<MagickImage> images, string output)
    {
        using var doc = new PdfDocument();
        foreach (var img in images)
        {
            using var png = new MemoryStream();
            using (var copy = (MagickImage)img.Clone()) { copy.Format = MagickFormat.Png; copy.Write(png); }
            png.Position = 0;
            using var x = XImage.FromStream(png);
            var page = doc.AddPage();
            // 72 points per inch at the image's own DPI (default 72 → 1px = 1pt).
            page.Width = XUnit.FromPoint(img.Width);
            page.Height = XUnit.FromPoint(img.Height);
            using var g = XGraphics.FromPdfPage(page);
            g.DrawImage(x, 0, 0, page.Width.Point, page.Height.Point);
        }
        doc.Save(output);
    }

    public static string CreateFromImageFiles(IReadOnlyList<string> paths)
    {
        var sorted = paths.OrderBy(Path.GetFileName, NaturalComparer.Instance).ToList();
        var output = Output.Unique(Output.DirectoryFor(sorted[0]), sorted.Count == 1 ? Path.GetFileNameWithoutExtension(sorted[0]) : "Images", "pdf");
        var images = sorted.Select(Images.Load).ToList();
        try { CreateFromImages(images, output); } finally { images.ForEach(i => i.Dispose()); }
        return output;
    }

    public static string Merge(IReadOnlyList<string> paths)
    {
        var sorted = paths.OrderBy(Path.GetFileName, NaturalComparer.Instance).ToList();
        var output = Output.PathFor(sorted[0], "pdf", "merged");
        using var outDoc = new PdfDocument();
        foreach (var p in sorted)
        {
            if (Formats.CategoryOf(p) is FileCategory.Image or FileCategory.Vector)
            {
                var tmp = Path.Combine(Output.TempDirectory(), "img.pdf");
                using (var img = Images.Load(p)) CreateFromImages([img], tmp);
                AppendAll(outDoc, tmp);
            }
            else AppendAll(outDoc, p);
        }
        outDoc.Save(output);
        return output;
    }

    static void AppendAll(PdfDocument target, string path)
    {
        using var src = Open(path, PdfDocumentOpenMode.Import);
        foreach (var page in src.Pages) target.AddPage(page);
    }

    public static List<(int From, int To)>? ParseRanges(string text, int max)
    {
        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;
        var list = new List<(int, int)>();
        foreach (var part in parts)
        {
            var ends = part.Split('-', StringSplitOptions.TrimEntries);
            if (ends.Length == 1 && int.TryParse(ends[0], out var a) && a >= 1 && a <= max) list.Add((a, a));
            else if (ends.Length == 2 && int.TryParse(ends[0], out var s) && int.TryParse(ends[1], out var e) && s >= 1 && s <= e) list.Add((s, Math.Min(e, max)));
            else return null;
        }
        return list;
    }

    public static List<string> Split(string path, IReadOnlyList<(int From, int To)>? ranges = null)
    {
        using var src = Open(path, PdfDocumentOpenMode.Import);
        var groups = ranges ?? Enumerable.Range(1, src.PageCount).Select(i => (i, i)).ToList();
        var dir = Output.NewFolder(path, "split");
        var name = Path.GetFileNameWithoutExtension(path);
        var outs = new List<string>();
        foreach (var (from, to) in groups)
        {
            using var part = new PdfDocument();
            for (int i = from; i <= to && i <= src.PageCount; i++) part.AddPage(src.Pages[i - 1]);
            if (part.PageCount == 0) continue;
            var output = Output.Unique(dir, from == to ? $"{name} page {from}" : $"{name} pages {from}-{to}", "pdf");
            part.Save(output);
            outs.Add(output);
        }
        return outs;
    }

    /// Reorders, rotates (by degrees per original index), deletes and optionally equalizes widths.
    public static string Organize(string path, IReadOnlyList<int> order, IReadOnlyDictionary<int, int> rotations, bool equalizeWidths)
    {
        if (order.Count == 0) throw new TangeloException("Keep at least one page.");
        using var src = Open(path, PdfDocumentOpenMode.Import);
        using var outDoc = new PdfDocument();
        var maxWidth = src.Pages.Cast<PdfPage>().Max(p => p.Width.Point);
        foreach (var idx in order)
        {
            var page = outDoc.AddPage(src.Pages[idx]);
            if (rotations.TryGetValue(idx, out var deg) && deg != 0) page.Rotate = ((page.Rotate + deg) % 360 + 360) % 360;
        }
        var output = Output.PathFor(path, "pdf", "organized");
        outDoc.Save(output);
        if (equalizeWidths && outDoc.Pages.Cast<PdfPage>().Any(p => Math.Abs(p.Width.Point - maxWidth) > 1))
        {
            // Re-render pages that are narrower so every page shares the widest page's width.
            var images = RenderPages(output, 150).ToList();
            foreach (var img in images.Where(i => Math.Abs(i.Width - images.Max(x => x.Width)) > 2))
                img.Resize(new MagickGeometry(images.Max(x => x.Width), 0));
            File.Delete(output);
            CreateFromImages(images, output);
            images.ForEach(i => i.Dispose());
        }
        return output;
    }

    /// Re-renders pages as JPEG at the chosen DPI; refuses results larger than the original.
    public static string Compress(string path, int quality, int dpi, long? targetBytes = null)
    {
        var output = Output.PathFor(path, "pdf", "compressed");
        var original = new FileInfo(path).Length;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            using (var doc = new PdfDocument())
            {
                using var src = Open(path, PdfDocumentOpenMode.Import);
                int i = 0;
                foreach (var img in RenderPages(path, dpi))
                {
                    using (img)
                    {
                        var size = src.Pages[i++];
                        img.Format = MagickFormat.Jpeg;
                        img.Quality = (uint)quality;
                        using var ms = new MemoryStream(img.ToByteArray());
                        using var x = XImage.FromStream(ms);
                        var page = doc.AddPage();
                        bool rotated = size.Rotate % 180 != 0;
                        page.Width = rotated ? size.Height : size.Width;
                        page.Height = rotated ? size.Width : size.Height;
                        using var g = XGraphics.FromPdfPage(page);
                        g.DrawImage(x, 0, 0, page.Width.Point, page.Height.Point);
                    }
                }
                if (File.Exists(output)) File.Delete(output);
                doc.Save(output);
            }
            var size2 = new FileInfo(output).Length;
            if (targetBytes is not { } t || size2 <= t) break;
            quality = Math.Max(15, quality - 12); dpi = Math.Max(60, dpi - 20);
        }
        if (new FileInfo(output).Length >= original)
        {
            File.Delete(output);
            throw new TangeloException("Compressed output would exceed the original file size. This PDF is already compact.");
        }
        return output;
    }

    /// `nameSource` names the output folder and files (defaults to the PDF itself).
    public static List<string> ExportPages(string path, ConversionFormat f, int dpi = 144, string? nameSource = null)
    {
        var source = nameSource ?? path;
        var dir = Output.NewFolder(source, nameSource == null ? "pages" : "slides");
        var name = Path.GetFileNameWithoutExtension(source);
        var outs = new List<string>();
        int i = 1;
        foreach (var img in RenderPages(path, dpi))
        {
            using (img)
            {
                var output = Output.Unique(dir, $"{name} page {i++}", f.Extension());
                Images.Save(img, output, f, 92);
                outs.Add(output);
            }
        }
        return outs;
    }

    /// Per-page text; empty strings mark pages without a text layer (callers may OCR them).
    public static List<string> PageTexts(string path)
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
        return doc.GetPages().Select(p => UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(p)).ToList();
    }

    public static Dictionary<string, string> Info(string path)
    {
        using var doc = Open(path, PdfDocumentOpenMode.Import);
        var i = doc.Info;
        return new()
        {
            ["Pages"] = doc.PageCount.ToString(), ["Title"] = i.Title, ["Author"] = i.Author, ["Subject"] = i.Subject,
            ["Keywords"] = i.Keywords, ["Creator"] = i.Creator, ["Producer"] = i.Producer,
            ["Created"] = i.CreationDate == default ? "" : i.CreationDate.ToString("g"),
        };
    }

    public static string StripMetadata(string path)
    {
        using var doc = Open(path, PdfDocumentOpenMode.Modify);
        foreach (var key in doc.Info.Elements.Keys.ToList()) doc.Info.Elements.Remove(key);
        doc.Internals.Catalog.Elements.Remove("/Metadata");
        var output = Output.PathFor(path, "pdf", "clean");
        doc.Save(output);
        return output;
    }
}

/// PDFsharp needs explicit font files outside Windows; map the families we use to system fonts.
static class Fonts
{
    static bool installed;
    public static void Install()
    {
        if (installed) return;
        installed = true;
        if (OperatingSystem.IsWindows()) { GlobalFontSettings.UseWindowsFontsUnderWindows = true; return; }
        GlobalFontSettings.FontResolver ??= new SystemFontResolver();
    }

    sealed class SystemFontResolver : IFontResolver
    {
        static readonly string[] Dirs = ["/System/Library/Fonts/Supplemental", "/Library/Fonts", "/usr/share/fonts/truetype/dejavu", "/usr/share/fonts/truetype/liberation"];

        public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic)
        {
            var suffix = (bold, italic) switch { (true, true) => " Bold Italic", (true, false) => " Bold", (false, true) => " Italic", _ => "" };
            return new FontResolverInfo("Arial" + suffix);
        }

        public byte[]? GetFont(string faceName)
        {
            foreach (var dir in Dirs)
            {
                var p = Path.Combine(dir, faceName + ".ttf");
                if (File.Exists(p)) return File.ReadAllBytes(p);
            }
            foreach (var dir in Dirs)
            {
                if (!Directory.Exists(dir)) continue;
                var any = Directory.EnumerateFiles(dir, "*.ttf").FirstOrDefault();
                if (any != null) return File.ReadAllBytes(any);
            }
            return null;
        }
    }
}
