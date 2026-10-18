import AppKit
import PDFKit
import Vision

enum DocumentKit {
    // MARK: Reading

    static func read(_ url: URL) async throws -> NSAttributedString {
        guard let format = ConversionFormat.from(url) else { throw TangeloError.unsupported("This file type is not supported.") }
        switch format {
        case .docx: return try attributed(url, type: .officeOpenXML)
        case .doc: return try attributed(url, type: .docFormat)
        case .odt: return try attributed(url, type: .openDocument)
        case .rtf: return try attributed(url, type: .rtf)
        case .rtfd: return try NSAttributedString(url: url, options: [.documentType: NSAttributedString.DocumentType.rtfd], documentAttributes: nil)
        case .html: return try attributed(url, type: .html)
        case .txt: return NSAttributedString(string: try readText(url), attributes: bodyAttributes)
        case .md: return try html(Markdown.toHTML(try String(contentsOf: url, encoding: .utf8)))
        case .textClipping: return try readClipping(url)
        case .epub: return try html(try await EPUB.readHTML(url))
        case .pdf: return try await readPDF(url)
        case .pages: return try await readPages(url)
        default: throw TangeloError.unsupported("\(format.label) is not a text document.")
        }
    }

    static var bodyAttributes: [NSAttributedString.Key: Any] {
        [.font: NSFont.systemFont(ofSize: 12), .foregroundColor: NSColor.black]
    }

    private static func attributed(_ url: URL, type: NSAttributedString.DocumentType) throws -> NSAttributedString {
        let data = try Data(contentsOf: url)
        guard data.count < 128 << 20 else { throw TangeloError.message("The document exceeds the 128 MB conversion limit.") }
        return try NSAttributedString(data: data, options: [.documentType: type, .characterEncoding: String.Encoding.utf8.rawValue],
                                      documentAttributes: nil)
    }

    static func html(_ html: String) throws -> NSAttributedString {
        let styled = "<html><head><meta charset=\"utf-8\"><style>body{font-family:-apple-system,Helvetica;font-size:12pt}</style></head><body>\(html)</body></html>"
        return try NSAttributedString(data: Data(styled.utf8), options: [.documentType: NSAttributedString.DocumentType.html,
                                                                       .characterEncoding: String.Encoding.utf8.rawValue],
                                      documentAttributes: nil)
    }

    private static func readClipping(_ url: URL) throws -> NSAttributedString {
        let data = try Data(contentsOf: url)
        guard let plist = try PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any],
              let utis = plist["UTI-Data"] as? [String: Any] else {
            throw TangeloError.message("This text clipping has an unsupported or malformed format.")
        }
        if let rtf = utis["public.rtf"] as? Data, let s = try? NSAttributedString(data: rtf, options: [.documentType: NSAttributedString.DocumentType.rtf], documentAttributes: nil) { return s }
        if let text = (utis["public.utf8-plain-text"] as? String) ?? (utis["public.utf16-plain-text"] as? String) {
            return NSAttributedString(string: text, attributes: bodyAttributes)
        }
        throw TangeloError.message("The clipping contains no supported text.")
    }

    /// Extracts text page by page; pages without a text layer are OCR'd with Vision.
    static func readPDF(_ url: URL) async throws -> NSAttributedString {
        guard let doc = PDFDocument(url: url), doc.pageCount > 0 else {
            throw TangeloError.message("The PDF cannot be read, is empty, or is password-protected.")
        }
        let out = NSMutableAttributedString()
        for i in 0..<doc.pageCount {
            guard let page = doc.page(at: i) else { continue }
            if let s = page.attributedString, s.string.trimmingCharacters(in: .whitespacesAndNewlines).count > 3 {
                out.append(s)
            } else {
                let img = PDFKitTools.render(page: page, scale: 2)
                let text = try await OCR.recognize(img).joined(separator: "\n")
                out.append(NSAttributedString(string: text, attributes: bodyAttributes))
            }
            if i < doc.pageCount - 1 { out.append(NSAttributedString(string: "\n\u{000C}\n")) }
        }
        return out
    }

    private static func readPages(_ url: URL) async throws -> NSAttributedString {
        if FileManager.default.fileExists(atPath: "/Applications/Pages.app") {
            let tmp = try Output.tempDirectory().appendingPathComponent("doc.docx")
            try await IWork.export(app: "Pages", input: url, output: tmp, as: "Microsoft Word")
            return try attributed(tmp, type: .officeOpenXML)
        }
        // Fallback: Pages files embed a preview PDF.
        let tmp = try Output.tempDirectory()
        try await Shell.check("/usr/bin/ditto", ["-x", "-k", url.path, tmp.path])
        for name in ["QuickLook/Preview.pdf", "preview.pdf"] {
            let p = tmp.appendingPathComponent(name)
            if FileManager.default.fileExists(atPath: p.path) { return try await readPDF(p) }
        }
        throw TangeloError.message("Install Pages to convert this Pages document.")
    }

    // MARK: Writing

    static func write(_ text: NSAttributedString, to url: URL, format: ConversionFormat, title: String) async throws {
        let range = NSRange(location: 0, length: text.length)
        func data(_ type: NSAttributedString.DocumentType) throws -> Data {
            try text.data(from: range, documentAttributes: [.documentType: type, .title: title])
        }
        switch format {
        case .docx: try data(.officeOpenXML).write(to: url)
        case .doc: try data(.docFormat).write(to: url)
        case .odt: try data(.openDocument).write(to: url)
        case .rtf: try data(.rtf).write(to: url)
        case .html: try data(.html).write(to: url)
        case .txt: try Data(text.string.utf8).write(to: url)
        case .md: try Data(Markdown.fromAttributed(text).utf8).write(to: url)
        case .rtfd:
            let wrapper = try text.fileWrapper(from: range, documentAttributes: [.documentType: NSAttributedString.DocumentType.rtfd])
            try wrapper.write(to: url, options: .atomic, originalContentsURL: nil)
        case .pdf: try PDFKitTools.renderText(text, to: url)
        case .epub:
            let html = String(decoding: try data(.html), as: UTF8.self)
            try await EPUB.write(html: html, title: title, to: url)
        case .pages:
            let tmp = try Output.tempDirectory().appendingPathComponent("doc.docx")
            try data(.officeOpenXML).write(to: tmp)
            try await IWork.convertToNative(app: "Pages", input: tmp, output: url)
        default:
            throw TangeloError.unsupported("Cannot write \(format.label) documents.")
        }
    }
}

// MARK: - OCR

enum OCR {
    static func recognize(_ image: CGImage) async throws -> [String] {
        try await withCheckedThrowingContinuation { cont in
            let request = VNRecognizeTextRequest { req, err in
                if let err { cont.resume(throwing: err); return }
                let lines = (req.results as? [VNRecognizedTextObservation])?.compactMap { $0.topCandidates(1).first?.string } ?? []
                cont.resume(returning: lines)
            }
            request.recognitionLevel = .accurate
            request.usesLanguageCorrection = true
            request.automaticallyDetectsLanguage = true
            do { try VNImageRequestHandler(cgImage: image).perform([request]) } catch { cont.resume(throwing: error) }
        }
    }
}

// MARK: - iWork automation (Pages / Numbers / Keynote)

enum IWork {
    static func installed(_ app: String) -> Bool { FileManager.default.fileExists(atPath: "/Applications/\(app).app") }

    /// Exports an iWork document (or anything the app can open) to another format.
    static func export(app: String, input: URL, output: URL, as format: String) async throws {
        guard installed(app) else { throw TangeloError.message("\(app) must be installed for this conversion.") }
        let script = """
        tell application "\(app)"
            set theDoc to open POSIX file "\(input.path)"
            export theDoc to POSIX file "\(output.path)" as \(format)
            close theDoc saving no
        end tell
        """
        try await Shell.check("/usr/bin/osascript", ["-e", script])
    }

    static func convertToNative(app: String, input: URL, output: URL) async throws {
        guard installed(app) else { throw TangeloError.message("\(app) must be installed to create \(app) files.") }
        let script = """
        tell application "\(app)"
            set theDoc to open POSIX file "\(input.path)"
            save theDoc in POSIX file "\(output.path)"
            close theDoc saving no
        end tell
        """
        try await Shell.check("/usr/bin/osascript", ["-e", script])
    }
}

// MARK: - EPUB

enum EPUB {
    static func readHTML(_ url: URL) async throws -> String {
        let dir = try Output.tempDirectory()
        try await Shell.check("/usr/bin/ditto", ["-x", "-k", url.path, dir.path])
        let container = try String(contentsOf: dir.appendingPathComponent("META-INF/container.xml"), encoding: .utf8)
        guard let opfPath = container.firstMatch(of: /full-path="([^"]+)"/)?.1 else {
            throw TangeloError.message("The EPUB does not identify a readable package document.")
        }
        let opfURL = dir.appendingPathComponent(String(opfPath))
        let opf = try String(contentsOf: opfURL, encoding: .utf8)
        var manifest: [String: String] = [:]
        for m in opf.matches(of: /<item\b[^>]*>/) {
            let tag = String(m.0)
            if let id = tag.firstMatch(of: /id="([^"]+)"/)?.1, let href = tag.firstMatch(of: /href="([^"]+)"/)?.1 {
                manifest[String(id)] = String(href)
            }
        }
        let base = opfURL.deletingLastPathComponent()
        var body = ""
        for m in opf.matches(of: /<itemref\b[^>]*idref="([^"]+)"/) {
            guard let href = manifest[String(m.1)]?.removingPercentEncoding,
                  let xhtml = try? String(contentsOf: base.appendingPathComponent(href), encoding: .utf8) else { continue }
            if let b = xhtml.firstMatch(of: /(?s)<body[^>]*>(.*)<\/body>/)?.1 { body += String(b) + "<hr/>" }
        }
        guard !body.isEmpty else { throw TangeloError.message("The EPUB has no readable chapters.") }
        return body
    }

    static func write(html: String, title: String, to url: URL) async throws {
        let dir = try Output.tempDirectory()
        let fm = FileManager.default
        try fm.createDirectory(at: dir.appendingPathComponent("META-INF"), withIntermediateDirectories: true)
        try fm.createDirectory(at: dir.appendingPathComponent("OEBPS"), withIntermediateDirectories: true)
        try "application/epub+zip".write(to: dir.appendingPathComponent("mimetype"), atomically: true, encoding: .ascii)
        try """
        <?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
        """.write(to: dir.appendingPathComponent("META-INF/container.xml"), atomically: true, encoding: .utf8)
        let safeTitle = title.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;")
        var body = html.firstMatch(of: /(?s)<body[^>]*>(.*)<\/body>/).map { String($0.1) } ?? html
        body = body.replacingOccurrences(of: "<br>", with: "<br/>")
        try """
        <?xml version="1.0" encoding="UTF-8"?><!DOCTYPE html><html xmlns="http://www.w3.org/1999/xhtml"><head><title>\(safeTitle)</title></head><body>\(body)</body></html>
        """.write(to: dir.appendingPathComponent("OEBPS/chapter1.xhtml"), atomically: true, encoding: .utf8)
        try """
        <?xml version="1.0" encoding="UTF-8"?><html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="chapter1.xhtml">\(safeTitle)</a></li></ol></nav></body></html>
        """.write(to: dir.appendingPathComponent("OEBPS/nav.xhtml"), atomically: true, encoding: .utf8)
        try """
        <?xml version="1.0" encoding="UTF-8"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="book-id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="book-id">urn:uuid:\(UUID().uuidString)</dc:identifier><dc:title>\(safeTitle)</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">\(ISO8601DateFormatter().string(from: Date()).prefix(19))Z</meta></metadata><manifest><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/><item id="c1" href="chapter1.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="c1"/></spine></package>
        """.write(to: dir.appendingPathComponent("OEBPS/content.opf"), atomically: true, encoding: .utf8)
        try? fm.removeItem(at: url)
        // mimetype must be first and stored uncompressed.
        try await Shell.check("/usr/bin/zip", ["-X0q", url.path, "mimetype"], currentDirectory: dir)
        try await Shell.check("/usr/bin/zip", ["-Xr9Dq", url.path, "META-INF", "OEBPS"], currentDirectory: dir)
    }
}

// MARK: - Markdown

enum Markdown {
    static func toHTML(_ md: String) -> String {
        var html = "", inList: String?, inCode = false, para: [String] = []
        var tableRows: [[String]] = []
        func flushPara() { if !para.isEmpty { html += "<p>\(inline(para.joined(separator: " ")))</p>\n"; para = [] } }
        func closeList() { if let l = inList { html += "</\(l)>\n"; inList = nil } }
        func flushTable() {
            guard !tableRows.isEmpty else { return }
            html += "<table border=\"1\" cellpadding=\"4\">"
            for (i, r) in tableRows.enumerated() {
                let tag = i == 0 ? "th" : "td"
                html += "<tr>" + r.map { "<\(tag)>\(inline($0))</\(tag)>" }.joined() + "</tr>"
            }
            html += "</table>\n"; tableRows = []
        }
        for raw in md.components(separatedBy: .newlines) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.hasPrefix("```") {
                flushPara(); closeList(); flushTable()
                html += inCode ? "</code></pre>\n" : "<pre><code>"
                inCode.toggle(); continue
            }
            if inCode { html += escape(raw) + "\n"; continue }
            if line.hasPrefix("|") {
                flushPara(); closeList()
                let cells = line.trimmingCharacters(in: CharacterSet(charactersIn: "|")).components(separatedBy: "|").map { $0.trimmingCharacters(in: .whitespaces) }
                if !cells.allSatisfy({ $0.allSatisfy { "-: ".contains($0) } }) { tableRows.append(cells) }
                continue
            } else { flushTable() }
            if line.isEmpty { flushPara(); closeList(); continue }
            if let m = line.firstMatch(of: /^(#{1,6})\s+(.*)$/) {
                flushPara(); closeList()
                html += "<h\(m.1.count)>\(inline(String(m.2)))</h\(m.1.count)>\n"; continue
            }
            if line == "---" || line == "***" { flushPara(); closeList(); html += "<hr/>\n"; continue }
            if line.hasPrefix("> ") { flushPara(); closeList(); html += "<blockquote>\(inline(String(line.dropFirst(2))))</blockquote>\n"; continue }
            if let m = line.firstMatch(of: /^([-*+]|\d+\.)\s+(.*)$/) {
                flushPara()
                let kind = m.1.first!.isNumber ? "ol" : "ul"
                if inList != kind { closeList(); html += "<\(kind)>\n"; inList = kind }
                html += "<li>\(inline(String(m.2)))</li>\n"; continue
            }
            closeList()
            para.append(line)
        }
        flushPara(); closeList(); flushTable()
        if inCode { html += "</code></pre>" }
        return html
    }

    static func escape(_ s: String) -> String {
        s.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;").replacingOccurrences(of: ">", with: "&gt;")
    }

    private static func inline(_ s: String) -> String {
        var t = escape(s)
        t = t.replacing(/!\[([^\]]*)\]\(([^)]+)\)/) { "<img alt=\"\($0.1)\" src=\"\($0.2)\"/>" }
        t = t.replacing(/\[([^\]]+)\]\(([^)]+)\)/) { "<a href=\"\($0.2)\">\($0.1)</a>" }
        t = t.replacing(/`([^`]+)`/) { "<code>\($0.1)</code>" }
        t = t.replacing(/\*\*([^*]+)\*\*/) { "<b>\($0.1)</b>" }
        t = t.replacing(/\*([^*]+)\*/) { "<i>\($0.1)</i>" }
        t = t.replacing(/~~([^~]+)~~/) { "<s>\($0.1)</s>" }
        return t
    }

    /// Converts rich text back to Markdown. Heading levels are inferred relative to the dominant body size;
    /// lists, links, emphasis and tables (NSTextTableBlock) are preserved.
    static func fromAttributed(_ text: NSAttributedString) -> String {
        let ns = text.string as NSString
        let full = NSRange(location: 0, length: ns.length)
        var sizeCounts: [CGFloat: Int] = [:]
        text.enumerateAttribute(.font, in: full) { v, r, _ in
            if let f = v as? NSFont { sizeCounts[f.pointSize.rounded(), default: 0] += r.length }
        }
        let body = sizeCounts.max { $0.value < $1.value }?.key ?? 12

        var out = ""
        var table: [[String]] = []
        func flushTable() {
            guard !table.isEmpty else { return }
            out += SheetKit.markdown(table) + "\n"
            table = []
        }
        ns.enumerateSubstrings(in: full, options: .byParagraphs) { _, pRange, _, _ in
            let plain = ns.substring(with: pRange)
            let style = pRange.length > 0 ? text.attribute(.paragraphStyle, at: pRange.location, effectiveRange: nil) as? NSParagraphStyle : nil
            if let cell = style?.textBlocks.compactMap({ $0 as? NSTextTableBlock }).first {
                while table.count <= cell.startingRow { table.append([]) }
                while table[cell.startingRow].count < cell.startingColumn { table[cell.startingRow].append("") }
                table[cell.startingRow].append(plain.trimmingCharacters(in: .whitespaces))
                return
            }
            flushTable()
            guard !plain.trimmingCharacters(in: .whitespaces).isEmpty else { return }

            var maxSize: CGFloat = 0
            var inline = ""
            text.enumerateAttributes(in: pRange) { attrs, r, _ in
                let s = ns.substring(with: r)
                let font = attrs[.font] as? NSFont
                maxSize = max(maxSize, font?.pointSize ?? body)
                let traits = font?.fontDescriptor.symbolicTraits ?? []
                let core = s.trimmingCharacters(in: .whitespaces)
                guard !core.isEmpty else { inline += s; return }
                var piece = core
                if let link = attrs[.link] { piece = "[\(piece)](\(link))" }
                if traits.contains(.bold) && traits.contains(.italic) { piece = "***\(piece)***" }
                else if traits.contains(.bold) { piece = "**\(piece)**" }
                else if traits.contains(.italic) { piece = "*\(piece)*" }
                let lead = String(s.prefix { $0 == " " }), trail = String(s.reversed().prefix { $0 == " " })
                inline += lead + piece + trail
            }

            let trimmedPlain = plain.trimmingCharacters(in: .whitespaces)
            let ratio = maxSize / max(body, 1)
            if ratio >= 1.6 { out += "# \(trimmedPlain)\n\n"; return }
            if ratio >= 1.3 { out += "## \(trimmedPlain)\n\n"; return }
            if ratio >= 1.12 { out += "### \(trimmedPlain)\n\n"; return }
            if let m = plain.firstMatch(of: /^\t*([•◦▪\-*]|\d+[.)])\t/) {
                let marker = m.1.first!.isNumber ? "1." : "-"
                let inner = inline.replacing(/^\s*([•◦▪\-*]|\d+[.)])\s*/, with: "")
                out += "\(marker) \(inner.trimmingCharacters(in: .whitespaces))\n"
                return
            }
            if out.hasSuffix("\n") && !out.hasSuffix("\n\n") && !out.isEmpty { out += "\n" }
            out += inline.trimmingCharacters(in: .whitespaces) + "\n\n"
        }
        flushTable()
        return out.replacing(/\n{3,}/, with: "\n\n")
    }
}
