import AppKit
import ServiceManagement
import SwiftUI

@main
struct TangeloApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate

    var body: some Scene {
        MenuBarExtra {
            MenuBarView()
                .environmentObject(History.shared)
                .environmentObject(JobRunner.shared)
        } label: {
            Image(systemName: "circle.hexagongrid.fill")
        }
        .menuBarExtraStyle(.window)

        Settings {
            SettingsView().environmentObject(History.shared)
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        MainActor.assumeIsolated {
            if SelfTest.runIfRequested() { return }
            DragMonitor.shared.start()
            FloatManager.shared.restore()
            if !UserDefaults.standard.bool(forKey: "didOnboard") { WelcomeWindow.show() }
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        MainActor.assumeIsolated { FloatManager.shared.save() }
    }

    /// Files opened via Dock/Finder "Open With" or `open -a Tangelo file` land in a Float.
    func application(_ application: NSApplication, open urls: [URL]) {
        MainActor.assumeIsolated { FloatManager.shared.newFloat(with: urls) }
    }
}

// MARK: - Menu bar popover

struct MenuBarView: View {
    @EnvironmentObject var history: History
    @EnvironmentObject var runner: JobRunner
    @State private var dropped: [URL] = []
    @State private var targeted = false
    @State private var tab: WheelKind = .conversions

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Image(systemName: "circle.hexagongrid.fill").foregroundStyle(Color.tangelo).font(.title2)
                Text("Tangelo").font(.headline)
                Spacer()
                if runner.isBusy { ProgressView().controlSize(.small) }
            }
            if dropped.isEmpty { dropZone } else { chooser }
            recent
            Divider()
            HStack(spacing: 14) {
                Button { FloatManager.shared.newEmptyFloat(near: NSEvent.mouseLocation) } label: { Label("New Float", systemImage: "paperclip") }
                Button { finderSelection() } label: { Label("Finder Selection", systemImage: "cursorarrow.click.2") }
                Spacer()
                SettingsLink { Image(systemName: "gearshape") }.help("Settings")
                Button { NSApp.terminate(nil) } label: { Image(systemName: "power") }.help("Quit Tangelo")
            }
            .buttonStyle(.plain)
            .font(.callout)
        }
        .padding(14)
        .frame(width: 340)
    }

    private var dropZone: some View {
        VStack(spacing: 6) {
            Image(systemName: "arrow.down.doc").font(.system(size: 26)).foregroundStyle(targeted ? Color.tangelo : .secondary)
            Text("Drop files to convert or edit").font(.callout.weight(.medium))
            Text("Or while dragging anywhere, tap \(WheelKind.conversions.mask.symbols) convert · \(WheelKind.tools.mask.symbols) tools · \(WheelKind.actions.mask.symbols) actions. Shake to float.")
                .font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity).padding(.vertical, 18)
        .background(RoundedRectangle(cornerRadius: 12).strokeBorder(targeted ? Color.tangelo : Color.secondary.opacity(0.4), style: StrokeStyle(lineWidth: 1.5, dash: [6, 4])))
        .onDrop(of: [.fileURL], isTargeted: $targeted) { providers in
            loadURLs(providers) { dropped = $0 }
            return true
        }
    }

    private var chooser: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(dropped.count == 1 ? dropped[0].lastPathComponent : "\(dropped.count) files").font(.callout.weight(.semibold)).lineLimit(1)
                Spacer()
                Button { dropped = [] } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary) }.buttonStyle(.plain)
            }
            Picker("", selection: $tab) {
                Text("Convert").tag(WheelKind.conversions); Text("Tools").tag(WheelKind.tools); Text("Actions").tag(WheelKind.actions)
            }.pickerStyle(.segmented).labelsHidden()
            let items = WheelController.shared.items(for: tab, urls: dropped).filter { $0.id != "more" }
            let all = tab == .conversions ? Dispatcher.conversionTargets(for: dropped).map { f in WheelItem(id: f.rawValue, title: f.label, symbol: f.category.symbol) { Dispatcher.convert($0, to: f) } }
                    : tab == .tools ? FileTool.available(for: dropped).map { t in WheelItem(id: t.rawValue, title: t.title, symbol: t.symbol) { Dispatcher.run(t, on: $0) } } : items
            if all.isEmpty { Text("No options for these files.").font(.caption).foregroundStyle(.secondary) }
            LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 6), count: 4), spacing: 6) {
                ForEach(all) { item in
                    Button {
                        let urls = dropped; dropped = []
                        item.run(urls)
                    } label: {
                        VStack(spacing: 3) { Image(systemName: item.symbol); Text(item.title).font(.system(size: 10, weight: .medium)).lineLimit(1) }
                            .frame(maxWidth: .infinity, minHeight: 46)
                            .background(Color.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 8))
                    }.buttonStyle(.plain)
                }
            }
        }
    }

    @ViewBuilder private var recent: some View {
        if !history.entries.isEmpty {
            VStack(alignment: .leading, spacing: 4) {
                Text("Recent").font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                ForEach(history.entries.prefix(5)) { e in
                    HStack {
                        Image(systemName: e.error == nil ? "checkmark.circle" : "exclamationmark.triangle").foregroundStyle(e.error == nil ? .green : .orange)
                        Text(e.title).lineLimit(1).font(.caption)
                        Spacer()
                        if e.error == nil {
                            Button { NSWorkspace.shared.activateFileViewerSelecting(e.outputs) } label: { Image(systemName: "magnifyingglass") }.buttonStyle(.plain).help("Reveal")
                            Button { history.trashOutputs(of: e) } label: { Image(systemName: "arrow.uturn.backward") }.buttonStyle(.plain).help("Undo — move outputs to Trash")
                        }
                    }
                    .help(e.error ?? e.outputs.map(\.lastPathComponent).joined(separator: ", "))
                }
            }
        }
    }

    private func finderSelection() {
        let script = "tell application \"Finder\" to set s to selection as alias list\nset out to \"\"\nrepeat with f in s\nset out to out & POSIX path of f & linefeed\nend repeat\nreturn out"
        Task {
            let r = try? await Shell.run("/usr/bin/osascript", ["-e", script])
            let urls = (r?.text ?? "").split(separator: "\n").map { URL(fileURLWithPath: String($0)) }
            await MainActor.run {
                if urls.isEmpty { Dispatcher.alert("Nothing selected", "Select files in Finder first. Tangelo may need Automation permission for Finder.") }
                else { dropped = urls }
            }
        }
    }
}

// MARK: - Welcome

@MainActor
enum WelcomeWindow {
    private static var window: NSWindow?
    static func show() {
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 560, height: 520), styleMask: [.titled, .closable, .fullSizeContentView], backing: .buffered, defer: false)
        w.titlebarAppearsTransparent = true
        w.isMovableByWindowBackground = true
        w.title = "Welcome to Tangelo"
        w.contentView = NSHostingView(rootView: WelcomeView { w.close(); UserDefaults.standard.set(true, forKey: "didOnboard") })
        w.isReleasedWhenClosed = false
        w.center()
        window = w
        NSApp.activate(ignoringOtherApps: true)
        w.makeKeyAndOrderFront(nil)
    }
}

struct WelcomeView: View {
    let done: () -> Void
    @AppStorage("launchAtLogin") private var launchAtLogin = true

    var body: some View {
        VStack(spacing: 18) {
            Image(systemName: "circle.hexagongrid.fill").font(.system(size: 60)).foregroundStyle(Color.tangelo)
            Text("Welcome to Tangelo").font(.largeTitle.bold())
            Text("Convert, edit and move files right where you drag them.").foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 14) {
                step("1", "Drag files", "Start dragging any files in Finder or on your desktop.")
                step("2", "Tap a modifier", "\(WheelKind.conversions.mask.symbols) opens conversions, \(WheelKind.tools.mask.symbols) opens tools, \(WheelKind.actions.mask.symbols) opens actions.")
                step("3", "Drop on a petal", "Results appear next to the original. Originals are never changed.")
                step("✦", "Shake to Float", "Shake while dragging to park files in a floating shelf.")
            }
            .padding(18)
            .background(Color.primary.opacity(0.04), in: RoundedRectangle(cornerRadius: 14))
            Toggle("Launch Tangelo at login", isOn: $launchAtLogin)
                .onChange(of: launchAtLogin) { LoginItem.set(launchAtLogin) }
            Text("Find the Tangelo icon near the clock in your menu bar.").font(.caption).foregroundStyle(.secondary)
            Button("Get Started", action: { LoginItem.set(launchAtLogin); done() }).buttonStyle(.borderedProminent).tint(.tangelo).controlSize(.large)
        }
        .padding(30)
        .frame(width: 560, height: 520)
    }

    private func step(_ n: String, _ title: String, _ detail: String) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Text(n).font(.headline).frame(width: 28, height: 28).background(Color.tangelo.opacity(0.18), in: Circle()).foregroundStyle(Color.tangelo)
            VStack(alignment: .leading, spacing: 2) { Text(title).font(.headline); Text(detail).font(.callout).foregroundStyle(.secondary) }
        }
    }
}

enum LoginItem {
    static func set(_ on: Bool) {
        do { if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() } }
        catch { NSLog("[Tangelo] Login item change failed: \(error.localizedDescription)") }
    }
    static var enabled: Bool { SMAppService.mainApp.status == .enabled }
}
