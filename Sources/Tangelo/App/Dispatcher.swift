import AppKit
import SwiftUI

/// Single entry point for everything the wheels, floats and menu can do.
@MainActor
enum Dispatcher {
    static func convert(_ urls: [URL], to format: ConversionFormat) {
        guard !urls.isEmpty else { return }
        let title = urls.count == 1 ? "\(urls[0].lastPathComponent) → \(format.label)" : "\(urls.count) files → \(format.label)"
        JobRunner.shared.run(title, inputs: urls) { progress in
            try await Converter.convert(urls, to: format, progress: progress)
        }
    }

    static func conversionTargets(for urls: [URL]) -> [ConversionFormat] {
        let cats = Set(urls.map(FileCategory.of))
        guard let first = cats.first else { return [] }
        let source = urls.count == 1 ? ConversionFormat.from(urls[0]) : nil
        var common = ConversionFormat.targets(for: first, source: source)
        for c in cats.dropFirst() {
            let t = Set(ConversionFormat.targets(for: c))
            common = common.filter(t.contains)
        }
        return common
    }

    static func run(_ tool: FileTool, on urls: [URL]) {
        guard !urls.isEmpty else { return }
        if !tool.runsImmediately {
            EditorWindows.open(tool, urls: urls)
            return
        }
        let title = "\(tool.title): " + (urls.count == 1 ? urls[0].lastPathComponent : "\(urls.count) files")
        switch tool {
        case .muteVideo:
            JobRunner.shared.run(title, inputs: urls) { p in
                var outs: [URL] = []
                for (i, u) in urls.enumerated() { outs.append(try await VideoOps.mute(u)); p(Double(i + 1) / Double(urls.count)) }
                return outs
            }
        case .extractArchive:
            JobRunner.shared.run(title, inputs: urls) { p in
                var outs: [URL] = []
                for (i, u) in urls.enumerated() { outs.append(try await ArchiveKit.extract(u)); p(Double(i + 1) / Double(urls.count)) }
                return outs
            }
        case .createPDF:
            JobRunner.shared.run(title, inputs: urls) { _ in
                let sorted = urls.sorted { $0.lastPathComponent.localizedStandardCompare($1.lastPathComponent) == .orderedAscending }
                let out = Output.unique(in: Output.directory(for: sorted[0]), base: sorted.count == 1 ? sorted[0].deletingPathExtension().lastPathComponent : "Images", ext: "pdf")
                try PDFKitTools.createPDF(from: sorted, to: out)
                return [out]
            }
        case .mergePDF:
            JobRunner.shared.run(title, inputs: urls) { _ in
                let sorted = urls.sorted { $0.lastPathComponent.localizedStandardCompare($1.lastPathComponent) == .orderedAscending }
                let out = Output.unique(in: Output.directory(for: sorted[0]), base: sorted[0].deletingPathExtension().lastPathComponent + " merged", ext: "pdf")
                try PDFKitTools.merge(sorted, to: out)
                return [out]
            }
        case .removeBackground:
            JobRunner.shared.run(title, inputs: urls) { p in
                var outs: [URL] = []
                for (i, u) in urls.enumerated() { outs.append(try ImageOps.removeBackground(u)); p(Double(i + 1) / Double(urls.count)) }
                return outs
            }
        case .readQRCode:
            Task {
                var results: [(URL, [String])] = []
                for u in urls { results.append((u, (try? ImageOps.readCodes(u)) ?? [])) }
                ResultWindows.showCodes(results)
            }
        case .extractText:
            Task {
                var text = ""
                for u in urls {
                    let t = (try? await ImageOps.extractText(u)) ?? ""
                    text += (urls.count > 1 ? "— \(u.lastPathComponent) —\n" : "") + t + "\n"
                }
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(text, forType: .string)
                ResultWindows.showText(text, source: urls[0])
            }
        default:
            EditorWindows.open(tool, urls: urls)
        }
    }

    static func perform(_ action: FileAction, on urls: [URL]) {
        guard !urls.isEmpty else { return }
        switch action {
        case .airDrop:
            guard let s = NSSharingService(named: .sendViaAirDrop), s.canPerform(withItems: urls) else {
                alert("AirDrop isn’t available", "Turn on Wi-Fi and Bluetooth, then try again."); return
            }
            s.perform(withItems: urls)
        case .float:
            FloatManager.shared.newFloat(with: urls)
        case .copy, .move:
            let panel = NSOpenPanel()
            panel.canChooseDirectories = true; panel.canChooseFiles = false; panel.canCreateDirectories = true
            panel.prompt = action == .copy ? "Copy Here" : "Move Here"
            panel.message = "Choose a destination folder for \(urls.count == 1 ? urls[0].lastPathComponent : "\(urls.count) items")"
            NSApp.activate(ignoringOtherApps: true)
            guard panel.runModal() == .OK, let dest = panel.url else { return }
            transfer(urls, to: dest, move: action == .move)
        case .copyPath:
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(urls.map(\.path).joined(separator: "\n"), forType: .string)
            Feedback.success()
        case .openTerminal:
            let folder = urls[0].hasDirectoryPath ? urls[0] : urls[0].deletingLastPathComponent()
            let app = UserDefaults.standard.string(forKey: "terminalApp") ?? "Terminal"
            let appURL = NSWorkspace.shared.urlForApplication(withBundleIdentifier: app == "iTerm" ? "com.googlecode.iterm2" : app == "Ghostty" ? "com.mitchellh.ghostty" : app == "Warp" ? "dev.warp.Warp-Stable" : "com.apple.Terminal")
                ?? URL(fileURLWithPath: "/System/Applications/Utilities/Terminal.app")
            NSWorkspace.shared.open([folder], withApplicationAt: appURL, configuration: NSWorkspace.OpenConfiguration())
        case .quickLook:
            QuickLook.shared.show(urls)
        case .reveal:
            NSWorkspace.shared.activateFileViewerSelecting(urls)
        }
    }

    static func transfer(_ urls: [URL], to dest: URL, move: Bool) {
        JobRunner.shared.run("\(move ? "Moving" : "Copying") \(urls.count) item\(urls.count == 1 ? "" : "s")", inputs: urls) { p in
            var outs: [URL] = []
            for (i, u) in urls.enumerated() {
                let ext = u.pathExtension
                let target = Output.unique(in: dest, base: u.deletingPathExtension().lastPathComponent, ext: ext)
                if move { try FileManager.default.moveItem(at: u, to: target) } else { try FileManager.default.copyItem(at: u, to: target) }
                outs.append(target)
                p(Double(i + 1) / Double(urls.count))
            }
            return outs
        }
    }

    static func alert(_ title: String, _ message: String) {
        let a = NSAlert()
        a.messageText = title; a.informativeText = message
        NSApp.activate(ignoringOtherApps: true)
        a.runModal()
    }
}
