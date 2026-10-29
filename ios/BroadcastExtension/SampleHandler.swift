import AVFoundation
import ReplayKit
import UIKit

/// ReplayKit Broadcast Upload Extension. iOS runs this in a separate process while the user's screen
/// broadcast is active (started from Control Center or the picker in the app). It cannot start itself,
/// and iOS can end it at any time (memory limit ~50 MB, device lock, user stop, system policy).
///
/// Frames go into 5-minute MP4 segments in the App Group container; the app uploads them to Windows.
final class SampleHandler: RPBroadcastSampleHandler {
    private let queue = DispatchQueue(label: "lifereplay.writer")
    private var writer: SegmentWriter?
    private var sessionId = UUID().uuidString.lowercased()
    private var state = BroadcastState(isBroadcasting: false, isPaused: false, segmentsWritten: 0, updatedAtMs: Clock.nowMs())
    private var nextBoundaryMs: Int64 = 0
    private var pausedAtMs: Int64?
    private var lastStateWrite = Date.distantPast
    private let thumbnails = ThumbnailMaker()
    private var heartbeat: DispatchSourceTimer?
    private var lastPTS: CMTime = .invalid
    private let deviceName = UIDevice.current.name

    // MARK: - Broadcast lifecycle

    override func broadcastStarted(withSetupInfo setupInfo: [String: NSObject]?) {
        queue.sync {
            sessionId = UUID().uuidString.lowercased()
            state = BroadcastState(isBroadcasting: true, isPaused: false, sessionId: sessionId, startedAtMs: Clock.nowMs(),
                                   lastFrameAtMs: nil, currentSegmentId: nil, segmentsWritten: 0, lastError: nil, updatedAtMs: Clock.nowMs())
            state.save()
        }
        // Keep the state file fresh even when the screen is static (no frames arrive then).
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 2, repeating: 2)
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            self.state.updatedAtMs = Clock.nowMs()
            self.state.save()
            // Rotate on time even if nothing changes on screen; the last picture is held until now.
            if self.writer != nil, Clock.nowMs() >= self.nextBoundaryMs { self.closeCurrent(reason: "finalized", endPTS: Self.hostNow()) }
        }
        timer.resume()
        heartbeat = timer
    }

    override func broadcastPaused() {
        queue.async { [self] in
            pausedAtMs = Clock.nowMs()
            closeCurrent(reason: "finalized", endPTS: Self.hostNow())
            state.isPaused = true
            writeState(force: true)
        }
    }

    override func broadcastResumed() {
        queue.async { [self] in
            if let p = pausedAtMs { PendingGap.append(PendingGap(startUtcMs: p, endUtcMs: Clock.nowMs(), detail: "broadcast paused")) }
            pausedAtMs = nil
            state.isPaused = false
            writeState(force: true)
        }
    }

    override func broadcastFinished() {
        heartbeat?.cancel()
        let done = DispatchSemaphore(value: 0)
        queue.async { [self] in
            closeCurrent(reason: "finalized", endPTS: Self.hostNow()) { done.signal() }
            state.isBroadcasting = false
            state.currentSegmentId = nil
            writeState(force: true)
        }
        // Give AVAssetWriter a moment to write the index before the process is torn down.
        _ = done.wait(timeout: .now() + 5)
    }

    // MARK: - Frames

    override func processSampleBuffer(_ sampleBuffer: CMSampleBuffer, with sampleBufferType: RPSampleBufferType) {
        guard sampleBufferType == .video, CMSampleBufferDataIsReady(sampleBuffer) else { return } // never audio
        queue.sync { [self] in
            let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
            let utcMs = Self.wallClockMs(for: pts)
            if writer != nil, utcMs >= nextBoundaryMs { closeCurrent(reason: "finalized", endPTS: pts) }
            if writer == nil { openSegment(for: sampleBuffer, utcMs: utcMs) }
            guard let writer else { return }
            if writer.append(sampleBuffer, utcMs: utcMs) {
                lastPTS = pts
                thumbnails.maybeSave(sampleBuffer, segmentId: writer.segmentId, utcMs: utcMs)
                state.lastFrameAtMs = utcMs
                writeState(force: false)
            }
        }
    }

    // MARK: - Segments

    private func openSegment(for sampleBuffer: CMSampleBuffer, utcMs: Int64) {
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        var width = CVPixelBufferGetWidth(pixelBuffer)
        var height = CVPixelBufferGetHeight(pixelBuffer)
        if width <= 0 || height <= 0 { width = 1170; height = 2532 }
        do {
            writer = try SegmentWriter(sessionId: sessionId, deviceName: deviceName, sourceWidth: width, sourceHeight: height, startUtcMs: utcMs)
            let segMs = Int64(AppGroup.segmentSeconds * 1000)
            nextBoundaryMs = (utcMs / segMs + 1) * segMs
            if nextBoundaryMs - utcMs < 30_000 { nextBoundaryMs += segMs }
            state.currentSegmentId = writer?.segmentId
            state.lastError = nil
        } catch {
            state.lastError = "Could not start segment: \(error.localizedDescription)"
            writer = nil
        }
        writeState(force: true)
    }

    private func closeCurrent(reason: String, endPTS: CMTime?, completion: (() -> Void)? = nil) {
        guard let current = writer else { completion?(); return }
        writer = nil
        state.currentSegmentId = nil
        let endUtc = endPTS.map(Self.wallClockMs(for:)) ?? Clock.nowMs()
        current.finish(endPTS: endPTS, endUtcMs: endUtc, reason: reason) { [weak self] meta in
            self?.queue.async {
                if meta.localStatus == "finalized" { self?.state.segmentsWritten += 1 }
                self?.writeState(force: true)
            }
            completion?()
        }
    }

    private func writeState(force: Bool) {
        guard force || Date().timeIntervalSince(lastStateWrite) > 1 else { return }
        lastStateWrite = Date()
        state.updatedAtMs = Clock.nowMs()
        state.save()
    }

    /// Current time on the clock ReplayKit stamps frames with.
    static func hostNow() -> CMTime { CMClockGetTime(CMClockGetHostTimeClock()) }

    /// ReplayKit timestamps use the host clock (mach absolute time). Convert to Unix ms via "now" on both clocks.
    static func wallClockMs(for pts: CMTime) -> Int64 {
        let hostNow = CMClockGetTime(CMClockGetHostTimeClock())
        let ageSeconds = (hostNow - pts).seconds
        let age = ageSeconds.isFinite ? ageSeconds : 0
        return Clock.nowMs() - Int64((age * 1000).rounded())
    }
}
