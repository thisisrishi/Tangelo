import AppKit
import AVKit
import SwiftUI

// MARK: - Trim (video & audio)

struct TrimEditor: View {
    let url: URL
    @StateObject private var clock: PlayerClock
    @StateObject private var wave: WaveformLoader
    @State private var range: ClosedRange<Double> = 0...1
    @State private var trimSilence = false
    @State private var threshold = -45.0
    private let isVideo: Bool

    init(url: URL) {
        self.url = url
        _clock = StateObject(wrappedValue: PlayerClock(url: url))
        _wave = StateObject(wrappedValue: WaveformLoader(url: url))
        isVideo = FileCategory.of(url) == .video
    }

    var body: some View {
        EditorScaffold(tool: isVideo ? .trimVideo : .trimAudio, primary: "Save Trimmed Copy", canApply: clock.duration > 0, apply: apply) {
            VStack(spacing: 12) {
                if isVideo { PlayerView(player: clock.player).clipShape(RoundedRectangle(cornerRadius: 10)) }
                ZStack {
                    WaveformView(levels: wave.levels, highlight: clock.duration > 0 ? [range.lowerBound / clock.duration...range.upperBound / clock.duration] : [])
                        .frame(height: isVideo ? 50 : 140)
                    if wave.levels.isEmpty && !wave.failed { ProgressView("Loading waveform").controlSize(.small) }
                }
                RangeSlider(range: $range, bounds: 0...max(clock.duration, 0.1), playhead: clock.time) { clock.seek($0) }
                HStack {
                    TimeField(label: "Start", value: Binding(get: { range.lowerBound }, set: { range = min($0, range.upperBound - 0.05)...range.upperBound }))
                    TimeField(label: "End", value: Binding(get: { range.upperBound }, set: { range = range.lowerBound...max($0, range.lowerBound + 0.05) }))
                    Text("Duration \(Fmt.time(range.upperBound - range.lowerBound))").foregroundStyle(.secondary).monospacedDigit()
                    Spacer()
                    Button { clock.playRange(range) } label: { Label("Play Selected Range", systemImage: "play.fill") }
                }
                if !isVideo {
                    HStack {
                        Toggle("Trim silent ends automatically", isOn: $trimSilence)
                        if trimSilence { Slider(value: $threshold, in: -70 ... -20).frame(width: 140); Text("\(Int(threshold)) dB").monospacedDigit() }
                        Spacer()
                    }
                }
            }
        } options: { EmptyView() }
        .onChange(of: clock.duration) { range = 0...clock.duration }
    }

    private func apply() {
        let (r, ts, th) = (range, trimSilence, threshold)
        let u = url
        JobRunner.shared.run("Trimming \(u.lastPathComponent)", inputs: [u]) { _ in
            [try await AudioOps.trim(u, range: r, trimSilence: ts, thresholdDB: th)]
        }
    }
}

// MARK: - Frame grabber shared by crop/redact

@MainActor
final class FramePreview: ObservableObject {
    @Published var image: CGImage?
    let url: URL
    private var task: Task<Void, Never>?
    init(url: URL) { self.url = url; load(at: 0) }
    func load(at t: Double) {
        task?.cancel()
        task = Task {
            try? await Task.sleep(nanoseconds: 60_000_000)
            guard !Task.isCancelled else { return }
            if let img = try? await VideoKit.frames(url, at: [t], maxDimension: 1280).first { self.image = img }
        }
    }
}

struct CropVideoEditor: View {
    let url: URL
    @StateObject private var frame: FramePreview
    @StateObject private var clock: PlayerClock
    @State private var rects: [RedactionRect] = [RedactionRect(rect: CGRect(x: 0.1, y: 0.1, width: 0.8, height: 0.8))]
    @State private var selected: UUID?
    @State private var time = 0.0

    init(url: URL) {
        self.url = url
        _frame = StateObject(wrappedValue: FramePreview(url: url))
        _clock = StateObject(wrappedValue: PlayerClock(url: url))
    }

    var body: some View {
        EditorScaffold(tool: .cropVideo, primary: "Save Cropped Copy", canApply: rects.first != nil, apply: apply) {
            VStack {
                if let img = frame.image {
                    AspectCanvas(aspect: CGFloat(img.width) / CGFloat(img.height)) {
                        Image(nsImage: NSImage(cg: img)).resizable()
                    } overlay: {
                        RectEditorOverlay(rects: $rects, selected: $selected, single: true, dimOutside: true)
                    }
                } else { ProgressView("Loading video").frame(maxHeight: .infinity) }
                Slider(value: $time, in: 0...max(clock.duration, 0.1)) { Text("Preview") }
                    .onChange(of: time) { frame.load(at: time) }
            }
        } options: {
            if let r = rects.first?.rect, let s = clock.info?.size {
                Text("\(Int(r.width * s.width)) × \(Int(r.height * s.height)) px").monospacedDigit().foregroundStyle(.secondary)
            }
        }
    }

    private func apply() {
        guard let r = rects.first?.rect else { return }
        let u = url
        JobRunner.shared.run("Cropping \(u.lastPathComponent)", inputs: [u]) { _ in [try await VideoOps.crop(u, rect: r)] }
    }
}

struct RedactVideoEditor: View {
    let url: URL
    @StateObject private var frame: FramePreview
    @StateObject private var clock: PlayerClock
    @State private var rects: [RedactionRect] = []
    @State private var selected: UUID?
    @State private var time = 0.0
    @State private var style: RedactionStyle = .blur
    @State private var range: ClosedRange<Double> = 0...1

    init(url: URL) {
        self.url = url
        _frame = StateObject(wrappedValue: FramePreview(url: url))
        _clock = StateObject(wrappedValue: PlayerClock(url: url))
    }

    var body: some View {
        EditorScaffold(tool: .redactVideo, primary: "Save Redacted Copy", canApply: !rects.isEmpty, apply: apply) {
            VStack(spacing: 10) {
                HStack {
                    Picker("Style", selection: $style) { ForEach(RedactionStyle.allCases) { Text($0.rawValue).tag($0) } }.pickerStyle(.segmented).frame(width: 260)
                    Spacer()
                    Button("Detect Faces in Frame") {
                        if let img = frame.image, let found = try? Redactor.detect(in: img) {
                            rects += found.map { RedactionRect(rect: $0, timeRange: range) }
                        }
                    }
                    Button { rects.removeAll { $0.id == selected } } label: { Image(systemName: "trash") }.disabled(selected == nil)
                }
                if let img = frame.image {
                    AspectCanvas(aspect: CGFloat(img.width) / CGFloat(img.height)) {
                        Image(nsImage: NSImage(cg: ImageIOKit.cgImage(from: Redactor.apply(CIImage(cgImage: img), rects: active, style: style)) ?? img)).resizable()
                    } overlay: {
                        RectEditorOverlay(rects: $rects, selected: $selected)
                    }
                } else { ProgressView().frame(maxHeight: .infinity) }
                Slider(value: $time, in: 0...max(clock.duration, 0.1)) { Text(Fmt.time(time)).monospacedDigit() }
                    .onChange(of: time) { frame.load(at: time) }
                HStack {
                    Text("Selected rectangle shows from").foregroundStyle(.secondary)
                    RangeSlider(range: Binding(get: { selectedRange }, set: { setRange($0) }), bounds: 0...max(clock.duration, 0.1), playhead: time) { time = $0 }
                    Button("Show for Entire Video") { setRange(0...clock.duration) }
                }
            }
        } options: { Text("Draw rectangles to cover areas.").font(.caption).foregroundStyle(.secondary) }
        .onChange(of: clock.duration) { range = 0...clock.duration }
        .onChange(of: rects.count) { if let last = rects.last, last.timeRange == nil, let i = rects.indices.last { rects[i].timeRange = 0...clock.duration } }
    }

    private var active: [CGRect] { rects.filter { $0.timeRange?.contains(time) ?? true }.map(\.rect) }
    private var selectedRange: ClosedRange<Double> { rects.first(where: { $0.id == selected })?.timeRange ?? range }
    private func setRange(_ r: ClosedRange<Double>) {
        if let i = rects.firstIndex(where: { $0.id == selected }) { rects[i].timeRange = r } else { range = r }
    }

    private func apply() {
        let (r, s, u) = (rects, style, url)
        JobRunner.shared.run("Redacting \(u.lastPathComponent)", inputs: [u]) { _ in [try await VideoOps.redact(u, rects: r, style: s)] }
    }
}

// MARK: - Speed

struct SpeedEditor: View {
    let urls: [URL]
    @State private var speed = 2.0
    private let presets: [Double] = [0.25, 0.5, 0.75, 1.25, 1.5, 2, 3, 4]

    var body: some View {
        EditorScaffold(tool: .changeVideoSpeed, primary: "Change Video Speed", canApply: speed != 1, apply: apply) {
            VStack(spacing: 20) {
                Text(String(format: "%.2g×", speed)).font(.system(size: 54, weight: .bold, design: .rounded)).foregroundStyle(Color.tangelo)
                Slider(value: $speed, in: 0.25...4, step: 0.05).frame(maxWidth: 420)
                HStack { ForEach(presets, id: \.self) { p in Button(String(format: "%g×", p)) { speed = p }.buttonStyle(.bordered) } }
                Text("Audio pitch is preserved.").font(.caption).foregroundStyle(.secondary)
            }
        } options: { Text("\(urls.count) video\(urls.count == 1 ? "" : "s")").foregroundStyle(.secondary) }
    }

    private func apply() { let s = speed; runBatch(.changeVideoSpeed, urls) { [try await VideoOps.changeSpeed($0, factor: s)] } }
}

// MARK: - Split

struct SplitVideoEditor: View {
    let url: URL
    @StateObject private var clock: PlayerClock
    @State private var points: [Double] = []
    @State private var removed: Set<Int> = []
    @State private var mode: VideoOps.SplitMode = .separate

    init(url: URL) { self.url = url; _clock = StateObject(wrappedValue: PlayerClock(url: url)) }

    private var clips: [ClosedRange<Double>] {
        let b = [0] + points.sorted() + [clock.duration]
        return (0..<(b.count - 1)).map { b[$0]...b[$0 + 1] }
    }

    var body: some View {
        EditorScaffold(tool: .splitVideo, primary: mode == .separate ? "Export Clips" : "Export One Video", canApply: !points.isEmpty && removed.count < clips.count, apply: apply) {
            HStack(spacing: 14) {
                VStack {
                    PlayerView(player: clock.player).clipShape(RoundedRectangle(cornerRadius: 10))
                    GeometryReader { g in
                        ZStack(alignment: .leading) {
                            Capsule().fill(Color.primary.opacity(0.1)).frame(height: 10)
                            ForEach(points, id: \.self) { p in
                                Rectangle().fill(Color.tangelo).frame(width: 3, height: 22).offset(x: CGFloat(p / max(clock.duration, 0.1)) * g.size.width - 1.5)
                            }
                            Rectangle().fill(Color.primary).frame(width: 2, height: 22).offset(x: CGFloat(clock.time / max(clock.duration, 0.1)) * g.size.width)
                        }
                    }.frame(height: 22)
                    HStack {
                        Button { addPoint() } label: { Label("Split at Playhead", systemImage: "scissors") }
                        Text(Fmt.time(clock.time)).monospacedDigit().foregroundStyle(.secondary)
                    }
                }
                List {
                    ForEach(Array(clips.enumerated()), id: \.offset) { i, c in
                        HStack {
                            Toggle("", isOn: Binding(get: { !removed.contains(i) }, set: { if $0 { removed.remove(i) } else { removed.insert(i) } })).labelsHidden()
                            VStack(alignment: .leading) {
                                Text("Clip \(i + 1)").font(.callout.weight(.medium))
                                Text("\(Fmt.time(c.lowerBound)) – \(Fmt.time(c.upperBound))").font(.caption).monospacedDigit().foregroundStyle(.secondary)
                            }
                            Spacer()
                            Button { clock.playRange(c) } label: { Image(systemName: "play.fill") }.buttonStyle(.plain)
                        }
                        .opacity(removed.contains(i) ? 0.45 : 1)
                    }
                    if !points.isEmpty { Button("Clear Split Points") { points = []; removed = [] } }
                }
                .frame(width: 220)
            }
        } options: {
            Picker("", selection: $mode) { ForEach(VideoOps.SplitMode.allCases) { Text($0.rawValue).tag($0) } }.pickerStyle(.segmented).frame(width: 260)
        }
    }

    private func addPoint() {
        let t = clock.time
        guard t > 0.05, t < clock.duration - 0.05, !points.contains(where: { abs($0 - t) < 0.05 }) else { return }
        points.append(t); points.sort(); removed = []
    }

    private func apply() {
        let (p, r, m, u) = (points, removed, mode, url)
        JobRunner.shared.run("Splitting \(u.lastPathComponent)", inputs: [u]) { _ in try await VideoOps.split(u, at: p, removed: r, mode: m) }
    }
}

// MARK: - Snapshots

struct SnapshotsEditor: View {
    let url: URL
    @StateObject private var clock: PlayerClock
    @State private var times: [Double] = []
    @State private var interval = 5.0

    init(url: URL) { self.url = url; _clock = StateObject(wrappedValue: PlayerClock(url: url)) }

    var body: some View {
        EditorScaffold(tool: .videoSnapshots, primary: "Save \(times.count) Frame\(times.count == 1 ? "" : "s")", canApply: !times.isEmpty, apply: apply) {
            HStack(spacing: 14) {
                VStack {
                    PlayerView(player: clock.player).clipShape(RoundedRectangle(cornerRadius: 10))
                    HStack {
                        Button { add(clock.time) } label: { Label("Add Frame at Playhead", systemImage: "camera") }
                        Spacer()
                        Text("Every"); TextField("", value: $interval, format: .number).frame(width: 50); Text("s")
                        Button("Add Frames") {
                            guard interval > 0.1 else { return }
                            stride(from: 0, to: clock.duration, by: interval).forEach(add)
                        }
                    }
                }
                List {
                    ForEach(times, id: \.self) { t in
                        HStack {
                            Text(Fmt.time(t)).monospacedDigit()
                            Spacer()
                            Button { clock.seek(t) } label: { Image(systemName: "eye") }.buttonStyle(.plain).help("Preview selected frame")
                            Button { times.removeAll { $0 == t } } label: { Image(systemName: "minus.circle") }.buttonStyle(.plain).help("Remove selected frame")
                        }
                    }
                }.frame(width: 200)
            }
        } options: { Text("Frames are saved as PNG.").font(.caption).foregroundStyle(.secondary) }
    }

    private func add(_ t: Double) { if !times.contains(where: { abs($0 - t) < 0.01 }) { times.append(t); times.sort() } }
    private func apply() { let (t, u) = (times, url); JobRunner.shared.run("Saving snapshots", inputs: [u]) { _ in try await VideoOps.snapshots(u, times: t) } }
}

// MARK: - GIF

struct GIFEditor: View {
    let url: URL
    @StateObject private var clock: PlayerClock
    @State private var range: ClosedRange<Double> = 0...5
    @State private var fps = 12.0
    @State private var width = 480.0

    init(url: URL) { self.url = url; _clock = StateObject(wrappedValue: PlayerClock(url: url)) }

    var body: some View {
        EditorScaffold(tool: .videoToGIF, primary: "Create GIF", apply: apply) {
            VStack(spacing: 12) {
                PlayerView(player: clock.player).clipShape(RoundedRectangle(cornerRadius: 10))
                RangeSlider(range: $range, bounds: 0...max(clock.duration, 0.1), playhead: clock.time) { clock.seek($0) }
                HStack {
                    Text("\(Fmt.time(range.lowerBound)) – \(Fmt.time(range.upperBound))").monospacedDigit()
                    Spacer()
                    Text("Frame rate"); Picker("", selection: $fps) { ForEach([8.0, 10, 12, 15, 20, 24], id: \.self) { Text("\(Int($0)) fps").tag($0) } }.frame(width: 90)
                    Text("Width"); Picker("", selection: $width) { ForEach([320.0, 480, 640, 800, 1080], id: \.self) { Text("\(Int($0)) px").tag($0) } }.frame(width: 90)
                }
            }
        } options: {
            Text("≈ \(Int((range.upperBound - range.lowerBound) * fps)) frames").foregroundStyle(.secondary)
        }
        .onChange(of: clock.duration) { range = 0...min(clock.duration, 6) }
    }

    private func apply() {
        let (r, f, w, u) = (range, fps, width, url)
        JobRunner.shared.run("Creating GIF", inputs: [u]) { _ in
            let out = Output.url(for: u, ext: "gif")
            try await VideoKit.gif(from: u, to: out, fps: f, maxWidth: w, range: r)
            return [out]
        }
    }
}

// MARK: - Normalize

struct NormalizeEditor: View {
    let urls: [URL]
    @State private var target = -16.0
    @State private var peak = -1.0

    var body: some View {
        EditorScaffold(tool: .normalizeAudio, primary: "Normalize Volume", apply: apply) {
            Form {
                Picker("Target loudness", selection: $target) {
                    Text("Podcast / streaming (−16 dB)").tag(-16.0)
                    Text("Music (−14 dB)").tag(-14.0)
                    Text("Broadcast (−23 dB)").tag(-23.0)
                    Text("Loud (−11 dB)").tag(-11.0)
                }
                LabeledContent("Peak ceiling") { HStack { Slider(value: $peak, in: -6...0, step: 0.5); Text("\(peak, specifier: "%.1f") dB").monospacedDigit().frame(width: 64) } }
                Section("Files") { ForEach(urls, id: \.self) { Text($0.lastPathComponent) } }
                Text("Videos keep their picture; only the audio track is replaced.").font(.caption).foregroundStyle(.secondary)
            }.formStyle(.grouped)
        } options: { EmptyView() }
    }

    private func apply() { let (t, p) = (target, peak); runBatch(.normalizeAudio, urls) { [try await AudioOps.normalize($0, targetDB: t, peakDB: p)] } }
}

// MARK: - Channels

struct ChannelsEditor: View {
    let urls: [URL]
    @State private var count = 1
    @State private var left = 1.0
    @State private var right = 1.0

    var body: some View {
        EditorScaffold(tool: .audioChannels, primary: "Convert Audio Channels", apply: apply) {
            Form {
                Picker("Output", selection: $count) { Text("Mono").tag(1); Text("Stereo").tag(2) }.pickerStyle(.segmented)
                LabeledContent("Left volume") { HStack { Slider(value: $left, in: 0...2); Text("\(Int(left * 100))%").monospacedDigit().frame(width: 50) } }
                LabeledContent("Right volume") { HStack { Slider(value: $right, in: 0...2); Text("\(Int(right * 100))%").monospacedDigit().frame(width: 50) } }
                HStack {
                    Button("Left only") { left = 1; right = 0 }
                    Button("Right only") { left = 0; right = 1 }
                    Button("Swap") { swap(&left, &right) }
                    Button("Reset") { left = 1; right = 1 }
                }
                Section("Files") { ForEach(urls, id: \.self) { Text($0.lastPathComponent) } }
            }.formStyle(.grouped)
        } options: { EmptyView() }
    }

    private func apply() {
        let (c, l, r) = (count, Float(left), Float(right))
        runBatch(.audioChannels, urls) { [try await AudioOps.channels($0, count: c, left: l, right: r)] }
    }
}

// MARK: - Bleep

struct BleepEditor: View {
    let url: URL
    @StateObject private var clock: PlayerClock
    @StateObject private var wave: WaveformLoader
    @State private var ranges: [ClosedRange<Double>] = []
    @State private var draft: ClosedRange<Double> = 0...0.5
    @State private var tone = true

    init(url: URL) {
        self.url = url
        _clock = StateObject(wrappedValue: PlayerClock(url: url))
        _wave = StateObject(wrappedValue: WaveformLoader(url: url))
    }

    var body: some View {
        EditorScaffold(tool: .redactAudio, primary: "Save Bleeped Copy", canApply: !ranges.isEmpty, apply: apply) {
            VStack(spacing: 12) {
                if FileCategory.of(url) == .video { PlayerView(player: clock.player).frame(maxHeight: 220).clipShape(RoundedRectangle(cornerRadius: 10)) }
                WaveformView(levels: wave.levels, highlight: (ranges + [draft]).map { $0.lowerBound / max(clock.duration, 0.1)...$0.upperBound / max(clock.duration, 0.1) })
                    .frame(height: 120)
                RangeSlider(range: $draft, bounds: 0...max(clock.duration, 0.1), playhead: clock.time) { clock.seek($0) }
                HStack {
                    Button { ranges.append(draft); ranges.sort { $0.lowerBound < $1.lowerBound } } label: { Label("Add Bleep", systemImage: "plus") }
                    Button { clock.playRange(draft) } label: { Label("Play", systemImage: "play.fill") }
                    Button("Bleep Entire Audio") { ranges = [0...clock.duration] }
                    Spacer()
                    Picker("", selection: $tone) { Text("Tone").tag(true); Text("Silence").tag(false) }.pickerStyle(.segmented).frame(width: 160)
                }
                ScrollView(.horizontal) {
                    HStack {
                        ForEach(Array(ranges.enumerated()), id: \.offset) { i, r in
                            HStack(spacing: 4) {
                                Text("\(Fmt.time(r.lowerBound))–\(Fmt.time(r.upperBound))").monospacedDigit().font(.caption)
                                Button { ranges.remove(at: i) } label: { Image(systemName: "xmark.circle.fill") }.buttonStyle(.plain)
                            }
                            .padding(.horizontal, 8).padding(.vertical, 4).background(Color.red.opacity(0.15), in: Capsule())
                        }
                    }
                }
            }
        } options: { Text("\(ranges.count) range\(ranges.count == 1 ? "" : "s")").foregroundStyle(.secondary) }
    }

    private func apply() { let (r, t, u) = (ranges, tone, url); JobRunner.shared.run("Bleeping \(u.lastPathComponent)", inputs: [u]) { _ in [try await AudioOps.bleep(u, ranges: r, tone: t)] } }
}

// MARK: - Visualizer

struct VisualizerEditor: View {
    let url: URL
    @State private var style: VisualizerStyle = .bars
    @State private var accent: Color = .tangelo
    @State private var bg = Color(white: 0.08)
    @State private var image: URL?
    @State private var title = ""
    @State private var sizeIndex = 0
    private let sizes: [(String, CGSize)] = [("Landscape 720p", CGSize(width: 1280, height: 720)), ("Square 1080", CGSize(width: 1080, height: 1080)), ("Story 1080×1920", CGSize(width: 1080, height: 1920)), ("Landscape 1080p", CGSize(width: 1920, height: 1080))]

    init(url: URL) { self.url = url; _title = State(initialValue: url.deletingPathExtension().lastPathComponent) }

    private var options: VisualizerOptions {
        VisualizerOptions(style: style, accent: NSColor(accent), background: NSColor(bg), image: image, size: sizes[sizeIndex].1, title: title)
    }

    var body: some View {
        EditorScaffold(tool: .audioToVideo, primary: "Create Visualization", apply: apply) {
            HStack(spacing: 16) {
                FittedImage(image: preview).clipShape(RoundedRectangle(cornerRadius: 10)).frame(maxWidth: .infinity, maxHeight: .infinity)
                Form {
                    Picker("Style", selection: $style) { ForEach(VisualizerStyle.allCases) { Text($0.rawValue).tag($0) } }
                    Picker("Size", selection: $sizeIndex) { ForEach(sizes.indices, id: \.self) { Text(sizes[$0].0).tag($0) } }
                    ColorPicker("Accent", selection: $accent)
                    ColorPicker("Background", selection: $bg)
                    TextField("Title", text: $title)
                    HStack {
                        Button(image?.lastPathComponent ?? "Choose Artwork…") {
                            let p = NSOpenPanel(); p.allowedContentTypes = [.image]; p.message = "Choose an image for the visualization."
                            if p.runModal() == .OK { image = p.url }
                        }
                        if image != nil { Button { image = nil } label: { Image(systemName: "xmark.circle.fill") }.buttonStyle(.plain) }
                    }
                }.formStyle(.grouped).frame(width: 260)
            }
        } options: { Text("Renders an MP4 with your audio.").font(.caption).foregroundStyle(.secondary) }
    }

    private var preview: NSImage {
        var o = options
        let scale = 480 / max(o.size.width, o.size.height)
        let s = CGSize(width: o.size.width * scale, height: o.size.height * scale)
        o.size = s
        let ctx = CGContext(data: nil, width: Int(s.width), height: Int(s.height), bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        let count = style == .circle ? 96 : 64
        var levels: [Float] = []
        for i in 0..<count {
            let x = Double(i)
            let v: Double = 0.25 + 0.6 * abs(sin(x * 0.37) * cos(x * 0.11))
            levels.append(Float(v))
        }
        Visualizer.drawFrame(ctx: ctx, size: s, levels: levels, options: o, artwork: o.image.flatMap { try? ImageIOKit.load($0) }, progress: 0.35)
        return NSImage(cg: ctx.makeImage()!)
    }

    private func apply() {
        let (o, u) = (options, url)
        JobRunner.shared.run("Creating visualization", inputs: [u]) { p in [try await Visualizer.render(u, options: o, progress: p)] }
    }
}
