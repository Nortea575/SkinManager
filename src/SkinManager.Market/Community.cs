using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkinManager.Core;

namespace SkinManager.Market;

public sealed partial class SkinMarketClient
{
    private const int MaxCommunityResponseBytes = 1024 * 1024;
    private const int MaxBootstrapBytes = 4 * 1024 * 1024;
    private const int MaxCookieHeaderLength = 16 * 1024;
    private const int CommentsPerPage = 20;
    private static readonly Uri DefaultMarketUri = new("https://blog.ancimen.top/?page_id=39");
    private static readonly Uri DefaultLoginUri = CreateDefaultLoginUri();
    private static readonly Uri DefaultRegistrationUri = new("https://blog.ancimen.top/wp-login.php?action=register");

    private readonly object _sessionLock = new();
    private string? _cookieHeader;
    private string? _restNonce;
    private SkinMarketAccount? _account;
    private int _sessionGeneration;

    public SkinMarketAccount? Account
    {
        get
        {
            lock (_sessionLock)
            {
                return _account;
            }
        }
    }

    public Uri LoginUri { get; private set; } = DefaultLoginUri;

    public Uri RegistrationUri => DefaultRegistrationUri;

    /// <summary>
    /// Reads the market page configuration using the supplied WebView cookie header.
    /// Cookie and REST nonce are kept only in memory for subsequent same-origin requests.
    /// </summary>
    public async Task<SkinMarketAccount?> RefreshAccountAsync(
        string? cookieHeader,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            ClearAccount();
            return null;
        }

        var normalizedCookie = ValidateCookieHeader(cookieHeader);
        var generation = BeginAccountRefresh();
        using var operationCancellation = CreateOperationCancellation(cancellationToken);
        try
        {
            var html = await GetBootstrapHtmlAsync(normalizedCookie, operationCancellation.Token).ConfigureAwait(false);
            var config = ReadBootstrapConfig(html);
            var loginUri = ValidateSameOrigin(config.LoginUrl, "登录页");

            if (!config.IsLoggedIn || config.UserId <= 0)
            {
                ApplyAnonymousBootstrap(generation, loginUri);
                return null;
            }

            if (string.IsNullOrWhiteSpace(config.Nonce) || config.Nonce.Length > 128)
            {
                throw new SkinMarketReauthenticationException(HttpStatusCode.Forbidden);
            }

            var displayName = await TryGetDisplayNameAsync(
                config.UserId,
                normalizedCookie,
                config.Nonce,
                operationCancellation.Token).ConfigureAwait(false);
            var account = new SkinMarketAccount(
                config.UserId,
                string.IsNullOrWhiteSpace(displayName) ? $"用户 {config.UserId.ToString(CultureInfo.InvariantCulture)}" : displayName,
                config.CanSubmit);

            return ApplyAccount(generation, normalizedCookie, config.Nonce, loginUri, account)
                ? account
                : Account;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("刷新市场账号状态超时，请重试。", exception);
        }
    }

    public void ClearAccount()
    {
        lock (_sessionLock)
        {
            _sessionGeneration++;
            _cookieHeader = null;
            _restNonce = null;
            _account = null;
        }
    }

    public async Task<SkinMarketCommentsPage> GetCommentsAsync(
        string skinId,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateSkinId(skinId);
        if (page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(page));
        }

        var session = CaptureSession();
        using var document = await SendCommunityJsonAsync(
            HttpMethod.Get,
            $"/skin-manager/v1/skins/{skinId}/comments",
            new Dictionary<string, string>
            {
                ["page"] = page.ToString(CultureInfo.InvariantCulture),
                ["per_page"] = CommentsPerPage.ToString(CultureInfo.InvariantCulture)
            },
            content: null,
            session,
            cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var itemsElement = RequiredArray(root, "items");
        var pagination = ReadPagination(root, page, CommentsPerPage, itemsElement.GetArrayLength());
        var comments = new List<SkinMarketComment>(itemsElement.GetArrayLength());
        foreach (var element in itemsElement.EnumerateArray())
        {
            comments.Add(ReadComment(element));
        }

        return new SkinMarketCommentsPage(comments, pagination);
    }

    public async Task<SkinMarketLikeResult> SetLikeAsync(
        string skinId,
        bool liked,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateSkinId(skinId);
        var session = CaptureRequiredSession();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { liked });
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var document = await SendCommunityJsonAsync(
            HttpMethod.Post,
            $"/skin-manager/v1/skins/{skinId}/like",
            query: null,
            content,
            session,
            cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var actualLiked = RequiredBoolean(root, "liked");
        var likeCount = RequiredNonNegativeInt32(root, "likeCount");
        return new SkinMarketLikeResult(actualLiked, likeCount);
    }

    public async Task<SkinMarketCommentResult> CreateCommentAsync(
        string skinId,
        string content,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateSkinId(skinId);
        ArgumentNullException.ThrowIfNull(content);
        var normalizedContent = content.Trim();
        var contentLength = CountUnicodeCharacters(normalizedContent);
        if (contentLength == 0)
        {
            throw new ArgumentException("评论内容不能为空。", nameof(content));
        }

        if (contentLength > 2000)
        {
            throw new ArgumentException("评论内容不能超过 2000 个字符。", nameof(content));
        }

        var session = CaptureRequiredSession();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { content = normalizedContent });
        using var requestContent = new ByteArrayContent(payload);
        requestContent.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var document = await SendCommunityJsonAsync(
            HttpMethod.Post,
            $"/skin-manager/v1/skins/{skinId}/comments",
            query: null,
            requestContent,
            session,
            cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var comment = ReadComment(RequiredObject(root, "comment"));
        var pending = RequiredBoolean(root, "pending");
        var message = CommunityOptionalString(root, "message", 200) ?? (pending ? "评论已提交，审核后显示" : "评论已发布");
        return new SkinMarketCommentResult(comment, pending, message);
    }

    public async Task<SkinMarketSubmissionResult> SubmitSkinAsync(
        SkinMarketSubmissionDraft draft,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(draft);
        var session = CaptureRequiredSession();
        if (session.Account?.CanSubmit != true)
        {
            throw new UnauthorizedAccessException("当前账号没有皮肤投稿权限。");
        }

        ValidateSubmissionFields(draft);

        var packagePath = Path.GetFullPath(draft.PackagePath);
        var fileInfo = new FileInfo(packagePath);
        if (!fileInfo.Exists || fileInfo.Length <= 0 || fileInfo.Length > SkinPackageReader.MaxArchiveBytes)
        {
            throw new InvalidDataException("ZIP 文件必须存在且大小在 1 字节到 64 MB 之间。");
        }

        using var operationCancellation = CreateOperationCancellation(cancellationToken, DownloadTimeout);
        byte[] packageBytes;
        try
        {
            packageBytes = await File.ReadAllBytesAsync(packagePath, operationCancellation.Token).ConfigureAwait(false);
            if (packageBytes.LongLength == 0 || packageBytes.LongLength > SkinPackageReader.MaxArchiveBytes)
            {
                throw new InvalidDataException("ZIP 文件必须存在且大小在 1 字节到 64 MB 之间。");
            }

            _ = SkinPackageReader.ReadZipBytes(packageBytes);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("读取并校验待投稿 ZIP 超时。", exception);
        }

        using var multipart = new MultipartFormDataContent();
        var packageContent = new ByteArrayContent(packageBytes);
        packageContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        multipart.Add(packageContent, "package", Path.GetFileName(packagePath));
        multipart.Add(new StringContent(draft.Description, Encoding.UTF8), "description");
        multipart.Add(new StringContent(draft.Author, Encoding.UTF8), "author");
        multipart.Add(new StringContent(draft.LicenseNotes, Encoding.UTF8), "licenseNotes");
        multipart.Add(new StringContent(draft.MinimumManagerVersion, Encoding.UTF8), "minManagerVersion");
        multipart.Add(new StringContent(JsonSerializer.Serialize(draft.Tags), Encoding.UTF8), "tags");

        using var document = await SendCommunityJsonAsync(
            HttpMethod.Post,
            "/skin-manager/v1/submissions",
            query: null,
            multipart,
            session,
            cancellationToken,
            timeout: DownloadTimeout).ConfigureAwait(false);
        var item = RequiredObject(document.RootElement, "item");
        var status = CommunityRequiredString(item, "status", 24);
        var message = status switch
        {
            "pending" => "投稿已提交，等待管理员审核。",
            "publish" => "皮肤已通过审核并上架。",
            "draft" => "投稿已保存为草稿。",
            _ => $"投稿状态：{status}"
        };
        return new SkinMarketSubmissionResult(
            RequiredPositiveInt32(item, "postId"),
            CommunityRequiredString(item, "id", 64),
            CommunityRequiredString(item, "name", 120),
            status,
            message);
    }

    private async Task<string> GetBootstrapHtmlAsync(string cookieHeader, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, DefaultMarketUri);
        request.Version = HttpVersion.Version20;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new InvalidDataException("市场登录状态读取遇到重定向；为保护账号会话，管理器不会跟随该地址。");
        }

        var bytes = await ReadBoundedAsync(response, MaxBootstrapBytes, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpException(response.StatusCode, bytes, null);
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("市场账号页面不是有效的 UTF-8 文本。", exception);
        }
    }

    private async Task<string?> TryGetDisplayNameAsync(
        int userId,
        string cookieHeader,
        string nonce,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await SendCommunityJsonAsync(
                HttpMethod.Get,
                "/wp/v2/users/me",
                query: null,
                content: null,
                new AccountSession(cookieHeader, nonce, null),
                cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (RequiredPositiveInt32(root, "id") != userId)
            {
                return null;
            }

            return CommunityOptionalString(root, "name", 160);
        }
        catch (SkinMarketReauthenticationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or InvalidDataException or JsonException or TimeoutException)
        {
            return null;
        }
    }

    private async Task<JsonDocument> SendCommunityJsonAsync(
        HttpMethod method,
        string route,
        IReadOnlyDictionary<string, string>? query,
        HttpContent? content,
        AccountSession session,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var uri = BuildRestUri(route, query);
        using var operationCancellation = CreateOperationCancellation(cancellationToken, timeout);
        try
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (session.CookieHeader is not null)
            {
                request.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);
            }

            if (session.Nonce is not null)
            {
                request.Headers.TryAddWithoutValidation("X-WP-Nonce", session.Nonce);
            }

            request.Content = content;
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                operationCancellation.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                throw new InvalidDataException("市场服务器返回了重定向；管理器不会跟随该地址。");
            }

            var bytes = await ReadBoundedAsync(response, MaxCommunityResponseBytes, operationCancellation.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw CreateHttpException(response.StatusCode, bytes, session);
            }

            try
            {
                return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("市场服务器返回的社区数据不是有效 JSON。", exception);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("市场社区请求超时，请重试。", exception);
        }
    }

    private AccountSession CaptureSession()
    {
        lock (_sessionLock)
        {
            return new AccountSession(_cookieHeader, _restNonce, _account);
        }
    }

    private AccountSession CaptureRequiredSession()
    {
        lock (_sessionLock)
        {
            if (_account is null || _cookieHeader is null || _restNonce is null)
            {
                throw new InvalidOperationException("请先登录市场账号后再操作。");
            }

            return new AccountSession(_cookieHeader, _restNonce, _account);
        }
    }

    private AccountSession CaptureCurrentSession() => CaptureSession();

    private int BeginAccountRefresh()
    {
        lock (_sessionLock)
        {
            _sessionGeneration++;
            _cookieHeader = null;
            _restNonce = null;
            _account = null;
            return _sessionGeneration;
        }
    }

    private void ApplyAnonymousBootstrap(int generation, Uri loginUri)
    {
        lock (_sessionLock)
        {
            if (_sessionGeneration == generation)
            {
                LoginUri = loginUri;
            }
        }
    }

    private bool ApplyAccount(int generation, string cookieHeader, string nonce, Uri loginUri, SkinMarketAccount account)
    {
        lock (_sessionLock)
        {
            if (_sessionGeneration != generation)
            {
                return false;
            }

            _cookieHeader = cookieHeader;
            _restNonce = nonce;
            _account = account;
            LoginUri = loginUri;
            return true;
        }
    }

    private void InvalidateSession(AccountSession session)
    {
        if (session.CookieHeader is null || session.Nonce is null)
        {
            return;
        }

        lock (_sessionLock)
        {
            if (string.Equals(_cookieHeader, session.CookieHeader, StringComparison.Ordinal) &&
                string.Equals(_restNonce, session.Nonce, StringComparison.Ordinal))
            {
                _sessionGeneration++;
                _cookieHeader = null;
                _restNonce = null;
                _account = null;
            }
        }
    }

    private HttpRequestException CreateHttpException(HttpStatusCode statusCode, byte[] body, AccountSession? session)
    {
        string? serverMessage = null;
        string? code = null;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 12 });
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                serverMessage = CommunityOptionalString(document.RootElement, "message", 500);
                code = CommunityOptionalString(document.RootElement, "code", 100);
            }
        }
        catch (JsonException)
        {
            // Error pages may be HTML; keep the status without exposing their contents.
        }

        var isAuthenticationFailure = statusCode == HttpStatusCode.Unauthorized ||
            ((int)statusCode == 403 && code is "rest_cookie_invalid_nonce" or "rest_not_logged_in" or "smm_authentication_required");
        if (isAuthenticationFailure)
        {
            if (session.HasValue)
            {
                InvalidateSession(session.Value);
            }

            return new SkinMarketReauthenticationException(statusCode);
        }

        var message = string.IsNullOrWhiteSpace(serverMessage)
            ? $"市场请求失败（HTTP {(int)statusCode}）。"
            : $"市场请求失败（HTTP {(int)statusCode}）：{serverMessage}";
        return new HttpRequestException(message, null, statusCode);
    }

    private static Uri BuildRestUri(string route, IReadOnlyDictionary<string, string>? query)
    {
        if (string.IsNullOrWhiteSpace(route) || !route.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("市场 API 路由无效。", nameof(route));
        }

        var parts = new List<string> { $"rest_route={Uri.EscapeDataString(route)}" };
        if (query is not null)
        {
            parts.AddRange(query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        }

        return new Uri(SiteRoot, "/?" + string.Join('&', parts));
    }

    private static string ValidateCookieHeader(string cookieHeader)
    {
        var value = cookieHeader.Trim();
        if (value.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["Cookie:".Length..].Trim();
        }

        if (value.Length == 0 || value.Length > MaxCookieHeaderLength ||
            value.Any(character => char.IsControl(character)))
        {
            throw new ArgumentException("市场账号 Cookie 格式无效。", nameof(cookieHeader));
        }

        return value;
    }

    private static SkinMarketBootstrapConfig ReadBootstrapConfig(string html)
    {
        var match = BootstrapConfigPattern().Match(html);
        if (!match.Success)
        {
            throw new InvalidDataException("市场页面未提供可读取的账号配置，请检查登录状态后重试。");
        }

        var json = WebUtility.HtmlDecode(match.Groups["config"].Value);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("市场账号配置格式无效。");
            }

            return new SkinMarketBootstrapConfig(
                RequiredBoolean(root, "isLoggedIn"),
                CommunityRequiredInt32(root, "userId"),
                CommunityOptionalBoolean(root, "canSubmit"),
                CommunityOptionalString(root, "nonce", 128) ?? string.Empty,
                CommunityRequiredString(root, "loginUrl", 2048));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("市场账号配置不是有效 JSON。", exception);
        }
    }

    private static SkinMarketComment ReadComment(JsonElement element)
    {
        return new SkinMarketComment(
            RequiredPositiveInt32(element, "id"),
            CommunityRequiredString(element, "author", 160),
            CommunityRequiredString(element, "content", 2000),
            CommunityRequiredString(element, "date", 64));
    }

    private static SkinMarketPagination ReadPagination(JsonElement root, int requestedPage, int perPage, int itemCount)
    {
        var element = RequiredObject(root, "pagination");
        var pagination = new SkinMarketPagination(
            RequiredPositiveInt32(element, "page"),
            RequiredPositiveInt32(element, "perPage"),
            RequiredNonNegativeInt32(element, "total"),
            RequiredNonNegativeInt32(element, "totalPages"));
        if (pagination.Page != requestedPage || pagination.PerPage != perPage || itemCount > perPage)
        {
            throw new InvalidDataException("市场评论分页信息无效。");
        }

        return pagination;
    }

    private static void ValidateSkinId(string skinId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skinId);
        if (skinId.Length > 64 || !SkinIdPattern().IsMatch(skinId))
        {
            throw new ArgumentException("皮肤 ID 格式无效。", nameof(skinId));
        }
    }

    private static void ValidateSubmissionFields(SkinMarketSubmissionDraft draft)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.PackagePath);
        ValidateLength(draft.Description, 5000, "皮肤介绍", allowEmpty: true);
        ValidateLength(draft.Author, 2000, "作者说明", allowEmpty: true);
        ValidateLength(draft.LicenseNotes, 3000, "版权与素材授权说明", allowEmpty: true);
        ValidateLength(draft.MinimumManagerVersion, 40, "最低管理器版本", allowEmpty: true);
        ArgumentNullException.ThrowIfNull(draft.Tags);
        if (draft.Tags.Count > 12)
        {
            throw new ArgumentException("标签最多填写 12 个。", nameof(draft));
        }

        foreach (var tag in draft.Tags)
        {
            ValidateLength(tag, 40, "标签", allowEmpty: true);
        }
    }

    private static void ValidateLength(string value, int maxLength, string fieldName, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(value);
        if ((!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Length > maxLength)
        {
            throw new ArgumentException($"{fieldName}不能超过 {maxLength} 个字符。", fieldName);
        }
    }

    private static int CountUnicodeCharacters(string value)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    private static Uri CreateDefaultLoginUri() => new(
        "https://blog.ancimen.top/wp-login.php?redirect_to=" + Uri.EscapeDataString(DefaultMarketUri.AbsoluteUri));

    private static JsonElement RequiredObject(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"市场响应缺少有效的 {name} 对象。");
        }

        return property;
    }

    private static JsonElement RequiredArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"市场响应缺少有效的 {name} 列表。");
        }

        return property;
    }

    private static string CommunityRequiredString(JsonElement element, string name, int maxLength)
    {
        var value = CommunityOptionalString(element, name, maxLength);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"市场响应缺少有效的 {name}。");
        }

        return value;
    }

    private static string? CommunityOptionalString(JsonElement element, string name, int maxLength)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"市场响应中的 {name} 格式无效。");
        }

        var value = property.GetString() ?? string.Empty;
        if (value.Length > maxLength)
        {
            throw new InvalidDataException($"市场响应中的 {name} 过长。");
        }

        return value;
    }

    private static int CommunityRequiredInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var value))
        {
            throw new InvalidDataException($"市场响应缺少有效的 {name}。");
        }

        return value;
    }

    private static int RequiredPositiveInt32(JsonElement element, string name)
    {
        var value = CommunityRequiredInt32(element, name);
        if (value <= 0)
        {
            throw new InvalidDataException($"市场响应中的 {name} 必须是正整数。");
        }

        return value;
    }

    private static int RequiredNonNegativeInt32(JsonElement element, string name)
    {
        var value = CommunityRequiredInt32(element, name);
        if (value < 0)
        {
            throw new InvalidDataException($"市场响应中的 {name} 必须是非负整数。");
        }

        return value;
    }

    private static bool RequiredBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"市场响应缺少有效的 {name} 布尔值。");
        }

        return property.GetBoolean();
    }

    private static bool CommunityOptionalBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"市场响应中的 {name} 必须是布尔值。");
        }

        return property.GetBoolean();
    }

    private readonly record struct AccountSession(string? CookieHeader, string? Nonce, SkinMarketAccount? Account);

    private sealed record SkinMarketBootstrapConfig(bool IsLoggedIn, int UserId, bool CanSubmit, string Nonce, string LoginUrl);

    [GeneratedRegex("<section\\b[^>]*\\bdata-smm-config\\s*=\\s*([\"'])(?<config>.*?)\\1", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex BootstrapConfigPattern();
}

public sealed record SkinMarketAccount(int UserId, string DisplayName, bool CanSubmit);

public sealed record SkinMarketComment(int Id, string Author, string Content, string Date);

public sealed record SkinMarketCommentsPage(IReadOnlyList<SkinMarketComment> Items, SkinMarketPagination Pagination);

public sealed record SkinMarketLikeResult(bool Liked, int LikeCount);

public sealed record SkinMarketCommentResult(SkinMarketComment Comment, bool Pending, string Message);

public sealed record SkinMarketSubmissionDraft(
    string PackagePath,
    string Description,
    string Author,
    string LicenseNotes,
    string MinimumManagerVersion,
    IReadOnlyList<string> Tags);

public sealed record SkinMarketSubmissionResult(int PostId, string SkinId, string Name, string Status, string Message);

public sealed class SkinMarketReauthenticationException(HttpStatusCode statusCode)
    : HttpRequestException("市场登录状态或安全验证已失效，请重新登录后重试。", null, statusCode);
