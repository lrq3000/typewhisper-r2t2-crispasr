using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace R2T2CrispASR;

internal sealed class ManagedRuntime(string directory, HttpClient http, Action<string> log)
{
    private Process? process;
    private ProcessJob? job;
    private string? configuration;
    private readonly object outputLock = new();
    private string tail = "";
    public Uri HttpUrl { get; private set; } = null!;
    public Uri RealtimeUrl { get; private set; } = null!;
    public string ApiKey { get; private set; } = "";
    public bool Matches(string path, string? language) => process is { HasExited: false }
        && configuration == path + "\n" + (language ?? "auto");

    public async Task StartAsync(string path, string? language, CancellationToken ct)
    {
        if (Matches(path, language)) return;
        await StopAsync();
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "runtime.json")));
        if (!metadata.RootElement.GetProperty("loopbackOnly").GetBoolean()
            || metadata.RootElement.GetProperty("revision").GetString() != "340d7085eaa53c40a46dcb73a6d3d0448a480006")
            throw new InvalidDataException("The plugin requires its pinned loopback-only CrispASR runtime.");
        Exception? failure = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                int port = FreePort();
                int rawPort = AdjacentPorts();
                HttpUrl = new Uri($"http://127.0.0.1:{port}");
                RealtimeUrl = new Uri($"ws://127.0.0.1:{rawPort + 1}/v1/realtime");
                ApiKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                var start = new ProcessStartInfo(Path.Combine(directory, "crispasr.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = directory };
                foreach (string argument in new[] { "--server", "--backend", "qwen3", "--model", path, "--host", "127.0.0.1", "--port", port.ToString(),
                    "--ws-port", rawPort.ToString(), "--language", language ?? "auto", "--threads", Math.Clamp(Environment.ProcessorCount - 2, 1, 8).ToString() })
                    start.ArgumentList.Add(argument);
                start.Environment["CRISPASR_API_KEYS"] = ApiKey;
                start.Environment["CRISPASR_QWEN3_STREAM"] = "1";
                start.Environment["CRISPASR_QWEN3_STREAM_STEP_MS"] = "320";
                start.Environment["CRISPASR_CACHE_DIR"] = Path.GetDirectoryName(path)!;
                process = new Process { StartInfo = start };
                process.OutputDataReceived += Capture;
                process.ErrorDataReceived += Capture;
                lock (outputLock) tail = "";
                if (!process.Start()) throw new IOException("Could not start CrispASR.");
                job = new ProcessJob(process);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
                startup.CancelAfter(TimeSpan.FromMinutes(5));
                while (true)
                {
                    startup.Token.ThrowIfCancellationRequested();
                    if (process.HasExited) throw new IOException("CrispASR exited during startup. " + OutputTail());
                    try
                    {
                        using var probe = CancellationTokenSource.CreateLinkedTokenSource(startup.Token);
                        probe.CancelAfter(TimeSpan.FromSeconds(2));
                        using var response = await http.GetAsync(new Uri(HttpUrl, "/health"), probe.Token);
                        if (response.IsSuccessStatusCode) break;
                    }
                    catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { }
                    await Task.Delay(100, startup.Token);
                }
                configuration = path + "\n" + (language ?? "auto");
                log("CrispASR is ready for local R2T2 transcription.");
                return;
            }
            catch (Exception error)
            {
                failure = error;
                await StopAsync();
                // Retry only bind races, not model corruption or a 5-minute timeout.
                string output = OutputTail();
                if (ct.IsCancellationRequested || !(output.Contains("bind", StringComparison.OrdinalIgnoreCase)
                    || output.Contains("address already in use", StringComparison.OrdinalIgnoreCase))) throw;
            }
        }
        throw new IOException("Could not bind local CrispASR listeners.", failure);
    }

    private void Capture(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is not string line) return;
        lock (outputLock)
        {
            tail += line + "\n";
            if (tail.Length > 12000) tail = tail[^12000..];
        }
    }
    private string OutputTail() { lock (outputLock) return tail; }
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private static int AdjacentPorts()
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            int port = FreePort();
            if (port >= 65535) continue;
            try
            {
                using var first = new TcpListener(IPAddress.Loopback, port);
                using var second = new TcpListener(IPAddress.Loopback, port + 1);
                first.Start(); second.Start();
                return port;
            }
            catch (SocketException) { }
        }
        throw new IOException("Cannot allocate local WebSocket ports.");
    }

    public async Task StopAsync()
    {
        var running = process;
        process = null;
        configuration = null;
        try
        {
            if (running is { HasExited: false })
            {
                running.Kill(entireProcessTree: true);
                await running.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
        }
        finally
        {
            job?.Dispose(); job = null;
            running?.Dispose();
        }
    }
}
