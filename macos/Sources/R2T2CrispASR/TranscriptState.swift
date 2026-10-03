import Foundation

enum R2T2Error: LocalizedError {
    case message(String)
    var errorDescription: String? { if case let .message(text) = self { text } else { nil } }
}

struct RealtimeEvent: Decodable, Sendable {
    let type: String
    let delta: String?
    let transcript: String?
    let partial_transcription: Bool?
    let turn_detection: String?
    let max_turn_seconds: Int?
    let audio_duration_ms: Int64?
}

struct TranscriptState: Sendable {
    private var turns: [String] = []
    private var draft = ""
    private var sentSamples: Int64 = 0
    private var processedMilliseconds: Int64 = 0
    private var completedTurns: Int64 = 0
    private var maxTurnSamples: Int64 = 480000
    private var ended = false
    private(set) var ready = false
    var text: String { (turns + [draft]).filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }.joined(separator: " ") }
    var isFinished: Bool { ended && completedTurns >= sentSamples / maxTurnSamples + 1 && processedMilliseconds >= sentSamples / 16 }

    mutating func addSamples(_ count: Int) throws {
        guard !ended && count >= 0 else { throw R2T2Error.message("Audio input is closed.") }
        sentSamples += Int64(count)
    }
    mutating func endInput() { ended = true }
    mutating func accept(_ event: RealtimeEvent) throws {
        switch event.type {
        case "session.created":
            guard event.partial_transcription == true, event.turn_detection == "client_commit",
                  let seconds = event.max_turn_seconds, (1...300).contains(seconds) else {
                throw R2T2Error.message("CrispASR did not enable the required R2T2 realtime contract.")
            }
            maxTurnSamples = Int64(seconds) * 16000
            ready = true
        case "session.updated":
            if event.partial_transcription == false { throw R2T2Error.message("CrispASR lost native realtime decoding.") }
        case "conversation.item.input_audio_transcription.delta":
            guard let delta = event.delta else { throw R2T2Error.message("Missing realtime delta.") }
            draft += delta
        case "conversation.item.input_audio_transcription.completed":
            guard let final = event.transcript, let duration = event.audio_duration_ms, duration >= 0 else {
                throw R2T2Error.message("Invalid realtime completion.")
            }
            if !final.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { turns.append(final) }
            draft = ""
            processedMilliseconds += duration
            completedTurns += 1
        case "error": throw R2T2Error.message("CrispASR reported an inference error.")
        default: break
        }
    }
}

enum PCM16 {
    static func encode(_ samples: [Float]) -> Data {
        var result = Data(capacity: samples.count * 2)
        for sample in samples {
            let value: Int16 = sample.isFinite ? Int16(max(-32768, min(32767, sample * 32768))) : 0
            var littleEndian = value.littleEndian
            withUnsafeBytes(of: &littleEndian) { result.append(contentsOf: $0) }
        }
        return result
    }
}
