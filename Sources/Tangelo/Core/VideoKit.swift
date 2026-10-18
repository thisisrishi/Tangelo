import AVFoundation
import CoreImage
import Foundation

struct VideoInfo {
    var duration: Double
    var size: CGSize          // display size after orientation
    var hasAudio: Bool
    var hasVideo: Bool
    var frameRate: Float
    var bitrate: Float
}

enum VideoKit {
    static func info(_ url: URL) async throws -> VideoInfo {
        let asset = AVURLAsset(url: url)
        let duration = try await asset.load(.duration).seconds
        let v = try await asset.loadTracks(withMediaType: .video).first
        let a = try await asset.loadTracks(withMediaType: .audio).first
        var size = CGSize.zero, fps: Float = 30, rate: Float = 0
        if let v {
            let (natural, transform, f, r) = try await v.load(.naturalSize, .preferredTransform, .nominalFrameRate, .estimatedDataRate)
            let rect = CGRect(origin: .zero, size: natural).applying(transform)
            size = CGSize(width: abs(rect.width), height: abs(rect.height))
            fps = f > 0 ? f : 30
            rate = r
        }
        return VideoInfo(duration: duration, size: size, hasAudio: a != nil, hasVideo: v != nil, frameRate: fps, bitrate: rate)
    }

    static func fileType(for format: ConversionFormat) -> AVFileType {
        switch format {
        case .mov: return .mov
        case .m4v: return .m4v
        case .m4a: return .m4a
        default: return .mp4
        }
    }

    static func export(_ asset: AVAsset, to url: URL, preset: String = AVAssetExportPresetHighestQuality,
                       fileType: AVFileType = .mp4, timeRange: CMTimeRange? = nil,
                       videoComposition: AVVideoComposition? = nil, audioMix: AVAudioMix? = nil,
                       audioPitch: AVAudioTimePitchAlgorithm? = nil) async throws {
        guard let session = AVAssetExportSession(asset: asset, presetName: preset) else {
            throw TangeloError.message("This video cannot be exported with the chosen settings.")
        }
        try? FileManager.default.removeItem(at: url)
        session.outputURL = url
        session.outputFileType = session.supportedFileTypes.contains(fileType) ? fileType : session.supportedFileTypes.first
        session.shouldOptimizeForNetworkUse = true
        if let timeRange { session.timeRange = timeRange }
        if let videoComposition { session.videoComposition = videoComposition }
        if let audioMix { session.audioMix = audioMix }
        if let audioPitch { session.audioTimePitchAlgorithm = audioPitch }
        await withCheckedContinuation { (c: CheckedContinuation<Void, Never>) in
            session.exportAsynchronously { c.resume() }
        }
        if session.status != .completed {
            throw session.error ?? TangeloError.message("The video export did not complete.")
        }
    }

    /// Builds a composition that runs every frame through Core Image.
    static func filterComposition(for asset: AVAsset, renderSize: CGSize? = nil,
                                  _ handler: @escaping @Sendable (CIImage, Double) -> CIImage) async throws -> AVVideoComposition {
        let comp = try await AVMutableVideoComposition.videoComposition(with: asset) { request in
            let out = handler(request.sourceImage, request.compositionTime.seconds)
            request.finish(with: out, context: nil)
        }
        if let renderSize { comp.renderSize = CGSize(width: floor(renderSize.width / 2) * 2, height: floor(renderSize.height / 2) * 2) }
        return comp
    }

    // MARK: Frames

    static func frames(_ url: URL, at times: [Double], maxDimension: CGFloat? = nil) async throws -> [CGImage] {
        let gen = AVAssetImageGenerator(asset: AVURLAsset(url: url))
        gen.appliesPreferredTrackTransform = true
        gen.requestedTimeToleranceBefore = .zero
        gen.requestedTimeToleranceAfter = CMTime(value: 1, timescale: 60)
        if let maxDimension { gen.maximumSize = CGSize(width: maxDimension, height: maxDimension) }
        var out: [CGImage] = []
        for t in times {
            let (img, _) = try await gen.image(at: CMTime(seconds: t, preferredTimescale: 600))
            out.append(img)
        }
        return out
    }

    static func gif(from url: URL, to out: URL, fps: Double = 12, maxWidth: CGFloat = 640,
                    range: ClosedRange<Double>? = nil) async throws {
        let info = try await info(url)
        let r = range ?? 0...min(info.duration, 30)
        let count = max(1, Int((r.upperBound - r.lowerBound) * fps))
        let times = (0..<count).map { r.lowerBound + Double($0) / fps }
        let gen = AVAssetImageGenerator(asset: AVURLAsset(url: url))
        gen.appliesPreferredTrackTransform = true
        gen.maximumSize = CGSize(width: maxWidth, height: maxWidth * 4)
        gen.requestedTimeToleranceBefore = CMTime(value: 1, timescale: 30)
        gen.requestedTimeToleranceAfter = CMTime(value: 1, timescale: 30)
        var frames: [(CGImage, Double)] = []
        for t in times {
            if let (img, _) = try? await gen.image(at: CMTime(seconds: t, preferredTimescale: 600)) {
                frames.append((img, 1 / fps))
            }
        }
        try ImageIOKit.writeAnimatedGIF(frames: frames, to: out)
    }

    // MARK: Transcode (compression, target size)

    struct EncodeSettings {
        var maxHeight: CGFloat?
        var videoBitrate: Int?
        var hevc = false
        var audioBitrate = 128_000
        var includeAudio = true
    }

    /// Re-encodes with explicit bitrate and size limits using AVAssetReader/Writer.
    static func transcode(_ input: URL, to output: URL, settings: EncodeSettings, fileType: AVFileType = .mp4,
                          progress: (@Sendable (Double) -> Void)? = nil) async throws {
        let asset = AVURLAsset(url: input)
        let info = try await info(input)
        guard let vTrack = try await asset.loadTracks(withMediaType: .video).first else {
            throw TangeloError.message("This file has no playable video track.")
        }
        let aTrack = settings.includeAudio ? try await asset.loadTracks(withMediaType: .audio).first : nil
        var size = info.size
        if let mh = settings.maxHeight, size.height > mh { size = CGSize(width: size.width * mh / size.height, height: mh) }
        size = CGSize(width: max(2, floor(size.width / 2) * 2), height: max(2, floor(size.height / 2) * 2))

        try? FileManager.default.removeItem(at: output)
        let reader = try AVAssetReader(asset: asset)
        let writer = try AVAssetWriter(outputURL: output, fileType: fileType)

        // Composition output applies orientation and scaling for us.
        let comp = try await AVMutableVideoComposition.videoComposition(withPropertiesOf: asset)
        comp.renderSize = size
        let instruction = AVMutableVideoCompositionInstruction()
        instruction.timeRange = CMTimeRange(start: .zero, duration: try await asset.load(.duration))
        let layer = AVMutableVideoCompositionLayerInstruction(assetTrack: vTrack)
        let (natural, transform) = try await vTrack.load(.naturalSize, .preferredTransform)
        let oriented = CGRect(origin: .zero, size: natural).applying(transform)
        let scale = size.width / abs(oriented.width)
        var t = transform.concatenating(CGAffineTransform(translationX: -oriented.minX, y: -oriented.minY))
        t = t.concatenating(CGAffineTransform(scaleX: scale, y: scale))
        layer.setTransform(t, at: .zero)
        instruction.layerInstructions = [layer]
        comp.instructions = [instruction]

        let vOut = AVAssetReaderVideoCompositionOutput(videoTracks: [vTrack], videoSettings: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
        ])
        vOut.videoComposition = comp
        reader.add(vOut)
        var compression: [String: Any] = [AVVideoExpectedSourceFrameRateKey: info.frameRate]
        if let br = settings.videoBitrate { compression[AVVideoAverageBitRateKey] = max(br, 100_000) }
        let vIn = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: settings.hevc ? AVVideoCodecType.hevc : AVVideoCodecType.h264,
            AVVideoWidthKey: size.width, AVVideoHeightKey: size.height,
            AVVideoCompressionPropertiesKey: compression,
        ])
        vIn.expectsMediaDataInRealTime = false
        writer.add(vIn)

        var aOut: AVAssetReaderTrackOutput?
        var aIn: AVAssetWriterInput?
        if let aTrack {
            let o = AVAssetReaderTrackOutput(track: aTrack, outputSettings: [AVFormatIDKey: kAudioFormatLinearPCM])
            reader.add(o); aOut = o
            let i = AVAssetWriterInput(mediaType: .audio, outputSettings: [
                AVFormatIDKey: kAudioFormatMPEG4AAC, AVNumberOfChannelsKey: 2,
                AVSampleRateKey: 44100, AVEncoderBitRateKey: settings.audioBitrate,
            ])
            writer.add(i); aIn = i
        }
        guard reader.startReading() else { throw reader.error ?? TangeloError.message("Could not read the video.") }
        writer.startWriting()
        writer.startSession(atSourceTime: .zero)

        let duration = max(info.duration, 0.001)
        await withTaskGroup(of: Void.self) { group in
            group.addTask {
                await pump(output: vOut, input: vIn, queue: "video") { t in progress?(min(1, t / duration)) }
            }
            if let aOut, let aIn {
                group.addTask { await pump(output: aOut, input: aIn, queue: "audio", onTime: nil) }
            }
        }
        if reader.status == .failed { writer.cancelWriting(); throw reader.error ?? TangeloError.message("Reading failed.") }
        await writer.finishWriting()
        if writer.status != .completed { throw writer.error ?? TangeloError.message("Writing the video failed.") }
    }

    private static func pump(output: AVAssetReaderOutput, input: AVAssetWriterInput, queue: String,
                             onTime: ((Double) -> Void)?) async {
        await withCheckedContinuation { (c: CheckedContinuation<Void, Never>) in
            let q = DispatchQueue(label: "tangelo.pump.\(queue)")
            var finished = false
            input.requestMediaDataWhenReady(on: q) {
                guard !finished else { return }
                while input.isReadyForMoreMediaData, !finished {
                    if let sb = output.copyNextSampleBuffer() {
                        onTime?(CMSampleBufferGetPresentationTimeStamp(sb).seconds)
                        if !input.append(sb) { finished = true }
                    } else {
                        finished = true
                    }
                }
                if finished {
                    input.markAsFinished()
                    c.resume()
                }
            }
        }
    }
}
