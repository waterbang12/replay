import ReplayKit
import SwiftUI

@main
struct LifeReplayApp: App {
    @StateObject private var store = RecordingStore()
    @Environment(\.scenePhase) private var phase

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(store)
                .onOpenURL { store.handle(url: $0) }  // pairing QR scanned with the Camera app
                .onChange(of: phase) { newPhase in
                    if newPhase == .active { Task { await store.autoSyncIfAppropriate() } }
                }
        }
    }
}

struct ContentView: View {
    @EnvironmentObject var store: RecordingStore

    var body: some View {
        NavigationStack {
            List {
                Section {
                    StatusHeader()
                    BroadcastButton()
                        .frame(height: 56)
                        .listRowInsets(EdgeInsets())
                } footer: {
                    Text("iOS only allows screen recording that you start yourself. Tap the button (or long-press Screen Recording in Control Center and choose LifeReplay). Recording stops when you stop it, and iOS may stop it on its own. See Limitations below.")
                }

                Section("Sync to PC") {
                    if let server = store.server {
                        LabeledContent("Paired with", value: "\(server.name) (\(server.host))")
                        Toggle("Sync automatically on Wi-Fi", isOn: $store.autoSync)
                        Toggle("Delete from iPhone after upload", isOn: $store.deleteAfterUpload)
                        Button(store.isSyncing ? "Syncing…" : "Sync now") { Task { await store.syncNow() } }
                            .disabled(store.isSyncing)
                        Button("Unpair", role: .destructive) { store.unpair() }
                    } else {
                        Text("On your PC: tray icon → iPhone sync → Enable, then Pair an iPhone. Scan the QR code with the Camera app.")
                            .font(.callout)
                    }
                    if !store.syncStatus.isEmpty { Text(store.syncStatus).font(.footnote).foregroundStyle(.secondary) }
                    if !store.onWiFi { Text("Not on Wi-Fi: sync is paused.").font(.footnote).foregroundStyle(.orange) }
                }

                Section("Recordings on this iPhone (\(ByteCountFormatter.string(fromByteCount: store.totalBytes, countStyle: .file)))") {
                    if store.segments.isEmpty { Text("No segments yet").foregroundStyle(.secondary) }
                    ForEach(store.segments.reversed()) { seg in SegmentRow(seg: seg) }
                        .onDelete { idx in
                            let list = Array(store.segments.reversed())
                            idx.map { list[$0] }.forEach(store.delete)
                        }
                }

                Section("Limitations") {
                    NavigationLink("What iOS can and cannot record") { LimitationsView() }
                }
            }
            .navigationTitle("LifeReplay")
            .refreshable { store.refresh() }
            .alert("Pair with this PC?", isPresented: Binding(get: { store.pendingPairing != nil }, set: { if !$0 { store.pendingPairing = nil } })) {
                Button("Pair") { store.confirmPairing() }
                Button("Cancel", role: .cancel) { store.pendingPairing = nil }
            } message: {
                if let s = store.pendingPairing?.server { Text("\(s.name) at \(s.host):\(s.port)\nCertificate \(s.fingerprint.prefix(16))…") }
            }
        }
    }
}

struct StatusHeader: View {
    @EnvironmentObject var store: RecordingStore

    var body: some View {
        HStack(spacing: 12) {
            Circle().fill(dotColor).frame(width: 14, height: 14)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.headline)
                Text(subtitle).font(.caption).foregroundStyle(.secondary)
            }
        }
        .padding(.vertical, 4)
    }

    private var dotColor: Color {
        guard store.isRecording else { return Color.gray }
        return store.state?.isPaused == true ? Color.orange : Color.red
    }

    private var title: String {
        guard store.isRecording else { return "Not recording" }
        return store.state?.isPaused == true ? "Broadcast paused" : "Recording screen"
    }

    private var subtitle: String {
        if let s = store.state, store.isRecording, let started = s.startedAtMs {
            let since = Date(timeIntervalSince1970: Double(started) / 1000).formatted(date: .omitted, time: .shortened)
            return "Since \(since) · \(s.segmentsWritten) segment(s) finished"
        }
        if let err = store.state?.lastError { return err }
        if let s = store.state, s.isBroadcasting, s.isStale { return "The broadcast was ended by iOS" }
        return "Start a broadcast to record"
    }
}

/// The only public way to start a ReplayKit broadcast from an app is the system picker button.
struct BroadcastButton: UIViewRepresentable {
    func makeUIView(context: Context) -> RPSystemBroadcastPickerView {
        let picker = RPSystemBroadcastPickerView(frame: .zero)
        picker.preferredExtension = (Bundle.main.bundleIdentifier ?? "") + ".BroadcastExtension"
        picker.showsMicrophoneButton = false // LifeReplay never records audio
        return picker
    }

    func updateUIView(_ uiView: RPSystemBroadcastPickerView, context: Context) {}
}

struct SegmentRow: View {
    let seg: SegmentMetadata

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text("\(fmt(seg.startUtcMs)) – \(fmt(seg.endUtcMs))").font(.body.monospacedDigit())
            HStack {
                Text(label).foregroundStyle(color)
                Text("· \(seg.frameCount) frames · \(seg.width)×\(seg.height)")
            }
            .font(.caption)
            if let e = seg.uploadError { Text(e).font(.caption2).foregroundStyle(.red) }
        }
    }

    private func fmt(_ ms: Int64) -> String { Date(timeIntervalSince1970: Double(ms) / 1000).formatted(date: .abbreviated, time: .standard) }

    private var label: String {
        switch seg.localStatus {
        case "recording": return "Recording"
        case "finalized": return "Ready to sync"
        case "interrupted": return "Cut off by iOS (partial)"
        case "uploaded": return "Synced"
        default: return "Failed"
        }
    }

    private var color: Color {
        switch seg.localStatus {
        case "recording": return .red
        case "finalized", "uploaded": return .green
        case "interrupted": return .orange
        default: return .red
        }
    }
}

struct LimitationsView: View {
    var body: some View {
        List {
            Section("Recording must be started by you") {
                Text("iOS does not let any app record the screen in the background or start recording by itself. You start each broadcast from the button in this app or from Control Center, and iOS shows a red indicator while it runs.")
                Text("After a restart or after a broadcast ends, nothing records until you start it again.")
            }
            Section("iOS can stop recording at any time") {
                Text("The broadcast stops when you stop it from the indicator or Control Center, and iOS may also end it (for example when the extension exceeds its memory limit). The current segment is then cut off; LifeReplay keeps the complete part and marks the rest as a gap.")
                Text("Whether locking the phone, phone calls or low-power situations end or pause the broadcast depends on the iOS version. Gaps are recorded and shown on the PC timeline.")
            }
            Section("What may be missing from the video") {
                Text("DRM-protected video (most streaming apps) is shown black.")
                Text("Apps can detect screen recording and hide sensitive content (banking, password managers, some messaging apps).")
                Text("No audio is recorded.")
            }
        }
        .navigationTitle("Limitations")
    }
}
