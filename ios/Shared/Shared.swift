import CryptoKit
import Foundation

/// Values shared by the app and the Broadcast Upload Extension through an App Group container.
/// Change `appGroupId` (and the entitlements / project.yml) to an App Group registered to your team.
enum AppGroup {
    static let appGroupId = "group.com.example.lifereplay"

    static var container: URL {
        guard let url = FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: appGroupId) else {
            fatalError("App Group \(appGroupId) is not configured for this target")
        }
        return url
    }

    static var segmentsDir: URL {
        let dir = container.appendingPathComponent("Segments", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    static var stateURL: URL { container.appendingPathComponent("broadcast-state.json") }
    static var gapsURL: URL { container.appendingPathComponent("pending-gaps.json") }
    static var defaults: UserDefaults { UserDefaults(suiteName: appGroupId)! }

    /// Stable random id for this phone (not the hardware identifier).
    static var deviceId: String {
        if let id = defaults.string(forKey: "deviceId") { return id }
        let id = UUID().uuidString.lowercased()
        defaults.set(id, forKey: "deviceId")
        return id
    }

    static var segmentSeconds: Double { let v = defaults.double(forKey: "segmentSeconds"); return v > 0 ? v : 300 }
    static var maxFps: Double { let v = defaults.double(forKey: "maxFps"); return v > 0 ? v : 10 }
    static var maxOutputHeight: Int { let v = defaults.integer(forKey: "maxOutputHeight"); return v > 0 ? v : 1280 }
}

/// Sidecar JSON written next to each segment (<id>.json). Field names match what the Windows ingest expects
/// (see shared/segment-metadata.schema.json).
struct SegmentMetadata: Codable, Identifiable, Equatable {
    var segmentId: String
    var sessionId: String
    var deviceId: String
    var deviceName: String
    var startUtcMs: Int64
    var endUtcMs: Int64
    var width: Int
    var height: Int
    var fps: Double
    var frameCount: Int
    var sha256: String
    /// "finalized", or "interrupted" when the extension was stopped before the file was closed.
    var endReason: String?
    /// Local bookkeeping (not sent): "recording" | "finalized" | "interrupted" | "uploaded" | "failed"
    var localStatus: String
    var uploadError: String?

    var id: String { segmentId }
    var videoURL: URL { AppGroup.segmentsDir.appendingPathComponent("\(segmentId).mp4") }
    var metadataURL: URL { AppGroup.segmentsDir.appendingPathComponent("\(segmentId).json") }

    enum CodingKeys: String, CodingKey {
        case segmentId, sessionId, deviceId, deviceName, startUtcMs, endUtcMs, width, height, fps, frameCount, sha256, endReason, localStatus, uploadError
    }

    func save() throws {
        let data = try JSONEncoder().encode(self)
        try data.write(to: metadataURL, options: .atomic)
    }

    static func load(_ url: URL) -> SegmentMetadata? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(SegmentMetadata.self, from: data)
    }

    static func all() -> [SegmentMetadata] {
        let files = (try? FileManager.default.contentsOfDirectory(at: AppGroup.segmentsDir, includingPropertiesForKeys: nil)) ?? []
        return files.filter { $0.pathExtension == "json" }.compactMap(load).sorted { $0.startUtcMs < $1.startUtcMs }
    }
}

/// Live state written by the extension roughly once per second; the app polls it for the status UI.
struct BroadcastState: Codable {
    var isBroadcasting: Bool
    var isPaused: Bool
    var sessionId: String?
    var startedAtMs: Int64?
    var lastFrameAtMs: Int64?
    var currentSegmentId: String?
    var segmentsWritten: Int
    var lastError: String?
    var updatedAtMs: Int64

    static func load() -> BroadcastState? {
        guard let data = try? Data(contentsOf: AppGroup.stateURL) else { return nil }
        return try? JSONDecoder().decode(BroadcastState.self, from: data)
    }

    func save() {
        if let data = try? JSONEncoder().encode(self) { try? data.write(to: AppGroup.stateURL, options: .atomic) }
    }

    /// The extension can be killed by the system without any callback; stale state means it is gone.
    var isStale: Bool { Clock.nowMs() - updatedAtMs > 15_000 }
}

/// A period where the broadcast was paused or interrupted, uploaded to Windows as a timeline gap.
struct PendingGap: Codable {
    var startUtcMs: Int64
    var endUtcMs: Int64
    var detail: String?

    static func load() -> [PendingGap] {
        guard let data = try? Data(contentsOf: AppGroup.gapsURL) else { return [] }
        return (try? JSONDecoder().decode([PendingGap].self, from: data)) ?? []
    }

    static func save(_ gaps: [PendingGap]) {
        if let data = try? JSONEncoder().encode(gaps) { try? data.write(to: AppGroup.gapsURL, options: .atomic) }
    }

    static func append(_ gap: PendingGap) {
        var all = load()
        all.append(gap)
        save(all)
    }
}

enum Clock {
    static func nowMs() -> Int64 { Int64((Date().timeIntervalSince1970 * 1000).rounded()) }
}

enum FileHash {
    /// Streaming SHA-256 (the extension must stay under its memory limit).
    static func sha256(of url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 1 << 20), !chunk.isEmpty { hasher.update(data: chunk) }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }
}
