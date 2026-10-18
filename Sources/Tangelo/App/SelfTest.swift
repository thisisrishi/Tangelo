import AppKit
import AVFoundation

/// `Tangelo --selftest <dir>` generates sample files and exercises every engine, printing PASS/FAIL.
@MainActor
enum SelfTest {
    static func runIfRequested() -> Bool {
        let args = CommandLine.arguments
        if let i = args.firstIndex(of: "--demo"), i + 1 < args.count {
            let what = args[i + 1]
            let files = args[(i + 2)...].prefix { $0 != "--snap" }.map { URL(fileURLWithPath: $0) }
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                let center = NSPoint(x: NSScreen.main!.frame.midX, y: NSScreen.main!.frame.midY)
                switch what {
                case "wheel-conversions": WheelController.shared.show(.conversions, for: files, at: center)
                case "wheel-tools": WheelController.shared.show(.tools, for: files, at: center)
                case "wheel-actions": WheelController.shared.show(.actions, for: files, at: center)
                case "float": FloatManager.shared.newFloat(with: files, near: center)
                case "welcome": WelcomeWindow.show()
                default: if let t = FileTool(rawValue: what) { EditorWindows.open(t, urls: files) }
                }
                if what.hasPrefix("wheel") { WheelController.shared.model.highlighted = 1 }
                if let j = args.firstIndex(of: "--snap"), j + 1 < args.count {
                    DispatchQueue.main.asyncAfter(deadline: .now() + 2.5) {
                        let win = NSApp.windows.filter { $0.isVisible && $0.contentView != nil && $0.frame.width > 200 }.last
                        if let v = win?.contentView, let rep = v.bitmapImageRepForCachingDisplay(in: v.bounds) {
                            v.cacheDisplay(in: v.bounds, to: rep)
                            try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: args[j + 1]))
                        }
                        exit(0)
                    }
                }
            }
            return true
        }
        guard let i = args.firstIndex(of: "--selftest") else { return false }
        let dir = URL(fileURLWithPath: i + 1 < args.count ? args[i + 1] : NSTemporaryDirectory() + "tangelo-selftest")
        Task { await run(in: dir); exit(failures == 0 ? 0 : 1) }
        return true
    }

    private static var failures = 0

    private static func check(_ name: String, _ body: () async throws -> [URL]) async {
        do {
            let outs = try await body()
            let ok = !outs.isEmpty && outs.allSatisfy(nonEmpty)
            if !ok { failures += 1 }
            print("\(ok ? "PASS" : "FAIL")  \(name)  → \(outs.map { "\($0.lastPathComponent) (\(Fmt.bytes(Fmt.size(of: $0))))" }.joined(separator: ", "))")
        } catch {
            failures += 1
            print("FAIL  \(name)  ✗ \((error as? LocalizedError)?.errorDescription ?? "\(error)")")
        }
    }

    private static func nonEmpty(_ u: URL) -> Bool {
        if u.hasDirectoryPath || (try? u.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true {
            return !((try? FileManager.default.contentsOfDirectory(atPath: u.path)) ?? []).isEmpty
        }
        return Fmt.size(of: u) > 0
    }

    static func run(in dir: URL) async {
        try? FileManager.default.removeItem(at: dir)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        UserDefaults.standard.set(OutputLocation.sameFolder.rawValue, forKey: "outputLocation")
        print("Self-test in \(dir.path)")

        // Fixtures
        let png = dir.appendingPathComponent("sample.png")
        let ctx = CGContext(data: nil, width: 800, height: 600, bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        let g = CGGradient(colorsSpace: nil, colors: [NSColor.systemOrange.cgColor, NSColor.systemPink.cgColor] as CFArray, locations: nil)!
        ctx.drawLinearGradient(g, start: .zero, end: CGPoint(x: 800, y: 600), options: [])
        NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: false)
        NSAttributedString(string: "Hello Tangelo 123", attributes: [.font: NSFont.boldSystemFont(ofSize: 64), .foregroundColor: NSColor.black]).draw(at: CGPoint(x: 60, y: 260))
        NSGraphicsContext.current = nil
        try? ImageIOKit.write(ctx.makeImage()!, to: png, format: .png)
        let png2 = dir.appendingPathComponent("sample2.png")
        try? FileManager.default.copyItem(at: png, to: png2)

        let wav = dir.appendingPathComponent("tone.wav")
        let sr = 44100.0
        let samples = (0..<Int(sr * 3)).map { i -> Float in i < Int(sr * 0.3) ? 0 : Float(sin(2 * .pi * 440 * Double(i) / sr) * 0.4) }
        try? await PCMAudio(channels: [samples, samples], sampleRate: sr).write(to: wav, format: .wav)

        let md = dir.appendingPathComponent("notes.md")
        try? "# Title\n\nSome **bold** and *italic* text with a [link](https://example.com).\n\n- one\n- two\n\n| a | b |\n|---|---|\n| 1 | 2 |\n".write(to: md, atomically: true, encoding: .utf8)
        let csv = dir.appendingPathComponent("table.csv")
        try? "name,age,city\n\"Doe, Jane\",34,Paris\nBob,27,\"New \"\"York\"\"\"\n".write(to: csv, atomically: true, encoding: .utf8)
        let srt = dir.appendingPathComponent("subs.srt")
        try? "1\n00:00:01,000 --> 00:00:02,500\nHello\n\n2\n00:00:03,000 --> 00:00:04,000\nWorld\n".write(to: srt, atomically: true, encoding: .utf8)

        // Images
        for f in [ConversionFormat.jpg, .heic, .avif, .webp, .tiff, .gif, .bmp, .ico, .pdf] {
            await check("image png → \(f.label)") { try await Converter.convert(png, to: f) { _ in } }
        }
        await check("compress image (preset)") { [try ImageOps.compress(png, preset: .small, targetBytes: nil, keepFormat: false)] }
        await check("compress image (target 20 KB)") {
            let u = try ImageOps.compress(png, preset: nil, targetBytes: 20_000, keepFormat: true)
            guard Fmt.size(of: u) <= 20_000 else { throw TangeloError.message("over target: \(Fmt.size(of: u))") }
            return [u]
        }
        await check("adjust image") {
            var a = ImageAdjustments(); a.exposure = 0.5; a.grain = 0.3; a.vignette = 1
            return [try ImageOps.save(ImageIOKit.cgImage(from: a.apply(CIImage(cgImage: try ImageIOKit.load(png))))!, like: png, suffix: "edited")]
        }
        await check("annotate image") {
            let anns = [Annotation(kind: .arrow, points: [CGPoint(x: 0.1, y: 0.1), CGPoint(x: 0.5, y: 0.5)]),
                        Annotation(kind: .text, points: [CGPoint(x: 0.2, y: 0.8)], text: "Hi"),
                        Annotation(kind: .magnifier, points: [CGPoint(x: 0.5, y: 0.5), CGPoint(x: 0.6, y: 0.6)])]
            return [try ImageOps.save(AnnotationRenderer.render(try ImageIOKit.load(png), annotations: anns), like: png, suffix: "annotated")]
        }
        await check("frame image") { [try ImageOps.save(FrameOptions().render(try ImageIOKit.load(png)), like: png, suffix: "framed", forcePNG: true)] }
        await check("collage") {
            let out = dir.appendingPathComponent("collage.png")
            try ImageIOKit.write(CollageOptions().render([try ImageIOKit.load(png), try ImageIOKit.load(png2)])!, to: out, format: .png)
            return [out]
        }
        await check("redact image") {
            [try ImageOps.save(ImageIOKit.cgImage(from: Redactor.apply(CIImage(cgImage: try ImageIOKit.load(png)), rects: [CGRect(x: 0.1, y: 0.1, width: 0.3, height: 0.3)], style: .pixelate))!, like: png, suffix: "redacted")]
        }
        await check("OCR image") {
            let t = try await ImageOps.extractText(png)
            guard t.localizedCaseInsensitiveContains("Tangelo") else { throw TangeloError.message("OCR read: \(t)") }
            let out = dir.appendingPathComponent("ocr.txt"); try t.write(to: out, atomically: true, encoding: .utf8); return [out]
        }
        await check("remove background") { [try ImageOps.removeBackground(png)] }
        await check("metadata strip image") { [try await MetadataOps.removeAll(dir.appendingPathComponent("sample.jpg"))] }
        await check("create PDF from images") { let o = dir.appendingPathComponent("images.pdf"); try PDFKitTools.createPDF(from: [png, png2], to: o); return [o] }

        // Audio
        for f in [ConversionFormat.m4a, .flac, .aiff] + (Helpers.ffmpeg != nil ? [.mp3, .ogg, .opus] : []) {
            await check("audio wav → \(f.label)") { try await Converter.convert(wav, to: f) { _ in } }
        }
        await check("trim audio + silence") { [try await AudioOps.trim(wav, range: 0...2, trimSilence: true, thresholdDB: -40)] }
        await check("normalize audio") { [try await AudioOps.normalize(wav, targetDB: -16, peakDB: -1)] }
        await check("audio channels mono") { [try await AudioOps.channels(wav, count: 1, left: 1, right: 0.5)] }
        await check("bleep audio") { [try await AudioOps.bleep(wav, ranges: [1...1.5], tone: true)] }
        await check("compress audio") { [try await AudioOps.compress(wav, preset: .voice, targetBytes: nil)] }

        // Video (generated from audio)
        var video: URL?
        await check("audio → visualizer video") {
            let v = try await Visualizer.render(wav, options: VisualizerOptions(size: CGSize(width: 640, height: 360), title: "Test"), progress: { _ in })
            video = v; return [v]
        }
        if let video {
            await check("video → MOV") { try await Converter.convert(video, to: .mov) { _ in } }
            await check("video → GIF") { try await Converter.convert(video, to: .gif) { _ in } }
            await check("video → M4A (extract audio)") { try await Converter.convert(video, to: .m4a) { _ in } }
            await check("trim video") { [try await VideoOps.trim(video, range: 0.5...2)] }
            await check("crop video") { [try await VideoOps.crop(video, rect: CGRect(x: 0.25, y: 0.25, width: 0.5, height: 0.5))] }
            await check("speed video 2x") { [try await VideoOps.changeSpeed(video, factor: 2)] }
            await check("mute video") { [try await VideoOps.mute(video)] }
            await check("join videos") { try await [VideoOps.joinVideos([video, video])] }
            await check("split video (separate)") { try await VideoOps.split(video, at: [1, 2], removed: [1], mode: .separate) }
            await check("split video (join kept)") { try await VideoOps.split(video, at: [1, 2], removed: [1], mode: .joinKept) }
            await check("snapshots") { try await VideoOps.snapshots(video, times: [0.5, 1.5]) }
            await check("redact video") { [try await VideoOps.redact(video, rects: [RedactionRect(rect: CGRect(x: 0, y: 0, width: 0.5, height: 0.5), timeRange: 0...1.5)], style: .blur)] }
            await check("compress video (target 150 KB)") { [try await VideoOps.compress(video, preset: nil, targetBytes: 150_000) { _ in }] }
            await check("normalize video audio") { [try await AudioOps.normalize(video, targetDB: -14, peakDB: -1)] }
            await check("metadata strip video") { [try await MetadataOps.removeAll(video)] }
        }

        // Documents
        for f in [ConversionFormat.docx, .rtf, .html, .txt, .pdf, .epub, .odt, .rtfd] {
            await check("markdown → \(f.label)") { try await Converter.convert(md, to: f) { _ in } }
        }
        await check("docx → md") { try await Converter.convert(dir.appendingPathComponent("notes.docx"), to: .md) { _ in } }
        await check("epub → txt") { try await Converter.convert(dir.appendingPathComponent("notes.epub"), to: .txt) { _ in } }
        await check("pdf → docx") { try await Converter.convert(dir.appendingPathComponent("notes.pdf"), to: .docx) { _ in } }
        await check("pdf → png pages") { try await Converter.convert(dir.appendingPathComponent("notes.pdf"), to: .png) { _ in } }
        await check("merge PDF") { let o = dir.appendingPathComponent("merged.pdf"); try PDFKitTools.merge([dir.appendingPathComponent("notes.pdf"), dir.appendingPathComponent("images.pdf")], to: o); return [o] }
        await check("split PDF") { try PDFKitTools.split(dir.appendingPathComponent("merged.pdf"), into: dir) }
        await check("organize PDF") { let o = dir.appendingPathComponent("organized.pdf"); try PDFKitTools.organize(dir.appendingPathComponent("merged.pdf"), order: [2, 0], rotations: [2: 90], equalizeWidths: true, to: o); return [o] }
        await check("QR (none expected → empty ok)") { _ = try ImageOps.readCodes(png); return [png] }

        // Spreadsheets, subtitles, archives
        for f in [ConversionFormat.xlsx, .tsv, .json, .html, .md, .pdf] {
            await check("csv → \(f.label)") { try await Converter.convert(csv, to: f) { _ in } }
        }
        await check("xlsx → csv (round trip)") {
            let outs = try await Converter.convert(dir.appendingPathComponent("table.xlsx"), to: .csv) { _ in }
            let text = try String(contentsOf: outs[0], encoding: .utf8)
            guard text.contains("\"Doe, Jane\"") else { throw TangeloError.message("round trip mismatch: \(text)") }
            return outs
        }
        await check("srt → vtt") { try await Converter.convert(srt, to: .vtt) { _ in } }
        await check("vtt → srt") { try await Converter.convert(dir.appendingPathComponent("subs.vtt"), to: .srt) { _ in } }
        await check("files → zip") { try await Converter.convert([png, md], to: .zip) { _ in } }
        await check("file → tar.gz") { try await Converter.convert([csv], to: .gzip) { _ in } }
        await check("extract zip") { [try await ArchiveKit.extract(dir.appendingPathComponent("Archive.zip"))] }

        print(failures == 0 ? "\nALL PASSED" : "\n\(failures) FAILED")
    }
}
