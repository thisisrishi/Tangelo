import AppKit
import QuickLookThumbnailing
import QuickLookUI
import SwiftUI

@MainActor
final class FloatModel: ObservableObject, Identifiable {
    let id = UUID()
    @Published var urls: [URL] = [] { didSet { FloatManager.shared.save() } }
    @Published var selection: Set<URL> = []
    @Published var collapsed = false
    @Published var targeted = false

    var selectedOrAll: [URL] { selection.isEmpty ? urls : urls.filter(selection.contains) }

    func add(_ new: [URL]) {
        let fresh = new.filter { !urls.contains($0) }
        urls.append(contentsOf: fresh)
    }
    func remove(_ items: [URL]) {
        urls.removeAll(where: items.contains)
        selection.subtract(items)
    }
}

final class FloatPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    var model: FloatModel?

    override func keyDown(with event: NSEvent) {
        guard let model else { return super.keyDown(with: event) }
        MainActor.assumeIsolated {
            switch event.keyCode {
            case 49: QuickLook.shared.show(model.selectedOrAll)                       // space
            case 51, 117: model.remove(Array(model.selection))                        // delete
            case 0 where event.modifierFlags.contains(.command): model.selection = Set(model.urls) // ⌘A
            case 53: model.selection = []                                              // esc
            default: super.keyDown(with: event)
            }
        }
    }
}

@MainActor
final class FloatManager: ObservableObject {
    static let shared = FloatManager()
    @Published private(set) var floats: [(model: FloatModel, panel: FloatPanel)] = []
    private var restoring = false

    func newFloat(with urls: [URL], near point: NSPoint? = nil) {
        let model = FloatModel()
        model.urls = urls
        let panel = makePanel(model)
        let p = point ?? NSEvent.mouseLocation
        panel.setFrameOrigin(NSPoint(x: p.x - 120, y: p.y - 150))
        panel.orderFrontRegardless()
        floats.append((model, panel))
        save()
    }

    func newEmptyFloat(near point: NSPoint) { newFloat(with: [], near: point) }

    func addToFrontmost(_ urls: [URL]) {
        if let f = floats.last { f.model.add(urls); f.panel.orderFrontRegardless() } else { newFloat(with: urls) }
    }

    func close(_ model: FloatModel) {
        guard let i = floats.firstIndex(where: { $0.model === model }) else { return }
        floats[i].panel.orderOut(nil)
        floats.remove(at: i)
        save()
    }

    func closeAll() { floats.forEach { $0.panel.orderOut(nil) }; floats.removeAll(); save() }

    private func makePanel(_ model: FloatModel) -> FloatPanel {
        let panel = FloatPanel(contentRect: NSRect(x: 0, y: 0, width: 250, height: 300),
                               styleMask: [.borderless, .nonactivatingPanel, .resizable, .fullSizeContentView],
                               backing: .buffered, defer: false)
        panel.model = model
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.level = .floating
        panel.isMovableByWindowBackground = true
        panel.hidesOnDeactivate = false
        panel.minSize = NSSize(width: 180, height: 120)
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.contentView = NSHostingView(rootView: FloatView(model: model))
        return panel
    }

    // Persistence: floats survive relaunches.
    func save() {
        guard !restoring else { return }
        let data = floats.map { f -> [String: Any] in
            ["paths": f.model.urls.map(\.path), "frame": NSStringFromRect(f.panel.frame)]
        }
        UserDefaults.standard.set(data, forKey: "floats.v1")
    }

    func restore() {
        guard UserDefaults.standard.object(forKey: "restoreFloats") as? Bool ?? true,
              let saved = UserDefaults.standard.array(forKey: "floats.v1") as? [[String: Any]] else { return }
        restoring = true
        for entry in saved {
            let urls = (entry["paths"] as? [String] ?? []).map { URL(fileURLWithPath: $0) }.filter { FileManager.default.fileExists(atPath: $0.path) }
            guard !urls.isEmpty else { continue }
            let model = FloatModel(); model.urls = urls
            let panel = makePanel(model)
            if let f = entry["frame"] as? String { panel.setFrame(NSRectFromString(f), display: false) }
            panel.orderFrontRegardless()
            floats.append((model, panel))
        }
        restoring = false
    }
}

// MARK: - View

struct FloatView: View {
    @ObservedObject var model: FloatModel
    @AppStorage("removeAfterDragOut") private var removeAfterDragOut = false

    var body: some View {
        VStack(spacing: 0) {
            header
            if !model.collapsed {
                Divider().opacity(0.4)
                content
            }
        }
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 18, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 18, style: .continuous)
            .strokeBorder(model.targeted ? Color.tangelo : Color.white.opacity(0.15), lineWidth: model.targeted ? 2.5 : 1))
        .onDrop(of: [.fileURL], isTargeted: $model.targeted) { providers in
            loadURLs(providers) { model.add($0) }
            return true
        }
    }

    private var header: some View {
        HStack(spacing: 8) {
            DragAllHandle(urls: model.selectedOrAll) {
                if removeAfterDragOut { model.remove(model.selectedOrAll) }
            }
            .frame(width: 26, height: 26)
            .help("Drag \(model.selection.isEmpty ? "all" : "selected") files out")
            VStack(alignment: .leading, spacing: 0) {
                Text(model.urls.isEmpty ? "Float" : "\(model.urls.count) item\(model.urls.count == 1 ? "" : "s")").font(.system(size: 12, weight: .semibold))
                if !model.selection.isEmpty { Text("\(model.selection.count) selected").font(.system(size: 10)).foregroundStyle(.secondary) }
            }
            Spacer()
            Menu {
                actionsMenu(for: model.selectedOrAll)
                Divider()
                Button("Select All") { model.selection = Set(model.urls) }
                Button("Remove Selected") { model.remove(Array(model.selection)) }.disabled(model.selection.isEmpty)
                Button("Clear Float") { model.urls = []; model.selection = [] }
            } label: { Image(systemName: "ellipsis.circle") }
                .menuStyle(.borderlessButton).menuIndicator(.hidden).frame(width: 22)
            Button { withAnimation(.spring(duration: 0.25)) { model.collapsed.toggle() } } label: {
                Image(systemName: model.collapsed ? "chevron.down" : "chevron.up")
            }.buttonStyle(.plain).help(model.collapsed ? "Expand Float" : "Collapse Float")
            Button { FloatManager.shared.close(model) } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary) }
                .buttonStyle(.plain).help("Close Float without deleting files")
        }
        .padding(.horizontal, 10).padding(.vertical, 8)
    }

    @ViewBuilder private var content: some View {
        if model.urls.isEmpty {
            VStack(spacing: 8) {
                Image(systemName: "tray.and.arrow.down").font(.system(size: 28)).foregroundStyle(.secondary)
                Text("Drop files here").font(.callout).foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            ScrollView {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 72), spacing: 8)], spacing: 8) {
                    ForEach(model.urls, id: \.self) { url in
                        FloatTile(url: url, selected: model.selection.contains(url))
                            .onTapGesture(count: 2) { NSWorkspace.shared.open(url) }
                            .simultaneousGesture(TapGesture().onEnded { toggle(url) })
                            .onDrag { NSItemProvider(object: url as NSURL) }
                            .contextMenu {
                                let targets = model.selection.contains(url) ? model.selectedOrAll : [url]
                                Button("Open") { targets.forEach { NSWorkspace.shared.open($0) } }
                                Button("Quick Look") { QuickLook.shared.show(targets) }
                                Button("Reveal in Finder") { NSWorkspace.shared.activateFileViewerSelecting(targets) }
                                Divider()
                                actionsMenu(for: targets)
                                Divider()
                                Button("Remove from Float") { model.remove(targets) }
                            }
                    }
                }
                .padding(10)
            }
        }
    }

    @ViewBuilder private func actionsMenu(for urls: [URL]) -> some View {
        let targets = Dispatcher.conversionTargets(for: urls)
        if !targets.isEmpty {
            Menu("Convert to") { ForEach(targets) { f in Button(f.label) { Dispatcher.convert(urls, to: f) } } }
        }
        let tools = FileTool.available(for: urls)
        if !tools.isEmpty {
            Menu("Tools") { ForEach(tools) { t in Button { Dispatcher.run(t, on: urls) } label: { Label(t.title, systemImage: t.symbol) } } }
        }
        Menu("Actions") {
            ForEach(FileAction.allCases.filter { $0 != .float }) { a in Button { Dispatcher.perform(a, on: urls) } label: { Label(a.title, systemImage: a.symbol) } }
        }
    }

    private func toggle(_ url: URL) {
        if NSEvent.modifierFlags.contains(.command) || NSEvent.modifierFlags.contains(.shift) {
            if model.selection.contains(url) { model.selection.remove(url) } else { model.selection.insert(url) }
        } else {
            model.selection = model.selection == [url] ? [] : [url]
        }
    }
}

struct FloatTile: View {
    let url: URL
    let selected: Bool
    @State private var thumb: NSImage?

    var body: some View {
        VStack(spacing: 4) {
            Group {
                if let thumb { Image(nsImage: thumb).resizable().aspectRatio(contentMode: .fit) }
                else { Image(nsImage: NSWorkspace.shared.icon(forFile: url.path)).resizable().aspectRatio(contentMode: .fit) }
            }
            .frame(width: 56, height: 56)
            .shadow(color: .black.opacity(0.15), radius: 2, y: 1)
            Text(url.lastPathComponent).font(.system(size: 10)).lineLimit(2).multilineTextAlignment(.center).truncationMode(.middle)
        }
        .padding(5)
        .frame(width: 76)
        .background(selected ? Color.tangelo.opacity(0.22) : .clear, in: RoundedRectangle(cornerRadius: 8))
        .contentShape(Rectangle())
        .task(id: url) { thumb = await Thumbnails.image(for: url, size: 112) }
        .help(url.path)
    }
}

enum Thumbnails {
    static func image(for url: URL, size: CGFloat) async -> NSImage? {
        let req = QLThumbnailGenerator.Request(fileAt: url, size: CGSize(width: size, height: size), scale: 2, representationTypes: .thumbnail)
        return try? await QLThumbnailGenerator.shared.generateBestRepresentation(for: req).nsImage
    }
}

/// Starts an AppKit drag session carrying every URL (SwiftUI's onDrag only carries one).
struct DragAllHandle: NSViewRepresentable {
    let urls: [URL]
    let onEnded: () -> Void

    func makeNSView(context: Context) -> DragAllView { DragAllView() }
    func updateNSView(_ v: DragAllView, context: Context) { v.urls = urls; v.onEnded = onEnded }

    final class DragAllView: NSView, NSDraggingSource {
        var urls: [URL] = []
        var onEnded: () -> Void = {}

        override func draw(_ dirtyRect: NSRect) {
            NSColor.tangelo.withAlphaComponent(0.18).setFill()
            NSBezierPath(roundedRect: bounds, xRadius: 7, yRadius: 7).fill()
            let config = NSImage.SymbolConfiguration(pointSize: 13, weight: .semibold).applying(.init(paletteColors: [.tangelo]))
            guard let img = NSImage(systemSymbolName: "paperclip", accessibilityDescription: "Drag files")?.withSymbolConfiguration(config) else { return }
            let s = img.size
            img.draw(in: NSRect(x: bounds.midX - s.width / 2, y: bounds.midY - s.height / 2, width: s.width, height: s.height))
        }
        override func mouseDown(with event: NSEvent) {}
        override func mouseDragged(with event: NSEvent) {
            guard !urls.isEmpty else { return }
            let items: [NSDraggingItem] = urls.enumerated().map { i, url in
                let item = NSDraggingItem(pasteboardWriter: url as NSURL)
                let icon = NSWorkspace.shared.icon(forFile: url.path)
                item.setDraggingFrame(NSRect(x: CGFloat(i % 4) * 6, y: CGFloat(i % 4) * -6, width: 48, height: 48), contents: icon)
                return item
            }
            beginDraggingSession(with: items, event: event, source: self)
        }
        func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation {
            context == .outsideApplication ? [.copy, .move, .link, .generic] : [.copy, .generic]
        }
        func draggingSession(_ session: NSDraggingSession, endedAt screenPoint: NSPoint, operation: NSDragOperation) {
            if operation != [] { onEnded() }
        }
    }
}

func loadURLs(_ providers: [NSItemProvider], _ completion: @escaping @MainActor ([URL]) -> Void) {
    let group = DispatchGroup()
    final class Collector: @unchecked Sendable { var urls: [URL] = []; let lock = NSLock() }
    let c = Collector()
    for p in providers where p.hasItemConformingToTypeIdentifier("public.file-url") {
        group.enter()
        _ = p.loadObject(ofClass: URL.self) { url, _ in
            if let url { c.lock.lock(); c.urls.append(url); c.lock.unlock() }
            group.leave()
        }
    }
    group.notify(queue: .main) { MainActor.assumeIsolated { completion(c.urls) } }
}

// MARK: - Quick Look

@MainActor
final class QuickLook: NSObject, @preconcurrency QLPreviewPanelDataSource {
    static let shared = QuickLook()
    private var items: [URL] = []

    func show(_ urls: [URL]) {
        guard !urls.isEmpty, let panel = QLPreviewPanel.shared() else { return }
        items = urls
        panel.dataSource = self
        panel.reloadData()
        NSApp.activate(ignoringOtherApps: true)
        panel.makeKeyAndOrderFront(nil)
    }

    func numberOfPreviewItems(in panel: QLPreviewPanel!) -> Int { items.count }
    func previewPanel(_ panel: QLPreviewPanel!, previewItemAt index: Int) -> QLPreviewItem! { items[index] as NSURL }
}
