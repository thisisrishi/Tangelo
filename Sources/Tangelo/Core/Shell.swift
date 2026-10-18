import Foundation

enum TangeloError: LocalizedError {
    case message(String)
    case unsupported(String)
    case toolMissing(String)

    var errorDescription: String? {
        switch self {
        case .message(let m): return m
        case .unsupported(let m): return m
        case .toolMissing(let tool):
            return "\(tool) is required for this operation. Install it with Homebrew (brew install \(tool)) or set its path in Settings."
        }
    }
}

/// Runs command-line helpers off the main thread.
enum Shell {
    struct Result {
        let status: Int32
        let stdout: Data
        let stderr: String
        var text: String { String(decoding: stdout, as: UTF8.self) }
    }

    @discardableResult
    static func run(_ executable: String, _ arguments: [String], currentDirectory: URL? = nil) async throws -> Result {
        try await withCheckedThrowingContinuation { cont in
            let process = Process()
            process.executableURL = URL(fileURLWithPath: executable)
            process.arguments = arguments
            if let currentDirectory { process.currentDirectoryURL = currentDirectory }
            let out = Pipe(), err = Pipe()
            process.standardOutput = out
            process.standardError = err
            final class Box: @unchecked Sendable { var data = Data() }
            let outBox = Box(), errBox = Box()
            let group = DispatchGroup()
            group.enter(); group.enter()
            out.fileHandleForReading.readabilityHandler = { h in
                let d = h.availableData
                if d.isEmpty { h.readabilityHandler = nil; group.leave() } else { outBox.data.append(d) }
            }
            err.fileHandleForReading.readabilityHandler = { h in
                let d = h.availableData
                if d.isEmpty { h.readabilityHandler = nil; group.leave() } else { errBox.data.append(d) }
            }
            process.terminationHandler = { p in
                group.notify(queue: .global()) {
                    cont.resume(returning: Result(status: p.terminationStatus, stdout: outBox.data,
                                                  stderr: String(decoding: errBox.data, as: UTF8.self)))
                }
            }
            do { try process.run() } catch {
                out.fileHandleForReading.readabilityHandler = nil
                err.fileHandleForReading.readabilityHandler = nil
                cont.resume(throwing: error)
            }
        }
    }

    /// Runs and throws when the exit status is non-zero.
    @discardableResult
    static func check(_ executable: String, _ arguments: [String], currentDirectory: URL? = nil) async throws -> Result {
        let r = try await run(executable, arguments, currentDirectory: currentDirectory)
        guard r.status == 0 else {
            let tail = r.stderr.split(separator: "\n").suffix(3).joined(separator: "\n")
            throw TangeloError.message("\((executable as NSString).lastPathComponent) failed: \(tail)")
        }
        return r
    }
}

/// Locates optional external helpers (ffmpeg, cwebp, LibreOffice).
enum Helpers {
    static func find(_ name: String, override: String? = nil) -> String? {
        if let override, !override.isEmpty, FileManager.default.isExecutableFile(atPath: override) { return override }
        let candidates = ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/opt/local/bin"].map { "\($0)/\(name)" }
        return candidates.first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    static var ffmpeg: String? { find("ffmpeg", override: UserDefaults.standard.string(forKey: "ffmpegPath")) }
    static var ffprobe: String? { find("ffprobe") }
    static var cwebp: String? { find("cwebp") }
    static var soffice: String? {
        let app = "/Applications/LibreOffice.app/Contents/MacOS/soffice"
        return FileManager.default.isExecutableFile(atPath: app) ? app : find("soffice")
    }

    static func requireFFmpeg() throws -> String {
        guard let f = ffmpeg else { throw TangeloError.toolMissing("ffmpeg") }
        return f
    }
}

/// Reads text, detecting the encoding (UTF-8, UTF-16, Latin-1…).
func readText(_ url: URL) throws -> String {
    var enc = String.Encoding.utf8
    if let s = try? String(contentsOf: url, usedEncoding: &enc) { return s }
    let data = try Data(contentsOf: url)
    return String(data: data, encoding: .isoLatin1) ?? String(decoding: data, as: UTF8.self)
}
