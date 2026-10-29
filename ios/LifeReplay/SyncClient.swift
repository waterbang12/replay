import CryptoKit
import Foundation
import Security

/// Pairing details from the Windows QR code: lifereplay://pair?h=<host>&p=<port>&t=<token>&f=<sha256>&n=<name>
struct PairedServer: Codable, Equatable {
    var host: String
    var port: Int
    var fingerprint: String // SHA-256 of the server's DER certificate, lowercase hex
    var name: String

    static func parse(_ url: URL) -> (PairedServer, token: String)? {
        guard url.scheme == "lifereplay", url.host == "pair",
              let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems else { return nil }
        func v(_ k: String) -> String? { items.first { $0.name == k }?.value }
        guard let host = v("h"), let portText = v("p"), let port = Int(portText), let token = v("t"),
              let fp = v("f")?.lowercased(), fp.count == 64 else { return nil }
        return (PairedServer(host: host, port: port, fingerprint: fp, name: v("n") ?? host), token)
    }

    var baseURL: URL { URL(string: "https://\(host):\(port)")! }
}

/// Keychain storage for the access token (never in UserDefaults or files).
enum Keychain {
    private static let service = "LifeReplay.sync"

    static func saveToken(_ token: String) {
        let base: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: "token"]
        SecItemDelete(base as CFDictionary)
        var add = base
        add[kSecValueData as String] = Data(token.utf8)
        add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        SecItemAdd(add as CFDictionary, nil)
    }

    static func loadToken() -> String? {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: "token",
                                kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne]
        var out: AnyObject?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess, let data = out as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func deleteToken() {
        SecItemDelete([kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: "token"] as CFDictionary)
    }
}

/// HTTPS client that trusts exactly one certificate: the one whose fingerprint came from the pairing QR code.
final class SyncClient: NSObject, URLSessionDelegate {
    let server: PairedServer
    private let token: String
    private lazy var session: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.allowsCellularAccess = false          // local Wi-Fi sync only
        config.waitsForConnectivity = false
        config.timeoutIntervalForRequest = 60
        config.timeoutIntervalForResource = 30 * 60
        return URLSession(configuration: config, delegate: self, delegateQueue: nil)
    }()

    init(server: PairedServer, token: String) {
        self.server = server
        self.token = token
    }

    func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              let trust = challenge.protectionSpace.serverTrust,
              let chain = SecTrustCopyCertificateChain(trust) as? [SecCertificate], let leaf = chain.first
        else { completionHandler(.cancelAuthenticationChallenge, nil); return }
        let der = SecCertificateCopyData(leaf) as Data
        let fp = SHA256.hash(data: der).map { String(format: "%02x", $0) }.joined()
        if fp == server.fingerprint {
            completionHandler(.useCredential, URLCredential(trust: trust))
        } else {
            completionHandler(.cancelAuthenticationChallenge, nil) // not the paired PC: refuse
        }
    }

    private func request(_ path: String, method: String) -> URLRequest {
        var r = URLRequest(url: server.baseURL.appendingPathComponent(path))
        r.httpMethod = method
        r.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        return r
    }

    func hello() async throws {
        let (_, response) = try await session.data(for: request("api/v1/hello", method: "GET"))
        try check(response, nil)
    }

    func upload(_ meta: SegmentMetadata) async throws {
        var r = request("api/v1/segments/\(meta.segmentId)", method: "PUT")
        var wire = meta
        wire.uploadError = nil
        let json = try JSONEncoder().encode(wire)
        r.setValue(json.base64EncodedString(), forHTTPHeaderField: "X-LifeReplay-Metadata")
        r.setValue("video/mp4", forHTTPHeaderField: "Content-Type")
        let (data, response) = try await session.upload(for: r, fromFile: meta.videoURL)
        try check(response, data)
    }

    func uploadThumbnail(segmentId: String, utcMs: Int64, file: URL) async throws {
        var r = request("api/v1/segments/\(segmentId)/thumbnails/\(utcMs)", method: "PUT")
        r.setValue("image/jpeg", forHTTPHeaderField: "Content-Type")
        let (data, response) = try await session.upload(for: r, fromFile: file)
        try check(response, data)
    }

    func uploadGaps(_ gaps: [PendingGap]) async throws {
        var r = request("api/v1/devices/\(AppGroup.deviceId)/gaps", method: "POST")
        r.setValue("application/json", forHTTPHeaderField: "Content-Type")
        r.httpBody = try JSONEncoder().encode(gaps)
        let (data, response) = try await session.data(for: r)
        try check(response, data)
    }

    private func check(_ response: URLResponse, _ body: Data?) throws {
        guard let http = response as? HTTPURLResponse else { throw SyncError.message("No HTTP response") }
        guard (200..<300).contains(http.statusCode) else {
            let text = body.flatMap { String(data: $0, encoding: .utf8) } ?? ""
            if http.statusCode == 401 { throw SyncError.message("Not authorized: pair this iPhone again") }
            throw SyncError.message("Server returned \(http.statusCode) \(text)")
        }
    }
}

enum SyncError: LocalizedError {
    case message(String)
    var errorDescription: String? { if case .message(let m) = self { return m }; return nil }
}

/// Makes a segment left behind by a terminated extension uploadable: keeps only complete top-level MP4
/// boxes (ftyp, moov, and whole moof+mdat fragments) and drops the cut-off tail.
enum SegmentRepair {
    static func trimToCompleteFragments(_ url: URL) throws -> Bool {
        let handle = try FileHandle(forUpdating: url)
        defer { try? handle.close() }
        let length = try handle.seekToEnd()
        var offset: UInt64 = 0
        var lastGood: UInt64 = 0
        var sawMoov = false
        var pendingMoof = false
        while offset + 8 <= length {
            try handle.seek(toOffset: offset)
            guard let header = try handle.read(upToCount: 16), header.count >= 8 else { break }
            var size = UInt64(header[0]) << 24 | UInt64(header[1]) << 16 | UInt64(header[2]) << 8 | UInt64(header[3])
            let type = String(bytes: header[4..<8], encoding: .ascii) ?? ""
            if size == 1, header.count >= 16 { size = header[8..<16].reduce(0) { $0 << 8 | UInt64($1) } }
            if size < 8 || offset + size > length { break }
            offset += size
            switch type {
            case "moov": sawMoov = true; lastGood = offset
            case "moof": pendingMoof = true
            case "mdat": if pendingMoof { pendingMoof = false; lastGood = offset } else if !sawMoov { lastGood = offset }
            default: if !pendingMoof { lastGood = offset }
            }
        }
        guard sawMoov, lastGood > 0 else { return false }
        if lastGood < length { try handle.truncate(atOffset: lastGood) }
        return true
    }
}
