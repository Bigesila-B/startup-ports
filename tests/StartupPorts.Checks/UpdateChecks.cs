using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using StartupPorts;

internal static class UpdateChecks
{
    private const string Api = "https://api.github.com/repos/Bigesila-B/startup-ports/releases/latest";
    private const string LatestPage = "https://github.com/Bigesila-B/startup-ports/releases/latest";
    private const string ReleasePage = "https://github.com/Bigesila-B/startup-ports/releases/tag/v0.2.8";
    private const string Checksum = "https://github.com/Bigesila-B/startup-ports/releases/download/v0.2.8/SHA256SUMS.txt";
    private const string ReleaseAsset = "https://github.com/Bigesila-B/startup-ports/releases/download/v0.2.8/StartupPorts-0.2.8-win-x64-setup.exe";
    private const string Cdn = "https://release-assets.githubusercontent.com/fixture";
    private static readonly byte[] Payload = "Test installer bytes"u8.ToArray();
    private static readonly string Digest = Convert.ToHexString(SHA256.HashData(Payload));
    private static readonly Version OldVersion = new(0, 2, 6);

    public static async Task RunAsync()
    {
        var temp = Directory.CreateTempSubdirectory("StartupPorts updater ");
        try
        {
            using var http = new HttpClient(new Responses(request => request.RequestUri!.AbsoluteUri switch
            {
                Api => ReleaseResponse(),
                ReleaseAsset => Redirect(Cdn),
                Cdn => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) },
                _ => throw new InvalidOperationException("The updater contacted an unexpected URL."),
            }));
            var updater = new GitHubUpdates(http, temp.FullName);
            var release = await updater.FindLatestAsync(OldVersion, CancellationToken.None);
            Require(release?.Version == new Version(0, 2, 8) && release.Package?.Digest == Digest,
                "A newer public release was not detected.");
            Require(await updater.FindLatestAsync(new Version(0, 2, 8), CancellationToken.None) == null &&
                await updater.FindLatestAsync(new Version(0, 2, 9), CancellationToken.None) == null,
                "The updater offered a reinstall or downgrade.");

            await CheckReleaseAsync(temp.FullName, ReleaseResponse(prerelease: true), expectedVersion: false);
            await CheckReleaseAsync(temp.FullName, ReleaseResponse(draft: true), expectedVersion: false);
            await CheckReleaseAsync(temp.FullName, ReleaseResponse(url: "https://example.org/setup.exe"), expectedVersion: true);
            await CheckReleaseAsync(temp.FullName, ReleaseResponse(digest: ""), expectedVersion: true);

            using var limitedHttp = new HttpClient(new Responses(request => request.RequestUri!.AbsoluteUri switch
            {
                Api => new HttpResponseMessage(HttpStatusCode.Forbidden) { Headers = { { "X-RateLimit-Remaining", "0" } } },
                LatestPage => Redirect(ReleasePage),
                Checksum => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{Digest}  StartupPorts-0.2.8-win-x64-setup.exe\n"),
                },
                _ => throw new InvalidOperationException("The fallback contacted an unexpected URL."),
            }));
            var fallback = await new GitHubUpdates(limitedHttp, temp.FullName).FindLatestAsync(OldVersion, CancellationToken.None);
            Require(fallback?.Version == new Version(0, 2, 8) && fallback.Package?.Digest == Digest &&
                fallback.Package.Size == null, "GitHub rate limiting prevented the public release fallback.");

            var path = await updater.DownloadAsync(release!.Package!, null, CancellationToken.None);
            Require(File.ReadAllBytes(path).SequenceEqual(Payload), "The verified installer was not saved intact.");
            Require(File.ReadAllText(path + ":Zone.Identifier").Contains("ZoneId=3"),
                "The installer is missing its Windows download security zone.");
            await using (var verified = await GitHubUpdates.VerifyInstallerAsync(path, release.Package!, CancellationToken.None))
                Require(verified.Length == Payload.Length, "The saved installer could not be verified before opening.");

            var corrupt = Payload.ToArray();
            corrupt[0] ^= 1;
            File.WriteAllBytes(path, corrupt);
            await RejectAsync(() => GitHubUpdates.VerifyInstallerAsync(path, release.Package!, CancellationToken.None));
            File.Delete(path);

            using var corruptHttp = new HttpClient(new Responses(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(corrupt),
            }));
            await RejectAsync(() => new GitHubUpdates(corruptHttp, temp.FullName)
                .DownloadAsync(release.Package!, null, CancellationToken.None));

            using var partialHttp = new HttpClient(new Responses(_ => new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(Payload),
            }));
            await RejectAsync(() => new GitHubUpdates(partialHttp, temp.FullName)
                .DownloadAsync(release.Package!, null, CancellationToken.None));

            using var unsafeHttp = new HttpClient(new Responses(request => request.RequestUri!.AbsoluteUri == ReleaseAsset
                ? Redirect("https://example.org/setup.exe")
                : throw new InvalidOperationException("The updater followed an external redirect.")));
            await RejectAsync(() => new GitHubUpdates(unsafeHttp, temp.FullName)
                .DownloadAsync(release.Package!, null, CancellationToken.None));
            Require(!Directory.EnumerateFileSystemEntries(temp.FullName).Any(), "A failed download left executable files behind.");
            Console.WriteLine("Verified update discovery, download, digest, security marking, and unsafe asset rejection.");
        }
        finally { temp.Delete(recursive: true); }
    }

    public static async Task ProbeGitHubAsync()
    {
        var temp = Directory.CreateTempSubdirectory("StartupPorts GitHub probe ");
        try
        {
            var updater = new GitHubUpdates(downloadDirectory: temp.FullName);
            var release = await updater.FindLatestAsync(new Version(0, 0, 0), CancellationToken.None);
            Require(release?.Package != null, "GitHub did not return a verifiable release installer.");
            Console.WriteLine($"Read the official v{release!.Version} release and expected SHA-256.");
            var package = release!.Package!;
            var path = await updater.DownloadAsync(package, progress: null, CancellationToken.None);
            await using var verified = await GitHubUpdates.VerifyInstallerAsync(path, package, CancellationToken.None);
            Console.WriteLine($"Downloaded and verified the official v{release.Version} package ({verified.Length} bytes).");
        }
        finally { temp.Delete(recursive: true); }
    }

    private static async Task CheckReleaseAsync(string folder, HttpResponseMessage response, bool expectedVersion)
    {
        using var http = new HttpClient(new Responses(_ => response));
        var release = await new GitHubUpdates(http, folder).FindLatestAsync(OldVersion, CancellationToken.None);
        Require((release != null) == expectedVersion && release?.Package == null,
            "A draft, prerelease, or unverifiable installer was offered automatically.");
    }

    private static HttpResponseMessage ReleaseResponse(bool prerelease = false, bool draft = false,
        string url = ReleaseAsset, string? digest = null) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            tag_name = "v0.2.8",
            draft,
            prerelease,
            assets = new[] { new
            {
                name = "StartupPorts-0.2.8-win-x64-setup.exe",
                state = "uploaded",
                browser_download_url = url,
                digest = "sha256:" + (digest ?? Digest),
                size = Payload.Length,
            } },
        })),
    };

    private static HttpResponseMessage Redirect(string url) => new(HttpStatusCode.Redirect)
    {
        Headers = { Location = new Uri(url) },
    };

    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("The updater accepted an invalid installer.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class Responses(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response(request));
        }
    }
}
