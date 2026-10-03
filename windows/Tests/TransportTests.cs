using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace R2T2CrispASR.Tests;

public class TransportTests
{
    [Fact]
    public async Task FragmentedUtf8AndFinalCallbackAreDrainedBeforeFinalizeReturns()
    {
        await using var server = await StartServer(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        bool released = false, finalReceived = false;
        await using var session = await RealtimeSession.ConnectAsync(Url(server), success => { released = success; return Task.CompletedTask; }, deadline.Token);
        session.TranscriptReceived += update =>
        {
            if (update.IsFinal) { Thread.Sleep(100); finalReceived = true; }
        };
        await session.SendAudioAsync(new byte[32000], deadline.Token);
        await session.FinalizeAsync(deadline.Token);
        Assert.True(finalReceived);
        Assert.Equal("你好!", session.Text);
        await session.DisposeAsync();
        Assert.True(released);
    }

    [Fact]
    public async Task DisconnectedStreamFailsRatherThanReturningDraft()
    {
        await using var server = await StartServer(true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        bool success = true;
        await using var session = await RealtimeSession.ConnectAsync(Url(server), value => { success = value; return Task.CompletedTask; }, deadline.Token);
        await session.SendAudioAsync(new byte[32000], deadline.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => session.FinalizeAsync(deadline.Token));
        await session.DisposeAsync();
        Assert.False(success);
    }

    private static Uri Url(WebApplication app)
    {
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new Uri(address.Replace("http://", "ws://") + "/");
    }

    private static async Task<WebApplication> StartServer(bool disconnect)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/", async context =>
        {
            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            await Send(ws, """{"type":"session.created","partial_transcription":true,"turn_detection":"client_commit","max_turn_seconds":30}""");
            byte[] buffer = new byte[16384];
            while (ws.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult packet;
                do { packet = await ws.ReceiveAsync(buffer, CancellationToken.None); message.Write(buffer, 0, packet.Count); } while (!packet.EndOfMessage);
                if (packet.MessageType == WebSocketMessageType.Close) return;
                using var json = JsonDocument.Parse(message.ToArray());
                if (json.RootElement.GetProperty("type").GetString() != "input_audio_buffer.commit") continue;
                await Send(ws, """{"type":"conversation.item.input_audio_transcription.delta","delta":"你好"}""");
                if (disconnect) { await ws.CloseOutputAsync(WebSocketCloseStatus.InternalServerError, "test failure", CancellationToken.None); return; }
                await Send(ws, """{"type":"conversation.item.input_audio_transcription.completed","transcript":"你好!","audio_duration_ms":1000}""");
            }
        });
        await app.StartAsync();
        return app;
    }

    private static async Task Send(WebSocket ws, string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        // Fragment at every byte, including within a Chinese UTF-8 codepoint.
        for (int i = 0; i < bytes.Length; i++)
            await ws.SendAsync(bytes.AsMemory(i, 1), WebSocketMessageType.Text, i == bytes.Length - 1, CancellationToken.None);
    }
}
