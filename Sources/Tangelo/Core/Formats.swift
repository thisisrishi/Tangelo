import Foundation
import UniformTypeIdentifiers

enum FileCategory: String, CaseIterable, Codable {
    case image, vector, video, audio, document, pdf, spreadsheet, presentation, subtitle, archive, folder, other

    var title: String {
        switch self {
        case .image: return "Images"
        case .vector: return "Vector"
        case .video: return "Video"
        case .audio: return "Audio"
        case .document: return "Documents"
        case .pdf: return "PDF"
        case .spreadsheet: return "Spreadsheets"
        case .presentation: return "Presentations"
        case .subtitle: return "Subtitles"
        case .archive: return "Archives"
        case .folder: return "Folders"
        case .other: return "Other"
        }
    }

    var symbol: String {
        switch self {
        case .image: return "photo"
        case .vector: return "scribble.variable"
        case .video: return "film"
        case .audio: return "waveform"
        case .document: return "doc.text"
        case .pdf: return "doc.richtext"
        case .spreadsheet: return "tablecells"
        case .presentation: return "rectangle.on.rectangle"
        case .subtitle: return "captions.bubble"
        case .archive: return "archivebox"
        case .folder: return "folder"
        case .other: return "doc"
        }
    }

    static func of(_ url: URL) -> FileCategory {
        var isDir: ObjCBool = false
        if FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir), isDir.boolValue {
            let packageExts = ["rtfd", "pages", "numbers", "key"]
            if !packageExts.contains(url.pathExtension.lowercased()) { return .folder }
        }
        let ext = url.pathExtension.lowercased()
        if let f = ConversionFormat(rawValue: ext) ?? ConversionFormat.alias[ext] { return f.category }
        if let t = UTType(filenameExtension: ext) {
            if t.conforms(to: .image) { return .image }
            if t.conforms(to: .movie) { return .video }
            if t.conforms(to: .audio) { return .audio }
            if t.conforms(to: .pdf) { return .pdf }
            if t.conforms(to: .archive) { return .archive }
            if t.conforms(to: .text) { return .document }
        }
        return .other
    }
}

enum ConversionFormat: String, CaseIterable, Codable, Identifiable {
    // images
    case jpg, png, webp, heic, tiff, avif, bmp, gif, ico, svg
    // audio
    case mp3, m4a, wav, flac, ogg, opus, aiff, wma
    // video
    case mp4, mov, m4v, mkv, webm, avi, wmv
    // documents
    case docx, doc, odt, pages, txt, md, rtf, rtfd, html, epub, textClipping, pdf
    // spreadsheets
    case xlsx, xls, ods, numbers, csv, tsv, json
    // presentations
    case pptx, ppt, key
    // subtitles
    case srt, vtt
    // archives
    case zip, tar, gzip, rar, sevenZip = "7z"

    var id: String { rawValue }

    static let alias: [String: ConversionFormat] = [
        "jpeg": .jpg, "tif": .tiff, "heif": .heic, "aif": .aiff, "aac": .m4a, "markdown": .md,
        "htm": .html, "text": .txt, "gz": .gzip, "tgz": .tar, "textclipping": .textClipping, "mpeg": .mp4,
        "mpg": .mp4, "3gp": .mp4, "caf": .wav, "oga": .ogg, "xhtml": .html,
    ]

    static func from(_ url: URL) -> ConversionFormat? {
        let ext = url.pathExtension.lowercased()
        return ConversionFormat(rawValue: ext) ?? alias[ext]
    }

    var fileExtension: String {
        switch self {
        case .gzip: return "gz"
        case .textClipping: return "textClipping"
        default: return rawValue
        }
    }

    var label: String {
        switch self {
        case .textClipping: return "Clipping"
        case .sevenZip: return "7Z"
        case .gzip: return "GZ"
        case .md: return "Markdown"
        case .pages: return "Pages"
        case .numbers: return "Numbers"
        case .key: return "Keynote"
        default: return rawValue.uppercased()
        }
    }

    var category: FileCategory {
        switch self {
        case .jpg, .png, .webp, .heic, .tiff, .avif, .bmp, .gif, .ico: return .image
        case .svg: return .vector
        case .mp3, .m4a, .wav, .flac, .ogg, .opus, .aiff, .wma: return .audio
        case .mp4, .mov, .m4v, .mkv, .webm, .avi, .wmv: return .video
        case .docx, .doc, .odt, .pages, .txt, .md, .rtf, .rtfd, .html, .epub, .textClipping: return .document
        case .pdf: return .pdf
        case .xlsx, .xls, .ods, .numbers, .csv, .tsv, .json: return .spreadsheet
        case .pptx, .ppt, .key: return .presentation
        case .srt, .vtt: return .subtitle
        case .zip, .tar, .gzip, .rar, .sevenZip: return .archive
        }
    }

    /// Output formats offered for a given input category.
    static func targets(for category: FileCategory, source: ConversionFormat? = nil) -> [ConversionFormat] {
        let list: [ConversionFormat]
        switch category {
        case .image: list = [.jpg, .png, .webp, .heic, .avif, .tiff, .gif, .bmp, .ico, .pdf]
        case .vector: list = [.png, .jpg, .webp, .tiff, .pdf]
        case .video: list = [.mp4, .mov, .m4v, .mkv, .webm, .avi, .gif, .mp3, .m4a, .wav]
        case .audio: list = [.mp3, .m4a, .wav, .flac, .aiff, .ogg, .opus]
        case .document: list = [.docx, .pdf, .rtf, .txt, .md, .html, .epub, .odt, .rtfd, .pages]
        case .pdf: list = [.docx, .txt, .md, .rtf, .html, .epub, .png, .jpg]
        case .spreadsheet: list = [.xlsx, .csv, .tsv, .json, .html, .md, .pdf]
        case .presentation: list = [.pdf, .pptx, .png]
        case .subtitle: list = [.srt, .vtt, .txt]
        case .archive: list = [.zip, .tar, .gzip]
        case .folder, .other: list = [.zip, .tar, .gzip]
        }
        return list.filter { $0 != source }
    }

    var utType: UTType? {
        switch self {
        case .jpg: return .jpeg
        case .png: return .png
        case .heic: return .heic
        case .tiff: return .tiff
        case .gif: return .gif
        case .bmp: return .bmp
        case .ico: return .ico
        case .pdf: return .pdf
        case .webp: return .webP
        case .avif: return UTType("public.avif")
        default: return UTType(filenameExtension: fileExtension)
        }
    }
}
