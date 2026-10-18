import AppKit

enum WheelKind: String, CaseIterable, Identifiable {
    case conversions, tools, actions
    var id: String { rawValue }
    var title: String {
        switch self {
        case .conversions: return "Conversion wheel"
        case .tools: return "Tools wheel"
        case .actions: return "Actions wheel"
        }
    }
    var defaultsKey: String { "\(rawValue)DragModifierMask" }
    var defaultMask: NSEvent.ModifierFlags {
        switch self {
        case .conversions: return .shift
        case .tools: return .command
        case .actions: return .control
        }
    }
    var mask: NSEvent.ModifierFlags {
        let raw = UserDefaults.standard.object(forKey: defaultsKey) as? UInt
        return raw.map { NSEvent.ModifierFlags(rawValue: $0) } ?? defaultMask
    }
}

extension NSEvent.ModifierFlags {
    static let relevant: NSEvent.ModifierFlags = [.shift, .control, .option, .command]
    var symbols: String {
        var s = ""
        if contains(.control) { s += "⌃" }
        if contains(.option) { s += "⌥" }
        if contains(.shift) { s += "⇧" }
        if contains(.command) { s += "⌘" }
        return s.isEmpty ? "None" : s
    }
}

/// Watches system-wide file drags. Tapping a configured modifier mid-drag opens a wheel
/// under the cursor; shaking the cursor opens a new Float (when enabled).
@MainActor
final class DragMonitor {
    static let shared = DragMonitor()

    private var monitors: [Any] = []
    private var dragChangeCount = NSPasteboard(name: .drag).changeCount
    private var dragIsFiles = false
    private var previousFlags: NSEvent.ModifierFlags = []
    private var shakeSamples: [(x: CGFloat, t: TimeInterval)] = []
    private var lastShake: TimeInterval = 0
    private(set) var running = false

    func start() {
        guard !running else { return }
        running = true
        let drag = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDragged]) { [weak self] e in
            MainActor.assumeIsolated { self?.dragged(e) }
        }
        let flags = NSEvent.addGlobalMonitorForEvents(matching: [.flagsChanged]) { [weak self] e in
            MainActor.assumeIsolated { self?.flagsChanged(e.modifierFlags) }
        }
        let up = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseUp]) { [weak self] _ in
            MainActor.assumeIsolated { self?.mouseUp() }
        }
        monitors = [drag, flags, up].compactMap { $0 }
        NSLog("[Tangelo] Drag shortcut monitor started")
    }

    func stop() {
        monitors.forEach(NSEvent.removeMonitor)
        monitors = []
        running = false
    }

    private func currentDragURLs() -> [URL] {
        let pb = NSPasteboard(name: .drag)
        return (pb.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL]) ?? []
    }

    private func dragged(_ e: NSEvent) {
        let pb = NSPasteboard(name: .drag)
        if pb.changeCount != dragChangeCount {
            dragChangeCount = pb.changeCount
            dragIsFiles = pb.types?.contains(.fileURL) ?? false
            previousFlags = e.modifierFlags.intersection(.relevant)
            shakeSamples.removeAll()
        }
        guard dragIsFiles else { return }
        // Some modifier changes only show up on drag events; treat them like flagsChanged.
        let flags = e.modifierFlags.intersection(.relevant)
        if flags != previousFlags { flagsChanged(flags) }
        detectShake(at: NSEvent.mouseLocation.x)
    }

    private func flagsChanged(_ raw: NSEvent.ModifierFlags) {
        let flags = raw.intersection(.relevant)
        defer { previousFlags = flags }
        guard dragIsFiles, NSEvent.pressedMouseButtons & 1 == 1, flags != previousFlags, !flags.isEmpty else { return }
        guard !WheelController.shared.isVisible else { return }
        for kind in WheelKind.allCases where kind.mask == flags && UserDefaults.standard.object(forKey: "\(kind.rawValue)WheelEnabled") as? Bool ?? true {
            let urls = currentDragURLs()
            guard !urls.isEmpty else { return }
            WheelController.shared.show(kind, for: urls, at: NSEvent.mouseLocation)
            return
        }
    }

    private func detectShake(at x: CGFloat) {
        guard UserDefaults.standard.object(forKey: "shakeToFloat") as? Bool ?? true else { return }
        let now = ProcessInfo.processInfo.systemUptime
        shakeSamples.append((x, now))
        shakeSamples.removeAll { now - $0.t > 0.6 }
        guard shakeSamples.count > 6, now - lastShake > 1.5, !WheelController.shared.isVisible else { return }
        var reversals = 0, lastDir: CGFloat = 0, travel: CGFloat = 0
        for i in 1..<shakeSamples.count {
            let dx = shakeSamples[i].x - shakeSamples[i - 1].x
            guard abs(dx) > 2 else { continue }
            travel += abs(dx)
            let dir: CGFloat = dx > 0 ? 1 : -1
            if lastDir != 0, dir != lastDir { reversals += 1 }
            lastDir = dir
        }
        if reversals >= 4, travel > 250 {
            lastShake = now
            shakeSamples.removeAll()
            FloatManager.shared.newEmptyFloat(near: NSEvent.mouseLocation)
        }
    }

    private func mouseUp() {
        dragIsFiles = false
        WheelController.shared.dragEnded()
    }
}
