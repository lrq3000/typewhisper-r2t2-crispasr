import Foundation
import Testing
@testable import R2T2CrispASR

private final class ProgressProbe: @unchecked Sendable {
    private let lock = NSLock()
    private var latestText = ""
    private var released = false
    private var releaseCount = 0
    func update(_ text: String) -> Bool { lock.withLock { latestText = text }; return true }
    func release(_ success: Bool) { lock.withLock { released = success; releaseCount += 1 } }
    var result: (String, Bool, Int) { lock.withLock { (latestText, released, releaseCount) } }
}

struct TransportTests {
    private func startPeer() throws -> (Process, URL) {
        let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let peer = Process()
        peer.executableURL = URL(fileURLWithPath: "/usr/bin/env")
        peer.arguments = ["python3", root.appendingPathComponent("tests/fake_server.py").path]
        let output = Pipe()
        peer.standardOutput = output
        try peer.run()
        let portLine = String(decoding: output.fileHandleForReading.availableData, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        let port = try #require(Int(portLine))
        return (peer, URL(string: "ws://127.0.0.1:\(port)/v1/realtime")!)
    }
    @Test func realWebSocketUsesTextFramesAndDrainsUnicodeFinal() async throws {
        let (peer, url) = try startPeer()
        defer { if peer.isRunning { peer.terminate() } }
        let probe = ProgressProbe()
        let session = try await RealtimeSession.connect(url: url,
            release: { probe.release($0) }, onProgress: { probe.update($0) })
        do {
            try await session.appendAudio(samples: Array(repeating: 0, count: 16000))
            let result = try await session.finish()
            #expect(result.text == "你好!")
            #expect(probe.result.0 == "你好!")
            #expect(probe.result.1)
        } catch { await session.cancel(); throw error }
    }

    @Test func rejectedFinalCallbackFailsAndReleasesSession() async throws {
        let (peer, url) = try startPeer()
        defer { if peer.isRunning { peer.terminate() } }
        let probe = ProgressProbe()
        let session = try await RealtimeSession.connect(url: url, release: { probe.release($0) },
            onProgress: { $0 == "你好!" ? false : probe.update($0) })
        try await session.appendAudio(samples: Array(repeating: 0, count: 16000))
        await #expect(throws: CancellationError.self) { try await session.finish() }
        #expect(!probe.result.1)
        #expect(probe.result.2 == 1)
    }

    @Test func cancellationAtAppendEntryReleasesSession() async throws {
        let (peer, url) = try startPeer()
        defer { if peer.isRunning { peer.terminate() } }
        let probe = ProgressProbe()
        let session = try await RealtimeSession.connect(url: url, release: { probe.release($0) }, onProgress: { probe.update($0) })
        let work = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            try await session.appendAudio(samples: [0])
        }
        await #expect(throws: CancellationError.self) { try await work.value }
        #expect(!probe.result.1)
        #expect(probe.result.2 == 1)
    }
}
