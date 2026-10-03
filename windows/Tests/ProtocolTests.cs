using System.Text.Json;
using Xunit;

namespace R2T2CrispASR.Tests;

public class ProtocolTests
{
    private static void Event(TranscriptState state, string json)
    {
        using var message = JsonDocument.Parse(json);
        state.Accept(message.RootElement);
    }

    [Fact]
    public void CompletionReplacesDraftWithoutDuplicatingIt()
    {
        var state = new TranscriptState();
        Event(state, """{"type":"conversation.item.input_audio_transcription.delta","delta":"draft"}""");
        Event(state, """{"type":"conversation.item.input_audio_transcription.completed","transcript":"Correct final.","audio_duration_ms":1000}""");
        Assert.Equal("Correct final.", state.Text);
    }

    [Fact]
    public void ExactThirtySecondRecordingRequiresEmptyCommitAcknowledgement()
    {
        var state = new TranscriptState();
        Event(state, """{"type":"session.created","partial_transcription":true,"turn_detection":"client_commit","max_turn_seconds":30}""");
        state.AddSamples(480000);
        state.EndInput();
        Event(state, """{"type":"conversation.item.input_audio_transcription.completed","transcript":"First.","audio_duration_ms":30000}""");
        Assert.False(state.IsFinished); // The automatic turn is not our commit acknowledgement.
        Event(state, """{"type":"conversation.item.input_audio_transcription.completed","transcript":"","audio_duration_ms":0}""");
        Assert.True(state.IsFinished);
        Assert.Equal("First.", state.Text);
    }

    [Fact]
    public void MultipleTurnsRetainUnicodeAndFinalText()
    {
        var state = new TranscriptState();
        Event(state, """{"type":"session.created","partial_transcription":true,"turn_detection":"client_commit","max_turn_seconds":30}""");
        state.AddSamples(496000);
        state.EndInput();
        Event(state, """{"type":"conversation.item.input_audio_transcription.completed","transcript":"你好。","audio_duration_ms":30000}""");
        Event(state, """{"type":"conversation.item.input_audio_transcription.delta","delta":"Hello"}""");
        Assert.Equal("你好。 Hello", state.Text);
        Event(state, """{"type":"conversation.item.input_audio_transcription.completed","transcript":"Hello!","audio_duration_ms":1000}""");
        Assert.True(state.IsFinished);
        Assert.Equal("你好。 Hello!", state.Text);
    }

    [Theory]
    [InlineData("""{"type":"session.created","partial_transcription":false,"turn_detection":"client_commit","max_turn_seconds":30}""")]
    [InlineData("""{"type":"session.updated","partial_transcription":false}""")]
    [InlineData("""{"type":"error","error":{"message":"inference failed"}}""")]
    public void FailedRealtimeContractIsAnError(string json)
    {
        Assert.Throws<InvalidOperationException>(() => Event(new TranscriptState(), json));
    }

    [Fact]
    public void MissingAudioCoverageDoesNotCountAsComplete()
    {
        var state = new TranscriptState();
        state.AddSamples(16000);
        state.EndInput();
        Event(state, """{"type":"conversation.item.input_audio_transcription.completed","transcript":"partial","audio_duration_ms":500}""");
        Assert.False(state.IsFinished);
    }
}
