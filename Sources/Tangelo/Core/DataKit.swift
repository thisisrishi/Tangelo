import AppKit
import Foundation

// MARK: - Spreadsheets

enum SheetKit {
    typealias Table = [[String]]

    static func read(_ url: URL) async throws -> Table {
        switch ConversionFormat.from(url) {
        case .csv: return parseDelimited(try readText(url), separator: ",")
        case .tsv: return parseDelimited(try readText(url), separator: "\t")
        case .json: return try parseJSON(Data(contentsOf: url))
        case .xlsx: return try await readXLSX(url)
        case .ods: return try await readODS(url)
        case .numbers:
            let tmp = try Output.tempDirectory().appendingPathComponent("sheet.csv")
            try await IWork.export(app: "Numbers", input: url, output: tmp, as: "CSV")
            return try await read(firstCSV(at: tmp))
        case .xls:
            guard let soffice = Helpers.soffice else { throw TangeloError.message("Converting legacy .xls needs LibreOffice or Numbers.") }
            let dir = try Output.tempDirectory()
            try await Shell.check(soffice, ["--headless", "--convert-to", "csv", "--outdir", dir.path, url.path])
            return try await read(firstCSV(at: dir))
        default: throw TangeloError.unsupported("This file is not a spreadsheet.")
        }
    }

    private static func firstCSV(at url: URL) throws -> URL {
        var isDir: ObjCBool = false
        FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir)
        if !isDir.boolValue { return url }
        let items = try FileManager.default.contentsOfDirectory(at: url, includingPropertiesForKeys: nil)
        guard let csv = items.first(where: { $0.pathExtension == "csv" }) ?? items.first else { throw TangeloError.message("The converter produced no output.") }
        return try firstCSV(at: csv)
    }

    static func parseDelimited(_ text: String, separator: Character) -> Table {
        var rows: Table = [], row: [String] = [], field = "", inQuotes = false
        var it = Array(text).makeIterator()
        var pending: Character? = nil
        while let c = pending ?? it.next() {
            pending = nil
            if inQuotes {
                if c == "\"" {
                    if let n = it.next() { if n == "\"" { field.append("\"") } else { inQuotes = false; pending = n } } else { inQuotes = false }
                } else { field.append(c) }
            } else if c == "\"" { inQuotes = true }
            else if c == separator { row.append(field); field = "" }
            else if c == "\n" || c == "\r\n" { row.append(field); rows.append(row); row = []; field = "" }
            else if c == "\r" { continue }
            else { field.append(c) }
        }
        if !field.isEmpty || !row.isEmpty { row.append(field); rows.append(row) }
        return rows
    }

    private static func parseJSON(_ data: Data) throws -> Table {
        let obj = try JSONSerialization.jsonObject(with: data)
        if let arr = obj as? [[String: Any]] {
            var keys: [String] = []
            for d in arr { for k in d.keys.sorted() where !keys.contains(k) { keys.append(k) } }
            return [keys] + arr.map { d in keys.map { d[$0].map { "\($0)" } ?? "" } }
        }
        if let arr = obj as? [[Any]] { return arr.map { $0.map { "\($0)" } } }
        throw TangeloError.message("The JSON must be an array of objects or an array of arrays.")
    }

    private static func unzipEntry(_ archive: URL, _ entry: String) async throws -> Data? {
        let r = try await Shell.run("/usr/bin/unzip", ["-p", archive.path, entry])
        return r.status == 0 && !r.stdout.isEmpty ? r.stdout : nil
    }

    private static func readXLSX(_ url: URL) async throws -> Table {
        let shared = try await unzipEntry(url, "xl/sharedStrings.xml").map { XMLCells.sharedStrings($0) } ?? []
        guard let sheet = try await unzipEntry(url, "xl/worksheets/sheet1.xml") else {
            throw TangeloError.message("The spreadsheet has no readable worksheet.")
        }
        return XMLCells.xlsxSheet(sheet, shared: shared)
    }

    private static func readODS(_ url: URL) async throws -> Table {
        guard let content = try await unzipEntry(url, "content.xml") else { throw TangeloError.message("The spreadsheet is unreadable.") }
        return XMLCells.odsTable(content)
    }

    // Writing

    static func write(_ table: Table, to url: URL, format: ConversionFormat, title: String) async throws {
        switch format {
        case .csv: try Data(delimited(table, ",").utf8).write(to: url)
        case .tsv: try Data(delimited(table, "\t").utf8).write(to: url)
        case .json:
            guard let header = table.first else { try Data("[]".utf8).write(to: url); return }
            let objs = table.dropFirst().map { row in Dictionary(uniqueKeysWithValues: header.enumerated().map { ($0.element.isEmpty ? "column\($0.offset + 1)" : $0.element, $0.offset < row.count ? row[$0.offset] : "") }) }
            try JSONSerialization.data(withJSONObject: objs, options: [.prettyPrinted, .sortedKeys]).write(to: url)
        case .html: try Data(html(table, title: title).utf8).write(to: url)
        case .md: try Data(markdown(table).utf8).write(to: url)
        case .pdf: try PDFKitTools.renderText(try DocumentKit.html(html(table, title: title)), to: url, pageSize: CGSize(width: 792, height: 612))
        case .xlsx: try await writeXLSX(table, to: url)
        default: throw TangeloError.unsupported("Cannot write \(format.label) spreadsheets.")
        }
    }

    static func delimited(_ t: Table, _ sep: String) -> String {
        t.map { row in row.map { f in
            f.contains(sep) || f.contains("\"") || f.contains("\n") ? "\"" + f.replacingOccurrences(of: "\"", with: "\"\"") + "\"" : f
        }.joined(separator: sep) }.joined(separator: "\n") + "\n"
    }

    static func html(_ t: Table, title: String) -> String {
        let rows = t.enumerated().map { i, r in
            "<tr>" + r.map { "<\(i == 0 ? "th" : "td")>\(Markdown.escape($0))</\(i == 0 ? "th" : "td")>" }.joined() + "</tr>"
        }.joined(separator: "\n")
        return "<h3>\(Markdown.escape(title))</h3><table border=\"1\" cellspacing=\"0\" cellpadding=\"4\">\n\(rows)\n</table>"
    }

    static func markdown(_ t: Table) -> String {
        guard let h = t.first else { return "" }
        let width = t.map(\.count).max() ?? h.count
        func line(_ r: [String]) -> String { "| " + (0..<width).map { $0 < r.count ? r[$0].replacingOccurrences(of: "|", with: "\\|") : "" }.joined(separator: " | ") + " |" }
        return ([line(h), "|" + String(repeating: " --- |", count: width)] + t.dropFirst().map(line)).joined(separator: "\n") + "\n"
    }

    private static func writeXLSX(_ t: Table, to url: URL) async throws {
        let dir = try Output.tempDirectory()
        func put(_ path: String, _ s: String) throws {
            let u = dir.appendingPathComponent(path)
            try FileManager.default.createDirectory(at: u.deletingLastPathComponent(), withIntermediateDirectories: true)
            try s.write(to: u, atomically: true, encoding: .utf8)
        }
        func col(_ i: Int) -> String { var n = i + 1, s = ""; while n > 0 { let r = (n - 1) % 26; s = String(UnicodeScalar(65 + r)!) + s; n = (n - 1) / 26 }; return s }
        let rows = t.enumerated().map { ri, r in
            "<row r=\"\(ri + 1)\">" + r.enumerated().map { ci, v in
                let ref = "\(col(ci))\(ri + 1)"
                if let d = Double(v), !v.isEmpty, v.first != "0" || v == "0" || v.hasPrefix("0.") { return "<c r=\"\(ref)\"><v>\(d)</v></c>" }
                return "<c r=\"\(ref)\" t=\"inlineStr\"><is><t xml:space=\"preserve\">\(Markdown.escape(v))</t></is></c>"
            }.joined() + "</row>"
        }.joined()
        try put("[Content_Types].xml", #"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>"#)
        try put("_rels/.rels", #"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>"#)
        try put("xl/workbook.xml", #"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>"#)
        try put("xl/_rels/workbook.xml.rels", #"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>"#)
        try put("xl/worksheets/sheet1.xml", #"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>"# + rows + "</sheetData></worksheet>")
        try? FileManager.default.removeItem(at: url)
        try await Shell.check("/usr/bin/zip", ["-Xrq", url.path, "."], currentDirectory: dir)
    }
}

/// Small SAX helpers for spreadsheet XML.
final class XMLCells: NSObject, XMLParserDelegate {
    private var mode: String
    private var shared: [String] = []
    private var strings: [String] = []
    private var text = ""
    private var capturing = false
    private var table: SheetKit.Table = []
    private var row: [String] = []
    private var cellType: String?
    private var cellCol = 0
    private var repeatCount = 1

    private init(mode: String, shared: [String] = []) { self.mode = mode; self.shared = shared }

    static func sharedStrings(_ data: Data) -> [String] {
        let h = XMLCells(mode: "sst"); let p = XMLParser(data: data); p.delegate = h; p.parse(); return h.strings
    }
    static func xlsxSheet(_ data: Data, shared: [String]) -> SheetKit.Table {
        let h = XMLCells(mode: "sheet", shared: shared); let p = XMLParser(data: data); p.delegate = h; p.parse(); return h.table
    }
    static func odsTable(_ data: Data) -> SheetKit.Table {
        let h = XMLCells(mode: "ods"); let p = XMLParser(data: data); p.shouldProcessNamespaces = false; p.delegate = h; p.parse()
        while let last = h.table.last, last.allSatisfy(\.isEmpty) { h.table.removeLast() }
        return h.table
    }

    private static func column(from ref: String) -> Int {
        var n = 0
        for ch in ref.unicodeScalars where ch.properties.isAlphabetic { n = n * 26 + Int(ch.value) - 64 }
        return max(0, n - 1)
    }

    func parser(_ parser: XMLParser, didStartElement name: String, namespaceURI: String?, qualifiedName: String?, attributes: [String: String] = [:]) {
        switch (mode, name) {
        case ("sst", "si"): text = ""
        case ("sst", "t"), ("sheet", "v"), ("sheet", "t"): capturing = true
        case ("sheet", "row"), ("ods", "table:table-row"): row = []
        case ("sheet", "c"):
            cellType = attributes["t"]; text = ""
            cellCol = attributes["r"].map(Self.column) ?? row.count
        case ("ods", "table:table-cell"), ("ods", "table:covered-table-cell"):
            text = ""; repeatCount = min(Int(attributes["table:number-columns-repeated"] ?? "1") ?? 1, 256)
        case ("ods", "text:p"): capturing = true; if !text.isEmpty { text += "\n" }
        default: break
        }
    }

    func parser(_ parser: XMLParser, foundCharacters string: String) { if capturing { text += string } }

    func parser(_ parser: XMLParser, didEndElement name: String, namespaceURI: String?, qualifiedName: String?) {
        switch (mode, name) {
        case ("sst", "t"), ("sheet", "v"), ("sheet", "t"), ("ods", "text:p"): capturing = false
        case ("sst", "si"): strings.append(text)
        case ("sheet", "c"):
            var value = text
            if cellType == "s", let i = Int(text), i < shared.count { value = shared[i] }
            while row.count < cellCol { row.append("") }
            row.append(value)
        case ("sheet", "row"): table.append(row)
        case ("ods", "table:table-cell"), ("ods", "table:covered-table-cell"):
            for _ in 0..<repeatCount { row.append(text) }
        case ("ods", "table:table-row"):
            while let l = row.last, l.isEmpty { row.removeLast() }
            table.append(row)
        default: break
        }
    }
}

// MARK: - Subtitles

enum SubtitleKit {
    struct Cue { var start: Double; var end: Double; var text: String }

    static func read(_ url: URL) throws -> [Cue] {
        guard let raw = try? String(contentsOf: url, encoding: .utf8) else { throw TangeloError.message("The subtitle file is not valid UTF-8.") }
        let blocks = raw.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n\n")
        var cues: [Cue] = []
        for b in blocks {
            let lines = b.split(separator: "\n", omittingEmptySubsequences: true).map(String.init)
            guard let ti = lines.firstIndex(where: { $0.contains("-->") }) else { continue }
            let parts = lines[ti].components(separatedBy: "-->")
            guard parts.count == 2, let s = time(parts[0]), let e = time(parts[1].split(separator: " ").first.map(String.init) ?? "") else { continue }
            cues.append(Cue(start: s, end: e, text: lines[(ti + 1)...].joined(separator: "\n")))
        }
        guard !cues.isEmpty else { throw TangeloError.message("No valid subtitle cues were found.") }
        return cues
    }

    static func time(_ s: String) -> Double? {
        let parts = s.trimmingCharacters(in: .whitespaces).replacingOccurrences(of: ",", with: ".").split(separator: ":")
        let nums = parts.compactMap { Double($0) }
        guard nums.count == parts.count, !nums.isEmpty else { return nil }
        return nums.reduce(0) { $0 * 60 + $1 }
    }

    static func stamp(_ t: Double, comma: Bool) -> String {
        let ms = Int((t * 1000).rounded())
        return String(format: "%02d:%02d:%02d%@%03d", ms / 3_600_000, ms / 60_000 % 60, ms / 1000 % 60, comma ? "," : ".", ms % 1000)
    }

    static func write(_ cues: [Cue], to url: URL, format: ConversionFormat) throws {
        let text: String
        switch format {
        case .srt: text = cues.enumerated().map { "\($0.offset + 1)\n\(stamp($0.element.start, comma: true)) --> \(stamp($0.element.end, comma: true))\n\($0.element.text)" }.joined(separator: "\n\n") + "\n"
        case .vtt: text = "WEBVTT\n\n" + cues.map { "\(stamp($0.start, comma: false)) --> \(stamp($0.end, comma: false))\n\($0.text)" }.joined(separator: "\n\n") + "\n"
        default: text = cues.map { $0.text.replacing(/<[^>]+>/, with: "") }.joined(separator: "\n") + "\n"
        }
        try Data(text.utf8).write(to: url)
    }
}

// MARK: - Archives

enum ArchiveKit {
    static func create(_ items: [URL], format: ConversionFormat, to url: URL) async throws {
        let parent = items[0].deletingLastPathComponent()
        let names = items.map(\.lastPathComponent)
        switch format {
        case .zip:
            if items.count == 1 {
                try await Shell.check("/usr/bin/ditto", ["-c", "-k", "--sequesterRsrc", "--keepParent", items[0].path, url.path])
            } else {
                try await Shell.check("/usr/bin/zip", ["-rqy", url.path] + names + ["-x", "*.DS_Store"], currentDirectory: parent)
            }
        case .tar: try await Shell.check("/usr/bin/tar", ["--exclude", ".DS_Store", "-cf", url.path] + names, currentDirectory: parent)
        case .gzip: try await Shell.check("/usr/bin/tar", ["--exclude", ".DS_Store", "-czf", url.path] + names, currentDirectory: parent)
        default: throw TangeloError.unsupported("Creating \(format.label) archives is not supported.")
        }
    }

    static func outputExtension(for format: ConversionFormat, count: Int, isFolder: Bool) -> String {
        format == .gzip ? "tar.gz" : format.fileExtension
    }

    /// Extracts zip, tar, gz, tgz, xz, bz2, rar, 7z, cpio, iso — anything libarchive reads.
    static func extract(_ url: URL) async throws -> URL {
        var base = url.deletingPathExtension().lastPathComponent
        if base.hasSuffix(".tar") { base = String(base.dropLast(4)) }
        let dest = Output.unique(in: Output.directory(for: url), base: base, ext: "")
        try FileManager.default.createDirectory(at: dest, withIntermediateDirectories: true)
        let ext = url.pathExtension.lowercased()
        if ext == "gz" && !url.lastPathComponent.lowercased().hasSuffix(".tar.gz") {
            // Plain gzip of a single file.
            let out = dest.appendingPathComponent(base)
            let r = try await Shell.check("/usr/bin/gunzip", ["-c", url.path])
            try r.stdout.write(to: out)
        } else if ext == "zip" {
            try await Shell.check("/usr/bin/ditto", ["-x", "-k", url.path, dest.path])
        } else {
            try await Shell.check("/usr/bin/tar", ["-xf", url.path, "-C", dest.path])
        }
        // Collapse a single top-level folder.
        let contents = try FileManager.default.contentsOfDirectory(at: dest, includingPropertiesForKeys: nil).filter { $0.lastPathComponent != "__MACOSX" }
        if contents.count == 1, contents[0].hasDirectoryPath {
            let target = Output.unique(in: dest.deletingLastPathComponent(), base: contents[0].lastPathComponent, ext: "")
            try FileManager.default.moveItem(at: contents[0], to: target)
            try? FileManager.default.removeItem(at: dest)
            return target
        }
        try? FileManager.default.removeItem(at: dest.appendingPathComponent("__MACOSX"))
        return dest
    }
}

// MARK: - Presentations

enum SlideKit {
    static func convert(_ url: URL, to format: ConversionFormat, out: URL) async throws -> [URL] {
        let pdf: URL
        if format == .pdf { pdf = out } else { pdf = try Output.tempDirectory().appendingPathComponent("slides.pdf") }
        if format == .pptx {
            if IWork.installed("Keynote") {
                try await IWork.export(app: "Keynote", input: url, output: out, as: "Microsoft PowerPoint"); return [out]
            }
            guard let soffice = Helpers.soffice else { throw TangeloError.message("Install Keynote or LibreOffice to convert presentations.") }
            let dir = try Output.tempDirectory()
            try await Shell.check(soffice, ["--headless", "--convert-to", "pptx", "--outdir", dir.path, url.path])
            let produced = try FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)
            guard let f = produced.first else { throw TangeloError.message("The converter finished without creating an output file.") }
            try FileManager.default.moveItem(at: f, to: out); return [out]
        }
        if IWork.installed("Keynote") {
            try await IWork.export(app: "Keynote", input: url, output: pdf, as: "PDF")
        } else if let soffice = Helpers.soffice {
            let dir = try Output.tempDirectory()
            try await Shell.check(soffice, ["--headless", "--convert-to", "pdf", "--outdir", dir.path, url.path])
            guard let f = try FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil).first else {
                throw TangeloError.message("The converter finished without creating an output file.")
            }
            try? FileManager.default.removeItem(at: pdf)
            try FileManager.default.moveItem(at: f, to: pdf)
        } else {
            throw TangeloError.message("Install Keynote (free from the App Store) or LibreOffice to convert presentations.")
        }
        if format == .pdf { return [pdf] }
        let dir = Output.unique(in: Output.directory(for: url), base: url.deletingPathExtension().lastPathComponent + " slides", ext: "")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return try PDFKitTools.exportPages(pdf, format: format, into: dir)
    }
}
