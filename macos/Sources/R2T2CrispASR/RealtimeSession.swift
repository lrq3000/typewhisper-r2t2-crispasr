import Foundation
import TypeWhisperPluginSDK

actor RealtimeSession: LiveTranscriptionSession {
    private let network: URLSession
    private let socket: URLSessionWebSocketTask
    private let release: @Sendable (Bool) async -> Void
    private let onProgress: @Sendable (String) -> Bool
    private var state = TranscriptState()
    private var receiver: Task<Void, Never>?
    private var failure: Error?
    private var closed = false
    private var sending = false
    private var finishing = false

    private init(url: URL, release: @escaping @Sendable (Bool) async -> Void,
                 onProgress: @escaping @Sendable (String) -> Bool) {
        network = LocalNetwork.makeSession()
        socket = network.webSocketTask(with: url)
        socket.maximumMessageSize = 1024 * 1024
        self.release = release
        self.onProgress = onProgress
    }

    static func connect(url: URL, release: @escaping @Sendable (Bool) async -> Void,
                        onProgress: @escaping @Sendable (String) -> Bool) async throws -> RealtimeSession {
        guard url.host == "127.0.0.1", url.scheme == "ws" else { throw R2T2Error.message("Only loopback WebSockets are supported.") }
        let session = RealtimeSession(url: url, release: release, onProgress: onProgress)
        do { try await session.start(); return session }
        catch { await session.cancel(); throw error }
    }

    private func start() async throws {
        socket.resume()
        receiver = Task { await self.receive() }
        let deadline = Date().addingTimeInterval(15)
        while !state.ready {
            try checkOpen()
            guard Date() < deadline else { throw R2T2Error.message("Realtime handshake timed out.") }
            try await Task.sleep(for: .milliseconds(50))
        }
    }

    private func checkOpen() throws {
        try Task.checkCancellation()
        if let failure { throw failure }
        if closed { throw R2T2Error.message("Realtime session is closed.") }
    }
    private func checkEntry() async throws {
        do { try checkOpen() }
        catch { await cancel(); throw error }
    }

    func appendAudio(samples: [Float]) async throws {
        try await checkEntry()
        guard !sending && !finishing else { throw R2T2Error.message("Overlapping or finalized audio input.") }
        sending = true
        defer { sending = false }
        do {
            for offset in stride(from: 0, to: samples.count, by: 5120) {
                let chunk = Array(samples[offset..<min(samples.count, offset + 5120)])
                try state.addSamples(chunk.count)
                try await send(["type": "input_audio_buffer.append", "audio": PCM16.encode(chunk).base64EncodedString()])
            }
        } catch { await cancel(); throw error }
    }

    func finish() async throws -> PluginTranscriptionResult {
        try await checkEntry()
        guard !sending && !finishing else { throw R2T2Error.message("Session is already finishing or sending audio.") }
        finishing = true
        do {
            state.endInput()
            try await send(["type": "input_audio_buffer.commit"])
            let deadline = Date().addingTimeInterval(600)
            while !state.isFinished {
                try checkOpen()
                guard Date() < deadline else { throw R2T2Error.message("Final transcript timed out.") }
                try await Task.sleep(for: .milliseconds(50))
            }
            // The final progress callback can cancel after setting isFinished;
            // completion coverage alone must never override that failure.
            try checkOpen()
            let result = PluginTranscriptionResult(text: state.text)
            await close(success: true)
            return result
        } catch { await cancel(); throw error }
    }

    private func send(_ message: [String: String]) async throws {
        let json = try JSONEncoder().encode(message)
        try await socket.send(.string(String(decoding: json, as: UTF8.self)))
    }

    private func receive() async {
        do {
            while !closed {
                let message = try await socket.receive()
                let data: Data
                switch message {
                case .string(let text): data = Data(text.utf8)
                case .data(let bytes): data = bytes
                @unknown default: throw R2T2Error.message("Unsupported realtime frame.")
                }
                guard data.count <= 1024 * 1024 else { throw R2T2Error.message("Realtime message exceeds 1 MiB.") }
                let event = try JSONDecoder().decode(RealtimeEvent.self, from: data)
                try state.accept(event)
                if event.type.hasSuffix(".delta") || event.type.hasSuffix(".completed") {
                    guard onProgress(state.text) else { throw CancellationError() }
                }
            }
        } catch {
            if !closed { failure = error; await close(success: false) }
        }
    }

    func cancel() async { await close(success: false) }
    private func close(success: Bool) async {
        guard !closed else { return }
        closed = true
        receiver?.cancel()
        socket.cancel(with: .goingAway, reason: nil)
        network.invalidateAndCancel()
        await release(success)
    }
}

final class NoRedirects: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping @Sendable (URLRequest?) -> Void) { completionHandler(nil) }
}

enum LocalNetwork {
    static func makeSession() -> URLSession {
        let config = URLSessionConfiguration.ephemeral
        config.connectionProxyDictionary = ["HTTPEnable": 0, "HTTPSEnable": 0, "SOCKSEnable": 0]
        config.timeoutIntervalForRequest = 120
        config.timeoutIntervalForResource = 600
        return URLSession(configuration: config, delegate: NoRedirects(), delegateQueue: nil)
    }
}
