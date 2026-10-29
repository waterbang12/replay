import Foundation
import Network
import SwiftUI

/// App-side model: watches the extension's state file, lists segments, and uploads them to the paired PC.
@MainActor
final class RecordingStore: ObservableObject {
    @Published var state: BroadcastState?
    @Published var segments: [SegmentMetadata] = []
    @Published var server: PairedServer?
    @Published var syncStatus = ""
    @Published var isSyncing = false
    @Published var onWiFi = false
    @Published var pendingPairing: PendingPairing?
    @Published var autoSync: Bool = AppGroup.defaults.object(forKey: "autoSync") as? Bool ?? true {
        didSet { AppGroup.defaults.set(autoSync, forKey: "autoSync") }
    }
    @Published var deleteAfterUpload: Bool = AppGroup.defaults.object(forKey: "deleteAfterUpload") as? Bool ?? true {
        didSet { AppGroup.defaults.set(deleteAfterUpload, forKey: "deleteAfterUpload") }
    }

    struct PendingPairing {
        let server: PairedServer
        let token: String
    }

    private var timer: Timer?
    private let pathMonitor = NWPathMonitor()

    init() {
        if let data = AppGroup.defaults.data(forKey: "pairedServer") {
            server = try? JSONDecoder().decode(PairedServer.self, from: data)
        }
        pathMonitor.pathUpdateHandler = { [weak self] path in
            Task { @MainActor in self?.onWiFi = path.status == .satisfied && path.usesInterfaceType(.wifi) }
        }
        pathMonitor.start(queue: .global(qos: .utility))
        refresh()
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.refresh() }
        }
    }

    var isRecording: Bool { (state?.isBroadcasting ?? false) && !(state?.isStale ?? true) }
    var totalBytes: Int64 {
        segments.reduce(0) { sum, s in sum + Int64((try? s.videoURL.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0) }
    }

    func refresh() {
        state = BroadcastState.load()
        markInterruptedSegments()
        segments = SegmentMetadata.all()
    }

    /// A segment still marked "recording" while no broadcast is alive was cut off by the system.
    private func markInterruptedSegments() {
        let alive = isRecording ? state?.currentSegmentId : nil
        for var seg in SegmentMetadata.all() where seg.localStatus == "recording" && seg.segmentId != alive {
            if (try? SegmentRepair.trimToCompleteFragments(seg.videoURL)) == true, let hash = try? FileHash.sha256(of: seg.videoURL) {
                seg.localStatus = "interrupted"
                seg.endReason = "interrupted"
                seg.sha256 = hash
            } else {
                seg.localStatus = "failed"
                seg.uploadError = "Recording was cut off before any complete data was written"
            }
            try? seg.save()
            PendingGap.append(PendingGap(startUtcMs: seg.endUtcMs, endUtcMs: max(seg.endUtcMs, state?.updatedAtMs ?? seg.endUtcMs), detail: "broadcast ended unexpectedly"))
        }
    }

    // MARK: - Pairing

    func handle(url: URL) {
        if let parsed = PairedServer.parse(url) {
            pendingPairing = PendingPairing(server: parsed.0, token: parsed.token)
        } else {
            syncStatus = "That link is not a LifeReplay pairing code."
        }
    }

    func confirmPairing() {
        guard let pending = pendingPairing else { return }
        let server = pending.server
        Keychain.saveToken(pending.token)
        AppGroup.defaults.set(try? JSONEncoder().encode(server), forKey: "pairedServer")
        self.server = server
        pendingPairing = nil
        Task { await testConnection() }
    }

    func unpair() {
        Keychain.deleteToken()
        AppGroup.defaults.removeObject(forKey: "pairedServer")
        server = nil
        syncStatus = "Not paired"
    }

    private func client() -> SyncClient? {
        guard let server, let token = Keychain.loadToken() else { return nil }
        return SyncClient(server: server, token: token)
    }

    func testConnection() async {
        guard let c = client() else { syncStatus = "Not paired"; return }
        do { try await c.hello(); syncStatus = "Connected to \(c.server.name)" }
        catch { syncStatus = "Cannot reach \(c.server.host): \(error.localizedDescription)" }
    }

    // MARK: - Upload

    func autoSyncIfAppropriate() async {
        if autoSync, onWiFi, server != nil, !isSyncing { await syncNow() }
    }

    func syncNow() async {
        guard let c = client() else { syncStatus = "Pair with your PC first"; return }
        guard onWiFi else { syncStatus = "Sync runs on Wi-Fi only"; return }
        isSyncing = true
        defer { isSyncing = false; refresh() }
        let ready = SegmentMetadata.all().filter { $0.localStatus == "finalized" || $0.localStatus == "interrupted" }
        var uploaded = 0
        for var seg in ready {
            do {
                syncStatus = "Uploading \(uploaded + 1) of \(ready.count)…"
                try await c.upload(seg)
                for thumb in thumbnails(for: seg.segmentId) {
                    let ms = Int64(thumb.deletingPathExtension().lastPathComponent.split(separator: "_").last ?? "") ?? seg.startUtcMs
                    try? await c.uploadThumbnail(segmentId: seg.segmentId, utcMs: ms, file: thumb)
                }
                uploaded += 1
                if deleteAfterUpload {
                    try? FileManager.default.removeItem(at: seg.videoURL)
                    try? FileManager.default.removeItem(at: seg.metadataURL)
                    thumbnails(for: seg.segmentId).forEach { try? FileManager.default.removeItem(at: $0) }
                } else {
                    seg.localStatus = "uploaded"
                    seg.uploadError = nil
                    try? seg.save()
                }
            } catch {
                seg.uploadError = error.localizedDescription
                try? seg.save()
                syncStatus = "Upload failed: \(error.localizedDescription)"
                return
            }
        }
        let gaps = PendingGap.load()
        if !gaps.isEmpty, (try? await c.uploadGaps(gaps)) != nil { PendingGap.save([]) }
        syncStatus = uploaded == 0 ? "Nothing new to sync" : "Uploaded \(uploaded) segment(s)"
    }

    private func thumbnails(for segmentId: String) -> [URL] {
        let files = (try? FileManager.default.contentsOfDirectory(at: AppGroup.segmentsDir, includingPropertiesForKeys: nil)) ?? []
        return files.filter { $0.pathExtension == "jpg" && $0.lastPathComponent.hasPrefix(segmentId + "_") }
    }

    func delete(_ seg: SegmentMetadata) {
        guard seg.localStatus != "recording" else { return }
        try? FileManager.default.removeItem(at: seg.videoURL)
        try? FileManager.default.removeItem(at: seg.metadataURL)
        thumbnails(for: seg.segmentId).forEach { try? FileManager.default.removeItem(at: $0) }
        refresh()
    }
}
