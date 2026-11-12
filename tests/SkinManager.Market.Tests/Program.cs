using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
using SkinManager.Core;
using SkinManager.Market;

namespace SkinManager.Market.Tests;

internal static partial class Program
{
    private const string PackageResource = "SkinManager.Market.Tests.Resources.Ayanami.zip";
    private const string GifResource = "SkinManager.Market.Tests.Resources.transparent-animation.gif";

    private static async Task<int> Main()
    {
        try
        {
            var zipBytes = ReadEmbeddedPackage();
            var package = SkinPackageReader.ReadZipBytes(zipBytes);
            await CatalogContractAsync(package, zipBytes);
            await ExtendedCatalogContractAsync(package, zipBytes);
            await LegacyCatalogCompatibilityAsync(package, zipBytes);
            await RejectMalformedOptionalCountsAsync(package, zipBytes);
            await ValidateDetailUriAsync();
            await PreferThumbnailUrlAsync(package, zipBytes);
            await RejectCrossOriginThumbnailAsync(package, zipBytes);
            await RejectOversizedThumbnailAsync(package, zipBytes);
            await RetryConnectionFailureAsync(package, zipBytes);
            await RetryInterruptedThumbnailReadAsync(package, zipBytes);
            await RetryTruncatedThumbnailReadAsync(package, zipBytes);
            await LimitPersistentThumbnailRetriesAsync(package, zipBytes);
            await CancelThumbnailReadWithoutRetryAsync(package, zipBytes);
            await ValidDownloadAsync(package, zipBytes);
            await RejectWrongHashAsync(package, zipBytes);
            await RejectWrongSizeAsync(package, zipBytes);
            await RejectInvalidArchiveAsync(package);
            await RejectUnsupportedSkinFormatAsync(package, zipBytes);
            await RejectManifestVersionMismatchAsync(package, zipBytes);
            await RejectIncompatibleManagerAsync(package, zipBytes);
            await GifPackageMarketFlowAsync();
            await CancelStreamingDownloadAsync(package, zipBytes);
            await RejectRedirectAsync(package, zipBytes);
            await RejectCrossOriginUrlAsync(package, zipBytes);
            await CommunitySessionAndActionsAsync(package, zipBytes);
            var gifBytes = ReadEmbeddedGif();
            var gifZipBytes = CreateGifPackage(gifBytes);
            var gifPackage = SkinPackageReader.ReadZipBytes(gifZipBytes);
            await CommunitySessionAndActionsAsync(gifPackage, gifZipBytes, "1.0.0", "1.1.4");
            await CommunitySessionAndActionsAsync(gifPackage, gifZipBytes, "2.6.0", "2.6.0");
            await AnonymousCommunityReadAndUnauthenticatedWritesAsync();
            await RejectInvalidNonceWithoutRetryAsync();
            await DirectoryNonceExpiryClearsAccountAsync();
            await RejectCrossOriginAccountConfigAsync();
            await CancelAccountRefreshAsync();
            await IgnoreLateAccountRefreshAsync();
            Console.WriteLine("市场客户端离线验收通过。");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"市场客户端验收失败：{exception}");
            return 1;
        }
    }

    private static async Task CatalogContractAsync(SkinPackage package, byte[] zipBytes)
    {
        var item = CreateItem(package, zipBytes);
        HttpRequestMessage? received = null;
        using var client = new SkinMarketClient(new StubHandler(request =>
        {
            received = request;
            return JsonResponse(CreateCatalogJson(item, page: 2, perPage: 50, total: 68, totalPages: 2));
        }));

        var page = await client.GetPageAsync(2, 50, "Ayanami + light", "dark");
        Check(page.Items.Count == 1 && page.Items[0].Id == package.Id, "读取市场目录条目");
        Check(page.Pagination is { Page: 2, PerPage: 50, Total: 68, TotalPages: 2 }, "读取分页数据");
        Check(page.AvailableTags is { Count: 0 }, "旧目录缺少标签列表时按空列表兼容");
        var query = received?.RequestUri?.Query ?? string.Empty;
        Check(query.Contains("rest_route=%2Fskin-manager%2Fv1%2Fskins", StringComparison.OrdinalIgnoreCase) &&
              query.Contains("page=2", StringComparison.Ordinal) && query.Contains("per_page=50", StringComparison.Ordinal) &&
              query.Contains("appearance=dark", StringComparison.Ordinal) && query.Contains("sort=latest", StringComparison.Ordinal) &&
              query.Contains("q=Ayanami%20%2B%20light", StringComparison.OrdinalIgnoreCase),
            "按查询、外观、默认排序与分页参数调用同源 REST 接口");
    }

    private static async Task ExtendedCatalogContractAsync(SkinPackage package, byte[] zipBytes)
    {
        var item = CreateItem(package, zipBytes);
        var json = CreateCatalogJson(item, page: 1, perPage: 12, total: 1, totalPages: 1);
        var root = JsonNode.Parse(json)!.AsObject();
        var entry = root["items"]!.AsArray()[0]!.AsObject();
        entry["thumbnailUrl"] = "https://blog.ancimen.top/wp-content/uploads/ayanami-thumb.jpg";
        entry["preview"] = new JsonObject { ["html"] = "fake runtime payload that the manager ignores" };
        entry["fullDescription"] = "完整的皮肤说明。\n支持第二段内容。";
        entry["submitter"] = "AyanamiSubmitter";
        entry["detailUrl"] = "https://blog.ancimen.top/skins/ayanami";
        entry["tags"] = new JsonArray(new JsonObject { ["id"] = 12, ["name"] = "轻量" });
        entry["likeCount"] = 37;
        entry["commentCount"] = 4;
        entry["likedByMe"] = true;
        root["availableTags"] = new JsonArray(
            new JsonObject { ["id"] = 12, ["name"] = "轻量" },
            new JsonObject { ["id"] = 18, ["name"] = "浅色" });

        HttpRequestMessage? received = null;
        using var client = new SkinMarketClient(new StubHandler(request =>
        {
            received = request;
            return JsonResponse(root.ToJsonString());
        }));
        var page = await client.GetPageAsync(1, 12, "ayanami", "light", CancellationToken.None, "likes", "12");
        var parsed = page.Items.Single();
        Check(parsed.EffectiveDescription == "完整的皮肤说明。\n支持第二段内容。" && parsed.Submitter == "AyanamiSubmitter",
            "读取完整说明与投稿人可选字段");
        Check(parsed.Tags is { Count: 1 } && parsed.Tags[0] == new SkinMarketTag("12", "轻量") &&
              page.AvailableTags is { Count: 2 } && page.AvailableTags[0].Id == "12",
            "读取皮肤标签和可用标签列表");
        Check(parsed.LikeCount == 37 && parsed.CommentCount == 4 && parsed.LikedByMe,
            "读取点赞与评论展示计数");
        Check(parsed.EffectiveThumbnailUrl == "https://blog.ancimen.top/wp-content/uploads/ayanami-thumb.jpg" &&
              parsed.DetailUrl == "https://blog.ancimen.top/skins/ayanami",
            "读取封面缩略图与网站详情链接");
        var query = received?.RequestUri?.Query ?? string.Empty;
        Check(query.Contains("sort=likes", StringComparison.Ordinal) && query.Contains("tag=12", StringComparison.Ordinal),
            "将最多点赞排序和标签 ID 发送到目录接口");
    }

    private static async Task LegacyCatalogCompatibilityAsync(SkinPackage package, byte[] zipBytes)
    {
        var item = CreateItem(package, zipBytes);
        using var client = new SkinMarketClient(new StubHandler(_ =>
            JsonResponse(CreateCatalogJson(item, page: 1, perPage: 12, total: 1, totalPages: 1))));
        var parsed = (await client.GetPageAsync()).Items.Single();
        Check(parsed.FullDescription == string.Empty && parsed.Submitter == string.Empty && parsed.Tags is { Count: 0 } &&
              parsed.DetailUrl is null && parsed.LikeCount == 0 && parsed.CommentCount == 0 && !parsed.LikedByMe,
            "旧 schema v1 缺少新增字段时使用空值默认值");
    }

    private static async Task RejectMalformedOptionalCountsAsync(SkinPackage package, byte[] zipBytes)
    {
        var root = JsonNode.Parse(CreateCatalogJson(CreateItem(package, zipBytes), 1, 12, 1, 1))!.AsObject();
        root["items"]!.AsArray()[0]!["likeCount"] = -1;
        using var client = new SkinMarketClient(new StubHandler(_ => JsonResponse(root.ToJsonString())));
        await ThrowsAsync<InvalidDataException>(() => client.GetPageAsync(), "拒绝负点赞计数");
    }

    private static Task ValidateDetailUriAsync()
    {
        var relative = SkinMarketClient.GetDetailUri("/skins/ayanami");
        Check(relative.AbsoluteUri == "https://blog.ancimen.top/skins/ayanami", "相对详情地址解析为本站 HTTPS 页面");
        try
        {
            _ = SkinMarketClient.GetDetailUri("https://evil.example/skins/ayanami");
        }
        catch (InvalidDataException)
        {
            Check(true, "拒绝跨源网站详情链接");
            return Task.CompletedTask;
        }

        throw new InvalidDataException("跨源网站详情链接没有被拒绝。");
    }

    private static async Task PreferThumbnailUrlAsync(SkinPackage package, byte[] zipBytes)
    {
        var item = CreateItem(package, zipBytes) with
        {
            ThumbnailUrl = "https://blog.ancimen.top/wp-content/uploads/ayanami-thumb.jpg"
        };
        Uri? requested = null;
        using var client = new SkinMarketClient(new StubHandler(request =>
        {
            requested = request.RequestUri;
            return ThumbnailResponse(package.ImageBytes ?? throw new InvalidDataException("缺少 Ayanami 测试缩略图。"));
        }));
        var image = await client.GetThumbnailImageAsync(item);
        Check(image is { Length: > 0 } && requested?.AbsolutePath.EndsWith("ayanami-thumb.jpg", StringComparison.Ordinal) == true,
            "优先使用 thumbnailUrl 获取封面，并保留旧 previewUrl 回退入口");
    }

    private static async Task ValidDownloadAsync(SkinPackage package, byte[] zipBytes)
    {
        var item = CreateItem(package, zipBytes);
        using var client = new SkinMarketClient(new StubHandler(_ => ZipResponse(zipBytes)));
        var reported = new List<SkinMarketDownloadProgress>();
        var result = await client.DownloadPackageAsync(item, new Version(1, 0, 0), new InlineProgress<SkinMarketDownloadProgress>(reported.Add));
        Check(result.Id == package.Id && result.Version == package.Version, "流式下载后验证 SHA-256、ZIP 与 manifest");
        Check(reported.Count > 0 && reported[^1].BytesReceived == zipBytes.Length, "报告下载进度");
    }

    private static async Task RejectCrossOriginThumbnailAsync(SkinPackage package, byte[] zipBytes)
    {
        var item = CreateItem(package, zipBytes) with { PreviewUrl = "https://evil.example/preview.png" };
        var requestCount = 0;
        using var client = new SkinMarketClient(new StubHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        await ThrowsAsync<InvalidDataException>(
            () => client.GetThumbnailImageAsync(item), "拒绝跨源封面缩略图地址");
        Check(requestCount == 0, "跨源封面缩略图地址未被请求");
    }

    private static async Task RejectOversizedThumbnailAsync(SkinPackage package, byte[] zipBytes)
    {
        var item = CreateItem(package, zipBytes);
        var requestCount = 0;
        using var client = new SkinMarketClient(new StubHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[SkinMarketClient.MaxThumbnailBytes + 1])
            };
        }));
        await ThrowsAsync<InvalidDataException>(
            () => client.GetThumbnailImageAsync(item), "限制市场封面缩略图大小");
        Check(requestCount == 1, "超过缩略图上限时不重试");
    }

    private static async Task RetryInterruptedThumbnailReadAsync(SkinPackage package, byte[] zipBytes)
    {
        var item = CreateItem(package, zipBytes);
        var imageBytes = package.ImageBytes ?? throw new InvalidDataException("Ayanami 验收包缺少背景图片。");
        var requestCount = 0;
        using var client = new SkinMarketClient(new StubHandler(_ =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var content = new StreamContent(new InterruptedReadStream(imageBytes));
                content.Headers.ContentLength = imageBytes.LongLength;
                content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            return ThumbnailResponse(imageBytes);
        }));

        var result = await client.GetThumbnailImageAsync(item);
        Check(requestCount == 2 && result is not null && result.SequenceEqual(imageBytes),
            "中途读流中断时仅重试一次并读取完整缩略图");
    }

    private static async Task RetryConnectionFailureAsync(SkinPackage package, byte[] zipBytes)
    {
        var item = CreateItem(package, zipBytes);
        var imageBytes = package.ImageBytes ?? throw new InvalidDataException("Ayanami 验收包缺少背景图片。");
        var requestCount = 0;
        using var client = new SkinMarketClient(new StubHandler(_ =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                throw new HttpRequestException("Injected offline connection failure.");
            }

            return ThumbnailResponse(imageBytes);
        }));

        var result = await client.GetThumbnailImageAsync(item);
        Check(requestCount == 2 && result is not null && result.SequenceEqual(imageBytes),
            "连接失败时仅重试一次并读取完整缩略图");
    }

    private static async Task RetryTruncatedThumbnailReadAsync(SkinPackage package, byte[] zipBytes)
    {
        var item = CreateItem(package, zipBytes);
        var imageBytes = package.ImageBytes ?? throw new InvalidDataException("Ayanami 验收包缺少背景图片。");
        var requestCount = 0;
        using var client = new SkinMarketClient(new StubHandler(_ =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var content = new StreamContent(new MemoryStream(imageBytes, 0, imageBytes.Length / 2, writable: false));
                content.Headers.ContentLength = imageBytes.LongLength;
                content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            return ThumbnailResponse(imageBytes);
        }));

        var result = await client.GetThumbnailImageAsync(item);
        Check(requestCount == 2 && result is not null && result.SequenceEqual(imageBytes),
            "Content-Length 不匹配时只重取一次完整缩略图");
    }

    private static async Task LimitPersistentThumbnailRetriesAsync(SkinPackage package, byte[] zipBytes)
    {
        var item = CreateItem(package, zipBytes);
        var imageBytes = package.ImageBytes ?? throw new InvalidDataException("Ayanami 验收包缺少背景图片。");
        var requestCount = 0;
        using var client = new SkinMarketClient(new StubHandler(_ =>
        {
            requestCount++;
            return InterruptedThumbnailResponse(imageBytes);
        }));

        try
        {
            _ = await client.GetThumbnailImageAsync(item);
        }
        catch (AggregateException exception)
        {
            Check(requestCount == 2 && exception.Flatten().InnerExceptions.Count == 2,
                "持续断流最多发送两次请求并保留两次异常");
            return;
        }

        throw new InvalidDataException("持续断流时没有在两次请求后失败。");
    }

    private static async Task CancelThumbnailReadWithoutRetryAsync(SkinPackage package, byte[] zipBytes)
    {
        var item = CreateItem(package, zipBytes);
        var stream = new BlockingReadStream();
        var requestCount = 0;
        using var client = new SkinMarketClient(new StubHandler(_ =>
        {
            requestCount++;
            var content = new StreamContent(stream);
            content.Headers.ContentLength = 128;
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        using var cancellation = new CancellationTokenSource();
        var read = client.GetThumbnailImageAsync(item, cancellation.Token);
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => read, "取消封面缩略图读流");
        Check(requestCount == 1, "取消后不启动第二次缩略图请求");
    }

    private static HttpResponseMessage InterruptedThumbnailResponse(byte[] imageBytes)
    {
        var content = new StreamContent(new InterruptedReadStream(imageBytes));
        content.Headers.ContentLength = imageBytes.LongLength;
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static async Task RejectWrongHashAsync(SkinPackage package, byte[] zipBytes)
    {
        var item = CreateItem(package, zipBytes) with { Sha256 = new string('0', 64) };
        using var client = new SkinMarketClient(new StubHandler(_ => ZipResponse(zipBytes)));
        await ThrowsAsync<InvalidDataException>(
            () => client.DownloadPackageAsync(item, new Version(1, 0, 0)), "拒绝 SHA-256 不匹配的文件");
    }

    private static async Task RejectWrongSizeAsync(SkinPackage package, byte[] zipBytes)
    {
        var item = CreateItem(package, zipBytes) with { Size = zipBytes.LongLength - 1 };
        using var client = new SkinMarketClient(new StubHandler(_ => ZipResponse(zipBytes)));
        await ThrowsAsync<InvalidDataException>(
            () => client.DownloadPackageAsync(item, new Version(1, 0, 0)), "拒绝与目录大小不一致的文件");
    }

    private static async Task RejectInvalidArchiveAsync(SkinPackage package)
    {
        var bytes = Encoding.UTF8.GetBytes("not a skin zip");
        var item = CreateItem(package, bytes);
        using var client = new SkinMarketClient(new StubHandler(_ => ZipResponse(bytes)));
        await ThrowsAsync<InvalidDataException>(
            () => client.DownloadPackageAsync(item, new Version(1, 0, 0)), "拒绝哈希正确但 ZIP 格式无效的包");
    }

    private static async Task RejectManifestVersionMismatchAsync(SkinPackage package, byte[] zipBytes)
    {
        var item = CreateItem(package, zipBytes) with { Version = "99.0.0" };
        using var client = new SkinMarketClient(new StubHandler(_ => ZipResponse(zipBytes)));
        await ThrowsAsync<InvalidDataException>(
            () => client.DownloadPackageAsync(item, new Version(1, 0, 0)), "拒绝 manifest 版本与目录不一致的包");
    }

    private static async Task RejectUnsupportedSkinFormatAsync(SkinPackage package, byte[] zipBytes)
    {
        var item = CreateItem(package, zipBytes) with { SkinFormatVersion = package.Definition.FormatVersion + 1 };
        var requestCount = 0;
        using var client = new SkinMarketClient(new StubHandler(_ =>
        {
            requestCount++;
            return ZipResponse(zipBytes);
        }));
        await ThrowsAsync<InvalidDataException>(
            () => client.DownloadPackageAsync(item, new Version(1, 0, 0)), "拒绝不支持的皮肤包格式版本");
        Check(requestCount == 0, "格式不兼容时不启动下载");
    }

    private static async Task RejectIncompatibleManagerAsync(SkinPackage package, byte[] zipBytes)
    {
        var item = CreateItem(package, zipBytes) with { MinimumManagerVersion = "2.6.0" };
        var requestCount = 0;
        using var client = new SkinMarketClient(new StubHandler(_ =>
        {
            requestCount++;
            return ZipResponse(zipBytes);
        }));
        await ThrowsAsync<InvalidDataException>(
            () => client.DownloadPackageAsync(item, new Version(1, 0, 0)), "拒绝最低管理器版本不兼容的条目");
        Check(requestCount == 0, "不兼容时不启动下载");
    }

    private static async Task GifPackageMarketFlowAsync()
    {
        var gifBytes = ReadEmbeddedGif();
        var zipBytes = CreateGifPackage(gifBytes);
        var package = SkinPackageReader.ReadZipBytes(zipBytes);
        Check(package.ImagePath == "assets/transparent-animation.gif" &&
              package.Files.TryGetValue(package.ImagePath, out var storedGif) && storedGif.SequenceEqual(gifBytes),
            "Core 保留市场下载包内的原始多帧 GIF 字节");
        Check(SkinMarketClient.MinimumManagerVersionForPackage("1.0.0", package.Files.Keys) == "1.1.4",
            "投稿中的任意 GIF 资源自动将最低版本提升到 1.1.4");
        Check(SkinMarketClient.MinimumManagerVersionForPackage("2.6.0", package.Files.Keys) == "2.6.0" &&
              SkinMarketClient.MinimumManagerVersionForPackage("1.0.0", new[] { "assets/background.png" }) == "1.0.0" &&
              SkinMarketClient.MinimumManagerVersionForPackage("1.0.0", new[] { "assets/background.jpg" }) == "1.0.0" &&
              SkinMarketClient.MinimumManagerVersionForPackage("1.0.0", new[] { "assets/background.jpeg" }) == "1.0.0",
            "自动最低版本保留用户的更高要求且不改变 PNG、JPG、JPEG 包");

        var item = CreateItem(package, zipBytes) with { MinimumManagerVersion = SkinMarketClient.GifMinimumManagerVersion };
        var requestCount = 0;
        using var client = new SkinMarketClient(new StubHandler(_ =>
        {
            requestCount++;
            return ZipResponse(zipBytes);
        }));
        Check(!SkinMarketClient.IsManagerCompatible(item.MinimumManagerVersion, new Version(1, 1, 3), out _) &&
              SkinMarketClient.IsManagerCompatible(item.MinimumManagerVersion, new Version(1, 1, 4, 0), out _),
            "市场兼容性阻挡 1.1.3 并允许 Beta 1 程序版本 1.1.4.0");
        await ThrowsAsync<InvalidDataException>(
            () => client.DownloadPackageAsync(item, new Version(1, 1, 3)), "GIF 皮肤在下载前阻挡 1.1.3");
        Check(requestCount == 0, "版本不兼容时未请求 GIF ZIP");

        var downloaded = await client.DownloadPackageAsync(item, new Version(1, 1, 4, 0));
        Check(requestCount == 1 && downloaded.Files.TryGetValue("assets/transparent-animation.gif", out var downloadedGif) &&
              downloadedGif.SequenceEqual(gifBytes), "1.1.4.0 下载 GIF 包并保留原始资源字节");
        Check(item.Sha256 == Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant() && item.Size == zipBytes.LongLength,
            "GIF ZIP 目录 SHA-256 和文件长度覆盖原始包字节");
    }

    private static async Task CancelStreamingDownloadAsync(SkinPackage package, byte[] zipBytes)
    {
        var item = CreateItem(package, zipBytes);
        var stream = new BlockingReadStream();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "SkinManagerMarketTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            using var client = new SkinMarketClient(new StubHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
                response.Content.Headers.ContentLength = zipBytes.LongLength;
                return response;
            }), temporaryDirectory);
            using var cancellation = new CancellationTokenSource();
            var download = client.DownloadPackageAsync(item, new Version(1, 0, 0), cancellationToken: cancellation.Token);
            await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => download, "取消响应流读取");
            Check(!Directory.EnumerateFileSystemEntries(temporaryDirectory).Any(), "取消后删除不完整的临时 ZIP");
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static async Task RejectRedirectAsync(SkinPackage package, byte[] zipBytes)
    {
        var item = CreateItem(package, zipBytes);
        var requestCount = 0;
        using var client = new SkinMarketClient(new StubHandler(_ =>
        {
            requestCount++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://evil.example/skin.zip");
            return response;
        }));
        await ThrowsAsync<InvalidDataException>(
            () => client.DownloadPackageAsync(item, new Version(1, 0, 0)), "拒绝 HTTP 重定向");
        Check(requestCount == 1, "重定向目标未被请求");
    }

    private static async Task RejectCrossOriginUrlAsync(SkinPackage package, byte[] zipBytes)
    {
        var item = CreateItem(package, zipBytes) with { DownloadUrl = "https://evil.example/package.zip" };
        var requestCount = 0;
        using var client = new SkinMarketClient(new StubHandler(_ =>
        {
            requestCount++;
            return ZipResponse(zipBytes);
        }));
        await ThrowsAsync<InvalidDataException>(
            () => client.DownloadPackageAsync(item, new Version(1, 0, 0)), "拒绝跨源下载地址");
        Check(requestCount == 0, "跨源下载地址未被请求");
    }

    private static SkinMarketItem CreateItem(SkinPackage package, byte[] bytes) => new(
        package.Id,
        package.Name,
        package.Version,
        package.Appearance switch
        {
            SkinAppearance.Light => "light",
            SkinAppearance.Dark => "dark",
            _ => "system"
        },
        "离线皮肤测试样例",
        "Ancimen",
        "保留所有权利。",
        "https://blog.ancimen.top/wp-content/uploads/market-preview.png",
        $"https://blog.ancimen.top/?rest_route=/skin-manager/v1/skins/{package.Id}/download",
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        bytes.LongLength,
        package.Definition.FormatVersion,
        "1.0.0",
        "2026-11-03T00:00:00Z");

    private static string CreateCatalogJson(SkinMarketItem item, int page, int perPage, int total, int totalPages) =>
        JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            schemaVersion = 1,
            items = new[]
            {
                new
                {
                    id = item.Id,
                    name = item.Name,
                    version = item.Version,
                    appearance = item.Appearance,
                    description = item.Description,
                    author = item.Author,
                    licenseNotes = item.LicenseNotes,
                    previewUrl = item.PreviewUrl,
                    downloadUrl = item.DownloadUrl,
                    sha256 = item.Sha256,
                    size = item.Size,
                    skinFormatVersion = item.SkinFormatVersion,
                    minManagerVersion = item.MinimumManagerVersion,
                    updatedAt = item.UpdatedAt
                }
            },
            pagination = new { page, perPage, total, totalPages }
        });

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage ZipResponse(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/zip") } }
    };

    private static HttpResponseMessage ThumbnailResponse(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } }
    };

    private static byte[] ReadEmbeddedPackage()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PackageResource)
            ?? throw new FileNotFoundException($"缺少离线验收包资源：{PackageResource}");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] ReadEmbeddedGif()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(GifResource)
            ?? throw new FileNotFoundException($"缺少共享 GIF 验收资源：{GifResource}");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] CreateGifPackage(byte[] gifBytes)
    {
        using var source = new MemoryStream(ReadEmbeddedPackage(), writable: false);
        using var sourceArchive = new ZipArchive(source, ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var targetArchive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in sourceArchive.Entries)
            {
                if (entry.FullName.Equals("assets/ayanami-background.png", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var targetEntry = targetArchive.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var sourceStream = entry.Open();
                using var targetStream = targetEntry.Open();
                if (entry.FullName.Equals("skin.json", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(sourceStream, Encoding.UTF8);
                    var manifest = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
                    manifest["background"]!["image"] = "assets/transparent-animation.gif";
                    var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
                    targetStream.Write(manifestBytes);
                }
                else
                {
                    sourceStream.CopyTo(targetStream);
                }
            }

            var gifEntry = targetArchive.CreateEntry("assets/transparent-animation.gif", CompressionLevel.NoCompression);
            using var gifStream = gifEntry.Open();
            gifStream.Write(gifBytes);
        }

        return output.ToArray();
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action, string label) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            Console.WriteLine($"通过：{label}");
            return;
        }

        throw new InvalidDataException($"未拒绝预期的无效输入：{label}");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidDataException($"{label}失败。");
        }

        Console.WriteLine($"通过：{label}");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(handler(request));
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class BlockingReadStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class InterruptedReadStream(byte[] bytes) : Stream
    {
        private int _position;
        private bool _interrupted;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position > 0 && !_interrupted)
            {
                _interrupted = true;
                throw new IOException("Injected offline-test stream interruption.");
            }

            var count = Math.Min(buffer.Length, bytes.Length - _position);
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return ValueTask.FromResult(count);
        }
    }
}
