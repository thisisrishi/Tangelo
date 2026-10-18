using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ClosedXML.Excel;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace Tangelo.Core;

public static class Sheets
{
    public static async Task<List<List<string>>> Read(string path) => Formats.From(path) switch
    {
        ConversionFormat.Csv => ParseDelimited(TextFile.Read(path), ','),
        ConversionFormat.Tsv => ParseDelimited(TextFile.Read(path), '\t'),
        ConversionFormat.Json => ParseJson(File.ReadAllText(path)),
        ConversionFormat.Xlsx => ReadXlsx(path),
        ConversionFormat.Ods => ReadOds(path),
        ConversionFormat.Xls => ReadXlsx(await ViaLibreOffice(path, "xlsx")),
        _ => throw new TangeloException("This file is not a spreadsheet."),
    };

    static async Task<string> ViaLibreOffice(string path, string ext)
    {
        var soffice = Helpers.LibreOffice ?? throw new ToolMissingException("LibreOffice", "Converting legacy .xls needs LibreOffice (libreoffice.org).");
        var dir = Output.TempDirectory();
        await Shell.CheckAsync(soffice, ["--headless", "--convert-to", ext, "--outdir", dir, path]);
        return Directory.EnumerateFiles(dir).First();
    }

    public static List<List<string>> ParseDelimited(string text, char sep)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; } else quoted = false; }
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == sep) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row); row = []; }
            else if (c != '\r') field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    static List<List<string>> ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array) throw new TangeloException("The JSON must be an array of objects or arrays.");
        var items = root.EnumerateArray().ToList();
        if (items.All(i => i.ValueKind == JsonValueKind.Object))
        {
            var keys = new List<string>();
            foreach (var i in items) foreach (var p in i.EnumerateObject()) if (!keys.Contains(p.Name)) keys.Add(p.Name);
            return [keys, .. items.Select(i => keys.Select(k => i.TryGetProperty(k, out var v) ? Scalar(v) : "").ToList())];
        }
        return items.Select(i => i.ValueKind == JsonValueKind.Array ? i.EnumerateArray().Select(Scalar).ToList() : [Scalar(i)]).ToList();
    }

    static string Scalar(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Null => "",
        _ => v.GetRawText(),
    };

    static List<List<string>> ReadXlsx(string path)
    {
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.First();
        var range = ws.RangeUsed();
        if (range == null) return [];
        return range.Rows().Select(r => r.Cells().Select(c => c.GetFormattedString()).ToList()).ToList();
    }

    static List<List<string>> ReadOds(string path)
    {
        XNamespace t = "urn:oasis:names:tc:opendocument:xmlns:table:1.0", x = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        using var zip = ZipFile.OpenRead(path);
        using var s = (zip.GetEntry("content.xml") ?? throw new TangeloException("The spreadsheet is unreadable.")).Open();
        var table = XDocument.Load(s).Descendants(t + "table").FirstOrDefault();
        var rows = new List<List<string>>();
        foreach (var r in table?.Elements(t + "table-row") ?? [])
        {
            var row = new List<string>();
            foreach (var c in r.Elements().Where(e => e.Name == t + "table-cell" || e.Name == t + "covered-table-cell"))
            {
                var repeat = Math.Min(int.TryParse((string?)c.Attribute(t + "number-columns-repeated"), out var n) ? n : 1, 256);
                var text = string.Join("\n", c.Elements(x + "p").Select(p => p.Value));
                for (int i = 0; i < repeat; i++) row.Add(text);
            }
            while (row.Count > 0 && row[^1].Length == 0) row.RemoveAt(row.Count - 1);
            rows.Add(row);
        }
        while (rows.Count > 0 && rows[^1].Count == 0) rows.RemoveAt(rows.Count - 1);
        return rows;
    }

    public static void Write(List<List<string>> table, string output, ConversionFormat f, string title)
    {
        switch (f)
        {
            case ConversionFormat.Csv: File.WriteAllText(output, Delimited(table, ",")); break;
            case ConversionFormat.Tsv: File.WriteAllText(output, Delimited(table, "\t")); break;
            case ConversionFormat.Json: File.WriteAllText(output, Json(table)); break;
            case ConversionFormat.Html: File.WriteAllText(output, $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title></head><body>{Html(table, title)}</body></html>"); break;
            case ConversionFormat.Md: File.WriteAllText(output, Markdown(table)); break;
            case ConversionFormat.Pdf: Documents.Write(Html(table, title), output, ConversionFormat.Pdf, title).GetAwaiter().GetResult(); break;
            case ConversionFormat.Xlsx: WriteXlsx(table, output); break;
            default: throw new TangeloException($"Cannot write {f.Label()} spreadsheets.");
        }
    }

    public static string Delimited(List<List<string>> t, string sep) =>
        string.Join("\n", t.Select(r => string.Join(sep, r.Select(v => v.Contains(sep) || v.Contains('"') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v)))) + "\n";

    static string Json(List<List<string>> t)
    {
        if (t.Count == 0) return "[]";
        var header = t[0].Select((h, i) => string.IsNullOrEmpty(h) ? $"column{i + 1}" : h).ToList();
        var objs = t.Skip(1).Select(r => header.Select((h, i) => (h, v: i < r.Count ? r[i] : "")).ToDictionary(x => x.h, x => x.v));
        return JsonSerializer.Serialize(objs, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    public static string Html(List<List<string>> t, string title) =>
        $"<h3>{WebUtility.HtmlEncode(title)}</h3><table>" + string.Concat(t.Select((r, i) =>
            "<tr>" + string.Concat(r.Select(v => i == 0 ? $"<th>{WebUtility.HtmlEncode(v)}</th>" : $"<td>{WebUtility.HtmlEncode(v)}</td>")) + "</tr>")) + "</table>";

    public static string Markdown(List<List<string>> t)
    {
        if (t.Count == 0) return "";
        int width = t.Max(r => r.Count);
        string Line(List<string> r) => "| " + string.Join(" | ", Enumerable.Range(0, width).Select(i => i < r.Count ? r[i].Replace("|", "\\|").Replace("\n", " ") : "")) + " |";
        return string.Join("\n", new[] { Line(t[0]), "|" + string.Concat(Enumerable.Repeat(" --- |", width)) }.Concat(t.Skip(1).Select(Line))) + "\n";
    }

    static void WriteXlsx(List<List<string>> t, string output)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sheet1");
        for (int r = 0; r < t.Count; r++)
            for (int c = 0; c < t[r].Count; c++)
            {
                var v = t[r][c];
                var cell = ws.Cell(r + 1, c + 1);
                // Keep leading zeros (postcodes, IDs) as text.
                if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !(v.Length > 1 && v[0] == '0' && v[1] != '.')) cell.Value = d;
                else cell.Value = v;
            }
        if (t.Count > 0) { ws.Row(1).Style.Font.Bold = true; ws.Columns().AdjustToContents(); }
        wb.SaveAs(output);
    }
}

public static class Subtitles
{
    public sealed record Cue(double Start, double End, string Text);

    public static List<Cue> Read(string path)
    {
        var raw = TextFile.Read(path).Replace("\r\n", "\n");
        var cues = new List<Cue>();
        foreach (var block in raw.Split("\n\n"))
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var ti = Array.FindIndex(lines, l => l.Contains("-->"));
            if (ti < 0) continue;
            var parts = lines[ti].Split("-->");
            if (Time(parts[0]) is { } s && Time(parts[1].Trim().Split(' ')[0]) is { } e) cues.Add(new Cue(s, e, string.Join("\n", lines[(ti + 1)..])));
        }
        if (cues.Count == 0) throw new TangeloException("No valid subtitle cues were found.");
        return cues;
    }

    static double? Time(string s)
    {
        var parts = s.Trim().Replace(',', '.').Split(':');
        double total = 0;
        foreach (var p in parts) { if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null; total = total * 60 + v; }
        return total;
    }

    static string Stamp(double t, bool comma)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Round(t * 1000));
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}{(comma ? ',' : '.')}{ts.Milliseconds:000}";
    }

    public static void Write(List<Cue> cues, string output, ConversionFormat f)
    {
        var text = f switch
        {
            ConversionFormat.Srt => string.Join("\n\n", cues.Select((c, i) => $"{i + 1}\n{Stamp(c.Start, true)} --> {Stamp(c.End, true)}\n{c.Text}")) + "\n",
            ConversionFormat.Vtt => "WEBVTT\n\n" + string.Join("\n\n", cues.Select(c => $"{Stamp(c.Start, false)} --> {Stamp(c.End, false)}\n{c.Text}")) + "\n",
            _ => string.Join("\n", cues.Select(c => System.Text.RegularExpressions.Regex.Replace(c.Text, "<[^>]+>", ""))) + "\n",
        };
        File.WriteAllText(output, text);
    }
}

public static class Archives
{
    public static string Create(IReadOnlyList<string> items, ConversionFormat f)
    {
        var first = items[0];
        var baseName = items.Count == 1 ? Path.GetFileName(first.TrimEnd(Path.DirectorySeparatorChar)) : "Archive";
        var ext = f == ConversionFormat.Gzip ? "tar.gz" : f.Extension();
        var output = Output.Unique(Output.DirectoryFor(first), baseName, ext);
        var entries = new List<(string Full, string Name)>();
        foreach (var item in items)
        {
            var root = Path.GetDirectoryName(Path.GetFullPath(item.TrimEnd(Path.DirectorySeparatorChar)))!;
            if (Directory.Exists(item))
                foreach (var file in Directory.EnumerateFiles(item, "*", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(file) is ".DS_Store" or "Thumbs.db" or "desktop.ini") continue;
                    entries.Add((file, Path.GetRelativePath(root, file).Replace('\\', '/')));
                }
            else entries.Add((item, Path.GetFileName(item)));
        }
        switch (f)
        {
            case ConversionFormat.Zip:
                using (var zip = ZipFile.Open(output, ZipArchiveMode.Create))
                    foreach (var (full, name) in entries) zip.CreateEntryFromFile(full, name, CompressionLevel.Optimal);
                break;
            case ConversionFormat.Tar or ConversionFormat.Gzip:
                using (var fs = File.Create(output))
                using (Stream s = f == ConversionFormat.Gzip ? new GZipStream(fs, CompressionLevel.Optimal) : fs)
                using (var tar = new TarWriter(s, TarEntryFormat.Pax))
                    foreach (var (full, name) in entries) tar.WriteEntry(full, name);
                break;
            default: throw new TangeloException($"Creating {f.Label()} archives is not supported.");
        }
        return output;
    }

    /// Extracts zip, rar, 7z, tar, gz, tgz, bz2, xz. A single top-level folder is unwrapped.
    public static string Extract(string path)
    {
        var name = Path.GetFileName(path);
        var baseName = name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? name[..^7] : Path.GetFileNameWithoutExtension(path);
        if (baseName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) baseName = baseName[..^4];
        var dest = Output.Unique(Output.DirectoryFor(path), baseName, "");
        Directory.CreateDirectory(dest);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".gz" && !name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            using var input = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
            using var output = File.Create(Path.Combine(dest, baseName));
            input.CopyTo(output);
        }
        else if (ext == ".zip")
        {
            ZipFile.ExtractToDirectory(path, dest, overwriteFiles: true);
        }
        else if (ext is ".tar" or ".tgz" || name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            using var fs = File.OpenRead(path);
            using Stream s = ext == ".tar" ? fs : new GZipStream(fs, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(s, dest, overwriteFiles: true);
        }
        else
        {
            // rar, 7z, bz2, xz and anything else SharpCompress understands.
            using var archive = ArchiveFactory.OpenArchive(path);
            var destFull = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            {
                var target = Path.GetFullPath(Path.Combine(dest, entry.Key ?? ""));
                if (!target.StartsWith(destFull, StringComparison.Ordinal)) continue; // zip-slip guard
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.WriteToFile(target, new ExtractionOptions { Overwrite = true });
            }
        }
        var macos = Path.Combine(dest, "__MACOSX");
        if (Directory.Exists(macos)) Directory.Delete(macos, true);
        var contents = Directory.EnumerateFileSystemEntries(dest).ToList();
        if (contents.Count == 1 && Directory.Exists(contents[0]))
        {
            var target = Output.Unique(Path.GetDirectoryName(dest)!, Path.GetFileName(contents[0]), "");
            var tmp = dest + "-" + Guid.NewGuid().ToString("N")[..6];
            Directory.Move(contents[0], tmp);
            Directory.Delete(dest);
            Directory.Move(tmp, target);
            return target;
        }
        return dest;
    }
}

public static class Slides
{
    public static async Task<List<string>> Convert(string input, ConversionFormat f)
    {
        var soffice = Helpers.LibreOffice ?? throw new ToolMissingException("LibreOffice", "Presentation conversions use LibreOffice (libreoffice.org).");
        var dir = Output.TempDirectory();
        var target = f == ConversionFormat.Pptx ? "pptx" : "pdf";
        await Shell.CheckAsync(soffice, ["--headless", "--convert-to", target, "--outdir", dir, input]);
        var produced = Directory.EnumerateFiles(dir).FirstOrDefault() ?? throw new TangeloException("The converter finished without creating an output file.");
        if (f is ConversionFormat.Pdf or ConversionFormat.Pptx)
        {
            var output = Output.PathFor(input, f.Extension());
            File.Move(produced, output);
            return [output];
        }
        return Pdf.ExportPages(produced, f, nameSource: input);
    }
}
