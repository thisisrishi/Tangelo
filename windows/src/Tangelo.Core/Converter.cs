namespace Tangelo.Core;

public static class Converter
{
    /// Targets valid for every file in the selection.
    public static List<ConversionFormat> TargetsFor(IReadOnlyList<string> paths)
    {
        var cats = paths.Select(Formats.CategoryOf).Distinct().ToList();
        if (cats.Count == 0) return [];
        var source = paths.Count == 1 ? Formats.From(paths[0]) : null;
        var common = Formats.Targets(cats[0], source).ToList();
        foreach (var c in cats.Skip(1)) common = common.Intersect(Formats.Targets(c)).ToList();
        return common;
    }

    public static async Task<List<string>> Convert(IReadOnlyList<string> inputs, ConversionFormat f, IProgress<double>? progress = null)
    {
        if (f.Category() == FileCategory.Archive && (inputs.Count > 1 || inputs.Any(i => Formats.CategoryOf(i) != FileCategory.Archive)))
            return [Archives.Create(inputs, f)];
        var outs = new List<string>();
        for (int i = 0; i < inputs.Count; i++)
        {
            outs.AddRange(await Convert(inputs[i], f));
            progress?.Report((i + 1.0) / inputs.Count);
        }
        return outs;
    }

    public static async Task<List<string>> Convert(string input, ConversionFormat f)
    {
        var cat = Formats.CategoryOf(input);
        var title = Path.GetFileNameWithoutExtension(input);
        var output = Output.PathFor(input, f.Extension());
        switch (cat, f.Category())
        {
            case (FileCategory.Image or FileCategory.Vector, FileCategory.Image or FileCategory.Pdf):
                Images.Convert(input, output, f);
                return [output];
            case (FileCategory.Pdf, FileCategory.Image):
                return Pdf.ExportPages(input, f);
            case (FileCategory.Video, FileCategory.Image) when f == ConversionFormat.Gif:
                await Media.Gif(input, output, Settings.Current.GifFps, Settings.Current.GifWidth);
                return [output];
            case (FileCategory.Video, FileCategory.Video):
                await Media.ConvertVideo(input, output, f);
                return [output];
            case (FileCategory.Video or FileCategory.Audio, FileCategory.Audio):
                await Media.ConvertAudio(input, output, f);
                return [output];
            case (FileCategory.Document or FileCategory.Pdf, FileCategory.Document or FileCategory.Pdf):
                var html = await Documents.ReadHtml(input);
                await Documents.Write(html, output, f, title);
                return [output];
            case (FileCategory.Spreadsheet, _):
                Sheets.Write(await Sheets.Read(input), output, f, title);
                return [output];
            case (FileCategory.Presentation, _):
                return await Slides.Convert(input, f);
            case (FileCategory.Subtitle, _):
                Subtitles.Write(Subtitles.Read(input), output, f);
                return [output];
            case (FileCategory.Archive, FileCategory.Archive):
                var extracted = Archives.Extract(input);
                try { return [Archives.Create([extracted], f)]; }
                finally { Directory.Delete(extracted, true); }
            default:
                throw new TangeloException($"Conversion from {cat.Title().ToLowerInvariant()} to {f.Label()} is not available.");
        }
    }
}

public enum FileTool
{
    Compress, Metadata, BatchRename,
    EditImage, AnnotateImage, FrameImage, CropImage, ResizeImage, RemoveBackground, ExtractText, RedactImage, CreateCollage, ReadQRCode,
    TrimVideo, CropVideo, ChangeVideoSpeed, MuteVideo, JoinVideos, SplitVideo, VideoSnapshots, RedactVideo, VideoToGif,
    TrimAudio, NormalizeAudio, AudioChannels, RedactAudio, AudioToVideo,
    CreatePdf, MergePdf, SplitPdf, OrganizePdf, ExtractArchive,
}

public enum FileAction { Share, Float, CopyTo, MoveTo, CopyPath, Terminal, Preview, Reveal }

public static class ToolCatalog
{
    public static string Title(this FileTool t) => t switch
    {
        FileTool.Compress => "Compress", FileTool.Metadata => "Metadata", FileTool.BatchRename => "Rename",
        FileTool.EditImage => "Adjust", FileTool.AnnotateImage => "Annotate", FileTool.FrameImage => "Frame",
        FileTool.CropImage or FileTool.CropVideo => "Crop", FileTool.ResizeImage => "Resize", FileTool.RemoveBackground => "Remove BG",
        FileTool.ExtractText => "Copy Text", FileTool.RedactImage or FileTool.RedactVideo => "Redact", FileTool.CreateCollage => "Collage",
        FileTool.ReadQRCode => "Read QR", FileTool.TrimVideo or FileTool.TrimAudio => "Trim", FileTool.ChangeVideoSpeed => "Speed",
        FileTool.MuteVideo => "Mute", FileTool.JoinVideos => "Join", FileTool.SplitVideo => "Split", FileTool.VideoSnapshots => "Snapshots",
        FileTool.VideoToGif => "Make GIF", FileTool.NormalizeAudio => "Normalize", FileTool.AudioChannels => "Channels",
        FileTool.RedactAudio => "Bleep", FileTool.AudioToVideo => "Visualize", FileTool.CreatePdf => "Make PDF",
        FileTool.MergePdf => "Merge PDF", FileTool.SplitPdf => "Split PDF", FileTool.OrganizePdf => "Organize",
        FileTool.ExtractArchive => "Extract", _ => t.ToString(),
    };

    /// Segoe Fluent Icons glyphs.
    public static string Glyph(this FileTool t) => t switch
    {
        FileTool.Compress => "", FileTool.Metadata => "", FileTool.BatchRename => "",
        FileTool.EditImage => "", FileTool.AnnotateImage => "", FileTool.FrameImage => "",
        FileTool.CropImage or FileTool.CropVideo => "", FileTool.ResizeImage => "", FileTool.RemoveBackground => "",
        FileTool.ExtractText => "", FileTool.RedactImage or FileTool.RedactVideo => "", FileTool.CreateCollage => "",
        FileTool.ReadQRCode => "", FileTool.TrimVideo or FileTool.TrimAudio => "", FileTool.ChangeVideoSpeed => "",
        FileTool.MuteVideo => "", FileTool.JoinVideos => "", FileTool.SplitVideo => "", FileTool.VideoSnapshots => "",
        FileTool.VideoToGif => "", FileTool.NormalizeAudio => "", FileTool.AudioChannels => "",
        FileTool.RedactAudio => "", FileTool.AudioToVideo => "", FileTool.CreatePdf => "",
        FileTool.MergePdf => "", FileTool.SplitPdf => "", FileTool.OrganizePdf => "",
        FileTool.ExtractArchive => "", _ => "",
    };

    public static string Subtitle(this FileTool t) => t switch
    {
        FileTool.Compress => "Shrink images, video, audio and PDFs — by preset or target size",
        FileTool.Metadata => "View, edit or strip EXIF, GPS and document metadata",
        FileTool.BatchRename => "Rename many files with patterns, numbering and find/replace",
        FileTool.EditImage => "Exposure, contrast, warmth, sharpening and more",
        FileTool.AnnotateImage => "Arrows, shapes, text, highlights and magnifiers",
        FileTool.FrameImage => "Place screenshots on gradient backgrounds with shadow",
        FileTool.CropImage => "Crop to a ratio or exact pixels, batch-friendly",
        FileTool.ResizeImage => "Scale by percent, width, height or longest side",
        FileTool.RemoveBackground => "Lift the subject onto a transparent background",
        FileTool.ExtractText => "Recognize text in images and PDFs (OCR) and copy it",
        FileTool.RedactImage => "Blur, pixelate or black out regions — auto-detect faces and text",
        FileTool.CreateCollage => "Combine images into grids and layouts",
        FileTool.ReadQRCode => "Find QR codes and barcodes and copy their contents",
        FileTool.TrimVideo => "Cut the start and end of a video",
        FileTool.CropVideo => "Crop the frame of a video",
        FileTool.ChangeVideoSpeed => "Speed up or slow down from 0.25× to 4×",
        FileTool.MuteVideo => "Remove the audio track",
        FileTool.JoinVideos => "Combine clips into one video",
        FileTool.SplitVideo => "Cut a video into clips at split points",
        FileTool.VideoSnapshots => "Save frames as PNG images",
        FileTool.RedactVideo => "Blur or cover regions across a time range",
        FileTool.VideoToGif => "Turn a segment into an animated GIF",
        FileTool.TrimAudio => "Cut audio, optionally trimming silent ends",
        FileTool.NormalizeAudio => "Even out loudness to a target level",
        FileTool.AudioChannels => "Mono/stereo and left/right balance",
        FileTool.RedactAudio => "Bleep or silence parts of audio",
        FileTool.AudioToVideo => "Make a waveform video for sharing audio",
        FileTool.CreatePdf => "Combine images into a PDF",
        FileTool.MergePdf => "Merge PDFs (and images) into one",
        FileTool.SplitPdf => "Split into pages or page ranges",
        FileTool.OrganizePdf => "Reorder, rotate and delete pages",
        FileTool.ExtractArchive => "Unpack zip, rar, 7z, tar, gz and more",
        _ => "",
    };

    public static bool RunsImmediately(this FileTool t) => t is FileTool.MuteVideo or FileTool.ExtractArchive or FileTool.CreatePdf
        or FileTool.MergePdf or FileTool.ReadQRCode or FileTool.RemoveBackground or FileTool.ExtractText or FileTool.JoinVideos;

    public static bool Applies(this FileTool t, IReadOnlySet<FileCategory> cats, int count)
    {
        if (cats.Count == 0) return false;
        bool Only(FileCategory c) => cats.Count == 1 && cats.Contains(c);
        bool Subset(params FileCategory[] allowed) => cats.All(allowed.Contains);
        bool imageLike = Subset(FileCategory.Image, FileCategory.Vector);
        bool media = Helpers.FFmpeg != null;
        return t switch
        {
            FileTool.Compress or FileTool.Metadata => Subset(FileCategory.Image, FileCategory.Video, FileCategory.Audio, FileCategory.Pdf) && (media || Subset(FileCategory.Image, FileCategory.Pdf)),
            FileTool.BatchRename => count > 1,
            FileTool.EditImage or FileTool.AnnotateImage or FileTool.FrameImage or FileTool.CropImage or FileTool.ResizeImage or FileTool.ReadQRCode => imageLike,
            FileTool.RemoveBackground => imageLike && Background.Available,
            FileTool.RedactImage => imageLike && count == 1,
            FileTool.ExtractText => Subset(FileCategory.Image, FileCategory.Vector, FileCategory.Pdf) && Platform.Ocr != null,
            FileTool.CreateCollage => imageLike && count >= 2,
            FileTool.MuteVideo or FileTool.ChangeVideoSpeed => media && Only(FileCategory.Video),
            FileTool.TrimVideo or FileTool.CropVideo or FileTool.VideoSnapshots or FileTool.VideoToGif or FileTool.RedactVideo or FileTool.SplitVideo => media && Only(FileCategory.Video) && count == 1,
            FileTool.JoinVideos => media && Only(FileCategory.Video) && count >= 2,
            FileTool.TrimAudio or FileTool.RedactAudio or FileTool.AudioToVideo => media && Subset(FileCategory.Audio, FileCategory.Video) && count == 1 && (t != FileTool.TrimAudio || Only(FileCategory.Audio)),
            FileTool.NormalizeAudio or FileTool.AudioChannels => media && Subset(FileCategory.Audio, FileCategory.Video),
            FileTool.CreatePdf => imageLike,
            FileTool.MergePdf => cats.Contains(FileCategory.Pdf) && Subset(FileCategory.Pdf, FileCategory.Image) && count >= 2,
            FileTool.SplitPdf or FileTool.OrganizePdf => Only(FileCategory.Pdf) && count == 1,
            FileTool.ExtractArchive => Only(FileCategory.Archive),
            _ => false,
        };
    }

    public static List<FileTool> Available(IReadOnlyList<string> paths)
    {
        var cats = paths.Select(Formats.CategoryOf).ToHashSet();
        return Enum.GetValues<FileTool>().Where(t => t.Applies(cats, paths.Count)).ToList();
    }

    public static string Title(this FileAction a) => a switch
    {
        FileAction.Share => "Share", FileAction.Float => "Float", FileAction.CopyTo => "Copy to…", FileAction.MoveTo => "Move to…",
        FileAction.CopyPath => "Copy Path", FileAction.Terminal => "Terminal", FileAction.Preview => "Preview", FileAction.Reveal => "Show in Explorer",
        _ => a.ToString(),
    };

    public static string Glyph(this FileAction a) => a switch
    {
        FileAction.Share => "", FileAction.Float => "", FileAction.CopyTo => "", FileAction.MoveTo => "",
        FileAction.CopyPath => "", FileAction.Terminal => "", FileAction.Preview => "", FileAction.Reveal => "",
        _ => "",
    };
}
