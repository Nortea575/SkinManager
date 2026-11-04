using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SkinManager.Updating;

public sealed class GitHubUpdateClient : IUpdateClient
{
    private static readonly Uri LatestReleaseUri = new(
        "https://api.github.com/repos/Nortea575/SkinManager/releases/latest");
    private static readonly Regex StableTagPattern = new(
        @"\Av?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Sha256Pattern = new(
        @"\A[0-9a-fA-F]{64}\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private const string RepositoryPath = "/Nortea575/SkinManager";
    private const string GitHubApiVersion = "2022-11-28";
    private const long MaximumInstallerSize = 1024L * 1024 * 1024;
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);
    private const int BufferSize = 64 * 1024;

    private readonly HttpClient _client;
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _downloadGate = new(1, 1);

    public GitHubUpdateClient(HttpClient client, string cacheDirectory)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);

        _client = client;
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
    }

    public async Task<UpdateCheckResult> CheckAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", GitHubApiVersion);
        request.Headers.UserAgent.ParseAdd($"SkinManager/{FormatVersion(currentVersion)}");

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            timeout.Token).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new UpdateCheckResult(UpdateCheckState.NotPublished);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GitHub update check failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                null,
                response.StatusCode);
        }

        ValidateApiResponseUri(response.RequestMessage?.RequestUri);

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var release = await JsonSerializer.DeserializeAsync<GitHubReleaseDto>(
            stream,
            cancellationToken: timeout.Token).ConfigureAwait(false);

        if (release is null || release.Draft != false || release.Prerelease != false)
        {
            return new UpdateCheckResult(UpdateCheckState.NotPublished);
        }

        var version = ParseStableTag(release.TagName);
        if (version is null)
        {
            return new UpdateCheckResult(UpdateCheckState.NotPublished);
        }

        // Do not require package metadata when this release cannot update the installed version.
        if (version.CompareTo(currentVersion) <= 0)
        {
            return new UpdateCheckResult(UpdateCheckState.UpToDate);
        }

        var expectedAssetName = GetAssetName(version);
        var matchingAssets = release.Assets?
            .Where(asset => string.Equals(asset.Name, expectedAssetName, StringComparison.Ordinal))
            .ToArray();

        if (matchingAssets is not { Length: 1 })
        {
            return new UpdateCheckResult(UpdateCheckState.PackageNotReady);
        }

        var asset = matchingAssets[0];
        if (!string.Equals(asset.State, "uploaded", StringComparison.OrdinalIgnoreCase)
            || asset.Size is null or <= 0 or > MaximumInstallerSize
            || !TryParseDigest(asset.Digest, out var digest))
        {
            return new UpdateCheckResult(UpdateCheckState.PackageNotReady);
        }

        if (!Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var downloadUri)
            || !IsExpectedAssetUri(downloadUri, release.TagName!, expectedAssetName))
        {
            throw new InvalidDataException("The update asset URL is not the expected HTTPS GitHub release asset.");
        }

        var updateRelease = new UpdateRelease(
            version,
            release.TagName!,
            release.Body ?? string.Empty,
            downloadUri,
            asset.Size.Value,
            digest);

        return new UpdateCheckResult(UpdateCheckState.Available, updateRelease);
    }

    public async Task<DownloadedUpdate> DownloadAsync(
        UpdateRelease release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ValidateRelease(release);

        await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (versionDirectory, finalPath, partialPath) = GetCachePaths(release);
            Directory.CreateDirectory(versionDirectory);

            if (File.Exists(finalPath))
            {
                try
                {
                    await VerifyFileAsync(finalPath, release, cancellationToken).ConfigureAwait(false);
                    return new DownloadedUpdate(release, finalPath);
                }
                catch (InvalidDataException)
                {
                    File.Delete(finalPath);
                }
            }

            TryDeleteFile(partialPath);
            try
            {
                await DownloadToPartialFileAsync(release, partialPath, progress, cancellationToken)
                    .ConfigureAwait(false);
                File.Move(partialPath, finalPath, overwrite: true);
                return new DownloadedUpdate(release, finalPath);
            }
            catch
            {
                TryDeleteFile(partialPath);
                throw;
            }
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    public async Task ValidateAsync(DownloadedUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(update.Release);
        ValidateRelease(update.Release);

        var (_, expectedPath, _) = GetCachePaths(update.Release);
        var actualPath = Path.GetFullPath(update.FilePath);
        if (!PathEquals(actualPath, expectedPath) || !IsWithinCache(actualPath))
        {
            throw new InvalidDataException("The downloaded update is outside its versioned cache path.");
        }

        await VerifyFileAsync(actualPath, update.Release, cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadToPartialFileAsync(
        UpdateRelease release,
        string partialPath,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        request.Headers.UserAgent.ParseAdd($"SkinManager/{FormatVersion(release.Version)}");

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        ValidateDownloadResponseUri(response.RequestMessage?.RequestUri);

        if (response.Content.Headers.ContentLength is long contentLength && contentLength != release.Size)
        {
            throw new InvalidDataException("The update response length does not match the published asset size.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            partialPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        var bytesReceived = 0L;
        var lastProgressTimestamp = Stopwatch.GetTimestamp();

        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            bytesReceived += read;
            if (bytesReceived > release.Size)
            {
                throw new InvalidDataException("The downloaded update exceeds the published asset size.");
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

            if (bytesReceived < release.Size
                && Stopwatch.GetElapsedTime(lastProgressTimestamp) >= ProgressInterval)
            {
                progress?.Report(new UpdateDownloadProgress(bytesReceived, release.Size));
                lastProgressTimestamp = Stopwatch.GetTimestamp();
            }
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);

        if (bytesReceived != release.Size)
        {
            throw new InvalidDataException("The downloaded update length does not match the published asset size.");
        }

        var actualDigest = hash.GetHashAndReset();
        var expectedDigest = Convert.FromHexString(release.Sha256);
        if (!CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
        {
            throw new InvalidDataException("The downloaded update SHA-256 does not match the published digest.");
        }

        progress?.Report(new UpdateDownloadProgress(bytesReceived, release.Size));
    }

    private static async Task VerifyFileAsync(
        string filePath,
        UpdateRelease release,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("The downloaded update file is missing.", filePath);
        }

        if (fileInfo.Length != release.Size)
        {
            throw new InvalidDataException("The cached update length does not match the published asset size.");
        }

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
        }

        var actualDigest = hash.GetHashAndReset();
        var expectedDigest = Convert.FromHexString(release.Sha256);
        if (!CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
        {
            throw new InvalidDataException("The cached update SHA-256 does not match the published digest.");
        }
    }

    private (string VersionDirectory, string FinalPath, string PartialPath) GetCachePaths(UpdateRelease release)
    {
        var versionDirectory = Path.GetFullPath(Path.Combine(_cacheDirectory, FormatVersion(release.Version)));
        var finalPath = Path.GetFullPath(Path.Combine(versionDirectory, GetAssetName(release.Version)));
        if (!IsWithinCache(finalPath))
        {
            throw new InvalidDataException("The update cache path escaped the configured cache directory.");
        }

        return (versionDirectory, finalPath, finalPath + ".partial");
    }

    private bool IsWithinCache(string fullPath)
    {
        var root = _cacheDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, PathComparison);
    }

    private static void ValidateRelease(UpdateRelease release)
    {
        ArgumentNullException.ThrowIfNull(release.Version);
        ArgumentNullException.ThrowIfNull(release.Tag);
        ArgumentNullException.ThrowIfNull(release.DownloadUri);
        ArgumentNullException.ThrowIfNull(release.Sha256);

        var parsedVersion = ParseStableTag(release.Tag);
        if (parsedVersion is null || parsedVersion != release.Version)
        {
            throw new InvalidDataException("The release must use a stable three-part version tag matching its version.");
        }

        if (release.Size <= 0 || release.Size > MaximumInstallerSize)
        {
            throw new InvalidDataException("The published update asset size is outside the supported range.");
        }

        if (!Sha256Pattern.IsMatch(release.Sha256))
        {
            throw new InvalidDataException("The published update asset does not have a complete SHA-256 digest.");
        }

        if (!IsExpectedAssetUri(release.DownloadUri, release.Tag, GetAssetName(release.Version)))
        {
            throw new InvalidDataException("The update asset URL is not the expected HTTPS GitHub release asset.");
        }
    }

    private static Version? ParseStableTag(string? tag)
    {
        if (tag is null)
        {
            return null;
        }

        var match = StableTagPattern.Match(tag);
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return null;
        }

        return new Version(major, minor, patch);
    }

    private static bool TryParseDigest(string? digest, out string normalizedDigest)
    {
        normalizedDigest = string.Empty;
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = digest["sha256:".Length..];
        if (!Sha256Pattern.IsMatch(value))
        {
            return false;
        }

        normalizedDigest = value.ToLowerInvariant();
        return true;
    }

    private static bool IsExpectedAssetUri(Uri uri, string tag, string assetName)
    {
        if (!uri.IsAbsoluteUri
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Port != 443
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var expectedPath = $"{RepositoryPath}/releases/download/{tag}/{assetName}";
        return string.Equals(Uri.UnescapeDataString(uri.AbsolutePath), expectedPath, StringComparison.Ordinal);
    }

    private static void ValidateApiResponseUri(Uri? uri)
    {
        if (uri is null)
        {
            return;
        }

        if (!uri.IsAbsoluteUri
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Port != 443
            || !string.Equals(uri.AbsolutePath, "/repos/Nortea575/SkinManager/releases/latest", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The update metadata response came from an unexpected URL.");
        }
    }

    private static void ValidateDownloadResponseUri(Uri? uri)
    {
        if (uri is null)
        {
            return;
        }

        var allowedHost = string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        if (!uri.IsAbsoluteUri
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || uri.Port != 443
            || !allowedHost
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("The update download redirected outside GitHub's HTTPS release hosts.");
        }
    }

    private static string GetAssetName(Version version) => $"SkinManager-Setup-{FormatVersion(version)}.exe";

    private static string FormatVersion(Version version) =>
        string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{Math.Max(version.Minor, 0)}.{Math.Max(version.Build, 0)}");

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("draft")]
        public bool? Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool? Prerelease { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("assets")]
        public GitHubAssetDto[]? Assets { get; init; }
    }

    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("state")]
        public string? State { get; init; }

        [JsonPropertyName("size")]
        public long? Size { get; init; }

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }
    }
}
