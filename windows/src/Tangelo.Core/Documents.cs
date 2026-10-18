using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using DocumentFormat.OpenXml.Packaging;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Tangelo.Core;

/// Documents convert through HTML: every reader produces an HTML body, every writer consumes one.
public static partial class Documents
{
    const int MaxBytes = 128 << 20;

    public static async Task<string> ReadHtml(string path)
    {
        var len = new FileInfo(path).Length;
        if (len > MaxBytes) throw new TangeloException("The document exceeds the 128 MB conversion limit.");
        return Formats.From(path) switch
        {
            ConversionFormat.Md => MarkdownToHtml(TextFile.Read(path)),
            ConversionFormat.Txt => "<p>" + string.Join("</p>\n<p>", WebUtility.HtmlEncode(TextFile.Read(path)).Replace("\r\n", "\n").Split("\n\n")).Replace("\n", "<br/>") + "</p>",
            ConversionFormat.Html => TextFile.Read(path),
            ConversionFormat.Rtf => RtfPipe.Rtf.ToHtml(new RtfPipe.RtfSource(new StreamReader(path))),
            ConversionFormat.Docx => DocxToHtml(path),
            ConversionFormat.Odt => OdtToHtml(path),
            ConversionFormat.Epub => EpubToHtml(path),
            ConversionFormat.Pdf => await PdfToHtml(path),
            ConversionFormat.Doc => DocxToHtml(await LibreOfficeConvert(path, "docx")),
            _ => throw new TangeloException("This file is not a supported document."),
        };
    }

    public static async Task Write(string html, string output, ConversionFormat f, string title)
    {
        switch (f)
        {
            case ConversionFormat.Html: File.WriteAllText(output, WrapHtml(Body(html), title)); break;
            case ConversionFormat.Md: File.WriteAllText(output, HtmlToMarkdown(html)); break;
            case ConversionFormat.Txt: File.WriteAllText(output, HtmlToText(html)); break;
            case ConversionFormat.Docx: HtmlToDocx(html, output, title); break;
            case ConversionFormat.Rtf: File.WriteAllText(output, HtmlToRtf(html), Encoding.ASCII); break;
            case ConversionFormat.Odt: HtmlToOdt(html, output); break;
            case ConversionFormat.Epub: Epub.Write(Body(html), title, output); break;
            case ConversionFormat.Pdf: HtmlToPdf(html, output, title); break;
            case ConversionFormat.Doc:
                var tmp = Path.Combine(Output.TempDirectory(), Path.GetFileNameWithoutExtension(output) + ".docx");
                HtmlToDocx(html, tmp, title);
                File.Move(await LibreOfficeConvert(tmp, "doc"), output, true);
                break;
            default: throw new TangeloException($"Cannot write {f.Label()} documents.");
        }
    }

    static async Task<string> LibreOfficeConvert(string input, string ext)
    {
        var soffice = Helpers.LibreOffice ?? throw new ToolMissingException("LibreOffice", "Install it from libreoffice.org.");
        var dir = Output.TempDirectory();
        await Shell.CheckAsync(soffice, ["--headless", "--convert-to", ext, "--outdir", dir, input]);
        return Directory.EnumerateFiles(dir).FirstOrDefault() ?? throw new TangeloException("The converter finished without creating an output file.");
    }

    // MARK: HTML helpers

    static readonly HtmlParser Parser = new();

    public static string Body(string html)
    {
        var doc = Parser.ParseDocument(html);
        return doc.Body?.InnerHtml ?? html;
    }

    static string WrapHtml(string body, string title) =>
        $"<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title>" +
        "<style>body{font-family:'Segoe UI',system-ui,sans-serif;max-width:46em;margin:2em auto;line-height:1.5;padding:0 1em}table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:4px 8px}img{max-width:100%}</style>" +
        $"</head><body>\n{body}\n</body></html>\n";

    public static string MarkdownToHtml(string md) =>
        Markdig.Markdown.ToHtml(md, Markdig.MarkdownExtensions.UseAdvancedExtensions(new Markdig.MarkdownPipelineBuilder()).Build());

    public static string HtmlToMarkdown(string html)
    {
        var converter = new ReverseMarkdown.Converter(new ReverseMarkdown.Config
        {
            GithubFlavored = true,
            RemoveComments = true,
            SmartHrefHandling = true,
            UnknownTags = ReverseMarkdown.Config.UnknownTagsOption.Bypass,
        });
        return Regex.Replace(converter.Convert(Body(html)).Trim(), @"\n{3,}", "\n\n") + "\n";
    }

    public static string HtmlToText(string html)
    {
        var doc = Parser.ParseDocument(html);
        var sb = new StringBuilder();
        void Walk(INode n)
        {
            if (n is IText t) { sb.Append(Regex.Replace(t.Data, @"\s+", " ")); return; }
            if (n is not IElement e) return;
            var tag = e.LocalName;
            if (tag is "script" or "style" or "head") return;
            if (tag == "br") { sb.Append('\n'); return; }
            if (tag == "li") sb.Append("• ");
            foreach (var c in e.ChildNodes) Walk(c);
            if (tag is "td" or "th") sb.Append('\t');
            if (tag is "p" or "div" or "li" or "tr" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote" or "pre" or "hr") sb.Append('\n');
            if (tag is "p" or "h1" or "h2" or "h3" or "table" or "ul" or "ol") sb.Append('\n');
        }
        Walk(doc.Body ?? (INode)doc.DocumentElement);
        return Regex.Replace(Regex.Replace(sb.ToString(), @"[ \t]+\n", "\n"), @"\n{3,}", "\n\n").Trim() + "\n";
    }

    // MARK: DOCX

    static void HtmlToDocx(string html, string output, string title)
    {
        using var pkg = WordprocessingDocument.Create(output, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var main = pkg.AddMainDocumentPart();
        main.Document = new W.Document(new W.Body());
        var converter = new HtmlToOpenXml.HtmlConverter(main);
        converter.ParseBody(Body(html));
        pkg.PackageProperties.Title = title;
        main.Document.Save();
    }

    static string DocxToHtml(string path)
    {
        using var pkg = WordprocessingDocument.Open(path, false);
        var main = pkg.MainDocumentPart ?? throw new TangeloException("This file is not a readable DOCX document.");
        var body = main.Document?.Body ?? throw new TangeloException("This file is not a readable DOCX document.");
        var styles = main.StyleDefinitionsPart?.Styles?.Elements<W.Style>().ToDictionary(s => s.StyleId?.Value ?? "", s => s.StyleName?.Val?.Value ?? "") ?? [];
        var sb = new StringBuilder();
        string? openList = null;

        void CloseList() { if (openList != null) { sb.Append($"</{openList}>\n"); openList = null; } }

        string Runs(DocumentFormat.OpenXml.OpenXmlElement container)
        {
            var r = new StringBuilder();
            foreach (var child in container.ChildElements)
            {
                if (child is W.Hyperlink link)
                {
                    var href = link.Id?.Value is { } id ? main.HyperlinkRelationships.FirstOrDefault(h => h.Id == id)?.Uri.ToString() : null;
                    var inner = Runs(link);
                    r.Append(href != null ? $"<a href=\"{WebUtility.HtmlEncode(href)}\">{inner}</a>" : inner);
                }
                else if (child is W.Run run)
                {
                    var text = new StringBuilder();
                    foreach (var part in run.ChildElements)
                    {
                        if (part is W.Text t) text.Append(WebUtility.HtmlEncode(t.Text));
                        else if (part is W.TabChar) text.Append('\t');
                        else if (part is W.Break) text.Append("<br/>");
                        else if (part is W.Drawing d && d.Descendants<DocumentFormat.OpenXml.Drawing.Blip>().FirstOrDefault()?.Embed?.Value is { } rid
                                 && main.GetPartById(rid) is ImagePart img)
                        {
                            using var s = img.GetStream();
                            using var ms = new MemoryStream(); s.CopyTo(ms);
                            text.Append($"<img src=\"data:{img.ContentType};base64,{Convert.ToBase64String(ms.ToArray())}\"/>");
                        }
                    }
                    var str = text.ToString();
                    var props = run.RunProperties;
                    if (props?.Bold != null && props.Bold.Val?.Value != false) str = $"<b>{str}</b>";
                    if (props?.Italic != null && props.Italic.Val?.Value != false) str = $"<i>{str}</i>";
                    if (props?.Underline != null) str = $"<u>{str}</u>";
                    if (props?.Strike != null) str = $"<s>{str}</s>";
                    r.Append(str);
                }
                else if (child is W.SimpleField or W.SdtRun or W.SdtContentRun) r.Append(Runs(child));
            }
            return r.ToString();
        }

        foreach (var el in body.ChildElements)
        {
            if (el is W.Paragraph p)
            {
                var styleId = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
                var styleName = styles.GetValueOrDefault(styleId, styleId).ToLowerInvariant();
                var content = Runs(p);
                var numbered = p.ParagraphProperties?.NumberingProperties != null || styleName.Contains("list");
                if (numbered)
                {
                    var kind = styleName.Contains("number") ? "ol" : "ul";
                    if (openList != kind) { CloseList(); sb.Append($"<{kind}>\n"); openList = kind; }
                    sb.Append($"<li>{content}</li>\n");
                    continue;
                }
                CloseList();
                var m = Regex.Match(styleName, @"heading\s*(\d)");
                if (m.Success) sb.Append($"<h{m.Groups[1].Value}>{content}</h{m.Groups[1].Value}>\n");
                else if (styleName == "title") sb.Append($"<h1>{content}</h1>\n");
                else if (styleName.Contains("quote")) sb.Append($"<blockquote>{content}</blockquote>\n");
                else if (content.Length > 0) sb.Append($"<p>{content}</p>\n");
            }
            else if (el is W.Table table)
            {
                CloseList();
                sb.Append("<table>\n");
                foreach (var row in table.Elements<W.TableRow>())
                {
                    sb.Append("<tr>");
                    foreach (var cell in row.Elements<W.TableCell>())
                    {
                        var span = cell.TableCellProperties?.GridSpan?.Val?.Value is int g && g > 1 ? $" colspan=\"{g}\"" : "";
                        sb.Append($"<td{span}>{string.Join("<br/>", cell.Elements<W.Paragraph>().Select(Runs))}</td>");
                    }
                    sb.Append("</tr>\n");
                }
                sb.Append("</table>\n");
            }
        }
        CloseList();
        return sb.ToString();
    }

    // MARK: ODT

    static readonly XNamespace OText = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    static readonly XNamespace OTable = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    static readonly XNamespace OOffice = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    static readonly XNamespace OStyle = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    static readonly XNamespace OFo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";

    static string OdtToHtml(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("content.xml") ?? throw new TangeloException("The document is not a valid ODT file.");
        using var s = entry.Open();
        var xml = XDocument.Load(s);
        var bold = new HashSet<string>(); var italic = new HashSet<string>();
        foreach (var st in xml.Descendants(OStyle + "style"))
        {
            var name = (string?)st.Attribute(OStyle + "name") ?? "";
            var tp = st.Element(OStyle + "text-properties");
            if ((string?)tp?.Attribute(OFo + "font-weight") == "bold") bold.Add(name);
            if ((string?)tp?.Attribute(OFo + "font-style") == "italic") italic.Add(name);
        }
        string Inline(XElement e)
        {
            var sb = new StringBuilder();
            foreach (var n in e.Nodes())
            {
                if (n is XText t) sb.Append(WebUtility.HtmlEncode(t.Value));
                else if (n is XElement c)
                {
                    if (c.Name == OText + "span")
                    {
                        var st = (string?)c.Attribute(OText + "style-name") ?? "";
                        var inner = Inline(c);
                        if (bold.Contains(st)) inner = $"<b>{inner}</b>";
                        if (italic.Contains(st)) inner = $"<i>{inner}</i>";
                        sb.Append(inner);
                    }
                    else if (c.Name == OText + "a") sb.Append($"<a href=\"{WebUtility.HtmlEncode((string?)c.Attribute(XNamespace.Get("http://www.w3.org/1999/xlink") + "href") ?? "")}\">{Inline(c)}</a>");
                    else if (c.Name == OText + "line-break") sb.Append("<br/>");
                    else if (c.Name == OText + "tab") sb.Append('\t');
                    else if (c.Name == OText + "s") sb.Append(' ');
                    else sb.Append(Inline(c));
                }
            }
            return sb.ToString();
        }
        string Block(XElement e) => e.Name.LocalName switch
        {
            "h" => $"<h{(string?)e.Attribute(OText + "outline-level") ?? "1"}>{Inline(e)}</h{(string?)e.Attribute(OText + "outline-level") ?? "1"}>\n",
            "p" => $"<p>{Inline(e)}</p>\n",
            "list" => "<ul>\n" + string.Concat(e.Elements(OText + "list-item").Select(li => $"<li>{string.Concat(li.Elements().Select(x => x.Name.LocalName == "p" ? Inline(x) : Block(x)))}</li>\n")) + "</ul>\n",
            "table" => "<table>\n" + string.Concat(e.Descendants(OTable + "table-row").Select(r => "<tr>" + string.Concat(r.Elements(OTable + "table-cell").Select(c => $"<td>{string.Join("<br/>", c.Elements(OText + "p").Select(Inline))}</td>")) + "</tr>\n")) + "</table>\n",
            "section" => string.Concat(e.Elements().Select(Block)),
            _ => "",
        };
        var text = xml.Descendants(OOffice + "text").FirstOrDefault() ?? throw new TangeloException("The document has no text.");
        return string.Concat(text.Elements().Select(Block));
    }

    static void HtmlToOdt(string html, string output)
    {
        var doc = Parser.ParseDocument(html);
        var body = new StringBuilder();
        string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? "";
        string Inline(INode n) => n switch
        {
            IText t => Esc(t.Data),
            IElement e when e.LocalName is "b" or "strong" => $"<text:span text:style-name=\"B\">{string.Concat(e.ChildNodes.Select(Inline))}</text:span>",
            IElement e when e.LocalName is "i" or "em" => $"<text:span text:style-name=\"I\">{string.Concat(e.ChildNodes.Select(Inline))}</text:span>",
            IElement e when e.LocalName == "a" => $"<text:a xlink:href=\"{Esc(e.GetAttribute("href") ?? "")}\">{string.Concat(e.ChildNodes.Select(Inline))}</text:a>",
            IElement e when e.LocalName == "br" => "<text:line-break/>",
            IElement e => string.Concat(e.ChildNodes.Select(Inline)),
            _ => "",
        };
        void Block(IElement e)
        {
            switch (e.LocalName)
            {
                case var h when Regex.IsMatch(h, "^h[1-6]$"):
                    body.Append($"<text:h text:outline-level=\"{h[1]}\" text:style-name=\"H{h[1]}\">{Inline(e)}</text:h>"); break;
                case "p" or "blockquote" or "pre":
                    body.Append($"<text:p>{Inline(e)}</text:p>"); break;
                case "ul" or "ol":
                    body.Append("<text:list>");
                    foreach (var li in e.Children.Where(c => c.LocalName == "li")) body.Append($"<text:list-item><text:p>{Inline(li)}</text:p></text:list-item>");
                    body.Append("</text:list>"); break;
                case "table":
                    var rows = e.QuerySelectorAll("tr").ToList();
                    var cols = rows.Count == 0 ? 1 : rows.Max(r => r.Children.Count(c => c.LocalName is "td" or "th"));
                    body.Append($"<table:table><table:table-column table:number-columns-repeated=\"{cols}\"/>");
                    foreach (var r in rows)
                        body.Append("<table:table-row>" + string.Concat(r.Children.Where(c => c.LocalName is "td" or "th").Select(c => $"<table:table-cell><text:p>{Inline(c)}</text:p></table:table-cell>")) + "</table:table-row>");
                    body.Append("</table:table>"); break;
                case "hr": break;
                default:
                    if (e.Children.Any(c => c.LocalName is "p" or "ul" or "ol" or "table" or "div" or "h1" or "h2" or "h3")) foreach (var c in e.Children) Block(c);
                    else if (e.TextContent.Trim().Length > 0) body.Append($"<text:p>{Inline(e)}</text:p>");
                    break;
            }
        }
        foreach (var c in (doc.Body?.Children ?? Enumerable.Empty<IElement>())) Block(c);

        var content = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" office:version=\"1.3\">" +
            "<office:automatic-styles><style:style style:name=\"B\" style:family=\"text\"><style:text-properties fo:font-weight=\"bold\"/></style:style><style:style style:name=\"I\" style:family=\"text\"><style:text-properties fo:font-style=\"italic\"/></style:style>" +
            string.Concat(Enumerable.Range(1, 6).Select(i => $"<style:style style:name=\"H{i}\" style:family=\"paragraph\"><style:text-properties fo:font-size=\"{24 - i * 2}pt\" fo:font-weight=\"bold\"/></style:style>")) +
            $"</office:automatic-styles><office:body><office:text>{body}</office:text></office:body></office:document-content>";
        if (File.Exists(output)) File.Delete(output);
        using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
        var mime = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var w = new StreamWriter(mime.Open())) w.Write("application/vnd.oasis.opendocument.text");
        void Put(string name, string text) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(text); }
        Put("content.xml", content);
        Put("META-INF/manifest.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.3\"><manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"application/vnd.oasis.opendocument.text\"/><manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/></manifest:manifest>");
    }

    // MARK: RTF

    static string HtmlToRtf(string html)
    {
        var doc = Parser.ParseDocument(html);
        var sb = new StringBuilder(@"{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}{\f1 Consolas;}}\fs22 ");
        string Esc(string s)
        {
            var o = new StringBuilder();
            foreach (var ch in s)
            {
                if (ch is '\\' or '{' or '}') o.Append('\\').Append(ch);
                else if (ch == '\n') o.Append(' ');
                else if (ch > 127) o.Append($"\\u{(short)ch}?");
                else o.Append(ch);
            }
            return o.ToString();
        }
        void Inline(INode n)
        {
            switch (n)
            {
                case IText t: sb.Append(Esc(Regex.Replace(t.Data, @"\s+", " "))); break;
                case IElement e when e.LocalName is "b" or "strong": sb.Append(@"{\b "); foreach (var c in e.ChildNodes) Inline(c); sb.Append('}'); break;
                case IElement e when e.LocalName is "i" or "em": sb.Append(@"{\i "); foreach (var c in e.ChildNodes) Inline(c); sb.Append('}'); break;
                case IElement e when e.LocalName == "u": sb.Append(@"{\ul "); foreach (var c in e.ChildNodes) Inline(c); sb.Append('}'); break;
                case IElement e when e.LocalName is "s" or "del" or "strike": sb.Append(@"{\strike "); foreach (var c in e.ChildNodes) Inline(c); sb.Append('}'); break;
                case IElement e when e.LocalName == "code": sb.Append(@"{\f1 "); foreach (var c in e.ChildNodes) Inline(c); sb.Append('}'); break;
                case IElement e when e.LocalName == "a":
                    sb.Append($@"{{\field{{\*\fldinst{{HYPERLINK ""{Esc(e.GetAttribute("href") ?? "")}""}}}}{{\fldrslt{{\ul ");
                    foreach (var c in e.ChildNodes) Inline(c);
                    sb.Append("}}}"); break;
                case IElement e when e.LocalName == "br": sb.Append(@"\line "); break;
                case IElement e: foreach (var c in e.ChildNodes) Inline(c); break;
            }
        }
        void Block(IElement e)
        {
            switch (e.LocalName)
            {
                case var h when Regex.IsMatch(h, "^h[1-6]$"):
                    sb.Append($@"{{\pard\sb240\sa120\b\fs{44 - (h[1] - '0') * 4} "); Inline(e); sb.Append(@"\par}"); break;
                case "p" or "div" when !e.Children.Any(c => c.LocalName is "p" or "div" or "table" or "ul" or "ol"):
                    sb.Append(@"{\pard\sa160 "); Inline(e); sb.Append(@"\par}"); break;
                case "blockquote": sb.Append(@"{\pard\li720\i\sa160 "); Inline(e); sb.Append(@"\par}"); break;
                case "pre": sb.Append(@"{\pard\f1\fs20\sa160 "); sb.Append(Esc(e.TextContent).Replace("\r", "")); sb.Append(@"\par}"); break;
                case "ul" or "ol":
                    int i = 1;
                    foreach (var li in e.Children.Where(c => c.LocalName == "li"))
                    {
                        sb.Append($@"{{\pard\fi-360\li720\sa60 {(e.LocalName == "ol" ? $"{i++}." : "\\bullet")}\tab ");
                        Inline(li); sb.Append(@"\par}");
                    }
                    break;
                case "table":
                    foreach (var r in e.QuerySelectorAll("tr"))
                    {
                        var cells = r.Children.Where(c => c.LocalName is "td" or "th").ToList();
                        sb.Append(@"\trowd\trgaph108");
                        for (int c = 1; c <= cells.Count; c++) sb.Append($@"\clbrdrt\brdrs\clbrdrl\brdrs\clbrdrb\brdrs\clbrdrr\brdrs\cellx{c * 2400}");
                        foreach (var c in cells) { sb.Append(@"\pard\intbl "); Inline(c); sb.Append(@"\cell"); }
                        sb.Append(@"\row ");
                    }
                    sb.Append(@"\pard\par "); break;
                case "hr": sb.Append(@"{\pard\brdrb\brdrs\brdrw10\brsp20 \par}"); break;
                default: foreach (var c in e.Children) Block(c); if (!e.Children.Any() && e.TextContent.Trim().Length > 0) { sb.Append(@"{\pard\sa160 "); Inline(e); sb.Append(@"\par}"); } break;
            }
        }
        foreach (var c in (doc.Body?.Children ?? Enumerable.Empty<IElement>())) Block(c);
        return sb.Append('}').ToString();
    }

    // MARK: PDF (read)

    static async Task<string> PdfToHtml(string path)
    {
        var pages = Pdf.PageTexts(path);
        var sb = new StringBuilder();
        for (int i = 0; i < pages.Count; i++)
        {
            var text = pages[i];
            if (text.Trim().Length < 4 && Platform.Ocr != null)
            {
                using var img = Pdf.RenderPages(path, 200, i).First();
                text = string.Join("\n", await Platform.Ocr(img));
            }
            foreach (var para in Regex.Split(text.Replace("\r", ""), @"\n\s*\n"))
                if (para.Trim().Length > 0) sb.Append($"<p>{WebUtility.HtmlEncode(para.Trim()).Replace("\n", "<br/>")}</p>\n");
            if (i < pages.Count - 1) sb.Append("<hr/>\n");
        }
        if (sb.Length == 0) throw new TangeloException("The PDF contains no text that can be exported.");
        return sb.ToString();
    }

    // MARK: PDF (write) via MigraDoc

    static void HtmlToPdf(string html, string output, string title)
    {
        Fonts.Install();
        var doc = new MigraDoc.DocumentObjectModel.Document();
        doc.Info.Title = title;
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = "Arial";
        normal.Font.Size = 11;
        normal.ParagraphFormat.SpaceAfter = 6;
        for (int i = 1; i <= 6; i++)
        {
            var h = doc.Styles[$"Heading{i}"]!;
            h.Font.Size = 22 - i * 2;
            h.Font.Bold = true;
            h.ParagraphFormat.SpaceBefore = 10;
            h.ParagraphFormat.SpaceAfter = 6;
        }
        var section = doc.AddSection();
        section.PageSetup = doc.DefaultPageSetup.Clone();
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(2.2);
        var dom = Parser.ParseDocument(html);

        void Inline(INode n, Paragraph p, TextFormat fmt = 0, string? link = null)
        {
            switch (n)
            {
                case IText t:
                    var text = Regex.Replace(t.Data, @"\s+", " ");
                    if (text.Length == 0) return;
                    if (link != null)
                    {
                        var h = p.AddHyperlink(link, HyperlinkType.Web);
                        h.AddFormattedText(text, fmt | TextFormat.Underline).Color = Colors.Blue;
                    }
                    else p.AddFormattedText(text, fmt);
                    break;
                case IElement e:
                    var f = fmt | e.LocalName switch
                    {
                        "b" or "strong" or "th" => TextFormat.Bold,
                        "i" or "em" => TextFormat.Italic,
                        "u" => TextFormat.Underline,
                        _ => 0,
                    };
                    if (e.LocalName == "br") { p.AddLineBreak(); return; }
                    if (e.LocalName == "img") { AddImage(e, p); return; }
                    var l = e.LocalName == "a" ? e.GetAttribute("href") : link;
                    foreach (var c in e.ChildNodes) Inline(c, p, f, l);
                    break;
            }
        }

        void AddImage(IElement e, Paragraph p)
        {
            var src = e.GetAttribute("src") ?? "";
            try
            {
                string? file = null;
                if (src.StartsWith("data:") && src.Contains(";base64,"))
                {
                    file = Path.Combine(Output.TempDirectory(), "img.png");
                    using var img = new ImageMagick.MagickImage(Convert.FromBase64String(src[(src.IndexOf(',') + 1)..]));
                    img.Write(file, ImageMagick.MagickFormat.Png);
                }
                else if (File.Exists(src)) file = src;
                if (file == null) return;
                var image = p.AddImage(file);
                image.LockAspectRatio = true;
                image.Width = Unit.FromCentimeter(15);
            }
            catch { }
        }

        void Block(IElement e, Section s)
        {
            switch (e.LocalName)
            {
                case var h when Regex.IsMatch(h, "^h[1-6]$"):
                    var hp = s.AddParagraph(); hp.Style = $"Heading{h[1]}"; Inline(e, hp); break;
                case "p" or "div" when !e.Children.Any(c => c.LocalName is "p" or "div" or "table" or "ul" or "ol" or "h1" or "h2"):
                    if (e.TextContent.Trim().Length > 0 || e.QuerySelector("img") != null) Inline(e, s.AddParagraph()); break;
                case "blockquote":
                    var q = s.AddParagraph(); q.Format.LeftIndent = Unit.FromCentimeter(1); Inline(e, q, TextFormat.Italic); break;
                case "pre":
                    var pre = s.AddParagraph(); pre.Format.Font.Name = "Courier New"; pre.Format.Font.Size = 9; pre.AddText(e.TextContent); break;
                case "ul" or "ol":
                    int i = 1;
                    foreach (var li in e.Children.Where(c => c.LocalName == "li"))
                    {
                        var lp = s.AddParagraph();
                        lp.Format.LeftIndent = Unit.FromCentimeter(0.8);
                        lp.Format.FirstLineIndent = Unit.FromCentimeter(-0.5);
                        lp.AddText(e.LocalName == "ol" ? $"{i++}.  " : "•  ");
                        Inline(li, lp);
                    }
                    break;
                case "table":
                    var rows = e.QuerySelectorAll("tr").ToList();
                    var cols = rows.Count == 0 ? 0 : rows.Max(r => r.Children.Count(c => c.LocalName is "td" or "th"));
                    if (cols == 0) break;
                    var table = s.AddTable();
                    table.Borders.Width = 0.5;
                    table.Borders.Color = Colors.Gray;
                    var width = (s.PageSetup.PageWidth - s.PageSetup.LeftMargin - s.PageSetup.RightMargin) / cols;
                    for (int c = 0; c < cols; c++) table.AddColumn(width);
                    foreach (var r in rows)
                    {
                        var row = table.AddRow();
                        int c = 0;
                        foreach (var cell in r.Children.Where(x => x.LocalName is "td" or "th"))
                        {
                            if (c >= cols) break;
                            var cp = row.Cells[c++].AddParagraph();
                            Inline(cell, cp, cell.LocalName == "th" ? TextFormat.Bold : 0);
                        }
                    }
                    s.AddParagraph();
                    break;
                case "hr":
                    var hr = s.AddParagraph(); hr.Format.Borders.Bottom.Width = 0.5; hr.Format.Borders.Bottom.Color = Colors.LightGray; break;
                case "img": AddImage(e, s.AddParagraph()); break;
                default:
                    if (e.Children.Length > 0) foreach (var c in e.Children) Block(c, s);
                    else if (e.TextContent.Trim().Length > 0) Inline(e, s.AddParagraph());
                    break;
            }
        }
        var root = dom.Body;
        if (root != null)
        {
            // Loose text directly in <body> (e.g. from plain HTML) becomes a paragraph.
            foreach (var node in root.ChildNodes)
            {
                if (node is IElement el) Block(el, section);
                else if (node is IText t && t.Data.Trim().Length > 0) section.AddParagraph(t.Data.Trim());
            }
        }
        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(output);
    }

    // MARK: EPUB (read)

    static string EpubToHtml(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        string ReadEntry(string name)
        {
            var e = zip.GetEntry(name) ?? zip.Entries.FirstOrDefault(x => x.FullName.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new TangeloException($"The EPUB is missing {name}.");
            using var r = new StreamReader(e.Open());
            return r.ReadToEnd();
        }
        var container = ReadEntry("META-INF/container.xml");
        var opfPath = Regex.Match(container, "full-path=\"([^\"]+)\"").Groups[1].Value;
        if (opfPath.Length == 0) throw new TangeloException("The EPUB does not identify a readable package document.");
        var opf = XDocument.Parse(ReadEntry(opfPath));
        XNamespace ns = "http://www.idpf.org/2007/opf";
        var manifest = opf.Descendants(ns + "item").ToDictionary(i => (string)i.Attribute("id")!, i => (string)i.Attribute("href")!);
        var baseDir = Path.GetDirectoryName(opfPath)?.Replace('\\', '/') ?? "";
        var sb = new StringBuilder();
        foreach (var itemref in opf.Descendants(ns + "itemref"))
        {
            if (!manifest.TryGetValue((string)itemref.Attribute("idref")!, out var href)) continue;
            var full = (baseDir.Length > 0 ? baseDir + "/" : "") + Uri.UnescapeDataString(href);
            try { sb.Append(Body(ReadEntry(full))).Append("\n<hr/>\n"); } catch (TangeloException) { }
        }
        if (sb.Length == 0) throw new TangeloException("The EPUB has no readable chapters.");
        return sb.ToString();
    }
}

public static class Epub
{
    public static void Write(string bodyHtml, string title, string output)
    {
        var safe = System.Security.SecurityElement.Escape(title);
        // XHTML needs well-formed markup; AngleSharp serializes void elements properly in XHTML mode.
        var doc = new HtmlParser().ParseDocument($"<body>{bodyHtml}</body>");
        var xhtmlBody = string.Concat(doc.Body!.ChildNodes.Select(n => AngleSharp.FormatExtensions.ToHtml(n, AngleSharp.Xhtml.XhtmlMarkupFormatter.Instance)));
        if (File.Exists(output)) File.Delete(output);
        using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
        var mime = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var w = new StreamWriter(mime.Open(), new UTF8Encoding(false))) w.Write("application/epub+zip");
        void Put(string name, string text) { using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); w.Write(text); }
        Put("META-INF/container.xml", "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
        Put("OEBPS/chapter1.xhtml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><!DOCTYPE html><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>{safe}</title></head><body>{xhtmlBody}</body></html>");
        Put("OEBPS/nav.xhtml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><head><title>Contents</title></head><body><nav epub:type=\"toc\"><ol><li><a href=\"chapter1.xhtml\">{safe}</a></li></ol></nav></body></html>");
        Put("OEBPS/content.opf", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"book-id\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"book-id\">urn:uuid:{Guid.NewGuid()}</dc:identifier><dc:title>{safe}</dc:title><dc:language>en</dc:language><meta property=\"dcterms:modified\">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss}Z</meta></metadata><manifest><item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/><item id=\"c1\" href=\"chapter1.xhtml\" media-type=\"application/xhtml+xml\"/></manifest><spine><itemref idref=\"c1\"/></spine></package>");
    }
}

public static class TextFile
{
    /// Reads text, detecting UTF-8/UTF-16 BOMs and falling back to Windows-1252/Latin-1.
    public static string Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }
}

/// Platform-specific services (OCR, face detection) registered by the Windows host.
public static class Platform
{
    public static Func<ImageMagick.MagickImage, Task<IReadOnlyList<string>>>? Ocr { get; set; }
    /// Returns normalized rectangles of recognized text lines.
    public static Func<ImageMagick.MagickImage, Task<IReadOnlyList<RectF>>>? TextRegions { get; set; }
    public static Func<ImageMagick.MagickImage, Task<IReadOnlyList<RectF>>>? Faces { get; set; }
}
