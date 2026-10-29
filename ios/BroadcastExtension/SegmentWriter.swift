import AVFoundation
import CoreImage
import Foundation
import ImageIO

/// Writes one MP4 segment with AVAssetWriter (hardware H.264 via VideoToolbox).
///
/// The file is written as a fragmented MP4 (a fragment every 10 s). If iOS terminates the extension without
/// warning, everything up to the last complete fragment is still readable; the app trims the incomplete
/// tail before uploading (see SegmentRepair).
final class SegmentWriter {
    private let writer: AVAssetWriter
    private let input: AVAssetWriterInput
    private(set) var meta: SegmentMetadata
    private var firstPTS: CMTime?
    private var lastPTS: CMTime = .invalid
    private var lastMetadataSave = Date.distantPast
    private let minFrameInterval: Double

    /// - Parameters:
    ///   - sourceWidth/sourceHeight: size of the incoming pixel buffers
    ///   - startUtcMs: wall-clock time of the first frame
    init(sessionId: String, deviceName: String, sourceWidth: Int, sourceHeight: Int, startUtcMs: Int64) throws {
        let maxHeight = AppGroup.maxOutputHeight
        let longSide = max(sourceWidth, sourceHeight)
        let scale = longSide > maxHeight ? Double(maxHeight) / Double(longSide) : 1.0
        let width = Int((Double(sourceWidth) * scale / 2).rounded()) * 2
        let height = Int((Double(sourceHeight) * scale / 2).rounded()) * 2
        let fps = AppGroup.maxFps
        minFrameInterval = 1.0 / fps

        let id = UUID().uuidString.lowercased()
        meta = SegmentMetadata(segmentId: id, sessionId: sessionId, deviceId: AppGroup.deviceId, deviceName: deviceName,
                               startUtcMs: startUtcMs, endUtcMs: startUtcMs, width: width, height: height, fps: fps,
                               frameCount: 0, sha256: "", endReason: nil, localStatus: "recording", uploadError: nil)

        writer = try AVAssetWriter(outputURL: meta.videoURL, fileType: .mp4)
        writer.movieFragmentInterval = CMTime(seconds: 10, preferredTimescale: 600)
        writer.shouldOptimizeForNetworkUse = false

        // Screen content compresses well; ~0.06 bits per pixel per frame.
        let bitrate = max(400_000, Int(Double(width * height) * fps * 0.06))
        let settings: [String: Any] = [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: width,
            AVVideoHeightKey: height,
            AVVideoScalingModeKey: AVVideoScalingModeResizeAspect,
            AVVideoCompressionPropertiesKey: [
                AVVideoAverageBitRateKey: bitrate,
                AVVideoMaxKeyFrameIntervalKey: Int(fps * 2),
                AVVideoExpectedSourceFrameRateKey: Int(fps),
                AVVideoProfileLevelKey: AVVideoProfileLevelH264HighAutoLevel,
                AVVideoAllowFrameReorderingKey: false,
            ],
        ]
        input = AVAssetWriterInput(mediaType: .video, outputSettings: settings)
        input.expectsMediaDataInRealTime = true
        guard writer.canAdd(input) else { throw NSError(domain: "LifeReplay", code: 1, userInfo: [NSLocalizedDescriptionKey: "Cannot add video input"]) }
        writer.add(input)
        guard writer.startWriting() else { throw writer.error ?? NSError(domain: "LifeReplay", code: 2) }
        try meta.save()
    }

    var segmentId: String { meta.segmentId }

    /// Appends a frame unless it arrives sooner than the frame-rate limit allows. Returns true if written.
    @discardableResult
    func append(_ sampleBuffer: CMSampleBuffer, utcMs: Int64) -> Bool {
        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        if firstPTS == nil {
            firstPTS = pts
            writer.startSession(atSourceTime: pts)
        }
        if lastPTS.isValid, (pts - lastPTS).seconds < minFrameInterval { return false }
        guard input.isReadyForMoreMediaData else { return false } // encoder busy: drop rather than buffer (50 MB limit)
        guard input.append(sampleBuffer) else { return false }
        lastPTS = pts
        meta.frameCount += 1
        meta.endUtcMs = utcMs
        // Keep the sidecar current so a killed extension still leaves an accurate end time behind.
        if Date().timeIntervalSince(lastMetadataSave) > 5 {
            lastMetadataSave = Date()
            try? meta.save()
        }
        return true
    }

    /// Closes the file. `endPTS`/`endUtcMs` extend the last frame to the real end time of the segment.
    func finish(endPTS: CMTime?, endUtcMs: Int64, reason: String?, completion: @escaping (SegmentMetadata) -> Void) {
        guard writer.status == .writing, firstPTS != nil else {
            writer.cancelWriting()
            try? FileManager.default.removeItem(at: meta.videoURL)
            try? FileManager.default.removeItem(at: meta.metadataURL)
            completion(meta)
            return
        }
        if let endPTS, endPTS > lastPTS { writer.endSession(atSourceTime: endPTS) }
        meta.endUtcMs = max(meta.endUtcMs, endUtcMs)
        input.markAsFinished()
        writer.finishWriting { [self] in
            if writer.status == .completed {
                meta.localStatus = "finalized"
                meta.endReason = reason
                meta.sha256 = (try? FileHash.sha256(of: meta.videoURL)) ?? ""
            } else {
                meta.localStatus = "interrupted"
                meta.uploadError = writer.error?.localizedDescription
            }
            try? meta.save()
            completion(meta)
        }
    }
}

/// Small JPEG previews for the Windows timeline, saved as <segmentId>_<utcMs>.jpg.
final class ThumbnailMaker {
    private lazy var context = CIContext(options: [.cacheIntermediates: false])
    private var lastUtcMs: Int64 = 0
    var intervalMs: Int64 = 30_000

    func maybeSave(_ sampleBuffer: CMSampleBuffer, segmentId: String, utcMs: Int64) {
        guard utcMs - lastUtcMs >= intervalMs, let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        lastUtcMs = utcMs
        let image = CIImage(cvPixelBuffer: pixelBuffer)
        let scale = 320.0 / max(1, image.extent.width)
        let small = image.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
        guard let colorSpace = CGColorSpace(name: CGColorSpace.sRGB),
              let data = context.jpegRepresentation(of: small, colorSpace: colorSpace, options: [kCGImageDestinationLossyCompressionQuality as CIImageRepresentationOption: 0.6])
        else { return }
        try? data.write(to: AppGroup.segmentsDir.appendingPathComponent("\(segmentId)_\(utcMs).jpg"), options: .atomic)
    }
}
