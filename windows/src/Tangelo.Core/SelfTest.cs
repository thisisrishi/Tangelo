using ImageMagick;
using ImageMagick.Drawing;

namespace Tangelo.Core;

/// End-to-end engine checks on generated fixtures. Used by `Tangelo.SelfTest` and `Tangelo.exe --selftest`.
public static class SelfTest
{
    public static async Task<int> Run(string dir, TextWriter log)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        Settings.Override(new Settings { OutputLocation = OutputLocation.SameFolder });
        int failures = 0, passes = 0;
        log.WriteLine($"Self-test in {dir}");

        async Task Check(string name, Func<Task<IEnumerable<string>>> body)
        {
            try
            {
                var outs = (await body()).ToList();
                bool ok = outs.Count > 0 && outs.All(o => Output.SizeOf(o) > 0 || Directory.Exists(o) && Directory.EnumerateFileSystemEntries(o).Any());
                if (ok) passes++; else failures++;
                log.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  → {string.Join(", ", outs.Select(o => $"{Path.GetFileName(o)} ({Output.FormatBytes(Output.SizeOf(o))})"))}");
            }
            catch (Exception e)
            {
                failures++;
                log.WriteLine($"FAIL  {name}  ✗ {e.GetType().Name}: {e.Message}");
            }
        }
        Task Sync(string name, Func<IEnumerable<string>> body) => Check(name, () => Task.FromResult(body()));
        string P(string name) => Path.Combine(dir, name);

        // Fixtures
        var png = P("sample.png");
        using (var img = new MagickImage())
        {
            img.Settings.SetDefine("gradient:angle", "135");
            img.Read("gradient:#FF8A00-#FF2E63", 800, 600);
            new Drawables().FontPointSize(64).FillColor(MagickColors.Black).Text(60, 330, "Hello Tangelo 123").Draw(img);
            img.Write(png);
        }
        File.Copy(png, P("sample2.png"));
        bool media = Helpers.FFmpeg != null;
        var wav = P("tone.wav");
        var video = P("clip.mp4");
        if (media)
        {
            await Shell.CheckAsync(Helpers.FFmpeg!, ["-y", "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo:d=0.3", "-f", "lavfi", "-i", "sine=f=440:d=2.7:r=44100",
                "-filter_complex", "[1:a]aformat=channel_layouts=stereo[s];[0:a][s]concat=n=2:v=0:a=1", wav]);
            await Shell.CheckAsync(Helpers.FFmpeg!, ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=3", "-f", "lavfi", "-i", "sine=f=330:d=3",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", video]);
        }
        File.WriteAllText(P("notes.md"), "# Title\n\nSome **bold** and *italic* text with a [link](https://example.com).\n\n- one\n- two\n\n| a | b |\n|---|---|\n| 1 | 2 |\n");
        File.WriteAllText(P("table.csv"), "name,age,city\n\"Doe, Jane\",34,Paris\nBob,27,\"New \"\"York\"\"\"\nZip,007,Oslo\n");
        File.WriteAllText(P("subs.srt"), "1\n00:00:01,000 --> 00:00:02,500\nHello\n\n2\n00:00:03,000 --> 00:00:04,000\nWorld\n");

        // Images
        foreach (var f in new[] { ConversionFormat.Jpg, ConversionFormat.Webp, ConversionFormat.Avif, ConversionFormat.Tiff, ConversionFormat.Gif, ConversionFormat.Bmp, ConversionFormat.Ico, ConversionFormat.Pdf })
            await Check($"image png → {f.Label()}", () => Converter.Convert([png], f).ContinueWith(t => (IEnumerable<string>)t.Result));
        await Sync("compress image (preset)", () => [Images.Compress(png, ImagePreset.Small, null, false)]);
        await Sync("compress image (target 20 KB)", () =>
        {
            var o = Images.Compress(png, null, 20_000, true);
            if (Output.SizeOf(o) > 20_000) throw new TangeloException($"over target: {Output.SizeOf(o)}");
            return [o];
        });
        await Sync("adjust image", () =>
        {
            using var src = Images.Load(png);
            using var r = Images.Adjust(src, new ImageAdjustments { Exposure = 0.5, Contrast = 1.2, Warmth = 0.5, Vignette = 0.6, Grain = 0.3, Sharpness = 1, Shadows = 0.3, Highlights = 0.7 });
            return [Images.SaveLike(r, png, "edited")];
        });
        await Sync("annotate image", () =>
        {
            using var src = Images.Load(png);
            using var r = Images.Annotate(src, [
                new Annotation(AnnotationKind.Arrow, [(0.1, 0.1), (0.45, 0.45)]),
                new Annotation(AnnotationKind.Rectangle, [(0.6, 0.1), (0.9, 0.3)], "#0A84FF"),
                new Annotation(AnnotationKind.Text, [(0.2, 0.75)], Text: "Hi"),
                new Annotation(AnnotationKind.Highlight, [(0.05, 0.45), (0.6, 0.6)], "#FFD60A"),
                new Annotation(AnnotationKind.Number, [(0.8, 0.8)]),
                new Annotation(AnnotationKind.Magnifier, [(0.5, 0.5), (0.6, 0.6)]),
                new Annotation(AnnotationKind.Pen, [(0.1, 0.9), (0.2, 0.85), (0.3, 0.9)]),
            ]);
            return [Images.SaveLike(r, png, "annotated")];
        });
        await Sync("frame image", () => { using var s = Images.Load(png); using var r = Images.Frame(s, new FrameOptions()); return [Images.SaveLike(r, png, "framed", true)]; });
        await Sync("crop image", () => { using var s = Images.Load(png); using var r = Images.Crop(s, new RectF(0.1, 0.1, 0.5, 0.5)); return [Images.SaveLike(r, png, "cropped")]; });
        await Sync("collage", () =>
        {
            var imgs = new[] { Images.Load(png), Images.Load(P("sample2.png")) };
            using var r = Images.Collage(imgs, new CollageOptions { CellSize = 300 });
            foreach (var i in imgs) i.Dispose();
            var o = P("collage.png"); r.Write(o); return [o];
        });
        foreach (var style in Enum.GetValues<RedactionStyle>())
            await Sync($"redact image ({style})", () => { using var s = Images.Load(png); using var r = Images.Redact(s, [new RectF(0.1, 0.4, 0.5, 0.2)], style); return [Images.SaveLike(r, png, $"redacted {style}")]; });
        await Sync("metadata edit + strip", () =>
        {
            var edited = Images.EditMetadata(P("sample.jpg"), "Title", "Me", "© Me", "Desc", true);
            if (!Images.Metadata(edited).Any(m => m.Value.Contains("Me"))) throw new TangeloException("edited field not found");
            return [edited, Images.StripMetadata(edited)];
        });
        await Sync("QR (none expected)", () => { _ = Images.ReadCodes(png); return [png]; });
        if (Background.Available) await Sync("remove background", () => [Background.RemoveBackground(png)]);
        else log.WriteLine("SKIP  remove background (model not found)");
        if (Platform.Ocr != null)
            await Check("OCR image", async () =>
            {
                using var img = Images.Load(png);
                var text = string.Join(" ", await Platform.Ocr(img));
                if (!text.Contains("Tangelo", StringComparison.OrdinalIgnoreCase)) throw new TangeloException($"OCR read: {text}");
                var o = P("ocr.txt"); File.WriteAllText(o, text); return [o];
            });
        else log.WriteLine("SKIP  OCR (platform service unavailable)");
        await Sync("create PDF from images", () => [Pdf.CreateFromImageFiles([png, P("sample2.png")])]);

        // Documents
        foreach (var f in new[] { ConversionFormat.Docx, ConversionFormat.Rtf, ConversionFormat.Html, ConversionFormat.Txt, ConversionFormat.Pdf, ConversionFormat.Epub, ConversionFormat.Odt })
            await Check($"markdown → {f.Label()}", async () => await Converter.Convert([P("notes.md")], f));
        await Check("docx → markdown (round trip)", async () =>
        {
            var o = (await Converter.Convert([P("notes.docx")], ConversionFormat.Md))[0];
            var md = File.ReadAllText(o);
            if (!md.Contains("**bold**") || !md.Contains("# Title")) throw new TangeloException($"lost formatting: {md}");
            return [o];
        });
        foreach (var src in new[] { "notes.rtf", "notes.odt", "notes.epub", "notes.html", "notes.pdf" })
            await Check($"{Path.GetExtension(src).TrimStart('.')} → txt", async () =>
            {
                var o = (await Converter.Convert([P(src)], ConversionFormat.Txt))[0];
                if (!File.ReadAllText(o).Contains("bold")) throw new TangeloException("text missing");
                return [o];
            });
        await Check("pdf → png pages", async () => await Converter.Convert([P("notes.pdf")], ConversionFormat.Png));
        var merged = "";
        await Sync("merge PDF", () => [merged = Pdf.Merge([P("notes.pdf"), P("Images.pdf"), png])]);
        await Sync("split PDF", () => Pdf.Split(merged));
        await Sync("split PDF (ranges)", () => Pdf.Split(merged, Pdf.ParseRanges("1-2, 3", Pdf.PageCount(merged))));
        await Sync("organize PDF", () => [Pdf.Organize(merged, [2, 0, 1], new Dictionary<int, int> { [2] = 90 }, true)]);
        await Sync("compress PDF", () => [Pdf.Compress(P("Images.pdf"), 50, 72)]);
        await Sync("strip PDF metadata", () => [Pdf.StripMetadata(P("notes.pdf"))]);

        // Spreadsheets, subtitles, archives
        foreach (var f in new[] { ConversionFormat.Xlsx, ConversionFormat.Tsv, ConversionFormat.Json, ConversionFormat.Html, ConversionFormat.Md, ConversionFormat.Pdf })
            await Check($"csv → {f.Label()}", async () => await Converter.Convert([P("table.csv")], f));
        await Check("xlsx → csv (round trip)", async () =>
        {
            var o = (await Converter.Convert([P("table.xlsx")], ConversionFormat.Csv))[0];
            var text = File.ReadAllText(o);
            if (!text.Contains("\"Doe, Jane\"") || !text.Contains("007")) throw new TangeloException($"round trip mismatch: {text}");
            return [o];
        });
        await Check("srt → vtt", async () => await Converter.Convert([P("subs.srt")], ConversionFormat.Vtt));
        await Check("vtt → srt", async () => await Converter.Convert([P("subs.vtt")], ConversionFormat.Srt));
        await Check("files → zip", async () => await Converter.Convert([png, P("notes.md")], ConversionFormat.Zip));
        await Check("file → tar.gz", async () => await Converter.Convert([P("table.csv")], ConversionFormat.Gzip));
        await Sync("extract zip", () => [Archives.Extract(P("Archive.zip"))]);
        await Sync("extract tar.gz", () => [Archives.Extract(P("table.csv.tar.gz"))]);

        // Audio & video
        if (media)
        {
            foreach (var f in new[] { ConversionFormat.Mp3, ConversionFormat.M4a, ConversionFormat.Flac, ConversionFormat.Ogg, ConversionFormat.Opus })
                await Check($"audio wav → {f.Label()}", async () => await Converter.Convert([wav], f));
            await Check("trim audio + silence", async () => [await Media.TrimAudio(wav, 0, 2, true, -40)]);
            await Check("normalize audio", async () => [await Media.Normalize(wav, -16, -1)]);
            await Check("audio channels mono", async () => [await Media.Channels(wav, 1, 1, 0.5)]);
            await Check("bleep audio", async () => [await Media.Bleep(wav, [(1, 1.5)], true)]);
            await Check("compress audio", async () => [await Media.CompressAudio(wav, Media.AudioPreset.Voice, null)]);
            await Check("waveform envelope", async () => { var e = await Media.Envelope(wav, 200); if (e.Length != 200) throw new TangeloException("bad envelope"); return [wav]; });
            foreach (var style in Enum.GetValues<VisualizerStyle>())
                await Check($"visualizer ({style})", async () => [await Media.Visualize(wav, new Media.VisualizerOptions(style, Width: 640, Height: 360, Title: "Test"))]);
            foreach (var f in new[] { ConversionFormat.Mov, ConversionFormat.Webm, ConversionFormat.Mkv, ConversionFormat.Gif, ConversionFormat.M4a })
                await Check($"video → {f.Label()}", async () => await Converter.Convert([video], f));
            await Check("trim video", async () => [await Media.Trim(video, 0.5, 2)]);
            await Check("crop video", async () => [await Media.Crop(video, new RectF(0.25, 0.25, 0.5, 0.5))]);
            await Check("speed video 2x", async () => [await Media.ChangeSpeed(video, 2)]);
            await Check("speed video 0.25x", async () => [await Media.ChangeSpeed(video, 0.25)]);
            await Check("mute video", async () => [await Media.Mute(video)]);
            await Check("join videos", async () => [await Media.JoinVideos([video, P("clip trimmed.mp4")])]);
            await Check("split video (separate)", async () => await Media.Split(video, [1, 2], new HashSet<int> { 1 }, false));
            await Check("split video (join kept)", async () => await Media.Split(video, [1, 2], new HashSet<int> { 1 }, true));
            await Check("snapshots", async () => await Media.Snapshots(video, [0.5, 1.5]));
            foreach (var style in Enum.GetValues<RedactionStyle>())
                await Check($"redact video ({style})", async () => [await Media.Redact(video, [new Redaction(new RectF(0, 0, 0.5, 0.5), 0, 1.5), new Redaction(new RectF(0.6, 0.6, 0.3, 0.3))], style)]);
            await Check("compress video (target 150 KB)", async () =>
            {
                var o = await Media.CompressVideo(video, null, 150_000);
                if (Output.SizeOf(o) > 190_000) throw new TangeloException($"too large: {Output.SizeOf(o)}");
                return [o];
            });
            await Check("normalize video audio", async () => [await Media.Normalize(video, -14, -1)]);
            await Check("bleep video audio", async () => [await Media.Bleep(video, [(0.5, 1)], false)]);
            await Check("strip video metadata", async () => [await Media.StripMetadata(video)]);
            await Check("make GIF (range)", async () => { var o = P("range.gif"); await Media.Gif(video, o, 10, 320, 0.5, 2); return [o]; });
        }
        else log.WriteLine("SKIP  audio/video (ffmpeg not found)");

        log.WriteLine(failures == 0 ? $"\nALL PASSED ({passes} checks)" : $"\n{failures} FAILED, {passes} passed");
        return failures;
    }
}
