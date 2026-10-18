import AppKit
import SwiftUI

struct WheelItem: Identifiable {
    let id: String
    let title: String
    let symbol: String
    let run: @MainActor ([URL]) -> Void
}

@MainActor
final class WheelModel: ObservableObject {
    @Published var items: [WheelItem] = []
    @Published var highlighted: Int?
    @Published var kind: WheelKind = .conversions
    @Published var summary = ""
    @Published var symbol = "doc"
    @Published var appeared = false
    var urls: [URL] = []
}

@MainActor
final class WheelController {
    static let shared = WheelController()
    static let size: CGFloat = 470
    static let inner: CGFloat = 62
    static let outer: CGFloat = 210

    let model = WheelModel()
    private var panel: NSPanel?
    private var hideWork: DispatchWorkItem?

    var isVisible: Bool { panel?.isVisible ?? false }

    func items(for kind: WheelKind, urls: [URL]) -> [WheelItem] {
        var items: [WheelItem]
        switch kind {
        case .conversions:
            items = Dispatcher.conversionTargets(for: urls).map { f in
                WheelItem(id: f.rawValue, title: f.label, symbol: f.category.symbol) { Dispatcher.convert($0, to: f) }
            }
        case .tools:
            items = FileTool.available(for: urls).map { t in
                WheelItem(id: t.rawValue, title: t.title, symbol: t.symbol) { Dispatcher.run(t, on: $0) }
            }
        case .actions:
            items = FileAction.allCases.map { a in
                WheelItem(id: a.rawValue, title: a.title, symbol: a.symbol) { Dispatcher.perform(a, on: $0) }
            }
        }
        if items.count > 12 {
            let all = items
            items = Array(items.prefix(11))
            items.append(WheelItem(id: "more", title: "More…", symbol: "ellipsis.circle") { urls in
                ChooserWindow.show(kind: kind, items: all, urls: urls)
            })
        }
        return items
    }

    func show(_ kind: WheelKind, for urls: [URL], at point: NSPoint) {
        let items = items(for: kind, urls: urls)
        guard !items.isEmpty else { return }
        hideWork?.cancel()
        model.items = items
        model.kind = kind
        model.urls = urls
        model.highlighted = nil
        let cats = Set(urls.map(FileCategory.of))
        model.symbol = cats.count == 1 ? cats.first!.symbol : "square.stack.3d.up"
        model.summary = urls.count == 1 ? urls[0].pathExtension.uppercased() : "\(urls.count) files"
        if panel == nil { panel = makePanel() }
        let s = Self.size
        var origin = NSPoint(x: point.x - s / 2, y: point.y - s / 2)
        if let screen = NSScreen.screens.first(where: { $0.frame.contains(point) }) {
            let f = screen.frame
            origin.x = min(max(origin.x, f.minX), f.maxX - s)
            origin.y = min(max(origin.y, f.minY), f.maxY - s)
        }
        panel?.setFrame(NSRect(origin: origin, size: CGSize(width: s, height: s)), display: true)
        model.appeared = false
        panel?.orderFrontRegardless()
        withAnimation(.spring(response: 0.28, dampingFraction: 0.72)) { model.appeared = true }
        Feedback.highlight()
    }

    func hide() {
        hideWork?.cancel()
        model.appeared = false
        panel?.orderOut(nil)
        model.highlighted = nil
    }

    func dragEnded() {
        guard isVisible else { return }
        let work = DispatchWorkItem { [weak self] in self?.hide() }
        hideWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.35, execute: work)
    }

    func perform(index: Int, urls: [URL]) {
        guard model.items.indices.contains(index) else { return }
        let item = model.items[index]
        hide()
        let dropped = urls.isEmpty ? model.urls : urls
        DispatchQueue.main.async { item.run(dropped) }
    }

    private func makePanel() -> NSPanel {
        let s = Self.size
        let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: s, height: s), styleMask: [.borderless, .nonactivatingPanel],
                            backing: .buffered, defer: false)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.level = .popUpMenu
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        let drop = WheelDropView(frame: NSRect(x: 0, y: 0, width: s, height: s))
        let host = NSHostingView(rootView: WheelView(model: model))
        host.frame = drop.bounds
        host.autoresizingMask = [.width, .height]
        drop.addSubview(host)
        panel.contentView = drop
        return panel
    }
}

/// Accepts the in-flight file drag and maps cursor angle to a petal.
final class WheelDropView: NSView {
    override init(frame: NSRect) {
        super.init(frame: frame)
        registerForDraggedTypes([.fileURL])
    }
    required init?(coder: NSCoder) { fatalError() }

    override func hitTest(_ point: NSPoint) -> NSView? { self }

    @MainActor private func index(at windowPoint: NSPoint) -> Int? {
        let p = convert(windowPoint, from: nil)
        let dx = p.x - bounds.midX, dy = p.y - bounds.midY
        let dist = hypot(dx, dy)
        let model = WheelController.shared.model
        guard dist > WheelController.inner, dist < WheelController.outer + 30, !model.items.isEmpty else { return nil }
        var angle = atan2(dx, dy)
        if angle < 0 { angle += 2 * .pi }
        let seg = 2 * .pi / Double(model.items.count)
        return Int((angle + seg / 2).truncatingRemainder(dividingBy: 2 * .pi) / seg) % model.items.count
    }

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation { draggingUpdated(sender) }

    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation {
        MainActor.assumeIsolated {
            let i = index(at: sender.draggingLocation)
            let model = WheelController.shared.model
            if i != model.highlighted {
                withAnimation(.easeOut(duration: 0.12)) { model.highlighted = i }
                if i != nil { Feedback.highlight() }
            }
            return i == nil ? [] : .copy
        }
    }

    override func draggingExited(_ sender: NSDraggingInfo?) {
        MainActor.assumeIsolated { WheelController.shared.model.highlighted = nil }
    }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        MainActor.assumeIsolated {
            guard let i = index(at: sender.draggingLocation) else { WheelController.shared.hide(); return false }
            let urls = (sender.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL]) ?? []
            WheelController.shared.perform(index: i, urls: urls)
            return true
        }
    }
}

// MARK: - Drawing

struct Petal: Shape {
    var index: Int
    var count: Int
    var inner: CGFloat
    var outer: CGFloat
    var gap: CGFloat = 0.035

    func path(in rect: CGRect) -> Path {
        let c = CGPoint(x: rect.midX, y: rect.midY)
        let seg = 2 * Double.pi / Double(count)
        let mid = Double(index) * seg
        let a0 = mid - seg / 2 + gap, a1 = mid + seg / 2 - gap
        var p = Path()
        p.addArc(center: c, radius: outer, startAngle: .radians(a0 - .pi / 2), endAngle: .radians(a1 - .pi / 2), clockwise: false)
        p.addArc(center: c, radius: inner, startAngle: .radians(a1 - .pi / 2), endAngle: .radians(a0 - .pi / 2), clockwise: true)
        p.closeSubpath()
        return p
    }
}

struct WheelView: View {
    @ObservedObject var model: WheelModel

    var body: some View {
        let n = model.items.count
        let inner = WheelController.inner, outer = WheelController.outer
        ZStack {
            Circle()
                .fill(.ultraThinMaterial)
                .frame(width: outer * 2 + 8, height: outer * 2 + 8)
                .shadow(color: .black.opacity(0.25), radius: 18, y: 6)
            ForEach(Array(model.items.enumerated()), id: \.element.id) { i, item in
                let on = model.highlighted == i
                Petal(index: i, count: n, inner: inner + 4, outer: on ? outer + 8 : outer)
                    .fill(on ? AnyShapeStyle(LinearGradient(colors: [Color(red: 1, green: 0.55, blue: 0.1), .tangelo], startPoint: .top, endPoint: .bottom))
                             : AnyShapeStyle(Color.primary.opacity(0.06)))
                Petal(index: i, count: n, inner: inner + 4, outer: on ? outer + 8 : outer)
                    .stroke(Color.primary.opacity(on ? 0 : 0.08), lineWidth: 1)
                label(item, on: on, at: angle(i, n), radius: (inner + outer) / 2 + 6)
            }
            VStack(spacing: 3) {
                Image(systemName: model.highlighted == nil ? model.symbol : "arrow.down.circle.fill")
                    .font(.system(size: 22, weight: .medium))
                    .foregroundStyle(model.highlighted == nil ? Color.secondary : Color.tangelo)
                Text(model.highlighted.map { model.items[$0].title } ?? model.summary)
                    .font(.system(size: 11, weight: .semibold)).lineLimit(1)
                Text(model.kind == .conversions ? "Convert" : model.kind == .tools ? "Tools" : "Actions")
                    .font(.system(size: 9)).foregroundStyle(.secondary)
            }
            .frame(width: inner * 2 - 8, height: inner * 2 - 8)
            .background(Circle().fill(.regularMaterial))
        }
        .frame(width: WheelController.size, height: WheelController.size)
        .scaleEffect(model.appeared ? 1 : 0.6)
        .opacity(model.appeared ? 1 : 0)
        .rotationEffect(.degrees(model.appeared ? 0 : -25))
    }

    private func angle(_ i: Int, _ n: Int) -> Double { Double(i) * 2 * .pi / Double(max(n, 1)) }

    private func label(_ item: WheelItem, on: Bool, at a: Double, radius: CGFloat) -> some View {
        VStack(spacing: 4) {
            Image(systemName: item.symbol).font(.system(size: on ? 20 : 17, weight: .medium))
            Text(item.title).font(.system(size: on ? 12 : 11, weight: .semibold)).lineLimit(1).minimumScaleFactor(0.7)
        }
        .frame(width: 76)
        .foregroundStyle(on ? Color.white : Color.primary)
        .offset(x: radius * sin(a), y: -radius * cos(a))
    }
}

// MARK: - Chooser for overflow items and the menu-bar drop zone

@MainActor
enum ChooserWindow {
    private static var window: NSWindow?

    static func show(kind: WheelKind, items: [WheelItem], urls: [URL]) {
        let view = ChooserView(title: kind == .conversions ? "Convert to" : kind == .tools ? "Tools" : "Actions", items: items) { item in
            window?.close()
            item.run(urls)
        }
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 440, height: 360), styleMask: [.titled, .closable, .fullSizeContentView], backing: .buffered, defer: false)
        w.titlebarAppearsTransparent = true
        w.title = urls.count == 1 ? urls[0].lastPathComponent : "\(urls.count) files"
        w.contentView = NSHostingView(rootView: view)
        w.isReleasedWhenClosed = false
        w.center()
        window = w
        NSApp.activate(ignoringOtherApps: true)
        w.makeKeyAndOrderFront(nil)
    }
}

struct ChooserView: View {
    let title: String
    let items: [WheelItem]
    let choose: (WheelItem) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(title).font(.headline)
            ScrollView {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 92), spacing: 10)], spacing: 10) {
                    ForEach(items) { item in
                        Button { choose(item) } label: {
                            VStack(spacing: 6) {
                                Image(systemName: item.symbol).font(.title2)
                                Text(item.title).font(.caption.weight(.semibold)).lineLimit(1)
                            }
                            .frame(maxWidth: .infinity, minHeight: 70)
                            .background(Color.primary.opacity(0.05), in: RoundedRectangle(cornerRadius: 12))
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
        }
        .padding(20)
        .frame(width: 440, height: 360)
    }
}
