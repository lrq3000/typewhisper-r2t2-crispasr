import Foundation
import Testing
@testable import R2T2CrispASR

struct TranscriptTests {
    private func event(_ json: String) throws -> RealtimeEvent {
        try JSONDecoder().decode(RealtimeEvent.self, from: Data(json.utf8))
    }

    @Test func authoritativeCompletionReplacesPreview() throws {
        var state = TranscriptState()
        try state.accept(event(#"{"type":"conversation.item.input_audio_transcription.delta","delta":"draft"}"#))
        try state.accept(event(#"{"type":"conversation.item.input_audio_transcription.completed","transcript":"Correct final.","audio_duration_ms":1000}"#))
        #expect(state.text == "Correct final.")
    }

    @Test func exactTurnBoundaryWaitsForExplicitCommit() throws {
        var state = TranscriptState()
        try state.accept(event(#"{"type":"session.created","partial_transcription":true,"turn_detection":"client_commit","max_turn_seconds":30}"#))
        try state.addSamples(480000)
        state.endInput()
        try state.accept(event(#"{"type":"conversation.item.input_audio_transcription.completed","transcript":"你好。","audio_duration_ms":30000}"#))
        #expect(!state.isFinished)
        try state.accept(event(#"{"type":"conversation.item.input_audio_transcription.completed","transcript":"","audio_duration_ms":0}"#))
        #expect(state.isFinished)
        #expect(state.text == "你好。")
    }

    @Test func multipleTurnsRetainFinalText() throws {
        var state = TranscriptState()
        try state.addSamples(496000)
        state.endInput()
        try state.accept(event(#"{"type":"conversation.item.input_audio_transcription.completed","transcript":"你好。","audio_duration_ms":30000}"#))
        try state.accept(event(#"{"type":"conversation.item.input_audio_transcription.delta","delta":"Hello"}"#))
        #expect(state.text == "你好。 Hello")
        try state.accept(event(#"{"type":"conversation.item.input_audio_transcription.completed","transcript":"Hello!","audio_duration_ms":1000}"#))
        #expect(state.isFinished)
        #expect(state.text == "你好。 Hello!")
    }

    @Test func fallbackHandshakeFails() throws {
        var state = TranscriptState()
        let message = try event(#"{"type":"session.created","partial_transcription":false,"turn_detection":"client_commit","max_turn_seconds":30}"#)
        #expect(throws: R2T2Error.self) { try state.accept(message) }
    }

    @Test func floatConversionClipsAndSanitizes() {
        #expect(PCM16.encode([-.infinity, -2, -1, 0, 1, 2, .nan]) == Data([0,0,0,128,0,128,0,0,255,127,255,127,0,0]))
    }
}
