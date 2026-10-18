import AVFoundation
import Foundation

/// Decoded audio held as de-interleaved float channels. All audio tools work on this.
struct PCMAudio {
    var channels: [[Float]]
    var sampleRate: Double

    var frameCount: Int { channels.first?.count ?? 0 }
    var duration: Double { Double(frameCount) / sampleRate }

    static func read(_ url: URL, timeRange: CMTimeRange? = nil) async throws -> PCMAudio {
        let asset = AVURLAsset(url: url)
        guard let track = try await asset.loadTracks(withMediaType: .audio).first else {
            throw TangeloError.message("This file has no playable audio track.")
        }
        let descs = try await track.load(.formatDescriptions)
        var sampleRate = 44100.0, channelCount = 2
        if let d = descs.first, let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(d)?.pointee {
            sampleRate = asbd.mSampleRate > 0 ? asbd.mSampleRate : 44100
            channelCount = max(1, min(Int(asbd.mChannelsPerFrame), 8))
        }
        let reader = try AVAssetReader(asset: asset)
        if let timeRange { reader.timeRange = timeRange }
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVLinearPCMBitDepthKey: 32,
            AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsNonInterleaved: false,
            AVLinearPCMIsBigEndianKey: false,
            AVSampleRateKey: sampleRate,
            AVNumberOfChannelsKey: channelCount,
        ])
        reader.add(output)
        guard reader.startReading() else { throw reader.error ?? TangeloError.message("Could not read the audio.") }
        var chans = Array(repeating: [Float](), count: channelCount)
        while let sb = output.copyNextSampleBuffer() {
            guard let block = CMSampleBufferGetDataBuffer(sb) else { continue }
            var length = 0
            var ptr: UnsafeMutablePointer<Int8>?
            CMBlockBufferGetDataPointer(block, atOffset: 0, lengthAtOffsetOut: nil, totalLengthOut: &length, dataPointerOut: &ptr)
            guard let ptr else { continue }
            let count = length / MemoryLayout<Float>.size
            ptr.withMemoryRebound(to: Float.self, capacity: count) { f in
                let frames = count / channelCount
                for c in 0..<channelCount {
                    chans[c].reserveCapacity(chans[c].count + frames)
                    for i in 0..<frames { chans[c].append(f[i * channelCount + c]) }
                }
            }
        }
        if reader.status == .failed { throw reader.error ?? TangeloError.message("Could not decode the audio.") }
        return PCMAudio(channels: chans, sampleRate: sampleRate)
    }

    /// Writes wav/aiff/flac/m4a natively; mp3/ogg/opus/wma through ffmpeg.
    func write(to url: URL, format: ConversionFormat, bitrate: Int = 192_000) async throws {
        switch format {
        case .wav, .aiff, .flac, .m4a:
            try writeNative(to: url, format: format, bitrate: bitrate)
        case .mp3, .ogg, .opus, .wma:
            let ffmpeg = try Helpers.requireFFmpeg()
            let tmp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".wav")
            defer { try? FileManager.default.removeItem(at: tmp) }
            try writeNative(to: tmp, format: .wav, bitrate: bitrate)
            try await Shell.check(ffmpeg, ["-y", "-v", "error", "-i", tmp.path] + FFmpeg.audioArgs(format, bitrate: bitrate) + [url.path])
        default:
            throw TangeloError.unsupported("\(format.label) is not an audio format.")
        }
    }

    private func writeNative(to url: URL, format: ConversionFormat, bitrate: Int) throws {
        let ch = max(1, channels.count)
        var settings: [String: Any] = [AVSampleRateKey: sampleRate, AVNumberOfChannelsKey: ch]
        switch format {
        case .wav, .aiff:
            settings[AVFormatIDKey] = kAudioFormatLinearPCM
            settings[AVLinearPCMBitDepthKey] = 16
            settings[AVLinearPCMIsFloatKey] = false
            settings[AVLinearPCMIsBigEndianKey] = format == .aiff
        case .flac:
            settings[AVFormatIDKey] = kAudioFormatFLAC
        default:
            settings[AVFormatIDKey] = kAudioFormatMPEG4AAC
            settings[AVEncoderBitRateKey] = min(bitrate, 320_000)
            if sampleRate > 48000 { settings[AVSampleRateKey] = 48000.0 }
        }
        try? FileManager.default.removeItem(at: url)
        guard let fmt = AVAudioFormat(standardFormatWithSampleRate: sampleRate, channels: AVAudioChannelCount(ch)) else {
            throw TangeloError.message("Choose valid audio channel settings.")
        }
        let file = try AVAudioFile(forWriting: url, settings: settings, commonFormat: .pcmFormatFloat32, interleaved: false)
        let chunk = 32768
        var offset = 0
        while offset < frameCount {
            let n = min(chunk, frameCount - offset)
            guard let buf = AVAudioPCMBuffer(pcmFormat: fmt, frameCapacity: AVAudioFrameCount(n)) else { break }
            buf.frameLength = AVAudioFrameCount(n)
            for c in 0..<ch {
                let src = channels[min(c, channels.count - 1)]
                src.withUnsafeBufferPointer { p in
                    buf.floatChannelData![c].update(from: p.baseAddress! + offset, count: n)
                }
            }
            try file.write(from: buf)
            offset += n
        }
    }

    // MARK: - Processing

    func slice(from start: Double, to end: Double) -> PCMAudio {
        let a = max(0, min(frameCount, Int(start * sampleRate)))
        let b = max(a, min(frameCount, Int(end * sampleRate)))
        return PCMAudio(channels: channels.map { Array($0[a..<b]) }, sampleRate: sampleRate)
    }

    var peak: Float { channels.map { $0.map(abs).max() ?? 0 }.max() ?? 0 }

    var rmsDB: Double {
        var sum = 0.0, n = 0
        for c in channels { for s in c { sum += Double(s * s) }; n += c.count }
        let rms = sqrt(sum / Double(max(n, 1)))
        return 20 * log10(max(rms, 1e-9))
    }

    /// Simple loudness normalization: gain to reach the target RMS level, limited by a true-peak ceiling.
    func normalized(targetDB: Double, peakCeilingDB: Double = -1) -> PCMAudio {
        let gainDB = targetDB - rmsDB
        var gain = Float(pow(10, gainDB / 20))
        let ceiling = Float(pow(10, peakCeilingDB / 20))
        if peak * gain > ceiling { gain = ceiling / max(peak, 1e-9) }
        return PCMAudio(channels: channels.map { $0.map { $0 * gain } }, sampleRate: sampleRate)
    }

    /// Drops leading/trailing audio under the threshold.
    func trimmingSilence(thresholdDB: Double) -> PCMAudio {
        let t = Float(pow(10, thresholdDB / 20))
        func loud(_ i: Int) -> Bool { channels.contains { abs($0[i]) > t } }
        var a = 0, b = frameCount - 1
        while a < b, !loud(a) { a += 1 }
        while b > a, !loud(b) { b -= 1 }
        return PCMAudio(channels: channels.map { Array($0[a...max(a, b)]) }, sampleRate: sampleRate)
    }

    func remixed(channelCount: Int, leftVolume: Float, rightVolume: Float) -> PCMAudio {
        let l = channels.first ?? []
        let r = channels.count > 1 ? channels[1] : l
        if channelCount == 1 {
            let mono = zip(l, r).map { ($0 * leftVolume + $1 * rightVolume) / max(1, (leftVolume > 0 ? 1 : 0) + (rightVolume > 0 ? 1 : 0)) }
            return PCMAudio(channels: [mono], sampleRate: sampleRate)
        }
        return PCMAudio(channels: [l.map { $0 * leftVolume }, r.map { $0 * rightVolume }], sampleRate: sampleRate)
    }

    /// Replaces ranges with a 1 kHz tone (or silence) — audio redaction.
    func bleeped(ranges: [ClosedRange<Double>], tone: Bool = true) -> PCMAudio {
        var out = channels
        for r in ranges {
            let a = max(0, Int(r.lowerBound * sampleRate)), b = min(frameCount, Int(r.upperBound * sampleRate))
            guard a < b else { continue }
            for c in out.indices {
                for i in a..<b {
                    out[c][i] = tone ? Float(sin(2 * .pi * 1000 * Double(i) / sampleRate) * 0.3) : 0
                }
            }
        }
        return PCMAudio(channels: out, sampleRate: sampleRate)
    }

    /// Peak envelope with `buckets` points, used for waveform drawing.
    func envelope(buckets: Int) -> [Float] {
        guard frameCount > 0, buckets > 0 else { return [] }
        let step = max(1, frameCount / buckets)
        return (0..<buckets).map { b in
            let a = b * step, e = min(frameCount, a + step)
            guard a < e else { return 0 }
            var m: Float = 0
            for c in channels { for i in stride(from: a, to: e, by: max(1, step / 64)) { m = max(m, abs(c[i])) } }
            return m
        }
    }
}

enum FFmpeg {
    static func audioArgs(_ format: ConversionFormat, bitrate: Int = 192_000) -> [String] {
        let k = "\(bitrate / 1000)k"
        switch format {
        case .mp3: return ["-c:a", "libmp3lame", "-b:a", k]
        case .ogg: return ["-c:a", "libvorbis", "-b:a", k]
        case .opus: return ["-c:a", "libopus", "-b:a", "\(min(bitrate, 256_000) / 1000)k"]
        case .wma: return ["-c:a", "wmav2", "-b:a", k]
        case .flac: return ["-c:a", "flac"]
        case .wav: return ["-c:a", "pcm_s16le"]
        case .aiff: return ["-c:a", "pcm_s16be"]
        case .m4a: return ["-c:a", "aac", "-b:a", k]
        default: return []
        }
    }

    static func videoArgs(_ format: ConversionFormat) -> [String] {
        switch format {
        case .webm: return ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-c:a", "libopus"]
        case .mkv: return ["-c:v", "libx264", "-crf", "20", "-preset", "medium", "-c:a", "aac"]
        case .avi: return ["-c:v", "mpeg4", "-q:v", "3", "-c:a", "libmp3lame"]
        case .wmv: return ["-c:v", "wmv2", "-b:v", "4M", "-c:a", "wmav2"]
        default: return ["-c:v", "libx264", "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart"]
        }
    }

    static func convert(_ input: URL, to output: URL, args: [String]) async throws {
        let ff = try Helpers.requireFFmpeg()
        try await Shell.check(ff, ["-y", "-v", "error", "-i", input.path] + args + [output.path])
    }
}
