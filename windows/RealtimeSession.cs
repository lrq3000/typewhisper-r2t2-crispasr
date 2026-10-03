using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace R2T2CrispASR;

public sealed class RealtimeSession : IStreamingSession
{
    private readonly ClientWebSocket socket = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writer = new(1);
    private readonly object sync = new();
    private readonly TranscriptState state = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<bool, Task> release;
    private Task? receiver;
    private int disposed;
    private bool succeeded;

    public event Action<StreamingTranscriptEvent>? TranscriptReceived;
    public string Text { get { lock (sync) return state.Text; } }

    private RealtimeSession(Func<bool, Task> release) => this.release = release;

    public static async Task<RealtimeSession> ConnectAsync(Uri url, Func<bool, Task> release, CancellationToken ct)
    {
        if (!url.IsLoopback || url.Scheme != "ws") throw new ArgumentException("Only loopback WebSockets are allowed.");
        var session = new RealtimeSession(release);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await session.socket.ConnectAsync(url, timeout.Token);
            session.receiver = session.ReceiveAsync();
            await session.ready.Task.WaitAsync(timeout.Token);
            return session;
        }
        catch { await session.DisposeAsync(); throw; }
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
    {
        if (pcm16Audio.Length % 2 != 0) throw new ArgumentException("PCM16 input contains an incomplete sample.");
        await writer.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            // Bound each frame even when the host supplies a whole recording.
            for (int offset = 0; offset < pcm16Audio.Length; offset += 10240)
            {
                var chunk = pcm16Audio.Slice(offset, Math.Min(10240, pcm16Audio.Length - offset));
                lock (sync) state.AddSamples(chunk.Length / 2);
                await SendJsonAsync(new { type = "input_audio_buffer.append", audio = Convert.ToBase64String(chunk.Span) }, ct);
            }
        }
        catch { socket.Abort(); throw; }
        finally { writer.Release(); }
    }

    public async Task FinalizeAsync(CancellationToken ct)
    {
        await writer.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            lock (sync) state.EndInput();
            await SendJsonAsync(new { type = "input_audio_buffer.commit" }, ct);
        }
        finally { writer.Release(); }
        // A transport close or automatic 30-second completion is not final success.
        try
        {
            await finished.Task.WaitAsync(TimeSpan.FromMinutes(10), ct);
            succeeded = true;
        }
        catch { socket.Abort(); throw; }
    }

    private async Task SendJsonAsync<T>(T message, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message).AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
    }

    private async Task ReceiveAsync()
    {
        var buffer = new byte[8192];
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult packet;
                do
                {
                    packet = await socket.ReceiveAsync(buffer.AsMemory(), lifetime.Token);
                    if (packet.MessageType != WebSocketMessageType.Text)
                        throw new IOException("CrispASR closed or returned a non-text realtime message.");
                    if (message.Length + packet.Count > 1024 * 1024) throw new IOException("Realtime message exceeds 1 MiB.");
                    message.Write(buffer, 0, packet.Count);
                } while (!packet.EndOfMessage);
                using var json = JsonDocument.Parse(message.ToArray());
                string snapshot;
                bool final, complete;
                lock (sync)
                {
                    state.Accept(json.RootElement);
                    if (state.Ready) ready.TrySetResult();
                    complete = state.IsFinished;
                    snapshot = state.CurrentDraft;
                    final = json.RootElement.GetProperty("type").GetString() == "conversation.item.input_audio_transcription.completed";
                }
                // SDK partials replace the current segment preview, so convert native
                // deltas to a per-turn snapshot before notifying the host.
                string type = json.RootElement.GetProperty("type").GetString()!;
                if (type.EndsWith(".delta", StringComparison.Ordinal))
                    TranscriptReceived?.Invoke(new(snapshot, false));
                else if (final)
                    TranscriptReceived?.Invoke(new(json.RootElement.GetProperty("transcript").GetString() ?? "", true));
                // Finalize must drain the host callback too, not merely the socket read.
                if (complete) finished.TrySetResult();
            }
        }
        catch (Exception error)
        {
            ready.TrySetException(error);
            finished.TrySetException(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        socket.Abort();
        if (receiver is not null) await receiver;
        socket.Dispose();
        lifetime.Dispose();
        await release(succeeded);
    }
}
