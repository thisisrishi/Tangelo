import AppKit
import AVFoundation
import CoreImage
import PDFKit

enum VideoOps {
    static func outputFormat(for url: URL) -> ConversionFormat {
        let f = ConversionFormat.from(url)
        return [.mov, .m4v, .mp4].contains(f) ? f! : .mp4
    }

    static func trim(_ url: URL, range: ClosedRange<Double>) async throws -> URL {
        let fmt = outputFormat(for: url)
        let out = Output.url(for: url, ext: fmt.fileExtension, suffix: "trimmed")
        let tr = CMTimeRange(start: CMTime(seconds: range.lowerBound, preferredTimescale: 600),
                             end: CMTime(seconds: range.upperBound, preferredTimescale: 600))
        let asset = AVURLAsset(url: url)
        do {
            try await VideoKit.export(asset, to: out, preset: AVAssetExportPresetPassthrough, fileType: VideoKit.fileType(for: fmt), timeRange: tr)
        } catch {
            try await VideoKit.export(asset, to: out, preset: AVAssetExportPresetHighestQuality, fileType: VideoKit.fileType(for: fmt), timeRange: tr)
        }
        return out
    }

    /// `rect` is normalized with a top-left origin.
    static func crop(_ url: URL, rect: CGRect) async throws -> URL {
        let info = try await VideoKit.info(url)
        let px = CGRect(x: rect.minX * info.size.width, y: (1 - rect.maxY) * info.size.height,
                        width: rect.width * info.size.width, height: rect.height * info.size.height).integral
        guard px.width >= 2, px.height >= 2 else { throw TangeloError.message("Make the crop at least 2 pixels wide.") }
        let asset = AVURLAsset(url: url)
        let comp = try await VideoKit.filterComposition(for: asset, renderSize: px.size) { img, _ in
            img.cropped(to: px).transformed(by: CGAffineTransform(translationX: -px.minX, y: -px.minY))
        }
        let fmt = outputFormat(for: url)
        let out = Output.url(for: url, ext: fmt.fileExtension, suffix: "cropped")
        try await VideoKit.export(asset, to: out, fileType: VideoKit.fileType(for: fmt), videoComposition: comp)
        return out
    }

    static func redact(_ url: URL, rects: [RedactionRect], style: RedactionStyle) async throws -> URL {
        guard !rects.isEmpty else { throw TangeloError.message("Draw a rectangle over the area to hide.") }
        let asset = AVURLAsset(url: url)
        let comp = try await VideoKit.filterComposition(for: asset) { img, t in
            let active = rects.filter { $0.timeRange?.contains(t) ?? true }.map(\.rect)
            return active.isEmpty ? img : Redactor.apply(img, rects: active, style: style)
        }
        let fmt = outputFormat(for: url)
        let out = Output.url(for: url, ext: fmt.fileExtension, suffix: "redacted")
        try await VideoKit.export(asset, to: out, fileType: VideoKit.fileType(for: fmt), videoComposition: comp)
        return out
    }

    static func changeSpeed(_ url: URL, factor: Double) async throws -> URL {
        guard (0.25...4).contains(factor) else { throw TangeloError.message("Choose a speed between 0.25× and 4×.") }
        let asset = AVURLAsset(url: url)
        let duration = try await asset.load(.duration)
        let comp = AVMutableComposition()
        let full = CMTimeRange(start: .zero, duration: duration)
        for type in [AVMediaType.video, .audio] {
            for track in try await asset.loadTracks(withMediaType: type) {
                guard let ct = comp.addMutableTrack(withMediaType: type, preferredTrackID: kCMPersistentTrackID_Invalid) else { continue }
                try ct.insertTimeRange(full, of: track, at: .zero)
                if type == .video { ct.preferredTransform = try await track.load(.preferredTransform) }
            }
        }
        comp.scaleTimeRange(full, toDuration: CMTimeMultiplyByFloat64(duration, multiplier: 1 / factor))
        let fmt = outputFormat(for: url)
        let label = factor == floor(factor) ? "\(Int(factor))x" : String(format: "%.2gx", factor)
        let out = Output.url(for: url, ext: fmt.fileExtension, suffix: label)
        try await VideoKit.export(comp, to: out, fileType: VideoKit.fileType(for: fmt), audioPitch: .spectral)
        return out
    }

    static func mute(_ url: URL) async throws -> URL {
        let asset = AVURLAsset(url: url)
        let comp = AVMutableComposition()
        let duration = try await asset.load(.duration)
        guard let v = try await asset.loadTracks(withMediaType: .video).first,
              let ct = comp.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid) else {
            throw TangeloError.message("Every selected file must contain a video track.")
        }
        try ct.insertTimeRange(CMTimeRange(start: .zero, duration: duration), of: v, at: .zero)
        ct.preferredTransform = try await v.load(.preferredTransform)
        let fmt = outputFormat(for: url)
        let out = Output.url(for: url, ext: fmt.fileExtension, suffix: "muted")
        try await VideoKit.export(comp, to: out, preset: AVAssetExportPresetPassthrough, fileType: VideoKit.fileType(for: fmt))
        return out
    }

    /// Joins clips (or the kept ranges of one clip) into a single video sized to the first clip.
    static func join(_ segments: [(URL, CMTimeRange?)], output: URL) async throws {
        let comp = AVMutableComposition()
        guard let vTrack = comp.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid),
              let aTrack = comp.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid) else {
            throw TangeloError.message("Could not prepare the joined video.")
        }
        var cursor = CMTime.zero
        var renderSize = CGSize.zero
        var layerInstructions: [(CMTimeRange, CGAffineTransform, CGSize)] = []
        for (url, range) in segments {
            let asset = AVURLAsset(url: url)
            let dur = try await asset.load(.duration)
            let r = range ?? CMTimeRange(start: .zero, duration: dur)
            guard let v = try await asset.loadTracks(withMediaType: .video).first else {
                throw TangeloError.message("Every selected file must contain a video track.")
            }
            try vTrack.insertTimeRange(r, of: v, at: cursor)
            if let a = try await asset.loadTracks(withMediaType: .audio).first {
                try? aTrack.insertTimeRange(r, of: a, at: cursor)
            }
            let (natural, transform) = try await v.load(.naturalSize, .preferredTransform)
            let oriented = CGRect(origin: .zero, size: natural).applying(transform)
            let size = CGSize(width: abs(oriented.width), height: abs(oriented.height))
            if renderSize == .zero { renderSize = size }
            let fixed = transform.concatenating(CGAffineTransform(translationX: -oriented.minX, y: -oriented.minY))
            layerInstructions.append((CMTimeRange(start: cursor, duration: r.duration), fixed, size))
            cursor = cursor + r.duration
        }
        let videoComp = AVMutableVideoComposition()
        videoComp.renderSize = CGSize(width: floor(renderSize.width / 2) * 2, height: floor(renderSize.height / 2) * 2)
        videoComp.frameDuration = CMTime(value: 1, timescale: 30)
        videoComp.instructions = layerInstructions.map { range, t, size in
            let ins = AVMutableVideoCompositionInstruction()
            ins.timeRange = range
            let layer = AVMutableVideoCompositionLayerInstruction(assetTrack: vTrack)
            let s = min(renderSize.width / size.width, renderSize.height / size.height)
            let dx = (renderSize.width - size.width * s) / 2, dy = (renderSize.height - size.height * s) / 2
            layer.setTransform(t.concatenating(CGAffineTransform(scaleX: s, y: s)).concatenating(CGAffineTransform(translationX: dx, y: dy)), at: range.start)
            ins.layerInstructions = [layer]
            return ins
        }
        if aTrack.segments.isEmpty { comp.removeTrack(aTrack) }
        try await VideoKit.export(comp, to: output, fileType: VideoKit.fileType(for: ConversionFormat.from(output) ?? .mp4), videoComposition: videoComp)
    }

    static func joinVideos(_ urls: [URL]) async throws -> URL {
        let urls = urls.sorted { $0.lastPathComponent.localizedStandardCompare($1.lastPathComponent) == .orderedAscending }
        let out = Output.unique(in: Output.directory(for: urls[0]), base: urls[0].deletingPathExtension().lastPathComponent + " joined", ext: "mp4")
        try await join(urls.map { ($0, nil) }, output: out)
        return out
    }

    enum SplitMode: String, CaseIterable, Identifiable { case separate = "Separate clips", joinKept = "Join kept clips"; var id: String { rawValue } }

    static func split(_ url: URL, at points: [Double], removed: Set<Int>, mode: SplitMode) async throws -> [URL] {
        let info = try await VideoKit.info(url)
        let bounds = ([0] + points.sorted() + [info.duration])
        var clips: [ClosedRange<Double>] = []
        for i in 0..<(bounds.count - 1) where bounds[i + 1] - bounds[i] >= 0.05 { clips.append(bounds[i]...bounds[i + 1]) }
        let kept = clips.enumerated().filter { !removed.contains($0.offset) }
        guard !kept.isEmpty else { throw TangeloError.message("Keep at least one clip.") }
        let fmt = outputFormat(for: url)
        if mode == .joinKept {
            let out = Output.url(for: url, ext: fmt.fileExtension, suffix: "edited")
            try await join(kept.map { (url, CMTimeRange(start: CMTime(seconds: $0.element.lowerBound, preferredTimescale: 600),
                                                       end: CMTime(seconds: $0.element.upperBound, preferredTimescale: 600))) }, output: out)
            return [out]
        }
        var outs: [URL] = []
        for (i, r) in kept {
            let out = Output.url(for: url, ext: fmt.fileExtension, suffix: "clip \(i + 1)")
            let tr = CMTimeRange(start: CMTime(seconds: r.lowerBound, preferredTimescale: 600), end: CMTime(seconds: r.upperBound, preferredTimescale: 600))
            try await VideoKit.export(AVURLAsset(url: url), to: out, preset: AVAssetExportPresetHighestQuality, fileType: VideoKit.fileType(for: fmt), timeRange: tr)
            outs.append(out)
        }
        return outs
    }

    static func snapshots(_ url: URL, times: [Double]) async throws -> [URL] {
        guard !times.isEmpty else { throw TangeloError.message("Select at least one frame within the video.") }
        let images = try await VideoKit.frames(url, at: times)
        let dir: URL
        if images.count > 1 {
            dir = Output.unique(in: Output.directory(for: url), base: url.deletingPathExtension().lastPathComponent + " frames", ext: "")
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        } else { dir = Output.directory(for: url) }
        var outs: [URL] = []
        for (img, t) in zip(images, times) {
            let out = Output.unique(in: dir, base: url.deletingPathExtension().lastPathComponent + String(format: " %.3fs", t), ext: "png")
            try ImageIOKit.write(img, to: out, format: .png)
            outs.append(out)
        }
        return outs
    }

    enum VideoPreset: String, CaseIterable, Identifiable {
        case high = "High (1080p HEVC)", balanced = "Balanced (1080p)", small = "Small (720p)", tiny = "Smallest (540p)"
        var id: String { rawValue }
        var settings: VideoKit.EncodeSettings {
            switch self {
            case .high: return .init(maxHeight: 1080, videoBitrate: 5_000_000, hevc: true)
            case .balanced: return .init(maxHeight: 1080, videoBitrate: 4_000_000)
            case .small: return .init(maxHeight: 720, videoBitrate: 2_000_000, audioBitrate: 96_000)
            case .tiny: return .init(maxHeight: 540, videoBitrate: 900_000, audioBitrate: 64_000)
            }
        }
    }

    static func compress(_ url: URL, preset: VideoPreset?, targetBytes: Int?, progress: @escaping @Sendable (Double) -> Void) async throws -> URL {
        let info = try await VideoKit.info(url)
        var settings = preset?.settings ?? .init(maxHeight: 1080)
        if let target = targetBytes {
            let audioBits = info.hasAudio ? 96_000.0 : 0
            let totalBits = Double(target) * 8 * 0.95
            let videoBits = totalBits / max(info.duration, 0.1) - audioBits
            guard videoBits > 80_000 else { throw TangeloError.message("The target is too small for this duration. Choose a larger size.") }
            settings.videoBitrate = Int(videoBits)
            settings.audioBitrate = 96_000
            settings.maxHeight = videoBits < 1_000_000 ? 540 : videoBits < 2_500_000 ? 720 : 1080
        }
        if let br = settings.videoBitrate, info.bitrate > 0, Float(br) > info.bitrate { settings.videoBitrate = Int(info.bitrate * 0.8) }
        let out = Output.url(for: url, ext: "mp4", suffix: "compressed")
        try await VideoKit.transcode(url, to: out, settings: settings, progress: progress)
        return out
    }
}

// MARK: - Audio

enum AudioOps {
    /// Audio-only output format matching the source when we can write it.
    static func outputFormat(for url: URL) -> ConversionFormat {
        let f = ConversionFormat.from(url) ?? .m4a
        if [.wav, .aiff, .flac, .m4a].contains(f) { return f }
        if [.mp3, .ogg, .opus, .wma].contains(f), Helpers.ffmpeg != nil { return f }
        return .m4a
    }

    /// Writes processed audio; for video inputs the processed audio replaces the original track.
    static func save(_ pcm: PCMAudio, source: URL, suffix: String) async throws -> URL {
        if FileCategory.of(source) == .video {
            let tmp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".m4a")
            try await pcm.write(to: tmp, format: .m4a, bitrate: 192_000)
            defer { try? FileManager.default.removeItem(at: tmp) }
            let fmt = VideoOps.outputFormat(for: source)
            let out = Output.url(for: source, ext: fmt.fileExtension, suffix: suffix)
            try await replaceAudio(in: source, with: tmp, output: out)
            return out
        }
        let fmt = outputFormat(for: source)
        let out = Output.url(for: source, ext: fmt.fileExtension, suffix: suffix)
        try await pcm.write(to: out, format: fmt, bitrate: ConversionPreferences.audioBitrate)
        return out
    }

    static func replaceAudio(in video: URL, with audio: URL, output: URL) async throws {
        let v = AVURLAsset(url: video), a = AVURLAsset(url: audio)
        let comp = AVMutableComposition()
        let duration = try await v.load(.duration)
        if let vt = try await v.loadTracks(withMediaType: .video).first,
           let ct = comp.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid) {
            try ct.insertTimeRange(CMTimeRange(start: .zero, duration: duration), of: vt, at: .zero)
            ct.preferredTransform = try await vt.load(.preferredTransform)
        }
        if let at = try await a.loadTracks(withMediaType: .audio).first,
           let ct = comp.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid) {
            let ad = try await a.load(.duration)
            try ct.insertTimeRange(CMTimeRange(start: .zero, duration: CMTimeMinimum(ad, duration)), of: at, at: .zero)
        }
        try await VideoKit.export(comp, to: output, preset: AVAssetExportPresetPassthrough, fileType: VideoKit.fileType(for: ConversionFormat.from(output) ?? .mp4))
    }

    static func trim(_ url: URL, range: ClosedRange<Double>, trimSilence: Bool, thresholdDB: Double) async throws -> URL {
        if FileCategory.of(url) == .video { return try await VideoOps.trim(url, range: range) }
        var pcm = try await PCMAudio.read(url).slice(from: range.lowerBound, to: range.upperBound)
        if trimSilence { pcm = pcm.trimmingSilence(thresholdDB: thresholdDB) }
        return try await save(pcm, source: url, suffix: "trimmed")
    }

    static func normalize(_ url: URL, targetDB: Double, peakDB: Double) async throws -> URL {
        try await save(try await PCMAudio.read(url).normalized(targetDB: targetDB, peakCeilingDB: peakDB), source: url, suffix: "normalized")
    }

    static func channels(_ url: URL, count: Int, left: Float, right: Float) async throws -> URL {
        let pcm = try await PCMAudio.read(url).remixed(channelCount: count, leftVolume: left, rightVolume: right)
        return try await save(pcm, source: url, suffix: count == 1 ? "mono" : "stereo")
    }

    static func bleep(_ url: URL, ranges: [ClosedRange<Double>], tone: Bool) async throws -> URL {
        guard !ranges.isEmpty else { throw TangeloError.message("Add at least one bleep range.") }
        return try await save(try await PCMAudio.read(url).bleeped(ranges: ranges, tone: tone), source: url, suffix: "bleeped")
    }

    enum AudioPreset: String, CaseIterable, Identifiable {
        case high = "High (192 kbps)", balanced = "Balanced (128 kbps)", small = "Small (96 kbps)", voice = "Voice (64 kbps mono)"
        var id: String { rawValue }
        var bitrate: Int { switch self { case .high: return 192_000; case .balanced: return 128_000; case .small: return 96_000; case .voice: return 64_000 } }
    }

    static func compress(_ url: URL, preset: AudioPreset?, targetBytes: Int?) async throws -> URL {
        var pcm = try await PCMAudio.read(url)
        var bitrate = preset?.bitrate ?? 128_000
        if let t = targetBytes { bitrate = max(32_000, min(320_000, Int(Double(t) * 8 * 0.97 / max(pcm.duration, 0.1)))) }
        if preset == .voice || bitrate < 64_000 { pcm = pcm.remixed(channelCount: 1, leftVolume: 1, rightVolume: 1) }
        let fmt: ConversionFormat = Helpers.ffmpeg != nil && ConversionFormat.from(url) == .mp3 ? .mp3 : .m4a
        let out = Output.url(for: url, ext: fmt.fileExtension, suffix: "compressed")
        try await pcm.write(to: out, format: fmt, bitrate: bitrate)
        return out
    }
}

// MARK: - Audio → video visualizer

enum VisualizerStyle: String, CaseIterable, Identifiable {
    case bars = "Bars", wave = "Wave", circle = "Circle", mirror = "Mirror"
    var id: String { rawValue }
}

struct VisualizerOptions {
    var style: VisualizerStyle = .bars
    var accent: NSColor = NSColor(srgbRed: 1, green: 0.32, blue: 0, alpha: 1)
    var background: NSColor = NSColor(white: 0.08, alpha: 1)
    var image: URL? = nil
    var size = CGSize(width: 1280, height: 720)
    var fps: Int32 = 30
    var title: String = ""
}

enum Visualizer {
    static func drawFrame(ctx: CGContext, size: CGSize, levels: [Float], options: VisualizerOptions, artwork: CGImage?, progress: Double) {
        ctx.setFillColor(options.background.cgColor)
        ctx.fill(CGRect(origin: .zero, size: size))
        if let artwork {
            let s = max(size.width / CGFloat(artwork.width), size.height / CGFloat(artwork.height))
            let w = CGFloat(artwork.width) * s, h = CGFloat(artwork.height) * s
            ctx.setAlpha(0.55)
            ctx.draw(artwork, in: CGRect(x: (size.width - w) / 2, y: (size.height - h) / 2, width: w, height: h))
            ctx.setAlpha(1)
        }
        ctx.setFillColor(options.accent.cgColor)
        ctx.setStrokeColor(options.accent.cgColor)
        let n = levels.count
        let mid = size.height / 2
        switch options.style {
        case .bars, .mirror:
            let bw = size.width * 0.8 / CGFloat(n)
            for (i, l) in levels.enumerated() {
                let h = max(4, CGFloat(l) * size.height * 0.6)
                let x = size.width * 0.1 + CGFloat(i) * bw
                let y = options.style == .mirror ? mid - h / 2 : size.height * 0.2
                ctx.addPath(CGPath(roundedRect: CGRect(x: x + bw * 0.15, y: y, width: bw * 0.7, height: h), cornerWidth: bw * 0.3, cornerHeight: bw * 0.3, transform: nil))
            }
            ctx.fillPath()
        case .wave:
            ctx.setLineWidth(4); ctx.setLineJoin(.round)
            for (i, l) in levels.enumerated() {
                let x = size.width * 0.05 + CGFloat(i) / CGFloat(max(n - 1, 1)) * size.width * 0.9
                let y = mid + CGFloat(l) * size.height * 0.35 * (i % 2 == 0 ? 1 : -1)
                i == 0 ? ctx.move(to: CGPoint(x: x, y: y)) : ctx.addLine(to: CGPoint(x: x, y: y))
            }
            ctx.strokePath()
        case .circle:
            let r = min(size.width, size.height) * 0.22
            let c = CGPoint(x: size.width / 2, y: mid)
            ctx.setLineWidth(max(2, r * 0.04)); ctx.setLineCap(.round)
            for (i, l) in levels.enumerated() {
                let a = CGFloat(i) / CGFloat(n) * 2 * .pi
                let len = r * 0.15 + CGFloat(l) * r * 0.9
                ctx.move(to: CGPoint(x: c.x + cos(a) * r, y: c.y + sin(a) * r))
                ctx.addLine(to: CGPoint(x: c.x + cos(a) * (r + len), y: c.y + sin(a) * (r + len)))
            }
            ctx.strokePath()
        }
        // Progress bar
        ctx.setFillColor(options.accent.withAlphaComponent(0.35).cgColor)
        ctx.fill(CGRect(x: 0, y: 0, width: size.width, height: 6))
        ctx.setFillColor(options.accent.cgColor)
        ctx.fill(CGRect(x: 0, y: 0, width: size.width * progress, height: 6))
        if !options.title.isEmpty {
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: false)
            let s = NSAttributedString(string: options.title, attributes: [.font: NSFont.systemFont(ofSize: size.height * 0.05, weight: .semibold), .foregroundColor: NSColor.white])
            s.draw(at: CGPoint(x: size.width * 0.06, y: size.height * 0.85))
            NSGraphicsContext.restoreGraphicsState()
        }
    }

    static func render(_ audioURL: URL, options: VisualizerOptions, progress: @escaping @Sendable (Double) -> Void) async throws -> URL {
        let pcm = try await PCMAudio.read(audioURL)
        let out = Output.url(for: audioURL, ext: "mp4", suffix: "visualizer")
        let videoOnly = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".mp4")
        defer { try? FileManager.default.removeItem(at: videoOnly) }
        let writer = try AVAssetWriter(outputURL: videoOnly, fileType: .mp4)
        let input = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264, AVVideoWidthKey: options.size.width, AVVideoHeightKey: options.size.height,
        ])
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: input, sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: options.size.width, kCVPixelBufferHeightKey as String: options.size.height,
        ])
        writer.add(input)
        writer.startWriting(); writer.startSession(atSourceTime: .zero)
        let artwork = options.image.flatMap { try? ImageIOKit.load($0) }
        let frames = Int(pcm.duration * Double(options.fps))
        let barCount = options.style == .circle ? 96 : 64
        let samplesPerFrame = Int(pcm.sampleRate) / Int(options.fps)
        var smoothed = [Float](repeating: 0, count: barCount)
        let mono = pcm.channels[0]
        for f in 0..<frames {
            while !input.isReadyForMoreMediaData { try await Task.sleep(nanoseconds: 2_000_000) }
            let start = f * samplesPerFrame
            var levels = [Float](repeating: 0, count: barCount)
            let window = max(1, samplesPerFrame / barCount)
            for b in 0..<barCount {
                var m: Float = 0
                let a = start + b * window
                for i in stride(from: a, to: min(a + window, mono.count), by: 4) { m = max(m, abs(mono[i])) }
                smoothed[b] = max(m, smoothed[b] * 0.82)
                levels[b] = min(1, sqrt(smoothed[b]))
            }
            guard let pool = adaptor.pixelBufferPool else { break }
            var pb: CVPixelBuffer?
            CVPixelBufferPoolCreatePixelBuffer(nil, pool, &pb)
            guard let pb else { continue }
            CVPixelBufferLockBaseAddress(pb, [])
            if let ctx = CGContext(data: CVPixelBufferGetBaseAddress(pb), width: Int(options.size.width), height: Int(options.size.height),
                                   bitsPerComponent: 8, bytesPerRow: CVPixelBufferGetBytesPerRow(pb), space: CGColorSpaceCreateDeviceRGB(),
                                   bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue) {
                drawFrame(ctx: ctx, size: options.size, levels: levels, options: options, artwork: artwork, progress: Double(f) / Double(max(frames, 1)))
            }
            CVPixelBufferUnlockBaseAddress(pb, [])
            adaptor.append(pb, withPresentationTime: CMTime(value: CMTimeValue(f), timescale: options.fps))
            if f % 30 == 0 { progress(Double(f) / Double(max(frames, 1)) * 0.9) }
        }
        input.markAsFinished()
        await writer.finishWriting()
        if writer.status != .completed { throw writer.error ?? TangeloError.message("Could not create the visualization.") }
        try await AudioOps.replaceAudio(in: videoOnly, with: audioURL, output: out)
        return out
    }
}

// MARK: - Metadata

struct MetadataField: Identifiable, Hashable {
    var id: String { group + "." + key }
    var group: String
    var key: String
    var value: String
}

enum MetadataOps {
    static func read(_ url: URL) async -> [MetadataField] {
        switch FileCategory.of(url) {
        case .image, .vector:
            var out: [MetadataField] = []
            func walk(_ dict: [CFString: Any], group: String) {
                for (k, v) in dict {
                    if let sub = v as? [CFString: Any] { walk(sub, group: (k as String).replacingOccurrences(of: "{", with: "").replacingOccurrences(of: "}", with: "")) }
                    else { out.append(MetadataField(group: group, key: k as String, value: "\(v)")) }
                }
            }
            walk(ImageIOKit.properties(url), group: "File")
            return out.sorted { ($0.group, $0.key) < ($1.group, $1.key) }
        case .video, .audio:
            let asset = AVURLAsset(url: url)
            var out: [MetadataField] = []
            if let items = try? await asset.load(.metadata) {
                for item in items {
                    let key = item.commonKey?.rawValue ?? item.identifier?.rawValue ?? "item"
                    var value = ""
                    if let s = try? await item.load(.stringValue) { value = s }
                    if value.isEmpty, let v = try? await item.load(.value) { value = "\(v)" }
                    out.append(MetadataField(group: item.keySpace?.rawValue ?? "Media", key: key, value: value))
                }
            }
            if let info = try? await VideoKit.info(url) {
                out.append(MetadataField(group: "Stream", key: "Duration", value: String(format: "%.2f s", info.duration)))
                if info.hasVideo { out.append(MetadataField(group: "Stream", key: "Dimensions", value: "\(Int(info.size.width))×\(Int(info.size.height))")) }
                if info.bitrate > 0 { out.append(MetadataField(group: "Stream", key: "Video bitrate", value: "\(Int(info.bitrate / 1000)) kbps")) }
            }
            return out
        case .pdf:
            guard let doc = PDFDocument(url: url) else { return [] }
            return (doc.documentAttributes ?? [:]).map { MetadataField(group: "PDF", key: "\($0.key)", value: "\($0.value)") } +
                [MetadataField(group: "PDF", key: "Pages", value: "\(doc.pageCount)")]
        default: return []
        }
    }

    /// Strips metadata. Images are cleaned losslessly when ImageIO supports it.
    static func removeAll(_ url: URL) async throws -> URL {
        switch FileCategory.of(url) {
        case .image:
            let ext = url.pathExtension.isEmpty ? "png" : url.pathExtension
            let out = Output.url(for: url, ext: ext, suffix: "clean")
            guard let src = CGImageSourceCreateWithURL(url as CFURL, nil), let type = CGImageSourceGetType(src),
                  let dest = CGImageDestinationCreateWithURL(out as CFURL, type, CGImageSourceGetCount(src), nil) else {
                throw TangeloError.message("Could not read the image.")
            }
            let empty = CGImageMetadataCreateMutable()
            let opts: [CFString: Any] = [kCGImageDestinationMetadata: empty, kCGImageDestinationMergeMetadata: false,
                                         kCGImageMetadataShouldExcludeGPS: true, kCGImageMetadataShouldExcludeXMP: true]
            var err: Unmanaged<CFError>?
            if !CGImageDestinationCopyImageSource(dest, src, opts as CFDictionary, &err) {
                // Fallback: re-encode at high quality without metadata dictionaries.
                try? FileManager.default.removeItem(at: out)
                let img = try ImageIOKit.load(url)
                try ImageIOKit.write(img, to: out, format: ConversionFormat.from(url) ?? .png, quality: 0.95, metadata: [:])
            }
            return out
        case .video, .audio:
            let asset = AVURLAsset(url: url)
            let fmt: ConversionFormat = FileCategory.of(url) == .audio ? .m4a : VideoOps.outputFormat(for: url)
            let out = Output.url(for: url, ext: fmt.fileExtension, suffix: "clean")
            guard let s = AVAssetExportSession(asset: asset, presetName: AVAssetExportPresetPassthrough) else { throw TangeloError.message("Could not export.") }
            s.outputURL = out
            s.outputFileType = s.supportedFileTypes.contains(VideoKit.fileType(for: fmt)) ? VideoKit.fileType(for: fmt) : s.supportedFileTypes.first
            s.metadata = []
            s.metadataItemFilter = .forSharing()
            await withCheckedContinuation { (c: CheckedContinuation<Void, Never>) in s.exportAsynchronously { c.resume() } }
            if s.status != .completed { throw s.error ?? TangeloError.message("Could not remove metadata.") }
            return out
        case .pdf:
            let doc = try PDFKitTools.open(url)
            doc.documentAttributes = [:]
            let out = Output.url(for: url, ext: "pdf", suffix: "clean")
            guard doc.write(to: out) else { throw TangeloError.message("Could not write the PDF.") }
            return out
        default:
            throw TangeloError.unsupported("This file type has no removable metadata.")
        }
    }

    /// Updates common descriptive fields on an image without recompressing.
    static func updateImage(_ url: URL, title: String?, artist: String?, copyright: String?, description: String?, removeGPS: Bool) throws -> URL {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil), let type = CGImageSourceGetType(src) else { throw TangeloError.message("Could not read the image.") }
        let meta = (CGImageSourceCopyMetadataAtIndex(src, 0, nil)).flatMap { CGImageMetadataCreateMutableCopy($0) } ?? CGImageMetadataCreateMutable()
        func set(_ path: String, _ value: String?) {
            guard let value, !value.isEmpty else { return }
            CGImageMetadataSetValueWithPath(meta, nil, path as CFString, value as CFString)
        }
        set("dc:title", title); set("dc:creator", artist); set("dc:rights", copyright); set("dc:description", description)
        set("tiff:Artist", artist); set("tiff:Copyright", copyright); set("tiff:ImageDescription", description)
        let out = Output.url(for: url, ext: url.pathExtension, suffix: "edited")
        guard let dest = CGImageDestinationCreateWithURL(out as CFURL, type, 1, nil) else { throw TangeloError.message("Could not save the edited image.") }
        var opts: [CFString: Any] = [kCGImageDestinationMetadata: meta, kCGImageDestinationMergeMetadata: true]
        if removeGPS { opts[kCGImageMetadataShouldExcludeGPS] = true }
        guard CGImageDestinationCopyImageSource(dest, src, opts as CFDictionary, nil) else { throw TangeloError.message("This metadata field is not supported for this format.") }
        return out
    }
}
