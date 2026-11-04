using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkinManager.Updating;

internal static class Program
{
    private static int _assertionCount;
    private static int _testCount;

    private static async Task<int> Main()
    {
        await RunAsync("stable version ordering and headers", CheckAvailableAndHeadersAsync);
        await RunAsync("up-to-date skips unavailable package metadata", UpToDateSkipsAssetAsync);
        await RunAsync("drafts and prereleases are skipped", DraftsAndPrereleasesAreSkippedAsync);
        await RunAsync("older published version is not a rollback", OlderReleaseIsIgnoredAsync);
        await RunAsync("404 means not published", NotFoundIsNotPublishedAsync);
        await RunAsync("rate limit and offline errors surface", NetworkFailuresSurfaceAsync);
        await RunAsync("missing package and digest are not ready", MissingPackageMetadataAsync);
        await RunAsync("non-GitHub asset URL is rejected", InvalidAssetUrlIsRejectedAsync);
        await RunAsync("streamed download verifies digest and reports progress", StreamedDownloadVerifiesAsync);
        await RunAsync("GitHub CDN redirect is accepted and foreign redirect is rejected", DownloadRedirectHostIsValidatedAsync);
        await RunAsync("download length and digest mismatches remove partials", InvalidDownloadsAreRemovedAsync);
        await RunAsync("cancelled and failed streams remove partials", InterruptedDownloadsAreRemovedAsync);
        await RunAsync("valid cache is reused without another request", ValidCacheIsReusedAsync);
        await RunAsync("install-time validation catches tampering and paths", ValidateRejectsTamperingAsync);

        Console.WriteLine($"PASS: {_testCount} offline tests, {_assertionCount} assertions.");
        return 0;
    }

    private static async Task CheckAvailableAndHeadersAsync()
    {
        using var temp = new TempDirectory();
        var payload = Encoding.UTF8.GetBytes("simulated installer bytes");
        HttpRequestMessage? seenRequest = null;
        var handler = new FakeHandler((request, _) =>
        {
            seenRequest = request;
            return Task.FromResult(JsonResponse(ReleaseJson("v2.3.0", [Asset("v2.3.0", payload)])));
        });
        using var http = new HttpClient(handler);
        var client = new GitHubUpdateClient(http, temp.Path);

        var result = await client.CheckAsync(new Version(2, 2, 0));

        Equal(UpdateCheckState.Available, result.State);
        Equal(new Version(2, 3, 0), result.Release!.Version);
        Equal("v2.3.0", result.Release.Tag);
        Equal("sha256:" + Digest(payload), "sha256:" + result.Release.Sha256);
        Equal(payload.LongLength, result.Release.Size);
        Equal("https://api.github.com/repos/Nortea575/SkinManager/releases/latest", seenRequest!.RequestUri!.ToString());
        True(seenRequest.Headers.UserAgent.ToString().Contains("SkinManager/2.2.0", StringComparison.Ordinal));
        True(seenRequest.Headers.TryGetValues("X-GitHub-Api-Version", out var apiVersions));
        Equal("2022-11-28", apiVersions!.Single());
        True(seenRequest.Headers.Accept.Any(header => header.MediaType == "application/vnd.github+json"));
    }

    private static async Task UpToDateSkipsAssetAsync()
    {
        using var temp = new TempDirectory();
        var handler = JsonHandler(ReleaseJson("2.2.0", []));
        using var http = new HttpClient(handler);
        var result = await new GitHubUpdateClient(http, temp.Path).CheckAsync(new Version(2, 2, 0));
        Equal(UpdateCheckState.UpToDate, result.State);
        True(result.Release is null);
    }

    private static async Task DraftsAndPrereleasesAreSkippedAsync()
    {
        using var temp = new TempDirectory();
        foreach (var json in new[]
        {
            ReleaseJson("v3.0.0", [], draft: true),
            ReleaseJson("v3.0.0", [], prerelease: true),
            ReleaseJson("v3.0.0-rc.1", []),
        })
        {
            using var http = new HttpClient(JsonHandler(json));
            var result = await new GitHubUpdateClient(http, temp.Path).CheckAsync(new Version(2, 2, 0));
            Equal(UpdateCheckState.NotPublished, result.State);
            True(result.Release is null);
        }
    }

    private static async Task OlderReleaseIsIgnoredAsync()
    {
        using var temp = new TempDirectory();
        using var http = new HttpClient(JsonHandler(ReleaseJson("2.9.9", [])));
        var result = await new GitHubUpdateClient(http, temp.Path).CheckAsync(new Version(3, 0, 0));
        Equal(UpdateCheckState.UpToDate, result.State);
    }

    private static async Task NotFoundIsNotPublishedAsync()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            RequestMessage = request
        }));
        using var http = new HttpClient(handler);
        var result = await new GitHubUpdateClient(http, temp.Path).CheckAsync(new Version(2, 2, 0));
        Equal(UpdateCheckState.NotPublished, result.State);
    }

    private static async Task NetworkFailuresSurfaceAsync()
    {
        using var temp = new TempDirectory();
        var limited = new FakeHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            RequestMessage = request
        }));
        using (var http = new HttpClient(limited))
        {
            var error = await ThrowsAsync<HttpRequestException>(
                () => new GitHubUpdateClient(http, temp.Path).CheckAsync(new Version(2, 2, 0)));
            Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        }

        var offline = new FakeHandler((_, _) => throw new HttpRequestException("offline"));
        using (var http = new HttpClient(offline))
        {
            await ThrowsAsync<HttpRequestException>(
                () => new GitHubUpdateClient(http, temp.Path).CheckAsync(new Version(2, 2, 0)));
        }
    }

    private static async Task MissingPackageMetadataAsync()
    {
        using var temp = new TempDirectory();
        using (var http = new HttpClient(JsonHandler(ReleaseJson("v2.3.0", []))))
        {
            var result = await new GitHubUpdateClient(http, temp.Path).CheckAsync(new Version(2, 2, 0));
            Equal(UpdateCheckState.PackageNotReady, result.State);
        }

        var payload = Encoding.UTF8.GetBytes("payload");
        using (var http = new HttpClient(JsonHandler(ReleaseJson("v2.3.0", [Asset("v2.3.0", payload, omitDigest: true)]))))
        {
            var result = await new GitHubUpdateClient(http, temp.Path).CheckAsync(new Version(2, 2, 0));
            Equal(UpdateCheckState.PackageNotReady, result.State);
        }
    }

    private static async Task InvalidAssetUrlIsRejectedAsync()
    {
        using var temp = new TempDirectory();
        var payload = Encoding.UTF8.GetBytes("payload");
        using var http = new HttpClient(JsonHandler(ReleaseJson("v2.3.0", [Asset(
            "v2.3.0", payload, url: "https://attacker.example/setup.exe")])));
        await ThrowsAsync<InvalidDataException>(
            () => new GitHubUpdateClient(http, temp.Path).CheckAsync(new Version(2, 2, 0)));
    }

    private static async Task StreamedDownloadVerifiesAsync()
    {
        using var temp = new TempDirectory();
        var payload = Enumerable.Range(0, 256 * 1024).Select(index => (byte)(index % 251)).ToArray();
        var progress = new RecordingProgress();
        var handler = DownloadHandler(
            (_, _) => Task.FromResult<Stream>(
                new ChunkedStream(payload, chunkSize: 32 * 1024, delay: TimeSpan.FromMilliseconds(110))),
            finalUri: new Uri("https://release-assets.githubusercontent.com/assets/installer"));
        using var http = new HttpClient(handler);
        var client = new GitHubUpdateClient(http, temp.Path);
        var release = MakeRelease(payload);

        var downloaded = await client.DownloadAsync(release, progress);
        await client.ValidateAsync(downloaded);

        Equal(Path.Combine(temp.Path, "2.3.0", "SkinManager-Setup-2.3.0.exe"), downloaded.FilePath);
        Equal(payload.LongLength, new FileInfo(downloaded.FilePath).Length);
        True(progress.Values.Count >= 2);
        Equal(payload.LongLength, progress.Values[^1].BytesReceived);
        Equal(100d, progress.Values[^1].Percent!.Value);
        True(File.ReadAllBytes(downloaded.FilePath).SequenceEqual(payload));
    }

    private static async Task DownloadRedirectHostIsValidatedAsync()
    {
        using var temp = new TempDirectory();
        var payload = Encoding.UTF8.GetBytes("redirect test bytes");
        var release = MakeRelease(payload);
        var handler = DownloadHandler(
            (_, _) => Task.FromResult<Stream>(new ChunkedStream(payload)),
            finalUri: new Uri("https://foreign.example/installer"));
        using var http = new HttpClient(handler);

        await ThrowsAsync<InvalidDataException>(() => new GitHubUpdateClient(http, temp.Path).DownloadAsync(release));
        AssertNoPartialOrInstaller(temp.Path);
    }

    private static async Task InvalidDownloadsAreRemovedAsync()
    {
        using var temp = new TempDirectory();
        var expected = Encoding.UTF8.GetBytes("expected");
        var wrongSameLength = Encoding.UTF8.GetBytes("xexpected")[..expected.Length];
        var release = MakeRelease(expected);

        var digestHandler = DownloadHandler((_, _) => Task.FromResult<Stream>(new ChunkedStream(wrongSameLength)));
        using (var http = new HttpClient(digestHandler))
        {
            await ThrowsAsync<InvalidDataException>(() => new GitHubUpdateClient(http, temp.Path).DownloadAsync(release));
        }
        AssertNoPartialOrInstaller(temp.Path);

        var lengthHandler = DownloadHandler((_, _) => Task.FromResult<Stream>(new ChunkedStream(expected[..^1])));
        using (var http = new HttpClient(lengthHandler))
        {
            await ThrowsAsync<InvalidDataException>(() => new GitHubUpdateClient(http, temp.Path).DownloadAsync(release));
        }
        AssertNoPartialOrInstaller(temp.Path);
    }

    private static async Task InterruptedDownloadsAreRemovedAsync()
    {
        using var temp = new TempDirectory();
        var payload = Enumerable.Repeat((byte)42, 64 * 1024).ToArray();
        var release = MakeRelease(payload);

        var cancelHandler = DownloadHandler((_, _) => Task.FromResult<Stream>(new ChunkedStream(
            payload, chunkSize: 1024, delay: TimeSpan.FromSeconds(2))));
        using (var http = new HttpClient(cancelHandler))
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            await ThrowsAsync<OperationCanceledException>(() =>
                new GitHubUpdateClient(http, temp.Path).DownloadAsync(release, cancellationToken: cancellation.Token));
        }
        AssertNoPartialOrInstaller(temp.Path);

        var failHandler = DownloadHandler((_, _) => Task.FromResult<Stream>(new ChunkedStream(
            payload, chunkSize: 4096, throwAfterBytes: 4096)));
        using (var http = new HttpClient(failHandler))
        {
            await ThrowsAsync<IOException>(() => new GitHubUpdateClient(http, temp.Path).DownloadAsync(release));
        }
        AssertNoPartialOrInstaller(temp.Path);
    }

    private static async Task ValidCacheIsReusedAsync()
    {
        using var temp = new TempDirectory();
        var payload = Encoding.UTF8.GetBytes("cached test installer");
        var release = MakeRelease(payload);
        var expectedPath = Path.Combine(temp.Path, "2.3.0", "SkinManager-Setup-2.3.0.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
        await File.WriteAllBytesAsync(expectedPath, payload);

        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("cache hit should not request HTTP"));
        using var http = new HttpClient(handler);
        var client = new GitHubUpdateClient(http, temp.Path);
        var result = await client.DownloadAsync(release);

        Equal(expectedPath, result.FilePath);
        Equal(0, handler.RequestCount);
    }

    private static async Task ValidateRejectsTamperingAsync()
    {
        using var temp = new TempDirectory();
        var payload = Encoding.UTF8.GetBytes("validated bytes");
        var release = MakeRelease(payload);
        var handler = DownloadHandler((_, _) => Task.FromResult<Stream>(new ChunkedStream(payload)));
        using var http = new HttpClient(handler);
        var client = new GitHubUpdateClient(http, temp.Path);
        var downloaded = await client.DownloadAsync(release);
        await client.ValidateAsync(downloaded);

        var tampered = await File.ReadAllBytesAsync(downloaded.FilePath);
        tampered[0] ^= 0xFF;
        await File.WriteAllBytesAsync(downloaded.FilePath, tampered);
        await ThrowsAsync<InvalidDataException>(() => client.ValidateAsync(downloaded));

        var outside = downloaded with { FilePath = Path.Combine(temp.Path, "other.exe") };
        await ThrowsAsync<InvalidDataException>(() => client.ValidateAsync(outside));
    }

    private static GitHubUpdateClient CreateClient(HttpMessageHandler handler, string cacheDirectory) =>
        new(new HttpClient(handler), cacheDirectory);

    private static FakeHandler JsonHandler(string json) => new((request, _) => Task.FromResult(JsonResponse(json, request)));

    private static FakeHandler DownloadHandler(
        Func<HttpRequestMessage, CancellationToken, Task<Stream>> streamFactory,
        Uri? finalUri = null) => new(async (request, cancellationToken) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = finalUri is null ? request : new HttpRequestMessage(request.Method, finalUri),
            Content = new StreamContent(await streamFactory(request, cancellationToken))
        });

    private static HttpResponseMessage JsonResponse(string json, HttpRequestMessage? request = null) =>
        new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static string ReleaseJson(
        string tag,
        object[] assets,
        bool draft = false,
        bool prerelease = false) => JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft,
            prerelease,
            body = "Offline test notes",
            assets
        });

    private static object Asset(
        string versionTag,
        byte[] payload,
        string? digest = null,
        string? url = null,
        string state = "uploaded",
        bool omitDigest = false)
    {
        var version = versionTag.TrimStart('v');
        return new
        {
            name = $"SkinManager-Setup-{version}.exe",
            state,
            size = payload.LongLength,
            digest = omitDigest ? null : digest ?? "sha256:" + Digest(payload),
            browser_download_url = url ?? $"https://github.com/Nortea575/SkinManager/releases/download/{versionTag}/SkinManager-Setup-{version}.exe"
        };
    }

    private static UpdateRelease MakeRelease(byte[] payload, Version? version = null)
    {
        version ??= new Version(2, 3, 0);
        var tag = "v" + version;
        return new UpdateRelease(
            version,
            tag,
            "Offline test notes",
            new Uri($"https://github.com/Nortea575/SkinManager/releases/download/{tag}/SkinManager-Setup-{version}.exe"),
            payload.LongLength,
            Digest(payload));
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void AssertNoPartialOrInstaller(string directory)
    {
        var files = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            : [];
        Equal(0, files.Length);
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        await test();
        _testCount++;
        Console.WriteLine($"PASS {name}");
    }

    private static void True(bool condition, string? message = null)
    {
        _assertionCount++;
        if (!condition)
        {
            throw new InvalidOperationException(message ?? "Expected condition to be true.");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        _assertionCount++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        _assertionCount++;
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Expected {typeof(TException).Name}, got {exception.GetType().Name}.", exception);
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}, but no exception was thrown.");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SkinManager.UpdateTests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class FakeHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var response = await handler(request, cancellationToken);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private sealed class RecordingProgress : IProgress<UpdateDownloadProgress>
    {
        public List<UpdateDownloadProgress> Values { get; } = [];

        public void Report(UpdateDownloadProgress value) => Values.Add(value);
    }

    private sealed class ChunkedStream(
        byte[] payload,
        int chunkSize = 16 * 1024,
        TimeSpan delay = default,
        int throwAfterBytes = -1) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            if (throwAfterBytes >= 0 && _position >= throwAfterBytes)
            {
                throw new IOException("simulated stream failure");
            }

            var remaining = payload.Length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var count = Math.Min(Math.Min(buffer.Length, chunkSize), remaining);
            payload.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
