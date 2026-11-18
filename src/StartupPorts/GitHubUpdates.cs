using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using static StartupPorts.UiText;

namespace StartupPorts;

internal sealed record UpdateRelease(Version Version, Uri Page, UpdatePackage? Package);
internal sealed record UpdatePackage(Uri Url, string Digest, long? Size);

internal sealed class GitHubUpdates
{
    private const string Repository = "Bigesila-B/startup-ports";
    private const long MaxInstallerSize = 256L * 1024 * 1024;
    private const string InstallerFileName = "StartupPorts-Update.exe";
    public static Version CurrentVersion { get; } = new(typeof(GitHubUpdates).Assembly.GetName().Version!.ToString(3));

    private static readonly HttpClient DefaultClient = CreateClient();
    private static readonly Uri ReleaseEndpoint = new($"https://api.github.com/repos/{Repository}/releases/latest");
    private static readonly Uri LatestPage = new($"https://github.com/{Repository}/releases/latest");
    private readonly HttpClient _http;
    private readonly string _downloadDirectory;

    public GitHubUpdates(HttpClient? http = null, string? downloadDirectory = null)
    {
        _http = http ?? DefaultClient;
        _downloadDirectory = downloadDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StartupPorts", "Updates");
    }

    public async Task<UpdateRelease?> FindLatestAsync(Version current, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await SendGetAsync(ReleaseEndpoint, timeout.Token, json: true).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests ||
            (response.StatusCode == HttpStatusCode.Forbidden &&
             response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0")))
            return await FindLatestFromPageAsync(current, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(content, cancellationToken: timeout.Token).ConfigureAwait(false);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || GetString(root, "tag_name") is not { } tag)
            throw new InvalidDataException(L("InvalidUpdateInfo"));
        if (IsTrue(root, "draft") || IsTrue(root, "prerelease") || ReadNewVersion(tag, current) is not { } version)
            return null;

        var page = ReleasePage(tag);
        var assetName = ReleaseAssetName(version);
        var assetUrl = ReleaseAsset(tag, assetName);
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object || GetString(asset, "name") != assetName ||
                    GetString(asset, "state") != "uploaded" || GetString(asset, "browser_download_url") != assetUrl.AbsoluteUri ||
                    !asset.TryGetProperty("size", out var length) || length.ValueKind != JsonValueKind.Number ||
                    !length.TryGetInt64(out var size) || size <= 0 || size > MaxInstallerSize)
                    continue;

                var digest = GetString(asset, "digest");
                if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
                    IsSha256(digest[7..]))
                    return new(version, page, new(assetUrl, digest[7..], size));
            }
        }
        return new(version, page, null);
    }

    private async Task<UpdateRelease?> FindLatestFromPageAsync(Version current, CancellationToken token)
    {
        using var response = await SendGetAsync(LatestPage, token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Redirect || response.Headers.Location is not { } location)
            throw new InvalidDataException(L("InvalidUpdateInfo"));
        var target = new Uri(LatestPage, location);
        var releasePrefix = $"/{Repository}/releases/tag/";
        if (target.Scheme != Uri.UriSchemeHttps || target.IdnHost != "github.com" || !target.IsDefaultPort ||
            target.UserInfo.Length > 0 || !target.AbsolutePath.StartsWith(releasePrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L("InvalidUpdateInfo"));
        var tag = target.AbsolutePath[releasePrefix.Length..];
        if (ReadNewVersion(tag, current) is not { } version)
            return null;

        var name = ReleaseAssetName(version);
        try
        {
            using var manifest = await OpenDownloadAsync(ReleaseAsset(tag, "SHA256SUMS.txt"), token).ConfigureAwait(false);
            await using var content = await manifest.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var buffer = new byte[8193];
            var length = await content.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken: token)
                .ConfigureAwait(false);
            if (length < buffer.Length)
            {
                foreach (var line in Encoding.UTF8.GetString(buffer, 0, length).Split('\n'))
                {
                    var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length == 2 && (fields[1] == name || fields[1] == "*" + name) && IsSha256(fields[0]))
                        return new(version, ReleasePage(tag), new(ReleaseAsset(tag, name), fields[0], null));
                }
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
        return new(version, ReleasePage(tag), null);
    }

    public async Task<string> DownloadAsync(UpdatePackage package, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        Directory.CreateDirectory(_downloadDirectory);
        var temporary = Path.Combine(_downloadDirectory, $"{Guid.NewGuid():N}.part");
        var installer = Path.Combine(_downloadDirectory, InstallerFileName);
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await TransferInstallerAsync(package, output, progress, token).ConfigureAwait(false);

            // Preserve the downloaded-file security zone before handing a release installer to Windows.
            await File.WriteAllTextAsync(temporary + ":Zone.Identifier",
                $"[ZoneTransfer]\r\nZoneId=3\r\nHostUrl={package.Url.AbsoluteUri}\r\n",
                Encoding.ASCII, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, installer, overwrite: true);
            return installer;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task TransferInstallerAsync(UpdatePackage package, Stream output, IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        using var stalled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        var token = stalled.Token;
        stalled.CancelAfter(TimeSpan.FromSeconds(35));
        using var response = await OpenDownloadAsync(package.Url, token).ConfigureAwait(false);
        var contentLength = response.Content.Headers.ContentLength;
        var total = package.Size ?? contentLength;
        if (response.StatusCode != HttpStatusCode.OK || total is <= 0 or > MaxInstallerSize ||
            (package.Size.HasValue && contentLength.HasValue && package.Size != contentLength))
            throw new InvalidDataException(L("UpdateVerificationFailed"));

        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        long received = 0;
        var lastProgress = -1;
        while (true)
        {
            stalled.CancelAfter(TimeSpan.FromSeconds(35));
            var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0)
                break;
            received += count;
            if (received > MaxInstallerSize || (total.HasValue && received > total.Value))
                throw new InvalidDataException(L("UpdateVerificationFailed"));
            digest.AppendData(buffer.AsSpan(0, count));
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            if (total is not { } length)
                continue;
            var percent = (int)(received * 100 / length);
            if (percent != lastProgress)
            {
                progress?.Report(percent);
                lastProgress = percent;
            }
        }
        if (received == 0 || (total.HasValue && received != total.Value) || !MatchesDigest(package, digest.GetHashAndReset()))
            throw new InvalidDataException(L("UpdateVerificationFailed"));
    }

    internal static async Task<FileStream> VerifyInstallerAsync(string path, UpdatePackage package, CancellationToken token)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (file.Length is <= 0 or > MaxInstallerSize ||
                (package.Size.HasValue && file.Length != package.Size.Value) ||
                !MatchesDigest(package, await SHA256.HashDataAsync(file, token).ConfigureAwait(false)))
                throw new InvalidDataException(L("UpdateVerificationFailed"));
            return file;
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<HttpResponseMessage> OpenDownloadAsync(Uri url, CancellationToken token)
    {
        for (var redirects = 0; redirects < 5; redirects++)
        {
            if (url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort || url.UserInfo.Length != 0 ||
                url.IdnHost is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException(L("InvalidUpdateInfo"));

            var response = await SendGetAsync(url, token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return response;
            using (response)
            {
                if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
                    response.EnsureSuccessStatusCode();
                if (response.Headers.Location is not { } location)
                    throw new InvalidDataException(L("InvalidUpdateInfo"));
                url = new Uri(url, location);
            }
        }
        throw new InvalidDataException(L("InvalidUpdateInfo"));
    }

    private async Task<HttpResponseMessage> SendGetAsync(Uri url, CancellationToken token, bool json = false)
    {
        var completion = json ? HttpCompletionOption.ResponseContentRead : HttpCompletionOption.ResponseHeadersRead;
        for (var retry = 0; ; retry++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (json)
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
            try { return await _http.SendAsync(request, completion, token).ConfigureAwait(false); }
            catch (HttpRequestException ex) when (retry < 2 && ex.StatusCode == null && ex.InnerException is IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300 * (retry + 1)), token).ConfigureAwait(false);
            }
        }
    }

    private static bool MatchesDigest(UpdatePackage package, byte[] digest) =>
        Convert.ToHexString(digest).Equals(package.Digest, StringComparison.OrdinalIgnoreCase);

    private static Version? ReadNewVersion(string tag, Version current) =>
        tag.StartsWith('v') && Version.TryParse(tag[1..], out var version) && version.Build >= 0 &&
        version.Revision < 0 && tag == $"v{version.ToString(3)}" && version > current ? version : null;

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static Uri ReleasePage(string tag) => new($"https://github.com/{Repository}/releases/tag/{tag}");
    private static Uri ReleaseAsset(string tag, string name) => new($"https://github.com/{Repository}/releases/download/{tag}/{name}");
    private static string ReleaseAssetName(Version version) => $"StartupPorts-{version.ToString(3)}-win-x64-setup.exe";

    private static string? GetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsTrue(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = 1024 * 1024
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"StartupPorts/{CurrentVersion.ToString(3)}");
        return client;
    }
}
