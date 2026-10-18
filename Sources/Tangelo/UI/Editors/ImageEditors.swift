import AppKit
import SwiftUI

@MainActor
func runBatch(_ tool: FileTool, _ urls: [URL], _ op: @escaping (URL) async throws -> [URL]) {
    let title = "\(tool.title): " + (urls.count == 1 ? urls[0].lastPathComponent : "\(urls.count) files")
    JobRunner.shared.run(title, inputs: urls) { p in
        var outs: [URL] = []
        for (i, u) in urls.enumerated() {
            outs += try await op(u)
            p(Double(i + 1) / Double(urls.count))
        }
        return outs
    }
}

// MARK: - Compress

struct CompressEditor: View {
    let urls: [URL]
    enum Mode: String, CaseIterable, Identifiable { case preset = "Preset", target = "Target size", percent = "Reduce by %"; var id: String { rawValue } }
    @State private var mode: Mode = .preset
    @State private var imagePreset: ImageOps.ImagePreset = .balanced
    @State private var videoPreset: VideoOps.VideoPreset = .balanced
    @State private var audioPreset: AudioOps.AudioPreset = .balanced
    @State private var pdfQuality = 0.6
    @State private var pdfDPI = 120.0
    @State private var targetValue = 2.0
    @State private var targetUnit = "MB"
    @State private var percent = 50.0
    @State private var keepFormat = true

    private var categories: Set<FileCategory> { Set(urls.map(FileCategory.of)) }

    var body: some View {
        EditorScaffold(tool: .compress, primary: "Compress", apply: apply) {
            Form {
                Picker("Compression mode", selection: $mode) { ForEach(Mode.allCases) { Text($0.rawValue).tag($0) } }.pickerStyle(.segmented)
                if mode == .preset {
                    if categories.contains(.image) {
                        Picker("Images", selection: $imagePreset) { ForEach(ImageOps.ImagePreset.allCases) { Text($0.rawValue).tag($0) } }
                        Text(imagePreset.detail).font(.caption).foregroundStyle(.secondary)
                        Toggle("Keep original format", isOn: $keepFormat)
                    }
                    if categories.contains(.video) { Picker("Video", selection: $videoPreset) { ForEach(VideoOps.VideoPreset.allCases) { Text($0.rawValue).tag($0) } } }
                    if categories.contains(.audio) { Picker("Audio", selection: $audioPreset) { ForEach(AudioOps.AudioPreset.allCases) { Text($0.rawValue).tag($0) } } }
                    if categories.contains(.pdf) {
                        LabeledContent("PDF quality") { Slider(value: $pdfQuality, in: 0.2...0.9) }
                        LabeledContent("PDF resolution") { HStack { Slider(value: $pdfDPI, in: 72...200); Text("\(Int(pdfDPI)) dpi").monospacedDigit().frame(width: 60) } }
                    }
                } else if mode == .target {
                    HStack {
                        TextField("Target file size", value: $targetValue, format: .number).frame(width: 90)
                        Picker("", selection: $targetUnit) { Text("KB").tag("KB"); Text("MB").tag("MB") }.frame(width: 80)
                    }
                    Text("The target is calculated separately for each file.").font(.caption).foregroundStyle(.secondary)
                } else {
                    LabeledContent("Reduce by") { HStack { Slider(value: $percent, in: 10...90, step: 5); Text("\(Int(percent))%").monospacedDigit().frame(width: 44) } }
                }
                Section("Files") {
                    ForEach(urls, id: \.self) { u in
                        LabeledContent(u.lastPathComponent) { Text(Fmt.bytes(Fmt.size(of: u))).foregroundStyle(.secondary) }
                    }
                }
            }
            .formStyle(.grouped)
        } options: { EmptyView() }
    }

    private func target(for url: URL) -> Int? {
        switch mode {
        case .preset: return nil
        case .target: return Int(targetValue * (targetUnit == "MB" ? 1_000_000 : 1000))
        case .percent: return Int(Double(Fmt.size(of: url)) * (1 - percent / 100))
        }
    }

    private func apply() {
        let (ip, vp, ap, q, dpi, keep) = (imagePreset, videoPreset, audioPreset, pdfQuality, pdfDPI, keepFormat)
        let targets = Dictionary(uniqueKeysWithValues: urls.map { ($0, target(for: $0)) })
        let preset = mode == .preset
        runBatch(.compress, urls) { u in
            let t = targets[u] ?? nil
            if let t, t < 1000 { throw TangeloError.message("Enter a target size of at least 1 KB.") }
            if let t, t >= Fmt.size(of: u) { throw TangeloError.message("Target size cannot exceed the original file size (\(Fmt.bytes(Fmt.size(of: u)))).") }
            switch FileCategory.of(u) {
            case .image: return [try ImageOps.compress(u, preset: preset ? ip : nil, targetBytes: t, keepFormat: keep)]
            case .video: return [try await VideoOps.compress(u, preset: preset ? vp : nil, targetBytes: t) { _ in }]
            case .audio: return [try await AudioOps.compress(u, preset: preset ? ap : nil, targetBytes: t)]
            case .pdf:
                let out = Output.url(for: u, ext: "pdf", suffix: "compressed")
                var quality = q, res = dpi
                if let t {
                    // Step down until under target.
                    for _ in 0..<6 {
                        try? FileManager.default.removeItem(at: out)
                        try? PDFKitTools.compress(u, quality: quality, dpi: res, to: out)
                        if Fmt.size(of: out) > 0, Fmt.size(of: out) <= t { break }
                        quality = max(0.15, quality - 0.12); res = max(60, res - 20)
                    }
                    guard FileManager.default.fileExists(atPath: out.path) else { throw TangeloError.message("Could not reach the target size.") }
                } else { try PDFKitTools.compress(u, quality: quality, dpi: res, to: out) }
                return [out]
            default: throw TangeloError.unsupported("This file type can’t be compressed.")
            }
        }
    }
}

// MARK: - Metadata

struct MetadataEditor: View {
    let urls: [URL]
    @State private var fields: [MetadataField] = []
    @State private var search = ""
    @State private var title = ""
    @State private var artist = ""
    @State private var copyright = ""
    @State private var desc = ""
    @State private var removeGPS = true

    private var single: URL? { urls.count == 1 ? urls[0] : nil }
    private var isImage: Bool { urls.allSatisfy { FileCategory.of($0) == .image } }

    var body: some View {
        EditorScaffold(tool: .metadata, primary: "Remove All Metadata", apply: removeAll) {
            VStack(alignment: .leading, spacing: 10) {
                if single != nil {
                    TextField("Search fields", text: $search).textFieldStyle(.roundedBorder)
                    List(fields.filter { search.isEmpty || $0.key.localizedCaseInsensitiveContains(search) || $0.value.localizedCaseInsensitiveContains(search) }) { f in
                        HStack(alignment: .top) {
                            Text(f.group).font(.caption).foregroundStyle(.secondary).frame(width: 80, alignment: .leading)
                            Text(f.key).font(.callout.weight(.medium)).frame(width: 170, alignment: .leading)
                            Text(f.value).font(.callout).textSelection(.enabled).lineLimit(3)
                        }
                    }
                    .overlay { if fields.isEmpty { Text("No readable metadata found.").foregroundStyle(.secondary) } }
                    if fields.contains(where: { $0.group == "GPS" }) {
                        Label("This file contains location data.", systemImage: "location.fill").foregroundStyle(.orange).font(.callout)
                    }
                } else {
                    Text("\(urls.count) files selected. Remove all metadata to create clean copies; originals stay unchanged.")
                        .foregroundStyle(.secondary)
                }
                if isImage {
                    GroupBox("Edit fields (lossless)") {
                        Grid(alignment: .leading) {
                            GridRow { Text("Title"); TextField("", text: $title) }
                            GridRow { Text("Author"); TextField("", text: $artist) }
                            GridRow { Text("Copyright"); TextField("", text: $copyright) }
                            GridRow { Text("Description"); TextField("", text: $desc) }
                        }.textFieldStyle(.roundedBorder)
                        HStack {
                            Toggle("Remove location", isOn: $removeGPS)
                            Spacer()
                            Button("Save Edited Copy") {
                                let (t, a, c, d, g) = (title, artist, copyright, desc, removeGPS)
                                runBatch(.metadata, urls) { u in [try MetadataOps.updateImage(u, title: t, artist: a, copyright: c, description: d, removeGPS: g)] }
                            }
                        }
                    }
                }
            }
        } options: { Text("Originals are never modified.").font(.caption).foregroundStyle(.secondary) }
        .task { if let single { fields = await MetadataOps.read(single) } }
    }

    private func removeAll() { runBatch(.metadata, urls) { [try await MetadataOps.removeAll($0)] } }
}

// MARK: - Rename

struct RenameEditor: View {
    let urls: [URL]
    @State private var pattern = "{name}"
    @State private var find = ""
    @State private var replace = ""
    @State private var start = 1
    @State private var caseMode = "Keep"
    @State private var sortByName = true

    private var ordered: [URL] { sortByName ? urls.sorted { $0.lastPathComponent.localizedStandardCompare($1.lastPathComponent) == .orderedAscending } : urls }

    private func newName(_ url: URL, _ i: Int) -> String {
        let base = url.deletingPathExtension().lastPathComponent
        let ext = url.pathExtension
        let date = (try? url.resourceValues(forKeys: [.creationDateKey]).creationDate) ?? Date()
        let df = DateFormatter(); df.dateFormat = "yyyy-MM-dd"
        let width = String(ordered.count + start).count
        var s = pattern.replacingOccurrences(of: "{name}", with: base)
            .replacingOccurrences(of: "{n}", with: String(format: "%0\(width)d", start + i))
            .replacingOccurrences(of: "{date}", with: df.string(from: date))
            .replacingOccurrences(of: "{folder}", with: url.deletingLastPathComponent().lastPathComponent)
        if !find.isEmpty { s = s.replacingOccurrences(of: find, with: replace) }
        switch caseMode {
        case "lowercase": s = s.lowercased()
        case "UPPERCASE": s = s.uppercased()
        case "Title Case": s = s.capitalized
        default: break
        }
        s = s.replacingOccurrences(of: "/", with: "-").replacingOccurrences(of: ":", with: "-")
        return ext.isEmpty ? s : s + "." + ext
    }

    var body: some View {
        EditorScaffold(tool: .batchRename, primary: "Rename \(urls.count) Files", canApply: valid, apply: apply) {
            HSplitView {
                Form {
                    TextField("Pattern", text: $pattern)
                    Text("Tokens: {name} {n} {date} {folder}").font(.caption).foregroundStyle(.secondary)
                    TextField("Find", text: $find)
                    TextField("Replace with", text: $replace)
                    Stepper("Start numbering at \(start)", value: $start, in: 0...100000)
                    Picker("Case", selection: $caseMode) { ForEach(["Keep", "lowercase", "UPPERCASE", "Title Case"], id: \.self) { Text($0) } }
                    Toggle("Order the files by name, as Finder lists them", isOn: $sortByName)
                }
                .formStyle(.grouped).frame(minWidth: 280)
                List(Array(ordered.enumerated()), id: \.offset) { i, u in
                    VStack(alignment: .leading, spacing: 2) {
                        Text(newName(u, i)).font(.callout.weight(.medium))
                        Text(u.lastPathComponent).font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
        } options: {
            if !valid { Label("Two files would get the same name.", systemImage: "exclamationmark.triangle").foregroundStyle(.orange).font(.callout) }
        }
    }

    private var valid: Bool {
        let names = ordered.enumerated().map { newName($0.element, $0.offset).lowercased() }
        return Set(names).count == names.count && !names.contains { $0.hasPrefix(".") || $0.isEmpty }
    }

    private func apply() {
        let pairs = ordered.enumerated().map { ($0.element, $0.element.deletingLastPathComponent().appendingPathComponent(newName($0.element, $0.offset))) }
        JobRunner.shared.run("Renaming \(pairs.count) files", inputs: urls) { _ in
            // Two-phase rename so swaps like a→b, b→a work.
            var temps: [(URL, URL)] = []
            for (src, dst) in pairs where src != dst {
                let tmp = src.deletingLastPathComponent().appendingPathComponent(".tangelo-\(UUID().uuidString)")
                try FileManager.default.moveItem(at: src, to: tmp); temps.append((tmp, dst))
            }
            for (tmp, dst) in temps {
                let final = FileManager.default.fileExists(atPath: dst.path) ? Output.unique(in: dst.deletingLastPathComponent(), base: dst.deletingPathExtension().lastPathComponent, ext: dst.pathExtension) : dst
                try FileManager.default.moveItem(at: tmp, to: final)
            }
            return pairs.map(\.1)
        }
    }
}

// MARK: - Adjust

struct AdjustEditor: View {
    let urls: [URL]
    @State private var adj = ImageAdjustments()
    @State private var source: CIImage?
    @State private var preview: NSImage?
    @State private var showOriginal = false

    var body: some View {
        EditorScaffold(tool: .editImage, primary: urls.count > 1 ? "Save \(urls.count) Copies" : "Save Edited Copy", apply: apply) {
            HStack(spacing: 16) {
                ZStack {
                    if let preview { FittedImage(image: showOriginal ? (source.flatMap { ImageIOKit.cgImage(from: $0) }.map(NSImage.init(cg:)) ?? preview) : preview) }
                    else { ProgressView("Loading preview") }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .onLongPressGesture(minimumDuration: 0.1, pressing: { showOriginal = $0 }, perform: {})
                .help("Press and hold to see the original")
                ScrollView {
                    VStack(alignment: .leading, spacing: 10) {
                        slider("Exposure", $adj.exposure, -2...2)
                        slider("Contrast", $adj.contrast, 0.5...1.5)
                        slider("Saturation", $adj.saturation, 0...2)
                        slider("Warmth", $adj.warmth, -1...1)
                        slider("Tint", $adj.tint, -1...1)
                        slider("Highlights", $adj.highlights, 0...1)
                        slider("Shadows", $adj.shadows, -1...1)
                        slider("Sharpen", $adj.sharpness, 0...2)
                        slider("Noise reduction", $adj.noiseReduction, 0...0.1)
                        slider("Vignette", $adj.vignette, 0...2)
                        slider("Grain", $adj.grain, 0...1)
                        Toggle("Black & white", isOn: $adj.blackAndWhite)
                        Button("Restore Defaults") { adj = ImageAdjustments() }
                    }
                }
                .frame(width: 230)
            }
        } options: { Text("Hold the preview to compare.").font(.caption).foregroundStyle(.secondary) }
        .task {
            if let cg = loadPreviewImage(urls[0], max: 1200) { source = CIImage(cgImage: cg); render() }
        }
        .onChange(of: adj) { render() }
    }

    private func slider(_ name: String, _ v: Binding<Double>, _ r: ClosedRange<Double>) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack { Text(name).font(.callout); Spacer(); Text(String(format: "%.2f", v.wrappedValue)).font(.caption).monospacedDigit().foregroundStyle(.secondary) }
            Slider(value: v, in: r)
        }
    }

    private func render() {
        guard let source, let cg = ImageIOKit.cgImage(from: adj.apply(source)) else { return }
        preview = NSImage(cg: cg)
    }

    private func apply() {
        let a = adj
        runBatch(.editImage, urls) { u in
            let ci = CIImage(cgImage: try ImageIOKit.load(u))
            guard let out = ImageIOKit.cgImage(from: a.apply(ci)) else { throw TangeloError.message("Could not render the photo adjustments.") }
            return [try ImageOps.save(out, like: u, suffix: "edited")]
        }
    }
}

// MARK: - Annotate

struct AnnotateEditor: View {
    let url: URL
    let others: [URL]
    @State private var image: CGImage?
    @State private var annotations: [Annotation] = []
    @State private var kind: AnnotationKind = .arrow
    @State private var color: Color = .red
    @State private var width: CGFloat = 0.006
    @State private var text = "Text"
    @State private var zoom: CGFloat = 2
    @State private var drawing: Annotation?
    @State private var selected: UUID?
    @State private var dragOrigin: [CGPoint]?

    var body: some View {
        EditorScaffold(tool: .annotateImage, primary: "Save Annotated Copy", canApply: !annotations.isEmpty, apply: apply) {
            VStack(spacing: 10) {
                toolbar
                if let image {
                    AspectCanvas(aspect: CGFloat(image.width) / CGFloat(image.height)) {
                        Image(nsImage: NSImage(cg: image)).resizable()
                    } overlay: {
                        canvas
                    }
                } else { ProgressView().frame(maxHeight: .infinity) }
            }
        } options: {
            Text(hint).font(.caption).foregroundStyle(.secondary)
        }
        .task { image = try? ImageIOKit.load(url) }
        .onDeleteCommand { annotations.removeAll { $0.id == selected } }
    }

    private var hint: String {
        switch kind {
        case .arrow: return "Drag from the tail toward the arrowhead. Drag an annotation to move it."
        case .pen: return "Drag to draw."
        case .highlight: return "Drag over the image to highlight."
        case .text: return "Click to place text. Edit the text field above first."
        case .magnifier: return "Choose a zoom level, then drag over the detail to place a magnifier bubble."
        case .number: return "Click to place numbered steps."
        default: return "Drag on the image to draw. Drag an annotation to move it."
        }
    }

    private var toolbar: some View {
        HStack(spacing: 8) {
            Picker("", selection: $kind) {
                ForEach(AnnotationKind.allCases) { k in Image(systemName: k.symbol).tag(k).help(k.rawValue.capitalized) }
            }.pickerStyle(.segmented).frame(maxWidth: 360)
            ColorPicker("", selection: $color).labelsHidden().frame(width: 40)
            if kind == .text { TextField("Text", text: $text).frame(width: 120).textFieldStyle(.roundedBorder) }
            else if kind == .magnifier { Picker("", selection: $zoom) { Text("2×").tag(CGFloat(2)); Text("3×").tag(CGFloat(3)); Text("4×").tag(CGFloat(4)) }.frame(width: 70) }
            else { Slider(value: $width, in: 0.002...0.02).frame(width: 100).help("Line width") }
            Spacer()
            Button { _ = annotations.popLast() } label: { Image(systemName: "arrow.uturn.backward") }.help("Undo last annotation").disabled(annotations.isEmpty)
            Button { annotations.removeAll { $0.id == selected } } label: { Image(systemName: "trash") }.help("Delete selected annotation").disabled(selected == nil)
        }
    }

    private var canvas: some View {
        GeometryReader { g in
            let size = g.size
            Canvas { ctx, s in
                ctx.withCGContext { cg in
                    let all = annotations + (drawing.map { [$0] } ?? [])
                    AnnotationRenderer.draw(all, in: cg, size: s, source: image.map { ImageIOKit.resize($0, to: s) })
                    if let sel = annotations.first(where: { $0.id == selected }) {
                        let r = bounds(sel, s)
                        cg.setStrokeColor(NSColor.tangelo.cgColor); cg.setLineDash(phase: 0, lengths: [4, 3]); cg.setLineWidth(1)
                        cg.stroke(r.insetBy(dx: -6, dy: -6))
                    }
                }
            }
            .contentShape(Rectangle())
            .gesture(DragGesture(minimumDistance: 0).onChanged { v in
                let p = CGPoint(x: v.location.x / size.width, y: v.location.y / size.height)
                let start = CGPoint(x: v.startLocation.x / size.width, y: v.startLocation.y / size.height)
                if drawing == nil, dragOrigin == nil {
                    if let hit = annotations.last(where: { bounds($0, size).insetBy(dx: -8, dy: -8).contains(v.startLocation) }) {
                        selected = hit.id; dragOrigin = hit.points
                    } else {
                        selected = nil
                        drawing = Annotation(kind: kind, points: [start], color: NSColor(color), lineWidth: width, text: text, zoom: zoom,
                                             number: annotations.filter { $0.kind == .number }.count + 1)
                    }
                }
                if let origin = dragOrigin, let i = annotations.firstIndex(where: { $0.id == selected }) {
                    let dx = p.x - start.x, dy = p.y - start.y
                    annotations[i].points = origin.map { CGPoint(x: $0.x + dx, y: $0.y + dy) }
                } else if drawing != nil {
                    if kind == .pen { drawing!.points.append(p) } else if [.text, .number].contains(kind) { drawing!.points = [p] } else { drawing!.points = [start, p] }
                }
            }.onEnded { _ in
                if var d = drawing {
                    if [.text, .number].contains(d.kind) || d.points.count > 1 && hypot(d.end.x - d.start.x, d.end.y - d.start.y) > 0.004 || d.kind == .pen && d.points.count > 3 {
                        if d.points.count == 1 && ![.text, .number].contains(d.kind) { d.points.append(d.points[0]) }
                        annotations.append(d); selected = d.id
                    }
                }
                drawing = nil; dragOrigin = nil
            })
        }
    }

    private func bounds(_ a: Annotation, _ s: CGSize) -> CGRect {
        let pts = a.points.map { CGPoint(x: $0.x * s.width, y: $0.y * s.height) }
        var r = CGRect(x: pts.map(\.x).min() ?? 0, y: pts.map(\.y).min() ?? 0, width: 0, height: 0)
        r.size = CGSize(width: (pts.map(\.x).max() ?? 0) - r.minX, height: (pts.map(\.y).max() ?? 0) - r.minY)
        if a.kind == .text { r.size = CGSize(width: max(40, CGFloat(a.text.count) * a.fontSize * s.width * 0.6), height: a.fontSize * s.width * 1.3) }
        if a.kind == .number { let rad = a.fontSize * s.width; r = CGRect(x: r.minX - rad, y: r.minY - rad, width: rad * 2, height: rad * 2) }
        if a.kind == .magnifier { let rad = max(r.width, r.height) / 2; r = CGRect(x: pts[0].x - rad, y: pts[0].y - rad, width: rad * 2, height: rad * 2) }
        return r
    }

    private func apply() {
        let a = annotations
        runBatch(.annotateImage, [url] + others) { u in
            [try ImageOps.save(AnnotationRenderer.render(try ImageIOKit.load(u), annotations: a), like: u, suffix: "annotated")]
        }
    }
}

// MARK: - Frame

struct FrameEditor: View {
    let urls: [URL]
    @State private var opts = FrameOptions()
    @State private var source: CGImage?
    @State private var useSolid = false
    @State private var solid: Color = .white

    var body: some View {
        EditorScaffold(tool: .frameImage, primary: "Save with Background", apply: apply) {
            HStack(spacing: 16) {
                Group {
                    if let source { FittedImage(image: NSImage(cg: current.render(source))) } else { ProgressView() }
                }.frame(maxWidth: .infinity, maxHeight: .infinity)
                ScrollView {
                    VStack(alignment: .leading, spacing: 12) {
                        Text("Background").font(.callout.weight(.semibold))
                        LazyVGrid(columns: Array(repeating: GridItem(.fixed(36)), count: 4), spacing: 8) {
                            ForEach(GradientPreset.all) { p in
                                RoundedRectangle(cornerRadius: 8)
                                    .fill(LinearGradient(colors: p.colors.map { Color(nsColor: $0) }, startPoint: .topLeading, endPoint: .bottomTrailing))
                                    .frame(width: 36, height: 36)
                                    .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(opts.preset == p && !useSolid && opts.backgroundImage == nil && !opts.transparent ? Color.primary : .clear, lineWidth: 2))
                                    .onTapGesture { opts.preset = p; useSolid = false; opts.backgroundImage = nil; opts.transparent = false }
                                    .help(p.id.capitalized)
                            }
                        }
                        HStack { Toggle("Solid", isOn: $useSolid); ColorPicker("", selection: $solid).labelsHidden() }
                        Toggle("Transparent", isOn: $opts.transparent)
                        Button(opts.backgroundImage == nil ? "Choose Image…" : opts.backgroundImage!.lastPathComponent) { chooseImage() }
                        Picker("Canvas", selection: $opts.ratio) { ForEach(CanvasRatio.allCases) { Text($0.rawValue).tag($0) } }
                        labeled("Padding", $opts.padding, 0...0.3)
                        labeled("Corners", $opts.cornerRadius, 0...0.08)
                        labeled("Shadow", $opts.shadow, 0...1)
                    }
                }.frame(width: 200)
            }
        } options: { if urls.count > 1 { Text("Applies to all \(urls.count) images.").font(.caption).foregroundStyle(.secondary) } }
        .task { source = loadPreviewImage(urls[0], max: 1000) }
    }

    private var current: FrameOptions {
        var o = opts
        o.solidColor = useSolid ? NSColor(solid) : nil
        return o
    }

    private func labeled(_ t: String, _ v: Binding<CGFloat>, _ r: ClosedRange<CGFloat>) -> some View {
        VStack(alignment: .leading, spacing: 2) { Text(t).font(.callout); Slider(value: v, in: r) }
    }

    private func chooseImage() {
        let p = NSOpenPanel(); p.allowedContentTypes = [.image]
        p.message = "Choose a background image"
        if p.runModal() == .OK { opts.backgroundImage = p.url; opts.transparent = false; useSolid = false }
    }

    private func apply() {
        let o = current
        runBatch(.frameImage, urls) { u in [try ImageOps.save(o.render(try ImageIOKit.load(u)), like: u, suffix: "framed", forcePNG: true)] }
    }
}

// MARK: - Crop

struct CropImageEditor: View {
    let urls: [URL]
    @State private var image: CGImage?
    @State private var rects: [RedactionRect] = [RedactionRect(rect: CGRect(x: 0.1, y: 0.1, width: 0.8, height: 0.8))]
    @State private var selected: UUID?
    @State private var ratio: String = "Free"
    private let ratios: [(String, CGFloat?)] = [("Free", nil), ("1:1", 1), ("4:3", 4 / 3), ("3:2", 3 / 2), ("16:9", 16 / 9), ("9:16", 9 / 16), ("4:5", 4 / 5)]

    var body: some View {
        EditorScaffold(tool: .cropImage, primary: urls.count > 1 ? "Crop \(urls.count) Images" : "Save Cropped Copy", canApply: rects.first != nil, apply: apply) {
            VStack {
                Picker("Ratio", selection: $ratio) { ForEach(ratios, id: \.0) { Text($0.0).tag($0.0) } }.pickerStyle(.segmented)
                    .onChange(of: ratio) { applyRatio() }
                if let image {
                    let aspect = CGFloat(image.width) / CGFloat(image.height)
                    AspectCanvas(aspect: aspect) {
                        Image(nsImage: NSImage(cg: image)).resizable()
                    } overlay: {
                        RectEditorOverlay(rects: $rects, selected: $selected, single: true, aspect: lockedAspect, imageAspect: aspect, dimOutside: true)
                    }
                }
            }
        } options: {
            if let r = rects.first?.rect, let image {
                Text("\(Int(r.width * CGFloat(image.width))) × \(Int(r.height * CGFloat(image.height))) px").monospacedDigit().foregroundStyle(.secondary)
            }
            if urls.count > 1 { Text("Uses the same share of each image.").font(.caption).foregroundStyle(.secondary) }
        }
        .task { image = loadPreviewImage(urls[0], max: 1600) }
    }

    /// Ratio in view space = pixel ratio (view preserves image aspect).
    private var lockedAspect: CGFloat? {
        ratios.first(where: { $0.0 == ratio })?.1
    }

    private func applyRatio() {
        guard let r = ratios.first(where: { $0.0 == ratio })?.1, let image else { return }
        let imgAspect = CGFloat(image.width) / CGFloat(image.height)
        // normalized width/height such that (w*W)/(h*H) = r
        var w: CGFloat = 0.9, h = w * imgAspect / r
        if h > 0.9 { h = 0.9; w = h * r / imgAspect }
        rects = [RedactionRect(rect: CGRect(x: (1 - w) / 2, y: (1 - h) / 2, width: w, height: h))]
    }

    private func apply() {
        guard let r = rects.first?.rect else { return }
        runBatch(.cropImage, urls) { u in
            let img = try ImageIOKit.load(u)
            let px = CGRect(x: r.minX * CGFloat(img.width), y: r.minY * CGFloat(img.height), width: r.width * CGFloat(img.width), height: r.height * CGFloat(img.height)).integral
            guard let c = img.cropping(to: px) else { throw TangeloError.message("Keep the crop inside the image.") }
            return [try ImageOps.save(c, like: u, suffix: "cropped")]
        }
    }
}

// MARK: - Resize

struct ResizeEditor: View {
    let urls: [URL]
    enum Mode: String, CaseIterable, Identifiable { case percent = "Percent", longest = "Longest side", width = "Width", height = "Height", exact = "Exact"; var id: String { rawValue } }
    @State private var mode: Mode = .percent
    @State private var percent = 50.0
    @State private var pixels = 1920.0
    @State private var exactW = 1920.0
    @State private var exactH = 1080.0
    @State private var format: ConversionFormat? = nil

    var body: some View {
        EditorScaffold(tool: .resizeImage, primary: "Resize", apply: apply) {
            Form {
                Picker("Resize by", selection: $mode) { ForEach(Mode.allCases) { Text($0.rawValue).tag($0) } }.pickerStyle(.segmented)
                switch mode {
                case .percent: LabeledContent("Scale") { HStack { Slider(value: $percent, in: 5...200, step: 5); Text("\(Int(percent))%").monospacedDigit().frame(width: 50) } }
                case .exact:
                    HStack { TextField("Width", value: $exactW, format: .number); Text("×"); TextField("Height", value: $exactH, format: .number); Text("px") }
                default: HStack { TextField("Pixels", value: $pixels, format: .number); Text("px") }
                }
                Picker("Format", selection: $format) {
                    Text("Keep original").tag(ConversionFormat?.none)
                    ForEach([ConversionFormat.jpg, .png, .heic, .webp, .avif], id: \.self) { Text($0.label).tag(ConversionFormat?.some($0)) }
                }
                Section("Files") { ForEach(urls, id: \.self) { u in Text(u.lastPathComponent) } }
            }.formStyle(.grouped)
        } options: { EmptyView() }
    }

    private func apply() {
        let (m, pc, px, w, h, fmt) = (mode, percent, pixels, exactW, exactH, format)
        runBatch(.resizeImage, urls) { u in
            let img = try ImageIOKit.load(u)
            let iw = CGFloat(img.width), ih = CGFloat(img.height)
            let size: CGSize
            switch m {
            case .percent: size = CGSize(width: iw * pc / 100, height: ih * pc / 100)
            case .longest: let s = px / max(iw, ih); size = CGSize(width: iw * s, height: ih * s)
            case .width: size = CGSize(width: px, height: ih * px / iw)
            case .height: size = CGSize(width: iw * px / ih, height: px)
            case .exact: size = CGSize(width: w, height: h)
            }
            let resized = ImageIOKit.resize(img, to: size)
            let f = fmt ?? ConversionFormat.from(u) ?? .png
            let out = Output.url(for: u, ext: f.fileExtension, suffix: "\(Int(size.width))×\(Int(size.height))")
            try ImageIOKit.write(resized, to: out, format: f, quality: 0.9)
            return [out]
        }
    }
}

// MARK: - Redact image

struct RedactImageEditor: View {
    let url: URL
    @State private var image: CGImage?
    @State private var rects: [RedactionRect] = []
    @State private var selected: UUID?
    @State private var style: RedactionStyle = .blur

    var body: some View {
        EditorScaffold(tool: .redactImage, primary: "Save Redacted Copy", canApply: !rects.isEmpty, apply: apply) {
            VStack {
                HStack {
                    Picker("Style", selection: $style) { ForEach(RedactionStyle.allCases) { Text($0.rawValue).tag($0) } }.pickerStyle(.segmented).frame(width: 260)
                    Spacer()
                    Button("Detect Faces") { detect(faces: true, text: false) }
                    Button("Detect Text") { detect(faces: false, text: true) }
                    Button { rects.removeAll { $0.id == selected } } label: { Image(systemName: "trash") }.disabled(selected == nil).help("Delete Redaction")
                }
                if let image {
                    AspectCanvas(aspect: CGFloat(image.width) / CGFloat(image.height)) {
                        Image(nsImage: NSImage(cg: previewImage(image))).resizable()
                    } overlay: {
                        RectEditorOverlay(rects: $rects, selected: $selected)
                    }
                }
            }
        } options: {
            Text(style == .blur ? "Blur softens details. Use Solid for sensitive information." : "Draw rectangles over areas to hide.")
                .font(.caption).foregroundStyle(.secondary)
        }
        .task { image = loadPreviewImage(url, max: 1400) }
        .onDeleteCommand { rects.removeAll { $0.id == selected } }
    }

    private func previewImage(_ img: CGImage) -> CGImage {
        ImageIOKit.cgImage(from: Redactor.apply(CIImage(cgImage: img), rects: rects.map(\.rect), style: style)) ?? img
    }

    private func detect(faces: Bool, text: Bool) {
        guard let image, let found = try? Redactor.detect(in: image, faces: faces, text: text) else { return }
        if found.isEmpty { Dispatcher.alert("Nothing found", faces ? "No faces were detected." : "No text was detected."); return }
        rects += found.map { RedactionRect(rect: $0) }
    }

    private func apply() {
        let (r, s) = (rects.map(\.rect), style)
        runBatch(.redactImage, [url]) { u in
            let ci = CIImage(cgImage: try ImageIOKit.load(u))
            guard let out = ImageIOKit.cgImage(from: Redactor.apply(ci, rects: r, style: s)) else { throw TangeloError.message("Could not prepare the redacted photo.") }
            // Redacted copies never keep metadata.
            return [try ImageOps.save(out, like: u, suffix: "redacted")]
        }
    }
}

// MARK: - Collage

struct CollageEditor: View {
    @State var urls: [URL]
    @State private var opts = CollageOptions()
    @State private var thumbs: [CGImage] = []
    @State private var bg: Color = .white

    var body: some View {
        EditorScaffold(tool: .createCollage, primary: "Create Collage", apply: apply) {
            HStack(spacing: 16) {
                Group {
                    if let img = previewOptions.render(thumbs) { FittedImage(image: NSImage(cg: img)) } else { ProgressView("Creating collage") }
                }.frame(maxWidth: .infinity, maxHeight: .infinity)
                VStack(alignment: .leading, spacing: 12) {
                    Picker("Layout", selection: $opts.style) { ForEach(CollageStyle.allCases) { Text($0.rawValue).tag($0) } }
                    Toggle("Fill cells (crop edges)", isOn: $opts.fill)
                    VStack(alignment: .leading) { Text("Spacing"); Slider(value: $opts.spacing, in: 0...60) }
                    VStack(alignment: .leading) { Text("Padding"); Slider(value: $opts.padding, in: 0...120) }
                    VStack(alignment: .leading) { Text("Corners"); Slider(value: $opts.cornerRadius, in: 0...80) }
                    ColorPicker("Background", selection: $bg)
                    HStack {
                        Button("Shuffle") { reorder(urls.shuffled()) }
                        Button("Reverse") { reorder(urls.reversed()) }
                    }
                    Spacer()
                }.frame(width: 200)
            }
        } options: { Text("\(urls.count) images").foregroundStyle(.secondary) }
        .task { loadThumbs() }
    }

    private var previewOptions: CollageOptions {
        var o = opts; o.background = NSColor(bg); o.cellSize = 300
        let s: CGFloat = 300 / 800; o.spacing *= s; o.padding *= s; o.cornerRadius *= s
        return o
    }

    private func reorder(_ new: [URL]) { urls = new; loadThumbs() }
    private func loadThumbs() { thumbs = urls.compactMap { loadPreviewImage($0, max: 600) } }

    private func apply() {
        var o = opts; o.background = NSColor(bg)
        let list = urls
        JobRunner.shared.run("Creating collage", inputs: list) { _ in
            let images = try list.map { ImageIOKit.resize(try ImageIOKit.load($0), maxDimension: 2000) }
            guard let img = o.render(images) else { throw TangeloError.message("Could not render the collage.") }
            let out = Output.unique(in: Output.directory(for: list[0]), base: "Collage", ext: "png")
            try ImageIOKit.write(img, to: out, format: .png)
            return [out]
        }
    }
}
