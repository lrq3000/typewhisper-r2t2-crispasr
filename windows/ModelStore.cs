using System.Security.Cryptography;
using System.Text.Json;

namespace R2T2CrispASR;

public sealed record CatalogModel(string Id, string Name, string File, long Size, string Sha256);
public sealed record ModelCatalog(string Repository, string Revision, string LicenseFile, string[] Languages, CatalogModel[] Models, Dictionary<string, string>? LanguageNames = null);

public sealed class ModelStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly string root;
    private readonly HttpClient http;
    private readonly Dictionary<string, CatalogModel> models;
    private readonly SemaphoreSlim changes = new(1);
    public ModelCatalog Catalog { get; }

    public ModelStore(string root, string catalogPath, HttpClient http)
    {
        this.root = root;
        this.http = http;
        Catalog = JsonSerializer.Deserialize<ModelCatalog>(System.IO.File.ReadAllText(catalogPath), Json)
            ?? throw new InvalidDataException("Missing model catalogue.");
        models = Catalog.Models.ToDictionary(model => model.Id, StringComparer.Ordinal);
        foreach (var model in models.Values)
            if (Path.GetFileName(model.File) != model.File || model.Id.IndexOfAny(['/', '\\']) >= 0 || model.Id is "." or "..")
                throw new InvalidDataException("Invalid catalogue path.");
    }

    public CatalogModel Model(string id) => models.TryGetValue(id, out var model) ? model : throw new ArgumentException("Unknown model.", nameof(id));
    // v0.8.41's native ISO map covers only a subset of Qwen3's languages.
    // Full names pass through unchanged and preserve every advertised hint.
    public string LanguageName(string code) => Catalog.LanguageNames?.TryGetValue(code, out var name) == true ? name : code;
    public string ModelPath(string id) => Path.Combine(root, Model(id).Id, Model(id).File);
    private string LicensePath(string id) => Path.Combine(root, Model(id).Id, "MODEL_LICENSE");
    private string StampPath(string id) => ModelPath(id) + ".verified.json";
    private sealed record Verification(long Size, long LastWriteTicks, string Sha256);

    public bool IsDownloaded(string id)
    {
        try
        {
            var model = Model(id);
            var info = new FileInfo(ModelPath(id));
            if (!info.Exists || !System.IO.File.Exists(LicensePath(id)) || !System.IO.File.Exists(StampPath(id))) return false;
            var stamp = JsonSerializer.Deserialize<Verification>(System.IO.File.ReadAllText(StampPath(id)));
            return info.Length == model.Size && stamp?.Size == model.Size
                && stamp.LastWriteTicks == info.LastWriteTimeUtc.Ticks
                && stamp.Sha256.Equals(model.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    public async Task VerifyForLoadAsync(string id, CancellationToken ct)
    {
        if (!IsDownloaded(id)) throw new InvalidOperationException("Download the selected model first.");
        await using var file = System.IO.File.OpenRead(ModelPath(id));
        var hash = await SHA256.HashDataAsync(file, ct);
        if (!Convert.ToHexString(hash).Equals(Model(id).Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Model checksum changed. Download the model again.");
    }

    public async Task DownloadAsync(string id, IProgress<double>? progress, CancellationToken ct)
    {
        await changes.WaitAsync(ct);
        string? weightsPart = null, licensePart = null;
        try
        {
            var model = Model(id);
            string directory = Path.Combine(root, model.Id);
            Directory.CreateDirectory(directory);
            if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace < model.Size + 64 * 1024 * 1024)
                throw new IOException("Insufficient free disk space for this model download.");
            weightsPart = ModelPath(id) + "." + Guid.NewGuid() + ".part";
            licensePart = LicensePath(id) + "." + Guid.NewGuid() + ".part";
            string baseUrl = $"https://huggingface.co/{Catalog.Repository}/resolve/{Catalog.Revision}/";
            await DownloadFileAsync(new Uri(baseUrl + Catalog.LicenseFile), licensePart, null, 65536, null, ct);
            if (new FileInfo(licensePart).Length == 0) throw new InvalidDataException("Empty model license.");
            await DownloadFileAsync(new Uri(baseUrl + model.File), weightsPart, model.Size, model.Size, progress, ct);
            await using (var file = System.IO.File.OpenRead(weightsPart))
            {
                var hash = await SHA256.HashDataAsync(file, ct);
                if (!Convert.ToHexString(hash).Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Model SHA-256 verification failed.");
            }
            ct.ThrowIfCancellationRequested();
            // All validation precedes promotion; cancellation cannot destroy existing weights.
            System.IO.File.Move(licensePart, LicensePath(id), true);
            System.IO.File.Move(weightsPart, ModelPath(id), true);
            var info = new FileInfo(ModelPath(id));
            string stampPart = StampPath(id) + ".part";
            System.IO.File.WriteAllText(stampPart, JsonSerializer.Serialize(new Verification(info.Length, info.LastWriteTimeUtc.Ticks, model.Sha256)));
            System.IO.File.Move(stampPart, StampPath(id), true);
            progress?.Report(1);
        }
        finally
        {
            if (weightsPart is not null) System.IO.File.Delete(weightsPart);
            if (licensePart is not null) System.IO.File.Delete(licensePart);
            changes.Release();
        }
    }

    private async Task DownloadFileAsync(Uri url, string destination, long? expected, long maximum, IProgress<double>? progress, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromHours(2));
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme is string scheme && scheme != "https")
            throw new InvalidDataException("Download redirected to an insecure URL.");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
        byte[] buffer = new byte[131072];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
        {
            total += count;
            if (total > maximum) throw new InvalidDataException("Download exceeds expected size.");
            await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
            if (expected is > 0) progress?.Report(Math.Min(0.99, (double)total / expected.Value));
        }
        if (expected is long size && total != size) throw new InvalidDataException("Incomplete model download.");
    }

    public async Task RemoveAsync(string id, CancellationToken ct)
    {
        await changes.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            string directory = Path.Combine(root, Model(id).Id);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        finally { changes.Release(); }
    }
}
