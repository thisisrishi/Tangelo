namespace Tangelo.Core;

public enum FileCategory { Image, Vector, Video, Audio, Document, Pdf, Spreadsheet, Presentation, Subtitle, Archive, Folder, Other }

public enum ConversionFormat
{
    // images
    Jpg, Png, Webp, Heic, Tiff, Avif, Bmp, Gif, Ico, Svg,
    // audio
    Mp3, M4a, Wav, Flac, Ogg, Opus, Aiff, Wma,
    // video
    Mp4, Mov, M4v, Mkv, Webm, Avi, Wmv,
    // documents
    Docx, Doc, Odt, Txt, Md, Rtf, Html, Epub, Pdf,
    // spreadsheets
    Xlsx, Xls, Ods, Csv, Tsv, Json,
    // presentations
    Pptx, Ppt, Odp,
    // subtitles
    Srt, Vtt,
    // archives
    Zip, Tar, Gzip, Rar, SevenZip,
}

public static class Formats
{
    static readonly Dictionary<string, ConversionFormat> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jpeg"] = ConversionFormat.Jpg, ["jfif"] = ConversionFormat.Jpg, ["tif"] = ConversionFormat.Tiff,
        ["heif"] = ConversionFormat.Heic, ["aif"] = ConversionFormat.Aiff, ["aac"] = ConversionFormat.M4a,
        ["markdown"] = ConversionFormat.Md, ["htm"] = ConversionFormat.Html, ["xhtml"] = ConversionFormat.Html,
        ["text"] = ConversionFormat.Txt, ["log"] = ConversionFormat.Txt, ["gz"] = ConversionFormat.Gzip,
        ["tgz"] = ConversionFormat.Tar, ["7z"] = ConversionFormat.SevenZip, ["mpeg"] = ConversionFormat.Mp4,
        ["mpg"] = ConversionFormat.Mp4, ["3gp"] = ConversionFormat.Mp4, ["oga"] = ConversionFormat.Ogg,
        ["mka"] = ConversionFormat.Ogg, ["ts"] = ConversionFormat.Mp4, ["flv"] = ConversionFormat.Mp4,
    };

    public static ConversionFormat? From(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        if (ext.Length == 0) return null;
        if (Aliases.TryGetValue(ext, out var a)) return a;
        return Enum.TryParse<ConversionFormat>(ext, true, out var f) && Enum.IsDefined(f) ? f : null;
    }

    public static string Extension(this ConversionFormat f) => f switch
    {
        ConversionFormat.Gzip => "gz",
        ConversionFormat.SevenZip => "7z",
        _ => f.ToString().ToLowerInvariant(),
    };

    public static string Label(this ConversionFormat f) => f switch
    {
        ConversionFormat.Md => "Markdown",
        ConversionFormat.SevenZip => "7Z",
        ConversionFormat.Gzip => "GZ",
        _ => f.ToString().ToUpperInvariant(),
    };

    public static FileCategory Category(this ConversionFormat f) => f switch
    {
        >= ConversionFormat.Jpg and <= ConversionFormat.Ico => FileCategory.Image,
        ConversionFormat.Svg => FileCategory.Vector,
        >= ConversionFormat.Mp3 and <= ConversionFormat.Wma => FileCategory.Audio,
        >= ConversionFormat.Mp4 and <= ConversionFormat.Wmv => FileCategory.Video,
        ConversionFormat.Pdf => FileCategory.Pdf,
        >= ConversionFormat.Docx and <= ConversionFormat.Epub => FileCategory.Document,
        >= ConversionFormat.Xlsx and <= ConversionFormat.Json => FileCategory.Spreadsheet,
        >= ConversionFormat.Pptx and <= ConversionFormat.Odp => FileCategory.Presentation,
        ConversionFormat.Srt or ConversionFormat.Vtt => FileCategory.Subtitle,
        _ => FileCategory.Archive,
    };

    public static FileCategory CategoryOf(string path)
    {
        if (Directory.Exists(path)) return FileCategory.Folder;
        return From(path)?.Category() ?? FileCategory.Other;
    }

    public static string Title(this FileCategory c) => c switch
    {
        FileCategory.Pdf => "PDF",
        _ => c.ToString(),
    };

    /// Segoe Fluent Icons glyph per category, used by the wheel and menus.
    public static string Glyph(this FileCategory c) => c switch
    {
        FileCategory.Image or FileCategory.Vector => "",
        FileCategory.Video => "",
        FileCategory.Audio => "",
        FileCategory.Document => "",
        FileCategory.Pdf => "",
        FileCategory.Spreadsheet => "",
        FileCategory.Presentation => "",
        FileCategory.Subtitle => "",
        FileCategory.Archive => "",
        FileCategory.Folder => "",
        _ => "",
    };

    public static IReadOnlyList<ConversionFormat> Targets(FileCategory c, ConversionFormat? source = null)
    {
        ConversionFormat[] list = c switch
        {
            FileCategory.Image => [ConversionFormat.Jpg, ConversionFormat.Png, ConversionFormat.Webp, ConversionFormat.Avif, ConversionFormat.Tiff, ConversionFormat.Gif, ConversionFormat.Bmp, ConversionFormat.Ico, ConversionFormat.Pdf],
            FileCategory.Vector => [ConversionFormat.Png, ConversionFormat.Jpg, ConversionFormat.Webp, ConversionFormat.Tiff, ConversionFormat.Pdf],
            FileCategory.Video => [ConversionFormat.Mp4, ConversionFormat.Mov, ConversionFormat.M4v, ConversionFormat.Mkv, ConversionFormat.Webm, ConversionFormat.Avi, ConversionFormat.Wmv, ConversionFormat.Gif, ConversionFormat.Mp3, ConversionFormat.M4a, ConversionFormat.Wav],
            FileCategory.Audio => [ConversionFormat.Mp3, ConversionFormat.M4a, ConversionFormat.Wav, ConversionFormat.Flac, ConversionFormat.Aiff, ConversionFormat.Ogg, ConversionFormat.Opus, ConversionFormat.Wma],
            FileCategory.Document => [ConversionFormat.Docx, ConversionFormat.Pdf, ConversionFormat.Rtf, ConversionFormat.Txt, ConversionFormat.Md, ConversionFormat.Html, ConversionFormat.Epub, ConversionFormat.Odt],
            FileCategory.Pdf => [ConversionFormat.Docx, ConversionFormat.Txt, ConversionFormat.Md, ConversionFormat.Html, ConversionFormat.Rtf, ConversionFormat.Epub, ConversionFormat.Png, ConversionFormat.Jpg],
            FileCategory.Spreadsheet => [ConversionFormat.Xlsx, ConversionFormat.Csv, ConversionFormat.Tsv, ConversionFormat.Json, ConversionFormat.Html, ConversionFormat.Md, ConversionFormat.Pdf],
            FileCategory.Presentation => [ConversionFormat.Pdf, ConversionFormat.Pptx, ConversionFormat.Png],
            FileCategory.Subtitle => [ConversionFormat.Srt, ConversionFormat.Vtt, ConversionFormat.Txt],
            _ => [ConversionFormat.Zip, ConversionFormat.Tar, ConversionFormat.Gzip],
        };
        return list.Where(f => f != source && Capabilities.CanWrite(f, c)).ToList();
    }
}

/// What this machine can actually produce (some targets need optional helpers).
public static class Capabilities
{
    public static bool CanWrite(ConversionFormat f, FileCategory from)
    {
        if (from is FileCategory.Video or FileCategory.Audio) return Helpers.FFmpeg != null;
        if (from == FileCategory.Presentation) return Helpers.LibreOffice != null;
        if (from == FileCategory.Spreadsheet && f == ConversionFormat.Xls) return Helpers.LibreOffice != null;
        return true;
    }
}
