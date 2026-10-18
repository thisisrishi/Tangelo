import AppKit
import Foundation

enum OutputLocation: String, CaseIterable, Identifiable {
    case sameFolder, downloads, desktop, custom
    var id: String { rawValue }
    var title: String {
        switch self {
        case .sameFolder: return "Next to the original"
        case .downloads: return "Downloads"
        case .desktop: return "Desktop"
        case .custom: return "Custom folder…"
        }
    }
}

enum Output {
    static var location: OutputLocation {
        OutputLocation(rawValue: UserDefaults.standard.string(forKey: "outputLocation") ?? "") ?? .sameFolder
    }

    static func directory(for source: URL) -> URL {
        let fm = FileManager.default
        switch location {
        case .sameFolder:
            let dir = source.deletingLastPathComponent()
            return fm.isWritableFile(atPath: dir.path) ? dir : fm.urls(for: .downloadsDirectory, in: .userDomainMask)[0]
        case .downloads: return fm.urls(for: .downloadsDirectory, in: .userDomainMask)[0]
        case .desktop: return fm.urls(for: .desktopDirectory, in: .userDomainMask)[0]
        case .custom:
            if let p = UserDefaults.standard.string(forKey: "outputCustomPath"), fm.fileExists(atPath: p) {
                return URL(fileURLWithPath: p)
            }
            return source.deletingLastPathComponent()
        }
    }

    /// "photo.heic" + "png" -> ".../photo.png", or "photo 2.png" when taken.
    static func url(for source: URL, ext: String, suffix: String? = nil, in dir: URL? = nil) -> URL {
        let base = source.deletingPathExtension().lastPathComponent + (suffix.map { " \($0)" } ?? "")
        return unique(in: dir ?? directory(for: source), base: base, ext: ext)
    }

    static func unique(in dir: URL, base: String, ext: String) -> URL {
        let fm = FileManager.default
        func make(_ name: String) -> URL {
            ext.isEmpty ? dir.appendingPathComponent(name) : dir.appendingPathComponent(name).appendingPathExtension(ext)
        }
        var candidate = make(base)
        var n = 2
        while fm.fileExists(atPath: candidate.path) {
            candidate = make("\(base) \(n)")
            n += 1
        }
        return candidate
    }

    static func tempDirectory() throws -> URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("Tangelo-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }
}

/// A record of every operation so outputs can be revealed or trashed later.
struct HistoryEntry: Codable, Identifiable, Hashable {
    var id = UUID()
    var date = Date()
    var title: String
    var inputs: [URL]
    var outputs: [URL]
    var error: String?
}

@MainActor
final class History: ObservableObject {
    static let shared = History()
    @Published private(set) var entries: [HistoryEntry] = []
    private let key = "history.v1"

    init() {
        if let data = UserDefaults.standard.data(forKey: key),
           let decoded = try? JSONDecoder().decode([HistoryEntry].self, from: data) {
            entries = decoded
        }
    }

    func add(_ entry: HistoryEntry) {
        entries.insert(entry, at: 0)
        if entries.count > 200 { entries.removeLast(entries.count - 200) }
        save()
    }

    func clear() { entries.removeAll(); save() }

    /// Moves an entry's outputs to the Trash — an "undo" for any operation.
    func trashOutputs(of entry: HistoryEntry) {
        NSWorkspace.shared.recycle(entry.outputs.filter { FileManager.default.fileExists(atPath: $0.path) })
        entries.removeAll { $0.id == entry.id }
        save()
    }

    private func save() {
        if let data = try? JSONEncoder().encode(entries) { UserDefaults.standard.set(data, forKey: key) }
    }
}
