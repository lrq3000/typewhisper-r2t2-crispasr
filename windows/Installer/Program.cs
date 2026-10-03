using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using TypeWhisper.PluginHost;

bool verification = args.Length == 2 && args[0] == "--verify-package";
if (args.Length is < 2 or > 3)
{
    Console.WriteLine("Usage: R2T2Installer <plugin.zip> <TypeWhisper-profile-directory> [host-version=1.1.0]");
    Console.WriteLine("Close TypeWhisper first. The installer uses the upstream verified package store; no network is used.");
    return 1;
}
if (!verification && Process.GetProcessesByName("TypeWhisper").Length > 0)
    throw new InvalidOperationException("Close TypeWhisper before updating its plugin package index.");
string archive = Path.GetFullPath(verification ? args[1] : args[0]);
string profile = verification
    ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(archive)!, "..", ".build", "installer-check-" + Guid.NewGuid().ToString("N")))
    : Path.GetFullPath(args[1]);
// Requiring an existing profile avoids accidentally initializing a second,
// unused profile after a typo. Existing plugins are preserved by the host store.
if (verification) Directory.CreateDirectory(profile);
else if (!Directory.Exists(profile)) throw new DirectoryNotFoundException(profile);
var hostVersion = Version.Parse(args.Length == 3 ? args[2] : "1.1.0");
string settings = Path.Combine(profile, "settings.json");
if (verification) File.WriteAllText(settings, "existing preference");
await using var input = File.OpenRead(archive);
string hash = Convert.ToHexString(await SHA256.HashDataAsync(input));
using var client = new HttpClient(new LocalArchive(archive));
var store = new PortablePluginStore(Path.Combine(profile, "PluginPackages"), hostVersion, client);
await store.InitializeAsync();
await store.InstallAsync(new PortableCatalogEntry
{
    Id = "com.typewhisper.r2t2-crispasr", Name = "R2T2 (CrispASR)", Version = "0.1.0",
    MinHostVersion = "1.1.0", DownloadUrl = "https://local-package.invalid/r2t2.zip",
    Sha256 = hash, Size = new FileInfo(archive).Length, Categories = ["transcription"], SupportedArchitectures = ["x64"]
});
if (verification)
{
    if (!store.IsInstalled("com.typewhisper.r2t2-crispasr") || File.ReadAllText(settings) != "existing preference")
        throw new InvalidDataException("Package verification failed.");
    Directory.Delete(profile, true);
    Console.WriteLine("Package-store installation and preference preservation PASS (isolated verification profile)");
}
else Console.WriteLine("Installed R2T2 (CrispASR). Start TypeWhisper, enable it in Integrations, download a model, and choose Use model.");
return 0;

sealed class LocalArchive(string path) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // The store performs its normal length/hash, archive path, assembly,
        // architecture, manifest and version validation against these local bytes.
        if (request.RequestUri?.Host != "local-package.invalid") throw new InvalidOperationException("Unexpected network request.");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { RequestMessage = request, Content = new StreamContent(File.OpenRead(path)) });
    }
}
