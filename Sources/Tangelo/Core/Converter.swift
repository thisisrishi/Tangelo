import AVFoundation
import Foundation

/// Conversion defaults stored per output format (quality, bitrate, etc).
struct ConversionPreferences {
    static var imageQuality: Double { UserDefaults.standard.object(forKey: "imageQuality") as? Double ?? 0.85 }
    static var audioBitrate: Int { UserDefaults.standard.object(forKey: "audioBitrate") as? Int ?? 192_000 }
    static var preserveMetadata: Bool { UserDefaults.standard.object(forKey: "preserveMetadata") as? Bool ?? true }
    static var gifFPS: Double { UserDefaults.standard.object(forKey: "gifFPS") as? Double ?? 12 }
    static var gifWidth: Double { UserDefaults.standard.object(forKey: "gifWidth") as? Double ?? 640 }
    static var preferHEVC: Bool { UserDefaults.standard.bool(forKey: "preferHEVC") }
}

enum Converter {
    /// Converts every input; archives combine all inputs into one file.
    static func convert(_ inputs: [URL], to format: ConversionFormat, progress: @escaping @Sendable (Double) -> Void) async throws -> [URL] {
        if format.category == .archive, inputs.contains(where: { FileCategory.of($0) != .archive }) || inputs.count > 1 {
            let first = inputs[0]
            let base = inputs.count == 1 ? first.lastPathComponent : "Archive"
            let out = Output.unique(in: Output.directory(for: first), base: base, ext: ArchiveKit.outputExtension(for: format, count: inputs.count, isFolder: first.hasDirectoryPath))
            try await ArchiveKit.create(inputs, format: format, to: out)
            return [out]
        }
        var outputs: [URL] = []
        for (i, input) in inputs.enumerated() {
            outputs += try await convert(input, to: format) { p in progress((Double(i) + p) / Double(inputs.count)) }
            progress(Double(i + 1) / Double(inputs.count))
        }
        return outputs
    }

    static func convert(_ input: URL, to format: ConversionFormat, progress: @escaping @Sendable (Double) -> Void) async throws -> [URL] {
        let category = FileCategory.of(input)
        let title = input.deletingPathExtension().lastPathComponent
        let out = Output.url(for: input, ext: format.fileExtension)

        switch (category, format.category) {
        case (.image, .image), (.vector, .image), (.image, .pdf), (.vector, .pdf):
            try convertImage(input, to: format, out: out)
            return [out]

        case (.pdf, .image):
            let dir = try pagesFolder(for: input)
            return try PDFKitTools.exportPages(input, format: format, into: dir)

        case (.video, .image) where format == .gif:
            try await VideoKit.gif(from: input, to: out, fps: ConversionPreferences.gifFPS, maxWidth: ConversionPreferences.gifWidth)
            return [out]

        case (.video, .video):
            try await convertVideo(input, to: format, out: out, progress: progress)
            return [out]

        case (.video, .audio), (.audio, .audio):
            try await convertAudio(input, to: format, out: out)
            return [out]

        case (.document, .document), (.document, .pdf), (.pdf, .document):
            let text = try await DocumentKit.read(input)
            try await DocumentKit.write(text, to: out, format: format, title: title)
            return [out]

        case (.spreadsheet, .spreadsheet), (.spreadsheet, .document), (.spreadsheet, .pdf):
            let table = try await SheetKit.read(input)
            if format == .xlsx || [.csv, .tsv, .json, .html, .md, .pdf].contains(format) {
                try await SheetKit.write(table, to: out, format: format, title: title)
                return [out]
            }
            throw TangeloError.unsupported("Cannot convert spreadsheets to \(format.label).")

        case (.presentation, _):
            return try await SlideKit.convert(input, to: format, out: out)

        case (.subtitle, _):
            try SubtitleKit.write(try SubtitleKit.read(input), to: out, format: format)
            return [out]

        case (.archive, .archive):
            // Repack: extract then re-archive into the chosen format.
            let extracted = try await ArchiveKit.extract(input)
            defer { try? FileManager.default.removeItem(at: extracted) }
            let repacked = Output.url(for: input, ext: ArchiveKit.outputExtension(for: format, count: 1, isFolder: true))
            try await ArchiveKit.create([extracted], format: format, to: repacked)
            return [repacked]

        default:
            throw TangeloError.unsupported("Conversion from \(category.title.lowercased()) to \(format.label) is not available.")
        }
    }

    private static func pagesFolder(for input: URL) throws -> URL {
        let dir = Output.unique(in: Output.directory(for: input), base: input.deletingPathExtension().lastPathComponent + " pages", ext: "")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    static func convertImage(_ input: URL, to format: ConversionFormat, out: URL) throws {
        // Keep animation for GIF → GIF-capable targets.
        if format == .gif, ImageIOKit.frameCount(input) > 1 {
            try FileManager.default.copyItem(at: input, to: out); return
        }
        let image = try ImageIOKit.load(input)
        var meta: [CFString: Any]? = nil
        if ConversionPreferences.preserveMetadata {
            let props = ImageIOKit.properties(input)
            var m: [CFString: Any] = [:]
            for key in [kCGImagePropertyExifDictionary, kCGImagePropertyGPSDictionary, kCGImagePropertyTIFFDictionary, kCGImagePropertyIPTCDictionary] {
                if let v = props[key] { m[key] = v }
            }
            meta = m
        }
        try ImageIOKit.write(image, to: out, format: format, quality: ConversionPreferences.imageQuality, metadata: meta)
    }

    static func convertVideo(_ input: URL, to format: ConversionFormat, out: URL, progress: @escaping @Sendable (Double) -> Void) async throws {
        switch format {
        case .mp4, .mov, .m4v:
            let asset = AVURLAsset(url: input)
            let preset = ConversionPreferences.preferHEVC ? AVAssetExportPresetHEVCHighestQuality : AVAssetExportPresetHighestQuality
            do {
                try await VideoKit.export(asset, to: out, preset: preset, fileType: VideoKit.fileType(for: format))
            } catch where Helpers.ffmpeg != nil {
                // AVFoundation can't read mkv/webm/avi etc; ffmpeg can.
                try await FFmpeg.convert(input, to: out, args: FFmpeg.videoArgs(format))
            }
        default:
            try await FFmpeg.convert(input, to: out, args: FFmpeg.videoArgs(format))
        }
    }

    static func convertAudio(_ input: URL, to format: ConversionFormat, out: URL) async throws {
        do {
            let pcm = try await PCMAudio.read(input)
            try await pcm.write(to: out, format: format, bitrate: ConversionPreferences.audioBitrate)
        } catch {
            // Fall back to ffmpeg for containers AVFoundation can't decode (ogg, opus, wma, mkv…).
            guard Helpers.ffmpeg != nil else { throw error }
            try await FFmpeg.convert(input, to: out, args: ["-vn"] + FFmpeg.audioArgs(format, bitrate: ConversionPreferences.audioBitrate))
        }
    }
}
