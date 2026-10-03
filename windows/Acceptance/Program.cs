using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using R2T2CrispASR;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

if (args.Length != 2) throw new ArgumentException("Usage: Acceptance <asset-directory> <16kHz mono PCM16 WAV>");
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
using var plugin = new R2T2Plugin();
await plugin.ActivateAsync(new ProbeHost(Path.GetFullPath(args[0])));
plugin.SelectModel("r2t2-q4_k");
if (!plugin.IsModelDownloaded("r2t2-q4_k"))
    await plugin.DownloadModelAsync("r2t2-q4_k", new DownloadProgress(), timeout.Token);
byte[] wave = await File.ReadAllBytesAsync(args[1], timeout.Token);
int offset = 12, dataOffset = -1, dataLength = 0;
while (offset + 8 <= wave.Length)
{
    int size = BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(offset + 4, 4));
    if (size < 0 || offset + 8L + size > wave.Length) throw new InvalidDataException("Invalid WAV chunk.");
    if (wave.AsSpan(offset, 4).SequenceEqual("data"u8)) { dataOffset = offset + 8; dataLength = size; break; }
    offset += 8 + size + (size & 1);
}
if (dataOffset < 0) throw new InvalidDataException("No audio samples.");
// Four seconds of real speech is enough to exercise pre-commit decoding without
// making CPU acceptance a lengthy performance benchmark.
var pcm = wave.AsMemory(dataOffset, Math.Min(dataLength, 4 * 32000));
for (int recording = 1; recording <= 2; recording++)
{
    bool partialBeforeCommit = false, committing = false;
    string final = "";
    await using var session = await plugin.StartStreamingAsync("en", timeout.Token);
    if (recording == 1)
    {
        using var running = RuntimeChild.Find();
        var listeners = RuntimeChild.Listeners(running.Id);
        if (listeners.Length != 3 || listeners.Any(endpoint => !IPAddress.IsLoopback(endpoint.Address)))
            throw new Exception("Expected three new loopback-only HTTP/WebSocket listeners.");
        Console.WriteLine("All three runtime listeners are loopback-only PASS");
    }
    session.TranscriptReceived += update =>
    {
        if (!committing && !update.IsFinal && !string.IsNullOrWhiteSpace(update.Text)) partialBeforeCommit = true;
        if (update.IsFinal) final += update.Text;
    };
    for (int position = 0; position < pcm.Length; position += 10240)
        await session.SendAudioAsync(pcm.Slice(position, Math.Min(10240, pcm.Length - position)), timeout.Token);
    // Permit decoding of queued appends before the explicit commit.
    var deadline = DateTime.UtcNow.AddMinutes(5);
    while (!partialBeforeCommit && DateTime.UtcNow < deadline) await Task.Delay(100, timeout.Token);
    if (!partialBeforeCommit) throw new Exception("No transcript delta before commit.");
    committing = true;
    await session.FinalizeAsync(timeout.Token);
    if (string.IsNullOrWhiteSpace(final)) throw new Exception("Missing final transcript.");
    Console.WriteLine($"Recording {recording}: pre-commit text + final flush PASS: {final}");
}
// A canonical WAV for the same speech exercises the separate HTTP batch path.
using var encoded = new MemoryStream();
using (var writer = new BinaryWriter(encoded, System.Text.Encoding.UTF8, leaveOpen: true))
{
    writer.Write("RIFF"u8); writer.Write(36 + pcm.Length); writer.Write("WAVEfmt "u8);
    writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000);
    writer.Write(32000); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(pcm.Length); writer.Write(pcm.Span);
}
var batch = await plugin.TranscribeAsync(encoded.ToArray(), "en", false, null, timeout.Token);
if (string.IsNullOrWhiteSpace(batch.Text) || batch.DurationSeconds != pcm.Length / 32000.0) throw new Exception("Batch transcription failed.");
Console.WriteLine($"HTTP batch transcription PASS: {batch.Text}");
using var child = RuntimeChild.Find();
await plugin.UnloadModelAsync();
if (!child.HasExited) throw new Exception("Runtime was not stopped on unload.");
Console.WriteLine("Runtime shutdown PASS");

sealed class DownloadProgress : IProgress<double>
{
    private int last = -1;
    public void Report(double value)
    {
        int bucket = (int)(value * 10);
        if (bucket > last) { last = bucket; Console.WriteLine($"Model download: {bucket * 10}%"); }
    }
}

static class RuntimeChild
{
    public static Process Find() => Process.GetProcessesByName("crispasr").Single(process => process.MainModule?.FileName == Path.Combine(AppContext.BaseDirectory, "Runtime", "crispasr.exe"));
    public static IPEndPoint[] Listeners(int pid)
    {
        int length = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref length, true, 2, 3, 0);
        IntPtr table = Marshal.AllocHGlobal(length);
        try
        {
            if (GetExtendedTcpTable(table, ref length, true, 2, 3, 0) != 0) throw new IOException("Cannot inspect the runtime's TCP listeners.");
            var endpoints = new List<IPEndPoint>();
            int count = Marshal.ReadInt32(table);
            for (int i = 0; i < count; i++)
            {
                IntPtr row = table + 4 + i * 24;
                if (Marshal.ReadInt32(row, 20) != pid) continue;
                byte[] address = BitConverter.GetBytes(Marshal.ReadInt32(row, 4));
                ushort port = unchecked((ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, 8)));
                endpoints.Add(new IPEndPoint(new IPAddress(address), port));
            }
            return endpoints.ToArray();
        }
        finally { Marshal.FreeHGlobal(table); }
    }
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int length, [MarshalAs(UnmanagedType.Bool)] bool sorted, int family, int tableClass, uint reserved);
}

sealed class ProbeHost(string directory) : IPluginHostServices
{
    private readonly Dictionary<string, object?> settings = [];
    public string PluginDataDirectory => directory;
    public string? ActiveAppProcessName => null;
    public string? ActiveAppName => null;
    public IPluginEventBus EventBus => throw new NotSupportedException();
    public IPluginLocalization Localization => throw new NotSupportedException();
    public IReadOnlyList<string> AvailableProfileNames => [];
    public Task StoreSecretAsync(string key, string value) => Task.CompletedTask;
    public Task<string?> LoadSecretAsync(string key) => Task.FromResult<string?>(null);
    public Task DeleteSecretAsync(string key) => Task.CompletedTask;
    public T? GetSetting<T>(string key) => settings.TryGetValue(key, out var value) ? (T?)value : default;
    public void SetSetting<T>(string key, T value) => settings[key] = value;
    public void Log(PluginLogLevel level, string text) => Console.WriteLine(text);
    public void NotifyCapabilitiesChanged() { }
}
