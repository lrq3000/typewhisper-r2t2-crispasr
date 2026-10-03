import Foundation
import TypeWhisperPluginSDK

actor PluginEngine {
    private let store: ModelStore
    private let runtime: ManagedRuntime
    private let network = LocalNetwork.makeSession()
    private var owner: UUID?
    init(store: ModelStore, runtime: ManagedRuntime) { self.store = store; self.runtime = runtime }
    private func begin() throws -> UUID {
        guard owner == nil else { throw R2T2Error.message("R2T2 is busy. Finish the current recording or model operation first.") }
        let token = UUID(); owner = token; return token
    }
    private func end(_ token: UUID, success: Bool, unload: Bool = false) {
        guard owner == token else { return }
        if !success || unload { runtime.stop() }
        owner = nil
    }
    private func ensureRuntime(id: String, language: String?) async throws {
        let model = try store.modelURL(id)
        if !runtime.matches(model: model, language: language) {
            try await store.verifyForLoad(id)
            try await runtime.start(model: model, language: language)
        }
    }
    func live(id: String, language: String?, unload: Bool,
              onProgress: @escaping @Sendable (String) -> Bool) async throws -> RealtimeSession {
        let token = try begin()
        do {
            try await ensureRuntime(id: id, language: language)
            return try await RealtimeSession.connect(url: runtime.endpoint().realtime, release: { success in
                await self.end(token, success: success, unload: unload)
            }, onProgress: onProgress)
        } catch { end(token, success: false); throw error }
    }
    func batch(id: String, audio: AudioData, language: String?, unload: Bool) async throws -> PluginTranscriptionResult {
        guard audio.wavData.count <= 256 * 1024 * 1024 else { throw R2T2Error.message("Recording exceeds the plugin's upload limit.") }
        let token = try begin()
        do {
            try await ensureRuntime(id: id, language: language)
            let endpoint = try runtime.endpoint()
            let boundary = "R2T2-" + UUID().uuidString
            var body = Data()
            func field(_ name: String, _ value: String) {
                body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".utf8))
            }
            field("response_format", "json")
            if let language { field("language", language) }
            body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"file\"; filename=\"recording.wav\"\r\nContent-Type: audio/wav\r\n\r\n".utf8))
            body.append(audio.wavData)
            body.append(Data("\r\n--\(boundary)--\r\n".utf8))
            var request = URLRequest(url: endpoint.http.appendingPathComponent("v1/audio/transcriptions"))
            request.httpMethod = "POST"
            request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
            request.setValue("Bearer \(endpoint.apiKey)", forHTTPHeaderField: "Authorization")
            request.httpBody = body
            let (data, response) = try await network.data(for: request)
            guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw R2T2Error.message("CrispASR batch transcription failed.") }
            struct Result: Decodable { let text: String }
            let result = try JSONDecoder().decode(Result.self, from: data)
            end(token, success: true, unload: unload)
            return PluginTranscriptionResult(text: result.text)
        } catch { end(token, success: false); throw error }
    }
    func download(_ id: String, progress: @escaping @Sendable (Double) -> Void) async throws {
        let token = try begin()
        defer { end(token, success: true) }
        try await store.download(id, progress: progress)
    }
    func remove(_ id: String) throws {
        let token = try begin()
        defer { end(token, success: true) }
        runtime.stop()
        try store.remove(id)
    }
    func unload() throws {
        let token = try begin()
        runtime.stop()
        end(token, success: true)
    }
}
