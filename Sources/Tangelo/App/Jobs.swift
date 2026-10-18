import AppKit
import SwiftUI

/// Tracks running operations and shows a small progress HUD.
@MainActor
final class JobRunner: ObservableObject {
    static let shared = JobRunner()

    struct Job: Identifiable {
        let id = UUID()
        var title: String
        var progress: Double = 0
        var status: Status = .running
        var outputs: [URL] = []
        enum Status: Equatable { case running, done, failed(String) }
    }

    @Published var jobs: [Job] = []
    private var hud: NSPanel?

    var isBusy: Bool { jobs.contains { $0.status == .running } }

    func run(_ title: String, inputs: [URL], reveal: Bool = true,
             _ work: @escaping (_ progress: @escaping @Sendable (Double) -> Void) async throws -> [URL]) {
        let job = Job(title: title)
        jobs.insert(job, at: 0)
        showHUD()
        let id = job.id
        Task { @MainActor in
            do {
                let outputs = try await work { p in Task { @MainActor in self.update(id) { $0.progress = p } } }
                update(id) { $0.status = .done; $0.progress = 1; $0.outputs = outputs }
                History.shared.add(HistoryEntry(title: title, inputs: inputs, outputs: outputs))
                Feedback.success()
                if reveal, !outputs.isEmpty, UserDefaults.standard.object(forKey: "revealOutputs") as? Bool ?? true {
                    NSWorkspace.shared.activateFileViewerSelecting(outputs)
                }
            } catch {
                let message = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
                update(id) { $0.status = .failed(message) }
                History.shared.add(HistoryEntry(title: title, inputs: inputs, outputs: [], error: message))
                Feedback.failure()
            }
            scheduleCleanup(id)
        }
    }

    private func update(_ id: UUID, _ change: (inout Job) -> Void) {
        guard let i = jobs.firstIndex(where: { $0.id == id }) else { return }
        change(&jobs[i])
    }

    private func scheduleCleanup(_ id: UUID) {
        let failed: Bool = { if case .failed = jobs.first(where: { $0.id == id })?.status { return true }; return false }()
        DispatchQueue.main.asyncAfter(deadline: .now() + (failed ? 8 : 3)) {
            self.jobs.removeAll { $0.id == id }
            if self.jobs.isEmpty { self.hud?.orderOut(nil) }
        }
    }

    func dismiss(_ id: UUID) {
        jobs.removeAll { $0.id == id }
        if jobs.isEmpty { hud?.orderOut(nil) }
    }

    private func showHUD() {
        if hud == nil {
            let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 320, height: 220),
                                styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
            panel.isOpaque = false
            panel.backgroundColor = .clear
            panel.level = .statusBar
            panel.hasShadow = false
            panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            panel.contentView = NSHostingView(rootView: JobHUDView().environmentObject(self))
            hud = panel
        }
        if let screen = NSScreen.main, let hud {
            let f = screen.visibleFrame
            hud.setFrameOrigin(NSPoint(x: f.maxX - 336, y: f.maxY - 236))
        }
        hud?.orderFrontRegardless()
    }
}

struct JobHUDView: View {
    @EnvironmentObject var runner: JobRunner

    var body: some View {
        VStack(alignment: .trailing, spacing: 8) {
            ForEach(runner.jobs.prefix(3)) { job in
                HStack(spacing: 10) {
                    icon(job)
                    VStack(alignment: .leading, spacing: 4) {
                        Text(job.title).font(.system(size: 12, weight: .semibold)).lineLimit(1)
                        switch job.status {
                        case .running: ProgressView(value: job.progress).tint(.tangelo).controlSize(.small)
                        case .done: Text(job.outputs.count == 1 ? job.outputs[0].lastPathComponent : "\(job.outputs.count) files created")
                                .font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
                        case .failed(let m): Text(m).font(.system(size: 11)).foregroundStyle(.red).lineLimit(3)
                        }
                    }
                    Spacer(minLength: 0)
                    if job.status != .running {
                        Button { runner.dismiss(job.id) } label: { Image(systemName: "xmark").font(.system(size: 9, weight: .bold)) }
                            .buttonStyle(.plain).foregroundStyle(.secondary)
                    }
                }
                .padding(12)
                .frame(width: 300)
                .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(.white.opacity(0.12)))
                .shadow(color: .black.opacity(0.2), radius: 10, y: 4)
                .onTapGesture { if !job.outputs.isEmpty { NSWorkspace.shared.activateFileViewerSelecting(job.outputs) } }
                .transition(.move(edge: .top).combined(with: .opacity))
            }
            Spacer(minLength: 0)
        }
        .padding(8)
        .frame(width: 320, height: 220, alignment: .top)
        .animation(.spring(duration: 0.3), value: runner.jobs.map(\.id))
    }

    @ViewBuilder func icon(_ job: JobRunner.Job) -> some View {
        switch job.status {
        case .running: ProgressView().controlSize(.small).frame(width: 22)
        case .done: Image(systemName: "checkmark.circle.fill").foregroundStyle(.green).font(.title3).frame(width: 22)
        case .failed: Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange).font(.title3).frame(width: 22)
        }
    }
}

enum Feedback {
    static var enabled: Bool { UserDefaults.standard.object(forKey: "soundFeedback") as? Bool ?? true }
    static func success() {
        guard enabled else { return }
        NSSound(named: "Glass")?.play()
        NSHapticFeedbackManager.defaultPerformer.perform(.levelChange, performanceTime: .now)
    }
    static func failure() { if enabled { NSSound(named: "Basso")?.play() } }
    static func highlight() {
        guard enabled else { return }
        NSHapticFeedbackManager.defaultPerformer.perform(.alignment, performanceTime: .now)
    }
}

extension Color {
    static let tangelo = Color(red: 1, green: 0.32, blue: 0)
}
extension NSColor {
    static let tangelo = NSColor(srgbRed: 1, green: 0.32, blue: 0, alpha: 1)
}
