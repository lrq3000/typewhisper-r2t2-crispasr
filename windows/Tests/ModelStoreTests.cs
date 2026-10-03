using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace R2T2CrispASR.Tests;

public sealed class ModelStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "r2t2-test-" + Guid.NewGuid());
    private readonly byte[] valid = Encoding.UTF8.GetBytes("test model contents");

    public ModelStoreTests() => Directory.CreateDirectory(root);

    private ModelStore Store(byte[] response)
    {
        string catalog = Path.Combine(root, "catalog.json");
        File.WriteAllText(catalog, JsonSerializer.Serialize(new { repository = "cstr/test", revision = "pinned", licenseFile = "MODEL_LICENSE", languages = new[] { "en" },
            models = new[] { new { id = "test", name = "Test", file = "test.gguf", size = valid.Length, sha256 = Convert.ToHexString(SHA256.HashData(valid)) } } }));
        return new ModelStore(root, catalog, new HttpClient(new DownloadHandler(response)));
    }

    [Fact]
    public async Task DownloadPublishesOnlyVerifiedWeightsAndLicense()
    {
        var store = Store(valid);
        await store.DownloadAsync("test", null, CancellationToken.None);
        Assert.True(store.IsDownloaded("test"));
        Assert.Equal(valid, await File.ReadAllBytesAsync(store.ModelPath("test")));
        Assert.True(File.Exists(Path.Combine(root, "test", "MODEL_LICENSE")));
        Assert.Empty(Directory.GetFiles(root, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CorruptDownloadDoesNotReplaceUsableModel()
    {
        var store = Store(valid);
        await store.DownloadAsync("test", null, CancellationToken.None);
        var corrupt = Store(Enumerable.Repeat((byte)0, valid.Length).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => corrupt.DownloadAsync("test", null, CancellationToken.None));
        Assert.True(store.IsDownloaded("test"));
        Assert.Equal(valid, await File.ReadAllBytesAsync(store.ModelPath("test")));
    }

    [Fact]
    public async Task CancellationLeavesNoPartialsOrDownloadedMarker()
    {
        var store = Store(valid);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DownloadAsync("test", null, new CancellationToken(true)));
        Assert.False(store.IsDownloaded("test"));
        Assert.Empty(Directory.GetFiles(root, "*.part", SearchOption.AllDirectories));
    }

    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData("yue", "Cantonese")]
    [InlineData("fil", "Filipino")]
    [InlineData("fi", "Finnish")]
    public void LanguageHintsUseCanonicalModelNames(string code, string expected)
    {
        var store = new ModelStore(root, Path.Combine(Path.GetDirectoryName(typeof(ModelStore).Assembly.Location)!, "models.json"), new HttpClient());
        Assert.Equal(expected, store.LanguageName(code));
    }

    private sealed class DownloadHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var content = request.RequestUri!.AbsolutePath.EndsWith("MODEL_LICENSE", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes("Model license") : bytes;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }
}
