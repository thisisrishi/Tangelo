import AppKit
import SwiftUI

struct SettingsView: View {
    var body: some View {
        TabView {
            GeneralSettings().tabItem { Label("General", systemImage: "gearshape") }
            ShortcutSettings().tabItem { Label("Shortcuts", systemImage: "keyboard") }
            ConversionSettings().tabItem { Label("Conversions", systemImage: "arrow.triangle.2.circlepath") }
            HelperSettings().tabItem { Label("Helpers", systemImage: "wrench.and.screwdriver") }
            FormatsMatrix().tabItem { Label("Formats", systemImage: "square.grid.3x3") }
            HistorySettings().tabItem { Label("History", systemImage: "clock.arrow.circlepath") }
        }
        .frame(width: 620, height: 470)
    }
}

struct GeneralSettings: View {
    @AppStorage("launchAtLogin") private var launchAtLogin = true
    @AppStorage("outputLocation") private var output = OutputLocation.sameFolder.rawValue
    @AppStorage("outputCustomPath") private var customPath = ""
    @AppStorage("revealOutputs") private var reveal = true
    @AppStorage("soundFeedback") private var sound = true
    @AppStorage("restoreFloats") private var restoreFloats = true
    @AppStorage("shakeToFloat") private var shake = true
    @AppStorage("removeAfterDragOut") private var removeAfterDragOut = false
    @AppStorage("terminalApp") private var terminal = "Terminal"

    var body: some View {
        Form {
            Toggle("Launch at login", isOn: $launchAtLogin).onChange(of: launchAtLogin) { LoginItem.set(launchAtLogin) }
            Picker("Save results", selection: $output) {
                ForEach(OutputLocation.allCases) { Text($0.title).tag($0.rawValue) }
            }
            .onChange(of: output) { if output == OutputLocation.custom.rawValue { chooseFolder() } }
            if output == OutputLocation.custom.rawValue {
                LabeledContent("Folder") { HStack { Text(customPath.isEmpty ? "None" : customPath).lineLimit(1).truncationMode(.middle); Button("Choose…", action: chooseFolder) } }
            }
            Toggle("Reveal results in Finder", isOn: $reveal)
            Toggle("Sound and haptic feedback", isOn: $sound)
            Section("Floats") {
                Toggle("Shake while dragging to create a Float", isOn: $shake)
                Toggle("Restore Floats after relaunch", isOn: $restoreFloats)
                Toggle("Remove files from a Float after dragging them out", isOn: $removeAfterDragOut)
            }
            Picker("Open terminal in", selection: $terminal) { ForEach(["Terminal", "iTerm", "Ghostty", "Warp"], id: \.self) { Text($0) } }
        }
        .formStyle(.grouped)
    }

    private func chooseFolder() {
        let p = NSOpenPanel(); p.canChooseDirectories = true; p.canChooseFiles = false; p.canCreateDirectories = true
        if p.runModal() == .OK, let u = p.url { customPath = u.path } else if customPath.isEmpty { output = OutputLocation.sameFolder.rawValue }
    }
}

struct ShortcutSettings: View {
    @State private var masks: [WheelKind: NSEvent.ModifierFlags] = Dictionary(uniqueKeysWithValues: WheelKind.allCases.map { ($0, $0.mask) })
    private let choices: [NSEvent.ModifierFlags] = [.shift, .command, .control, .option, [.shift, .command], [.control, .option], [.shift, .control], [.option, .command]]

    var body: some View {
        Form {
            Section {
                Text("Tap the chosen modifier combination while dragging files to open a wheel under the cursor.").foregroundStyle(.secondary)
            }
            ForEach(WheelKind.allCases) { kind in
                HStack {
                    Toggle(kind.title, isOn: Binding(
                        get: { UserDefaults.standard.object(forKey: "\(kind.rawValue)WheelEnabled") as? Bool ?? true },
                        set: { UserDefaults.standard.set($0, forKey: "\(kind.rawValue)WheelEnabled") }))
                    Spacer()
                    Picker("", selection: Binding(get: { masks[kind]!.rawValue }, set: { set(kind, NSEvent.ModifierFlags(rawValue: $0)) })) {
                        ForEach(choices, id: \.rawValue) { Text($0.symbols).tag($0.rawValue) }
                    }.frame(width: 110)
                }
            }
            if Set(masks.values.map(\.rawValue)).count < masks.count {
                Label("Two wheels share the same shortcut; only the first will open.", systemImage: "exclamationmark.triangle").foregroundStyle(.orange)
            }
            Section("Tip") {
                Text("⌥ Option changes Finder's copy/move behavior while dragging, so it is not used by default.").font(.caption).foregroundStyle(.secondary)
            }
        }.formStyle(.grouped)
    }

    private func set(_ kind: WheelKind, _ m: NSEvent.ModifierFlags) {
        masks[kind] = m
        UserDefaults.standard.set(m.rawValue, forKey: kind.defaultsKey)
    }
}

struct ConversionSettings: View {
    @AppStorage("imageQuality") private var quality = 0.85
    @AppStorage("audioBitrate") private var bitrate = 192_000
    @AppStorage("preserveMetadata") private var preserve = true
    @AppStorage("gifFPS") private var gifFPS = 12.0
    @AppStorage("gifWidth") private var gifWidth = 640.0
    @AppStorage("preferHEVC") private var hevc = false

    var body: some View {
        Form {
            Section("Images") {
                LabeledContent("Lossy quality") { HStack { Slider(value: $quality, in: 0.3...1); Text("\(Int(quality * 100))%").monospacedDigit().frame(width: 44) } }
                Toggle("Preserve metadata (EXIF, GPS) when converting", isOn: $preserve)
            }
            Section("Audio") {
                Picker("Bitrate", selection: $bitrate) { ForEach([96_000, 128_000, 192_000, 256_000, 320_000], id: \.self) { Text("\($0 / 1000) kbps").tag($0) } }
            }
            Section("Video") {
                Toggle("Prefer HEVC (smaller files, newer devices)", isOn: $hevc)
                Picker("GIF frame rate", selection: $gifFPS) { ForEach([8.0, 10, 12, 15, 20], id: \.self) { Text("\(Int($0)) fps").tag($0) } }
                Picker("GIF width", selection: $gifWidth) { ForEach([320.0, 480, 640, 800], id: \.self) { Text("\(Int($0)) px").tag($0) } }
            }
        }.formStyle(.grouped)
    }
}

struct HelperSettings: View {
    @AppStorage("ffmpegPath") private var ffmpegPath = ""
    @State private var refresh = 0

    var body: some View {
        Form {
            Section("Built in (macOS)") {
                row("Images: JPEG, PNG, HEIC, AVIF, TIFF, GIF, BMP, ICO, PDF", ok: true)
                row("Video: MP4, MOV, M4V, GIF · Audio: M4A, WAV, AIFF, FLAC", ok: true)
                row("Documents: DOCX, DOC, ODT, RTF, RTFD, HTML, TXT, Markdown, EPUB, PDF", ok: true)
                row("Spreadsheets: XLSX, ODS, CSV, TSV, JSON · Subtitles · Archives", ok: true)
                row("OCR, QR codes, face detection, background removal (Vision)", ok: true)
            }
            Section("Optional helpers") {
                row("ffmpeg — MP3, OGG, Opus, WMA, MKV, WebM, AVI, WMV", ok: Helpers.ffmpeg != nil, hint: "brew install ffmpeg")
                TextField("Custom ffmpeg path", text: $ffmpegPath).onSubmit { refresh += 1 }
                row("cwebp — WebP encoding", ok: Helpers.cwebp != nil, hint: "brew install webp")
                row("Pages — .pages documents", ok: IWork.installed("Pages"), hint: "Free on the App Store")
                row("Numbers — .numbers spreadsheets", ok: IWork.installed("Numbers"), hint: "Free on the App Store")
                row("Keynote or LibreOffice — PPT, PPTX, Keynote", ok: IWork.installed("Keynote") || Helpers.soffice != nil, hint: "Free on the App Store")
                row("LibreOffice — legacy XLS", ok: Helpers.soffice != nil, hint: "brew install --cask libreoffice")
            }
        }
        .formStyle(.grouped)
        .id(refresh)
    }

    private func row(_ title: String, ok: Bool, hint: String? = nil) -> some View {
        HStack {
            Image(systemName: ok ? "checkmark.circle.fill" : "circle.dashed").foregroundStyle(ok ? .green : .secondary)
            Text(title)
            Spacer()
            if !ok, let hint { Text(hint).font(.caption.monospaced()).foregroundStyle(.secondary).textSelection(.enabled) }
        }
    }
}

struct FormatsMatrix: View {
    private let rows: [FileCategory] = [.image, .vector, .video, .audio, .document, .pdf, .spreadsheet, .presentation, .subtitle, .archive]
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                Text("Format Compatibility").font(.headline)
                ForEach(rows, id: \.self) { c in
                    VStack(alignment: .leading, spacing: 4) {
                        Label(c.title, systemImage: c.symbol).font(.callout.weight(.semibold))
                        Text("→ " + ConversionFormat.targets(for: c).map(\.label).joined(separator: " · ")).font(.caption).foregroundStyle(.secondary)
                    }
                }
                Divider()
                Text("Tools").font(.headline)
                ForEach(FileTool.allCases) { t in
                    HStack(alignment: .top) {
                        Image(systemName: t.symbol).frame(width: 22).foregroundStyle(Color.tangelo)
                        VStack(alignment: .leading, spacing: 1) { Text(t.title).font(.callout.weight(.medium)); Text(t.subtitle).font(.caption).foregroundStyle(.secondary) }
                    }
                }
            }.padding(20)
        }
    }
}

struct HistorySettings: View {
    @EnvironmentObject var history: History
    var body: some View {
        VStack(alignment: .leading) {
            List(history.entries) { e in
                HStack {
                    Image(systemName: e.error == nil ? "checkmark.circle.fill" : "exclamationmark.triangle.fill").foregroundStyle(e.error == nil ? .green : .orange)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(e.title).lineLimit(1)
                        Text(e.error ?? e.outputs.map(\.lastPathComponent).joined(separator: ", ")).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                    }
                    Spacer()
                    Text(e.date, style: .relative).font(.caption).foregroundStyle(.secondary)
                    if e.error == nil {
                        Button("Reveal") { NSWorkspace.shared.activateFileViewerSelecting(e.outputs) }
                        Button("Undo") { history.trashOutputs(of: e) }.help("Move outputs to the Trash")
                    }
                }
            }
            .overlay { if history.entries.isEmpty { Text("No operations yet.").foregroundStyle(.secondary) } }
            HStack { Spacer(); Button("Clear History") { history.clear() }.disabled(history.entries.isEmpty) }.padding([.horizontal, .bottom])
        }
    }
}
