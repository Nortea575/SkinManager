using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkinManager.Core;

namespace SkinManager.Market;

public sealed partial class SkinMarketClient : IDisposable
{
    public const long MaxDownloadBytes = 64L * 1024 * 1024;
    public const int MaxThumbnailBytes = 4 * 1024 * 1024;
    public const string GifMinimumManagerVersion = "1.1.4";
    private const int MaxCatalogBytes = 1024 * 1024;
    private static readonly Uri SiteRoot = new("https://blog.ancimen.top/");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(90);
    private readonly HttpClient _httpClient;
    private readonly string _temporaryDirectory;
    private bool _disposed;

    public SkinMarketClient()
        : this(CreateDefaultHandler(), Path.GetTempPath())
    {
    }

    public SkinMarketClient(HttpMessageHandler messageHandler)
        : this(messageHandler, Path.GetTempPath())
    {
    }

    internal SkinMarketClient(HttpMessageHandler messageHandler, string temporaryDirectory)
    {
        ArgumentNullException.ThrowIfNull(messageHandler);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryDirectory);
        _temporaryDirectory = Path.GetFullPath(temporaryDirectory);
        _httpClient = new HttpClient(messageHandler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<SkinMarketPage> GetPageAsync(
        int page = 1,
        int perPage = 12,
        string? query = null,
        string? appearance = null,
        CancellationToken cancellationToken = default,
        string? sort = null,
        string? tag = null)
    {
        ThrowIfDisposed();
        if (page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(page));
        }

        if (perPage is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(perPage), AppLocalization.Get("每页数量必须在 1 到 50 之间。"));
        }

        query ??= string.Empty;
        if (query.Length > 200)
        {
            throw new ArgumentException(AppLocalization.Get("搜索内容不能超过 200 个字符。"), nameof(query));
        }

        if (!string.IsNullOrEmpty(appearance) && appearance is not ("light" or "dark" or "system"))
        {
            throw new ArgumentException(AppLocalization.Get("外观筛选只能是 light、dark 或 system。"), nameof(appearance));
        }

        sort = string.IsNullOrWhiteSpace(sort) ? "latest" : sort.Trim();
        if (sort is not ("latest" or "likes"))
        {
            throw new ArgumentException(AppLocalization.Get("排序方式只能是 latest 或 likes。"), nameof(sort));
        }

        tag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
        if (tag is { Length: > 80 })
        {
            throw new ArgumentException(AppLocalization.Get("标签不能超过 80 个字符。"), nameof(tag));
        }

        var queryParts = new List<string>
        {
            $"rest_route={Uri.EscapeDataString("/skin-manager/v1/skins")}",
            $"page={page.ToString(CultureInfo.InvariantCulture)}",
            $"per_page={perPage.ToString(CultureInfo.InvariantCulture)}",
            $"sort={Uri.EscapeDataString(sort)}"
        };
        if (!string.IsNullOrWhiteSpace(query))
        {
            queryParts.Add($"q={Uri.EscapeDataString(query.Trim())}");
        }

        if (!string.IsNullOrEmpty(appearance))
        {
            queryParts.Add($"appearance={Uri.EscapeDataString(appearance)}");
        }

        if (tag is not null)
        {
            queryParts.Add($"tag={Uri.EscapeDataString(tag)}");
        }

        var uri = new Uri(SiteRoot, "/?" + string.Join('&', queryParts));
        using var operationCancellation = CreateOperationCancellation(cancellationToken);
        try
        {
            using var response = await SendAsync(uri, operationCancellation.Token).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(response, MaxCatalogBytes, operationCancellation.Token)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
            var root = document.RootElement;
            if (RequiredInt32(root, "formatVersion") != 1 || RequiredInt32(root, "schemaVersion") != 1)
            {
                throw new InvalidDataException(AppLocalization.Get("市场目录使用了不受支持的格式或架构版本。"));
            }

            if (!root.TryGetProperty("items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(AppLocalization.Get("市场目录缺少 items 列表。"));
            }

            if (!root.TryGetProperty("pagination", out var paginationElement) || paginationElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(AppLocalization.Get("市场目录缺少分页信息。"));
            }

            var pagination = new SkinMarketPagination(
                RequiredInt32(paginationElement, "page"),
                RequiredInt32(paginationElement, "perPage"),
                RequiredInt32(paginationElement, "total"),
                RequiredInt32(paginationElement, "totalPages"));
            if (pagination.Page != page || pagination.PerPage != perPage || pagination.Total < 0 || pagination.TotalPages < 0 ||
                itemsElement.GetArrayLength() > perPage)
            {
                throw new InvalidDataException(AppLocalization.Get("市场目录返回了无效的分页数据。"));
            }

            var items = new List<SkinMarketItem>(itemsElement.GetArrayLength());
            foreach (var itemElement in itemsElement.EnumerateArray())
            {
                if (itemElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException(AppLocalization.Get("市场目录中的皮肤条目格式无效。"));
                }

                items.Add(ReadItem(itemElement));
            }

            var availableTags = ReadTags(root, "availableTags");
            return new SkinMarketPage(items, pagination, availableTags);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(AppLocalization.Get("读取市场目录超时，请重试。"), exception);
        }
    }

    public async Task<byte[]?> GetThumbnailImageAsync(
        SkinMarketItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(item.EffectiveThumbnailUrl))
        {
            return null;
        }

        var uri = ValidateSameOrigin(item.EffectiveThumbnailUrl, "封面缩略图");
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (!path.StartsWith("/wp-content/uploads/", StringComparison.OrdinalIgnoreCase) ||
            !(path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
              path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
              path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) ||
            path.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException(AppLocalization.Get("市场封面缩略图地址无效。"));
        }

        using var operationCancellation = CreateOperationCancellation(cancellationToken);
        Exception? firstReadFailure = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var response = await SendAsync(uri, operationCancellation.Token).ConfigureAwait(false);
                    return await ReadBoundedAsync(response, MaxThumbnailBytes, operationCancellation.Token, verifyContentLength: true)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (
                attempt == 0 && exception.StatusCode is null && !cancellationToken.IsCancellationRequested)
            {
                firstReadFailure = exception;
            }
            catch (IOException exception) when (
                attempt == 0 && !cancellationToken.IsCancellationRequested)
            {
                firstReadFailure = exception;
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                var timeout = new TimeoutException(AppLocalization.Get("读取市场封面缩略图超时。"), exception);
                if (firstReadFailure is not null)
                {
                    throw new AggregateException(AppLocalization.Get("读取封面缩略图失败并超时；已保留首次传输错误。"), firstReadFailure, timeout);
                }

                throw timeout;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (firstReadFailure is not null)
            {
                throw new AggregateException(AppLocalization.Get("读取封面缩略图重试后仍失败；已保留两次错误。"), firstReadFailure, exception);
            }
        }

        throw new InvalidOperationException(AppLocalization.Get("市场封面缩略图读取超过允许的尝试次数。"));
    }

    public static Uri GetDetailUri(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        return ValidateSameOrigin(raw, "皮肤详情页");
    }

    public async Task<SkinPackage> DownloadPackageAsync(
        SkinMarketItem item,
        Version managerVersion,
        IProgress<SkinMarketDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(managerVersion);
        ThrowIfDisposed();

        if (item.Size is <= 0 or > MaxDownloadBytes)
        {
            throw new InvalidDataException(AppLocalization.Get("市场皮肤包大小无效或超过 64 MB 上限。"));
        }

        if (!Sha256Pattern().IsMatch(item.Sha256))
        {
            throw new InvalidDataException(AppLocalization.Get("市场目录中的 SHA-256 校验值格式无效。"));
        }

        if (item.SkinFormatVersion != 1)
        {
            throw new InvalidDataException(AppLocalization.Format("该皮肤使用不受支持的格式版本 {0}。", item.SkinFormatVersion));
        }

        if (!IsManagerCompatible(item.MinimumManagerVersion, managerVersion, out var compatibilityMessage))
        {
            throw new InvalidDataException(compatibilityMessage);
        }

        var uri = ValidateDownloadUri(item.DownloadUrl, item.Id);
        using var operationCancellation = CreateOperationCancellation(cancellationToken, DownloadTimeout);
        string? temporaryPath = null;
        try
        {
            using var response = await SendAsync(uri, operationCancellation.Token).ConfigureAwait(false);
            Directory.CreateDirectory(_temporaryDirectory);
            var packagePath = Path.Combine(_temporaryDirectory, $"SkinManager-market-{Guid.NewGuid():N}.zip");
            temporaryPath = packagePath;
            var actualHash = await CopyBoundedToFileAsync(
                response,
                packagePath,
                MaxDownloadBytes,
                item.Size,
                progress,
                operationCancellation.Token).ConfigureAwait(false);
            var expectedHash = Convert.FromHexString(item.Sha256);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            {
                throw new InvalidDataException(AppLocalization.Get("下载文件的 SHA-256 与市场目录不一致。请刷新目录后重试。"));
            }

            var package = await Task.Run(() => SkinPackageReader.ReadZipFile(packagePath), operationCancellation.Token)
                .ConfigureAwait(false);
            if (!package.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(AppLocalization.Get("皮肤包 manifest ID 与市场目录中的 ID 不一致。"));
            }

            if (!package.Version.Equals(item.Version, StringComparison.Ordinal))
            {
                throw new InvalidDataException(AppLocalization.Get("皮肤包 manifest 版本与市场目录中的版本不一致。"));
            }

            if (package.Definition.FormatVersion != item.SkinFormatVersion)
            {
                throw new InvalidDataException(AppLocalization.Get("皮肤包 manifest 格式版本与市场目录不一致。"));
            }

            return package;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(AppLocalization.Get("下载市场皮肤超时，请重试。"), exception);
        }
        finally
        {
            if (temporaryPath is not null && File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static bool IsManagerCompatible(string minimumVersionText, Version managerVersion, out string message)
    {
        ArgumentNullException.ThrowIfNull(managerVersion);
        if (!Version.TryParse(minimumVersionText, out var minimumVersion) || minimumVersion.Major < 0 || minimumVersion.Minor < 0)
        {
            message = AppLocalization.Get("市场条目的最低管理器版本无法识别，无法安全安装。");
            return false;
        }

        if (managerVersion < minimumVersion)
        {
            message = AppLocalization.Format("需要管理器 {0} 或更新版本。", minimumVersion);
            return false;
        }

        message = AppLocalization.Format("兼容管理器 {0}。", managerVersion.ToString(3));
        return true;
    }

    internal static string MinimumManagerVersionForPackage(string minimumVersionText, IEnumerable<string> packagePaths)
    {
        ArgumentNullException.ThrowIfNull(packagePaths);
        if (!packagePaths.Any(path => Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase)))
        {
            return minimumVersionText;
        }

        if (Version.TryParse(minimumVersionText, out var minimumVersion) && minimumVersion >= new Version(1, 1, 4))
        {
            return minimumVersionText;
        }

        return GifMinimumManagerVersion;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ClearAccount();
        _disposed = true;
        _httpClient.Dispose();
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        uri = MarketLanguageUri.ForPublicRest(uri);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Version = HttpVersion.Version20;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var session = CaptureCurrentSession();
        if (session.CookieHeader is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);
        }

        if (session.Nonce is not null)
        {
            request.Headers.TryAddWithoutValidation("X-WP-Nonce", session.Nonce);
        }

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            response.Dispose();
            throw new InvalidDataException(AppLocalization.Get("市场服务器返回了重定向；为保护下载地址，管理器不会跟随该地址。"));
        }

        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                var errorBody = await ReadBoundedAsync(response, MaxCatalogBytes, cancellationToken)
                    .ConfigureAwait(false);
                throw CreateHttpException(response.StatusCode, errorBody, session);
            }
        }

        return response;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        long maxBytes,
        CancellationToken cancellationToken,
        bool verifyContentLength = false)
    {
        var declaredLength = ValidateResponseLength(response, maxBytes, expectedSize: null);

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream((int)Math.Min(maxBytes, 1024 * 1024));
        var buffer = new byte[32 * 1024];
        long received = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            received = checked(received + read);
            if (received > maxBytes)
            {
                throw new InvalidDataException(AppLocalization.Get("服务器返回的内容超过允许大小。"));
            }

            output.Write(buffer, 0, read);
        }

        if (verifyContentLength && declaredLength.HasValue && received != declaredLength.Value)
        {
            throw new IncompleteResponseException(declaredLength.Value, received);
        }

        return output.ToArray();
    }

    private static async Task<byte[]> CopyBoundedToFileAsync(
        HttpResponseMessage response,
        string destinationPath,
        long maxBytes,
        long expectedSize,
        IProgress<SkinMarketDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var declaredLength = ValidateResponseLength(response, maxBytes, expectedSize);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[32 * 1024];
        long received = 0;
        long nextProgressAt = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            received = checked(received + read);
            if (received > maxBytes || received > expectedSize)
            {
                throw new InvalidDataException(AppLocalization.Get("下载内容超过允许大小或市场目录声明的大小。"));
            }

            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (progress is not null && received >= nextProgressAt)
            {
                progress.Report(new SkinMarketDownloadProgress(received, expectedSize));
                nextProgressAt = received + 256 * 1024;
            }
        }

        if (received != expectedSize || (declaredLength.HasValue && received != declaredLength.Value))
        {
            throw new InvalidDataException(AppLocalization.Get("下载内容大小与市场目录声明不一致。"));
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(new SkinMarketDownloadProgress(received, expectedSize));
        return hash.GetHashAndReset();
    }

    private static long? ValidateResponseLength(HttpResponseMessage response, long maxBytes, long? expectedSize)
    {
        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength is < 0 || declaredLength > maxBytes ||
            (expectedSize.HasValue && declaredLength.HasValue && declaredLength.Value != expectedSize.Value))
        {
            throw new InvalidDataException(AppLocalization.Get("服务器声明的文件大小无效或与市场目录中的大小不一致。"));
        }

        return declaredLength;
    }

    private static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    };

    private static CancellationTokenSource CreateOperationCancellation(CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout ?? RequestTimeout);
        return source;
    }

    private static SkinMarketItem ReadItem(JsonElement element)
    {
        var id = RequiredString(element, "id", 64);
        if (!SkinIdPattern().IsMatch(id))
        {
            throw new InvalidDataException(AppLocalization.Get("市场目录包含无效的皮肤 ID。"));
        }

        var appearance = RequiredString(element, "appearance", 16);
        if (appearance is not ("light" or "dark" or "system"))
        {
            throw new InvalidDataException(AppLocalization.Format("皮肤“{0}”声明了无效的外观类型。", id));
        }

        return new SkinMarketItem(
            id,
            RequiredString(element, "name", 120),
            RequiredString(element, "version", 64),
            appearance,
            OptionalString(element, "description", 2000),
            OptionalString(element, "author", 160),
            OptionalString(element, "licenseNotes", 1000),
            OptionalNullableString(element, "previewUrl", 2048),
            RequiredString(element, "downloadUrl", 2048),
            RequiredString(element, "sha256", 64),
            RequiredInt64(element, "size"),
            RequiredInt32(element, "skinFormatVersion"),
            RequiredString(element, "minManagerVersion", 64),
            OptionalString(element, "updatedAt", 64),
            OptionalNullableString(element, "thumbnailUrl", 2048),
            OptionalString(element, "fullDescription", 20_000),
            OptionalString(element, "submitter", 160),
            ReadTags(element, "tags"),
            OptionalNullableString(element, "detailUrl", 2048),
            OptionalInt32(element, "likeCount"),
            OptionalInt32(element, "commentCount"),
            OptionalBoolean(element, "likedByMe"));
    }

    private static IReadOnlyList<SkinMarketTag> ReadTags(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<SkinMarketTag>();
        }

        if (property.ValueKind != JsonValueKind.Array || property.GetArrayLength() > 50)
        {
            throw new InvalidDataException(AppLocalization.Format("市场目录中的 {0} 格式无效。", propertyName));
        }

        var tags = new List<SkinMarketTag>(property.GetArrayLength());
        foreach (var tag in property.EnumerateArray())
        {
            if (tag.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(AppLocalization.Format("市场目录中的 {0} 项格式无效。", propertyName));
            }

            var id = OptionalTagId(tag, "id");
            var name = OptionalString(tag, "name", 80);
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidDataException(AppLocalization.Format("市场目录中的 {0} 项缺少有效的 id 或 name。", propertyName));
            }

            tags.Add(new SkinMarketTag(id, name));
        }

        return tags;
    }

    private static string? OptionalTagId(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number when property.TryGetInt64(out var value) && value >= 0 => value.ToString(CultureInfo.InvariantCulture),
            _ => throw new InvalidDataException(AppLocalization.Format("市场目录中的标签 {0} 格式无效。", name))
        };
    }

    private static int OptionalInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value) || value < 0)
        {
            throw new InvalidDataException(AppLocalization.Format("市场目录中的 {0} 必须是非负整数。", name));
        }

        return value;
    }

    private static bool OptionalBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException(AppLocalization.Format("市场目录中的 {0} 必须是布尔值。", name))
        };
    }

    private static Uri ValidateDownloadUri(string raw, string skinId)
    {
        var uri = ValidateSameOrigin(raw, "皮肤包");
        var route = GetQueryValue(uri, "rest_route");
        var expectedRoute = $"/skin-manager/v1/skins/{skinId}/download";
        if (uri.AbsolutePath != "/" || !string.Equals(route, expectedRoute, StringComparison.Ordinal))
        {
            throw new InvalidDataException(AppLocalization.Get("市场皮肤包地址与当前条目不匹配。"));
        }

        return uri;
    }

    private static Uri ValidateSameOrigin(string raw, string purpose)
    {
        Uri? uri = null;
        if (Uri.TryCreate(raw, UriKind.Absolute, out var absoluteUri))
        {
            uri = absoluteUri;
        }
        else if (Uri.TryCreate(SiteRoot, raw, out var relativeUri))
        {
            uri = relativeUri;
        }

        if (uri is null)
        {
            throw new InvalidDataException(AppLocalization.Format("市场{0}地址无效。", AppLocalization.Get(purpose)));
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals(SiteRoot.Host, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != SiteRoot.Port || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException(AppLocalization.Format(
                "市场{0}地址必须来自 {1} 的 HTTPS 同源地址。", AppLocalization.Get(purpose), SiteRoot.Host));
        }

        return uri;
    }

    private static string? GetQueryValue(Uri uri, string name)
    {
        string? result = null;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = separator < 0 ? part : part[..separator];
            if (!Uri.UnescapeDataString(key).Equals(name, StringComparison.Ordinal))
            {
                continue;
            }

            if (result is not null)
            {
                return null;
            }

            var value = separator < 0 ? string.Empty : part[(separator + 1)..];
            result = Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return result;
    }

    private static string RequiredString(JsonElement element, string name, int maxLength)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(AppLocalization.Format("市场目录缺少有效的 {0}。", name));
        }

        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            throw new InvalidDataException(AppLocalization.Format("市场目录中的 {0} 为空或过长。", name));
        }

        return value;
    }

    private static string OptionalString(JsonElement element, string name, int maxLength)
    {
        var value = OptionalNullableString(element, name, maxLength);
        return value ?? string.Empty;
    }

    private static string? OptionalNullableString(JsonElement element, string name, int maxLength)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(AppLocalization.Format("市场目录中的 {0} 格式无效。", name));
        }

        var value = property.GetString() ?? string.Empty;
        if (value.Length > maxLength)
        {
            throw new InvalidDataException(AppLocalization.Format("市场目录中的 {0} 过长。", name));
        }

        return value;
    }

    private static int RequiredInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value))
        {
            throw new InvalidDataException(AppLocalization.Format("市场目录缺少有效的 {0}。", name));
        }

        return value;
    }

    private static long RequiredInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var value))
        {
            throw new InvalidDataException(AppLocalization.Format("市场目录缺少有效的 {0}。", name));
        }

        return value;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SkinIdPattern();

    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    private sealed class IncompleteResponseException(long declaredBytes, long receivedBytes)
        : IOException(AppLocalization.Format(
            "服务器声明内容长度为 {0} bytes，实际只读取到 {1} bytes。", declaredBytes, receivedBytes));
}
