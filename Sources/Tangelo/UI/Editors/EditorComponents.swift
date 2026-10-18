import AppKit
import AVKit
import SwiftUI

@MainActor
enum EditorWindows {
    private static var windows: [NSWindow] = []

    static func open(_ tool: FileTool, urls: [URL]) {
        let close: () -> Void = { }
        let view = EditorRoot(tool: tool, urls: urls)
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 760, height: 560),
                         styleMask: [.titled, .closable, .resizable, .miniaturizable, .fullSizeContentView],
                         backing: .buffered, defer: false)
        w.titlebarAppearsTransparent = true
        w.title = tool.title + " — " + (urls.count == 1 ? urls[0].lastPathComponent : "\(urls.count) files")
        w.contentView = NSHostingView(rootView: view.environment(\.closeEditor, CloseAction { [weak w] in w?.close() }))
        w.isReleasedWhenClosed = false
        w.minSize = NSSize(width: 560, height: 420)
        w.center()
        windows.append(w)
        _ = close
        NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: w, queue: .main) { n in
            MainActor.assumeIsolated { windows.removeAll { $0 === n.object as? NSWindow } }
        }
        NSApp.activate(ignoringOtherApps: true)
        w.makeKeyAndOrderFront(nil)
    }
}

struct CloseAction { let run: () -> Void; func callAsFunction() { run() } }
private struct CloseEditorKey: EnvironmentKey { static let defaultValue = CloseAction {} }
extension EnvironmentValues {
    var closeEditor: CloseAction {
        get { self[CloseEditorKey.self] }
        set { self[CloseEditorKey.self] = newValue }
    }
}

struct EditorRoot: View {
    let tool: FileTool
    let urls: [URL]

    var body: some View {
        Group {
            switch tool {
            case .compress: CompressEditor(urls: urls)
            case .metadata: MetadataEditor(urls: urls)
            case .batchRename: RenameEditor(urls: urls)
            case .editImage: AdjustEditor(urls: urls)
            case .annotateImage: AnnotateEditor(url: urls[0], others: Array(urls.dropFirst()))
            case .frameImage: FrameEditor(urls: urls)
            case .cropImage: CropImageEditor(urls: urls)
            case .resizeImage: ResizeEditor(urls: urls)
            case .redactImage: RedactImageEditor(url: urls[0])
            case .createCollage: CollageEditor(urls: urls)
            case .trimVideo, .trimAudio: TrimEditor(url: urls[0])
            case .cropVideo: CropVideoEditor(url: urls[0])
            case .changeVideoSpeed: SpeedEditor(urls: urls)
            case .splitVideo: SplitVideoEditor(url: urls[0])
            case .videoSnapshots: SnapshotsEditor(url: urls[0])
            case .redactVideo: RedactVideoEditor(url: urls[0])
            case .videoToGIF: GIFEditor(url: urls[0])
            case .normalizeAudio: NormalizeEditor(urls: urls)
            case .audioChannels: ChannelsEditor(urls: urls)
            case .redactAudio: BleepEditor(url: urls[0])
            case .audioToVideo: VisualizerEditor(url: urls[0])
            case .splitPDF: SplitPDFEditor(url: urls[0])
            case .organizePDF: OrganizePDFEditor(url: urls[0])
            default: Text("This tool runs without an editor.").padding()
            }
        }
        .frame(minWidth: 560, minHeight: 420)
    }
}

/// Consistent editor chrome: content on top, options + primary button in the footer.
struct EditorScaffold<Content: View, Options: View>: View {
    let tool: FileTool
    var primary: String = "Save"
    var canApply = true
    let apply: () -> Void
    @ViewBuilder var content: () -> Content
    @ViewBuilder var options: () -> Options
    @Environment(\.closeEditor) private var close

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                Image(systemName: tool.symbol).font(.title2).foregroundStyle(Color.tangelo)
                VStack(alignment: .leading, spacing: 1) {
                    Text(tool.title).font(.headline)
                    Text(tool.subtitle).font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
            }
            .padding(.horizontal, 20).padding(.top, 34).padding(.bottom, 12)
            content()
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .padding(.horizontal, 20)
            Divider().padding(.top, 12)
            HStack(alignment: .center, spacing: 16) {
                options()
                Spacer(minLength: 8)
                Button("Cancel") { close() }.keyboardShortcut(.cancelAction)
                Button(primary) { apply(); close() }
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent).tint(.tangelo)
                    .disabled(!canApply)
            }
            .padding(.horizontal, 20).padding(.vertical, 14)
        }
    }
}

// MARK: - Media

struct PlayerView: NSViewRepresentable {
    let player: AVPlayer
    var controls: AVPlayerViewControlsStyle = .inline
    func makeNSView(context: Context) -> AVPlayerView {
        let v = AVPlayerView(); v.player = player; v.controlsStyle = controls; v.showsFullScreenToggleButton = false
        return v
    }
    func updateNSView(_ v: AVPlayerView, context: Context) { v.player = player }
}

/// Observes an AVPlayer's current time.
@MainActor
final class PlayerClock: ObservableObject {
    let player: AVPlayer
    @Published var time: Double = 0
    @Published var duration: Double = 0
    @Published var info: VideoInfo?
    private var token: Any?

    init(url: URL) {
        player = AVPlayer(url: url)
        token = player.addPeriodicTimeObserver(forInterval: CMTime(value: 1, timescale: 30), queue: .main) { [weak self] t in
            MainActor.assumeIsolated { self?.time = t.seconds }
        }
        Task { [weak self] in
            let info = try? await VideoKit.info(url)
            await MainActor.run { self?.info = info; self?.duration = info?.duration ?? 0 }
        }
    }

    func seek(_ t: Double) {
        player.seek(to: CMTime(seconds: t, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
        time = t
    }

    func playRange(_ r: ClosedRange<Double>) {
        seek(r.lowerBound)
        player.play()
        let end = r.upperBound
        DispatchQueue.main.asyncAfter(deadline: .now() + (r.upperBound - r.lowerBound)) { [weak self] in
            if let self, abs(self.time - end) < 0.5 { self.player.pause() }
        }
    }
}

struct RangeSlider: View {
    @Binding var range: ClosedRange<Double>
    let bounds: ClosedRange<Double>
    var playhead: Double? = nil
    var onScrub: ((Double) -> Void)? = nil

    var body: some View {
        GeometryReader { g in
            let w = g.size.width
            let span = max(bounds.upperBound - bounds.lowerBound, 0.0001)
            let x0 = CGFloat((range.lowerBound - bounds.lowerBound) / span) * w
            let x1 = CGFloat((range.upperBound - bounds.lowerBound) / span) * w
            ZStack(alignment: .leading) {
                Capsule().fill(Color.primary.opacity(0.1)).frame(height: 8)
                Rectangle().fill(Color.tangelo.opacity(0.45)).frame(width: max(0, x1 - x0), height: 8).offset(x: x0)
                if let playhead {
                    Rectangle().fill(Color.primary).frame(width: 2, height: 22).offset(x: CGFloat((playhead - bounds.lowerBound) / span) * w - 1)
                }
                handle.offset(x: x0 - 7).gesture(DragGesture().onChanged { v in
                    let t = bounds.lowerBound + Double(min(max(0, v.location.x), w) / w) * span
                    range = min(t, range.upperBound - 0.05)...range.upperBound
                    onScrub?(range.lowerBound)
                })
                handle.offset(x: x1 - 7).gesture(DragGesture().onChanged { v in
                    let t = bounds.lowerBound + Double(min(max(0, v.location.x), w) / w) * span
                    range = range.lowerBound...max(t, range.lowerBound + 0.05)
                    onScrub?(range.upperBound)
                })
            }
            .frame(height: 24)
            .contentShape(Rectangle())
        }
        .frame(height: 24)
    }

    private var handle: some View {
        RoundedRectangle(cornerRadius: 4).fill(Color.tangelo).frame(width: 14, height: 24)
            .overlay(Capsule().fill(.white.opacity(0.8)).frame(width: 2, height: 10))
            .shadow(radius: 1)
    }
}

struct TimeField: View {
    let label: String
    @Binding var value: Double
    var body: some View {
        HStack(spacing: 4) {
            Text(label).foregroundStyle(.secondary)
            TextField("", value: $value, format: .number.precision(.fractionLength(2))).frame(width: 64).textFieldStyle(.roundedBorder)
            Text("s").foregroundStyle(.secondary)
        }.font(.callout)
    }
}

struct WaveformView: View {
    let levels: [Float]
    var highlight: [ClosedRange<Double>] = []  // fractions 0...1
    var color: Color = .tangelo

    var body: some View {
        Canvas { ctx, size in
            for r in highlight {
                ctx.fill(Path(CGRect(x: r.lowerBound * size.width, y: 0, width: (r.upperBound - r.lowerBound) * size.width, height: size.height)),
                         with: .color(.red.opacity(0.25)))
            }
            guard !levels.isEmpty else { return }
            let bw = size.width / CGFloat(levels.count)
            for (i, l) in levels.enumerated() {
                let h = max(1, CGFloat(l) * size.height * 0.95)
                ctx.fill(Path(CGRect(x: CGFloat(i) * bw, y: (size.height - h) / 2, width: max(1, bw - 0.5), height: h)), with: .color(color.opacity(0.8)))
            }
        }
        .background(Color.primary.opacity(0.04), in: RoundedRectangle(cornerRadius: 8))
    }
}

/// Loads a waveform envelope asynchronously.
@MainActor
final class WaveformLoader: ObservableObject {
    @Published var levels: [Float] = []
    @Published var failed = false
    init(url: URL, buckets: Int = 600) {
        Task {
            do {
                let pcm = try await PCMAudio.read(url)
                let env = pcm.envelope(buckets: buckets)
                let m = env.max() ?? 1
                self.levels = env.map { $0 / max(m, 0.0001) }
            } catch { self.failed = true }
        }
    }
}

// MARK: - Images

struct FittedImage: View {
    let image: NSImage
    var body: some View {
        Image(nsImage: image).resizable().interpolation(.high).aspectRatio(contentMode: .fit)
    }
}

/// Lets the user draw, move and resize normalized rectangles over content of a known aspect ratio.
struct RectEditorOverlay: View {
    @Binding var rects: [RedactionRect]
    @Binding var selected: UUID?
    var single = false
    var aspect: CGFloat? = nil   // locks w/h when set (in pixel terms, given imageAspect)
    var imageAspect: CGFloat = 1
    var tint: Color = .tangelo
    var dimOutside = false

    @State private var drawStart: CGPoint?
    @State private var moveOrigin: CGRect?

    var body: some View {
        GeometryReader { g in
            let size = g.size
            ZStack(alignment: .topLeading) {
                if dimOutside, let r = rects.first?.rect {
                    Path { p in
                        p.addRect(CGRect(origin: .zero, size: size))
                        p.addRect(denorm(r, size))
                    }.fill(Color.black.opacity(0.5), style: FillStyle(eoFill: true))
                }
                Color.clear.contentShape(Rectangle())
                    .gesture(DragGesture(minimumDistance: 2).onChanged { v in
                        if drawStart == nil {
                            drawStart = v.startLocation
                            if single { rects = [] }
                            let new = RedactionRect(rect: .zero)
                            rects.append(new); selected = new.id
                        }
                        guard let s = drawStart, let i = rects.firstIndex(where: { $0.id == selected }) else { return }
                        var r = CGRect(x: min(s.x, v.location.x), y: min(s.y, v.location.y), width: abs(v.location.x - s.x), height: abs(v.location.y - s.y))
                        if let aspect {
                            let hPx = r.width / aspect * 1
                            r.size.height = hPx
                        }
                        rects[i].rect = norm(r.intersection(CGRect(origin: .zero, size: size)), size)
                    }.onEnded { _ in
                        drawStart = nil
                        rects.removeAll { $0.rect.width < 0.005 || $0.rect.height < 0.005 }
                    })
                ForEach($rects) { $r in
                    let frame = denorm(r.rect, size)
                    Rectangle()
                        .strokeBorder(selected == r.id ? tint : .white, style: StrokeStyle(lineWidth: 2, dash: selected == r.id ? [] : [5, 3]))
                        .background(tint.opacity(dimOutside ? 0 : 0.18))
                        .frame(width: frame.width, height: frame.height)
                        .offset(x: frame.minX, y: frame.minY)
                        .gesture(DragGesture().onChanged { v in
                            selected = r.id
                            if moveOrigin == nil { moveOrigin = r.rect }
                            guard let o = moveOrigin else { return }
                            var nr = o.offsetBy(dx: v.translation.width / size.width, dy: v.translation.height / size.height)
                            nr.origin.x = min(max(0, nr.origin.x), 1 - nr.width)
                            nr.origin.y = min(max(0, nr.origin.y), 1 - nr.height)
                            r.rect = nr
                        }.onEnded { _ in moveOrigin = nil })
                    // resize handle (bottom-right)
                    Circle().fill(tint).frame(width: 12, height: 12)
                        .offset(x: frame.maxX - 6, y: frame.maxY - 6)
                        .gesture(DragGesture().onChanged { v in
                            let p = CGPoint(x: min(max(v.location.x, frame.minX + 6), size.width), y: min(max(v.location.y, frame.minY + 6), size.height))
                            var nr = CGRect(x: frame.minX, y: frame.minY, width: p.x - frame.minX, height: p.y - frame.minY)
                            if let aspect { nr.size.height = nr.width / aspect }
                            r.rect = norm(nr, size)
                        })
                }
            }
        }
    }

    private func norm(_ r: CGRect, _ s: CGSize) -> CGRect { CGRect(x: r.minX / s.width, y: r.minY / s.height, width: r.width / s.width, height: r.height / s.height) }
    private func denorm(_ r: CGRect, _ s: CGSize) -> CGRect { CGRect(x: r.minX * s.width, y: r.minY * s.height, width: r.width * s.width, height: r.height * s.height) }
}

/// Shows content at its aspect ratio and overlays a view in the same coordinate space.
struct AspectCanvas<Content: View, Overlay: View>: View {
    let aspect: CGFloat
    @ViewBuilder var content: () -> Content
    @ViewBuilder var overlay: () -> Overlay
    var body: some View {
        GeometryReader { g in
            let fit = g.size.width / g.size.height > aspect
                ? CGSize(width: g.size.height * aspect, height: g.size.height)
                : CGSize(width: g.size.width, height: g.size.width / aspect)
            ZStack(alignment: .topLeading) { content(); overlay() }
                .frame(width: fit.width, height: fit.height)
                .position(x: g.size.width / 2, y: g.size.height / 2)
        }
    }
}

enum Fmt {
    static func bytes(_ n: Int) -> String { ByteCountFormatter.string(fromByteCount: Int64(n), countStyle: .file) }
    static func size(of url: URL) -> Int { (try? url.resourceValues(forKeys: [.totalFileSizeKey, .fileSizeKey])).flatMap { $0.totalFileSize ?? $0.fileSize } ?? 0 }
    static func time(_ t: Double) -> String {
        let s = max(0, t)
        return String(format: "%d:%05.2f", Int(s) / 60, s.truncatingRemainder(dividingBy: 60))
    }
}

extension NSImage {
    convenience init(cg: CGImage) { self.init(cgImage: cg, size: CGSize(width: cg.width, height: cg.height)) }
}

func loadPreviewImage(_ url: URL, max: Int = 1600) -> CGImage? {
    guard let img = try? ImageIOKit.load(url) else { return nil }
    return ImageIOKit.resize(img, maxDimension: max)
}
