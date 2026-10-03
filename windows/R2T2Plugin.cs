using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace R2T2CrispASR;

public sealed class R2T2Plugin : ITranscriptionEnginePlugin, IPluginSettingsActions
{
    private readonly HttpClient downloads = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient local = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim operation = new(1);
    private readonly object settingsLock = new();
    private IPluginHostServices? host;
    private ModelStore? store;
    private ManagedRuntime? runtime;
    private string selected = "r2t2-q8_0";
    private bool disposed;
    public string PluginId => "com.typewhisper.r2t2-crispasr";
    public string PluginName => "R2T2 (CrispASR)";
    public string PluginVersion => "0.1.0";
    public string ProviderId => "r2t2-crispasr";
    public string ProviderDisplayName => PluginName;
    public string? SelectedModelId { get { lock (settingsLock) return selected; } }
    public bool IsConfigured => host is not null && store?.IsDownloaded(SelectedModelId!) == true
        && File.Exists(Path.Combine(PackageDirectory, "Runtime", "runtime.json"));
    public bool SupportsTranslation => false;
    public bool SupportsStreaming => true;
    public bool SupportsStreamingCompletion => true;
    public bool SupportsModelDownload => true;
    public bool SupportsModelRemoval => true;
    public int MaximumAudioUploadBytes => 256 * 1024 * 1024;
    public IReadOnlyList<string> SupportedLanguages => store?.Catalog.Languages ?? [];
    private static string PackageDirectory => Path.GetDirectoryName(typeof(R2T2Plugin).Assembly.Location)!;

    public IReadOnlyList<PluginModelInfo> TranscriptionModels => store?.Catalog.Models.Select(model => new PluginModelInfo(model.Id, model.Name)
    {
        Publisher = "NetEase Youdao",
        SizeDescription = $"{model.Size / 1_000_000_000.0:F2} GB",
        EstimatedSizeMB = model.Size / 1_000_000,
        IsRecommended = model.Id == "r2t2-q8_0",
        LanguageCount = SupportedLanguages.Count,
        LanguageCodes = SupportedLanguages
    }).ToArray() ?? [];

    public Task ActivateAsync(IPluginHostServices services)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        host = services;
        store = new ModelStore(Path.Combine(services.PluginAssetDirectory, "models"), Path.Combine(PackageDirectory, "models.json"), downloads);
        string? saved = services.GetSetting<string>("selectedModel");
        if (saved is not null && store.Catalog.Models.Any(model => model.Id == saved)) selected = saved;
        runtime = new ManagedRuntime(Path.Combine(PackageDirectory, "Runtime"), local, text => services.Log(PluginLogLevel.Info, text));
        return Task.CompletedTask;
    }

    public void SelectModel(string modelId)
    {
        _ = Store.Model(modelId);
        lock (settingsLock)
        {
            host!.SetSetting("selectedModel", modelId);
            selected = modelId;
        }
        host!.NotifyCapabilitiesChanged();
    }

    private ModelStore Store => store ?? throw new InvalidOperationException("Plugin is inactive.");
    private ManagedRuntime Runtime => runtime ?? throw new InvalidOperationException("Plugin is inactive.");
    private async Task AcquireAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!await operation.WaitAsync(0, ct)) throw new InvalidOperationException("R2T2 is busy. Finish the current recording or model operation first.");
    }
    private string? ValidateLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language == "auto") return null;
        if (!SupportedLanguages.Contains(language, StringComparer.OrdinalIgnoreCase)) throw new NotSupportedException("Unsupported R2T2 language.");
        return Store.LanguageName(language.ToLowerInvariant());
    }
    private async Task EnsureRuntimeAsync(string id, string? language, CancellationToken ct)
    {
        string path = Store.ModelPath(id);
        if (!Runtime.Matches(path, language))
        {
            await Store.VerifyForLoadAsync(id, ct);
            await Runtime.StartAsync(path, language, ct);
        }
    }

    public bool IsModelDownloaded(string id) => Store.IsDownloaded(id);
    public async Task DownloadModelAsync(string id, IProgress<double>? progress, CancellationToken ct)
    {
        await AcquireAsync(ct);
        try { await Store.DownloadAsync(id, progress, ct); host!.NotifyCapabilitiesChanged(); }
        finally { operation.Release(); }
    }
    public async Task RemoveModelAsync(string id, CancellationToken ct)
    {
        await AcquireAsync(ct);
        try { await Runtime.StopAsync(); await Store.RemoveAsync(id, ct); host!.NotifyCapabilitiesChanged(); }
        finally { operation.Release(); }
    }
    public async Task LoadModelAsync(string id, CancellationToken ct)
    {
        await AcquireAsync(ct);
        try { await EnsureRuntimeAsync(id, null, ct); }
        finally { operation.Release(); }
    }
    public async Task UnloadModelAsync()
    {
        await AcquireAsync(CancellationToken.None);
        try { if (runtime is not null) await runtime.StopAsync(); }
        finally { operation.Release(); }
    }

    public async Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    {
        language = ValidateLanguage(language);
        await AcquireAsync(ct);
        bool handedOff = false;
        try
        {
            await EnsureRuntimeAsync(SelectedModelId!, language, ct);
            // ConnectAsync owns this callback even if its handshake fails.
            handedOff = true;
            return await RealtimeSession.ConnectAsync(Runtime.RealtimeUrl, async success =>
            {
                try { if (!success) await Runtime.StopAsync(); }
                finally { operation.Release(); }
            }, ct);
        }
        catch
        {
            if (!handedOff)
            {
                try { await Runtime.StopAsync(); }
                finally { operation.Release(); }
            }
            throw;
        }
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        if (translate) throw new NotSupportedException("R2T2 does not provide audio translation.");
        if (wavAudio.Length > MaximumAudioUploadBytes) throw new ArgumentException("Recording exceeds the plugin's upload limit.");
        double duration = WaveMetadata.Duration(wavAudio);
        language = ValidateLanguage(language);
        await AcquireAsync(ct);
        try
        {
            await EnsureRuntimeAsync(SelectedModelId!, language, ct);
            using var body = new MultipartFormDataContent();
            var audio = new ByteArrayContent(wavAudio);
            audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            body.Add(audio, "file", "recording.wav");
            body.Add(new StringContent("json"), "response_format");
            if (language is not null) body.Add(new StringContent(language), "language");
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Runtime.HttpUrl, "/v1/audio/transcriptions")) { Content = body };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Runtime.ApiKey);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var response = await local.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var result = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token));
            return new(result.RootElement.GetProperty("text").GetString() ?? "", null, duration, null);
        }
        catch { await Runtime.StopAsync(); throw; }
        finally { operation.Release(); }
    }

    public IReadOnlyList<PluginSettingsAction> SettingsActions => [new("unload", "Unload R2T2", "Stop the local inference process and release model memory.")];
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (id != "unload") throw new ArgumentException("Unknown settings action.");
        await UnloadModelAsync();
        return "R2T2 unloaded. It will start automatically on the next recording.";
    }
    public async Task DeactivateAsync()
    {
        await operation.WaitAsync().ConfigureAwait(false);
        try { if (runtime is not null) await runtime.StopAsync().ConfigureAwait(false); host = null; }
        finally { operation.Release(); }
    }
    public void Dispose()
    {
        if (disposed) return;
        DeactivateAsync().GetAwaiter().GetResult();
        disposed = true;
        downloads.Dispose(); local.Dispose();
    }
}
