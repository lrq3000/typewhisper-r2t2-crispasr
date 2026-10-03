using System.Text.Json;

namespace R2T2CrispASR;

// Lock ownership belongs to the transport. This class has no network side effects.
public sealed class TranscriptState
{
    private readonly List<string> turns = [];
    private string draft = "";
    private long sentSamples;
    private long processedMilliseconds;
    private int completedTurns;
    private int maxTurnSamples = 480000;
    private bool ended;

    public string Text => string.Join(" ", turns.Append(draft).Where(text => !string.IsNullOrWhiteSpace(text)));
    public string CurrentDraft => draft;
    public bool Ready { get; private set; }
    public bool IsFinished => ended && completedTurns >= sentSamples / maxTurnSamples + 1
        && processedMilliseconds >= sentSamples / 16;

    public void AddSamples(int count)
    {
        if (ended || count < 0) throw new InvalidOperationException("Audio input is closed.");
        sentSamples = checked(sentSamples + count);
    }

    public void EndInput() => ended = true;

    public void Accept(JsonElement message)
    {
        switch (message.GetProperty("type").GetString())
        {
            case "session.created":
                if (!message.GetProperty("partial_transcription").GetBoolean()
                    || message.GetProperty("turn_detection").GetString() != "client_commit")
                    throw new InvalidOperationException("CrispASR did not enable R2T2 realtime decoding with client commit.");
                int seconds = message.GetProperty("max_turn_seconds").GetInt32();
                if (seconds is < 1 or > 300) throw new InvalidOperationException("Invalid realtime turn limit.");
                maxTurnSamples = seconds * 16000;
                Ready = true;
                break;
            case "session.updated":
                if (message.TryGetProperty("partial_transcription", out var partial) && !partial.GetBoolean())
                    throw new InvalidOperationException("CrispASR lost native realtime decoding.");
                break;
            case "conversation.item.input_audio_transcription.delta":
                draft += message.GetProperty("delta").GetString();
                break;
            case "conversation.item.input_audio_transcription.completed":
                var text = message.GetProperty("transcript").GetString() ?? "";
                // Completed text replaces its draft; adding both would duplicate dictation.
                if (!string.IsNullOrWhiteSpace(text)) turns.Add(text);
                draft = "";
                long duration = message.GetProperty("audio_duration_ms").GetInt64();
                if (duration < 0) throw new InvalidOperationException("Invalid completion duration.");
                processedMilliseconds = checked(processedMilliseconds + duration);
                completedTurns++;
                break;
            case "error":
                throw new InvalidOperationException("CrispASR reported a realtime inference error.");
        }
    }
}
