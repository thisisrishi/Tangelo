import AppKit
import PDFKit
import SwiftUI
import UniformTypeIdentifiers

struct SplitPDFEditor: View {
    let url: URL
    @State private var everyPage = true
    @State private var ranges = ""
    @State private var pageCount = 0

    var body: some View {
        EditorScaffold(tool: .splitPDF, primary: "Split PDF", canApply: everyPage || parsed != nil, apply: apply) {
            Form {
                LabeledContent("Pages", value: "\(pageCount)")
                Picker("Split into", selection: $everyPage) {
                    Text("One file per page").tag(true)
                    Text("Custom ranges").tag(false)
                }.pickerStyle(.radioGroup)
                if !everyPage {
                    TextField("Ranges, e.g. 1-3, 4, 5-\(max(pageCount, 5))", text: $ranges)
                    if !ranges.isEmpty && parsed == nil { Text("Use page numbers between 1 and \(pageCount).").font(.caption).foregroundStyle(.red) }
                }
            }.formStyle(.grouped)
        } options: { EmptyView() }
        .task { pageCount = PDFDocument(url: url)?.pageCount ?? 0 }
    }

    private var parsed: [ClosedRange<Int>]? { PDFKitTools.parseRanges(ranges, max: pageCount) }

    private func apply() {
        let r = everyPage ? nil : parsed
        let u = url
        JobRunner.shared.run("Splitting \(u.lastPathComponent)", inputs: [u]) { _ in
            let dir = Output.unique(in: Output.directory(for: u), base: u.deletingPathExtension().lastPathComponent + " split", ext: "")
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            return try PDFKitTools.split(u, ranges: r, into: dir)
        }
    }
}

struct OrganizePDFEditor: View {
    let url: URL
    struct Page: Identifiable, Equatable { let id: Int; var rotation: Int = 0 }
    @State private var pages: [Page] = []
    @State private var thumbs: [Int: NSImage] = [:]
    @State private var selected: Set<Int> = []
    @State private var equalize = false
    @State private var dragging: Int?

    var body: some View {
        EditorScaffold(tool: .organizePDF, primary: "Save Organized PDF", canApply: !pages.isEmpty, apply: apply) {
            VStack {
                HStack {
                    Button { rotate(-90) } label: { Label("Rotate Left", systemImage: "rotate.left") }.disabled(selected.isEmpty)
                    Button { rotate(90) } label: { Label("Rotate Right", systemImage: "rotate.right") }.disabled(selected.isEmpty)
                    Button(role: .destructive) { pages.removeAll { selected.contains($0.id) }; selected = [] } label: { Label("Delete", systemImage: "trash") }.disabled(selected.isEmpty)
                    Button { pages.reverse() } label: { Label("Reverse", systemImage: "arrow.up.arrow.down") }
                    Spacer()
                    Toggle("Make all pages the same width", isOn: $equalize)
                }
                ScrollView {
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: 120), spacing: 14)], spacing: 14) {
                        ForEach(pages) { page in
                            VStack(spacing: 4) {
                                Group {
                                    if let t = thumbs[page.id] { Image(nsImage: t).resizable().aspectRatio(contentMode: .fit) }
                                    else { Rectangle().fill(Color.primary.opacity(0.06)) }
                                }
                                .frame(width: 110, height: 140)
                                .rotationEffect(.degrees(Double(page.rotation)))
                                .shadow(radius: 2)
                                Text("\(page.id + 1)").font(.caption).foregroundStyle(.secondary)
                            }
                            .padding(6)
                            .background(selected.contains(page.id) ? Color.tangelo.opacity(0.2) : .clear, in: RoundedRectangle(cornerRadius: 8))
                            .opacity(dragging == page.id ? 0.4 : 1)
                            .onTapGesture {
                                if NSEvent.modifierFlags.contains(.command) || NSEvent.modifierFlags.contains(.shift) {
                                    if selected.contains(page.id) { selected.remove(page.id) } else { selected.insert(page.id) }
                                } else { selected = [page.id] }
                            }
                            .onDrag { dragging = page.id; return NSItemProvider(object: "\(page.id)" as NSString) }
                            .onDrop(of: [.plainText], delegate: PageDrop(target: page.id, pages: $pages, dragging: $dragging))
                        }
                    }.padding(4)
                }
            }
        } options: { Text("Drag pages to reorder. \(pages.count) pages.").font(.caption).foregroundStyle(.secondary) }
        .task { load() }
    }

    struct PageDrop: DropDelegate {
        let target: Int
        @Binding var pages: [Page]
        @Binding var dragging: Int?
        func dropEntered(info: DropInfo) {
            guard let d = dragging, d != target, let from = pages.firstIndex(where: { $0.id == d }), let to = pages.firstIndex(where: { $0.id == target }) else { return }
            withAnimation(.easeOut(duration: 0.15)) { pages.move(fromOffsets: IndexSet(integer: from), toOffset: to > from ? to + 1 : to) }
        }
        func performDrop(info: DropInfo) -> Bool { dragging = nil; return true }
        func dropUpdated(info: DropInfo) -> DropProposal? { DropProposal(operation: .move) }
    }

    private func rotate(_ deg: Int) {
        for i in pages.indices where selected.contains(pages[i].id) { pages[i].rotation = (pages[i].rotation + deg + 360) % 360 }
    }

    private func load() {
        guard let doc = PDFDocument(url: url) else { return }
        pages = (0..<doc.pageCount).map { Page(id: $0) }
        Task {
            for i in 0..<doc.pageCount {
                if let p = doc.page(at: i) { thumbs[i] = p.thumbnail(of: CGSize(width: 220, height: 280), for: .mediaBox) }
                await Task.yield()
            }
        }
    }

    private func apply() {
        let order = pages.map(\.id)
        let rot = Dictionary(uniqueKeysWithValues: pages.map { ($0.id, $0.rotation) })
        let (eq, u) = (equalize, url)
        JobRunner.shared.run("Organizing \(u.lastPathComponent)", inputs: [u]) { _ in
            let out = Output.url(for: u, ext: "pdf", suffix: "organized")
            try PDFKitTools.organize(u, order: order, rotations: rot, equalizeWidths: eq, to: out)
            return [out]
        }
    }
}

// MARK: - Results

@MainActor
enum ResultWindows {
    private static var windows: [NSWindow] = []

    static func showCodes(_ results: [(URL, [String])]) {
        let all = results.flatMap { r in r.1.map { (r.0, $0) } }
        if all.count == 1 {
            NSPasteboard.general.clearContents(); NSPasteboard.general.setString(all[0].1, forType: .string)
        }
        present(title: "QR codes", size: CGSize(width: 460, height: 340), view: AnyView(CodesView(items: all)))
    }

    static func showText(_ text: String, source: URL) {
        present(title: "Recognized text — copied to clipboard", size: CGSize(width: 560, height: 460), view: AnyView(TextResultView(text: text, source: source)))
    }

    private static func present(title: String, size: CGSize, view: AnyView) {
        let w = NSWindow(contentRect: NSRect(origin: .zero, size: size), styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        w.title = title
        w.contentView = NSHostingView(rootView: view)
        w.isReleasedWhenClosed = false
        w.center()
        windows.append(w)
        NSApp.activate(ignoringOtherApps: true)
        w.makeKeyAndOrderFront(nil)
    }
}

struct CodesView: View {
    let items: [(URL, String)]
    var body: some View {
        if items.isEmpty {
            VStack(spacing: 8) {
                Image(systemName: "qrcode.viewfinder").font(.largeTitle).foregroundStyle(.secondary)
                Text("No QR codes found").font(.headline)
            }.frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            List(Array(items.enumerated()), id: \.offset) { _, item in
                HStack {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(item.1).textSelection(.enabled).lineLimit(4)
                        Text(item.0.lastPathComponent).font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    if let u = URL(string: item.1), u.scheme?.hasPrefix("http") == true {
                        Button("Open") { NSWorkspace.shared.open(u) }
                    }
                    Button("Copy") { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(item.1, forType: .string) }
                }
            }
        }
    }
}

struct TextResultView: View {
    @State var text: String
    let source: URL
    var body: some View {
        VStack(spacing: 0) {
            TextEditor(text: $text).font(.body).padding(8)
            Divider()
            HStack {
                Text("\(text.split(whereSeparator: \.isWhitespace).count) words").foregroundStyle(.secondary)
                Spacer()
                Button("Copy") { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(text, forType: .string) }
                Button("Save as Text File") {
                    let out = Output.url(for: source, ext: "txt", suffix: "text")
                    try? text.write(to: out, atomically: true, encoding: .utf8)
                    NSWorkspace.shared.activateFileViewerSelecting([out])
                }.buttonStyle(.borderedProminent).tint(.tangelo)
            }.padding(12)
        }
    }
}
