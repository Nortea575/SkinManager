using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SkinManager.Core;
using SkinManager.Market;

namespace SkinManager.Market.Tests;

internal static partial class Program
{
    private const string TestCookie = "wordpress_logged_in_test=opaque-session-value";
    private const string TestNonce = "nonce-for-offline-test";

    private static async Task CommunitySessionAndActionsAsync(
        SkinPackage package,
        byte[] zipBytes,
        string submittedMinimumVersion = "1.1.0",
        string expectedMinimumVersion = "1.1.0")
    {
        var requests = new List<ObservedRequest>();
        string? temporaryZip = null;
        byte[]? uploadedBody = null;
        try
        {
            temporaryZip = Path.Combine(Path.GetTempPath(), $"SkinManager-market-test-{Guid.NewGuid():N}.zip");
            await File.WriteAllBytesAsync(temporaryZip, zipBytes);
            using var client = new SkinMarketClient(new StubHandler(request =>
            {
                var observed = ObserveRequest(request);
                requests.Add(observed);
                if (observed.Query.Contains("page_id=39", StringComparison.Ordinal))
                {
                    return HtmlResponse(BootstrapHtml(isLoggedIn: true, userId: 42, canSubmit: true));
                }

                if (observed.Query.Contains("/wp/v2/users/me", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse("{\"id\":42,\"name\":\"离线测试用户\"}");
                }

                if (observed.Query.Contains("/skin-manager/v1/skins", StringComparison.Ordinal) &&
                    request.Method == HttpMethod.Get &&
                    !observed.Query.Contains("/comments", StringComparison.Ordinal))
                {
                    var item = CreateItem(package, zipBytes);
                    return JsonResponse(CreateCatalogJson(item, page: 1, perPage: 12, total: 1, totalPages: 1));
                }

                if (observed.Query.Contains("/comments", StringComparison.Ordinal))
                {
                    if (request.Method == HttpMethod.Get)
                    {
                        return JsonResponse("""
                            {"items":[{"id":7,"author":"测试用户","content":"公开评论","date":"2026-11-10T09:00:00"}],
                             "pagination":{"page":2,"perPage":20,"total":21,"totalPages":2}}
                            """);
                    }

                    return new HttpResponseMessage(HttpStatusCode.Created)
                    {
                        Content = new StringContent("""
                            {"comment":{"id":8,"author":"离线测试用户","content":"已整理的评论","date":"2026-11-10T10:00:00"},
                             "pending":true,"message":"评论已提交，审核后显示"}
                            """, Encoding.UTF8, "application/json")
                    };
                }

                if (observed.Query.Contains("/like", StringComparison.Ordinal))
                {
                    return JsonResponse("{\"liked\":false,\"likeCount\":6}");
                }

                if (observed.Query.Contains("/submissions", StringComparison.Ordinal))
                {
                    uploadedBody = observed.Body;
                    return JsonResponse(JsonSerializer.Serialize(new
                    {
                        item = new { postId = 987, status = "pending", id = package.Id, name = package.Name }
                    }));
                }

                throw new InvalidDataException($"未预期的离线市场请求：{observed.Method} {observed.Query}");
            }));

            var account = await client.RefreshAccountAsync(TestCookie);
            Check(account is { UserId: 42, DisplayName: "离线测试用户", CanSubmit: true }, "从 HTML bootstrap 与只读 users/me 读取账号信息");
            Check(client.LoginUri.Host == "blog.ancimen.top" && client.RegistrationUri.AbsolutePath == "/wp-login.php",
                "账号入口保持本站同源");
            Check(requests[0].Cookie == TestCookie && requests[0].Nonce is null, "bootstrap 请求只带 WebView cookie");

            _ = await client.GetPageAsync();
            var catalogRequest = requests.Single(request => request.Query.Contains("/skins", StringComparison.Ordinal) &&
                !request.Query.Contains("/comments", StringComparison.Ordinal) && !request.Query.Contains("/like", StringComparison.Ordinal));
            Check(catalogRequest.Cookie == TestCookie && catalogRequest.Nonce == TestNonce,
                "已有目录 GET 使用同一 cookie 与 REST nonce 返回 likedByMe");

            var comments = await client.GetCommentsAsync(package.Id, 2);
            Check(comments.Items.Single().Id == 7 && comments.Pagination is { Page: 2, PerPage: 20, Total: 21, TotalPages: 2 },
                "读取公开评论及真实分页结构");
            var like = await client.SetLikeAsync(package.Id, liked: false);
            Check(!like.Liked && like.LikeCount == 6, "按服务端结果取消点赞");
            var createdComment = await client.CreateCommentAsync(package.Id, "  已整理的评论  ");
            Check(createdComment.Pending && createdComment.Message == "评论已提交，审核后显示" &&
                  createdComment.Comment.Content == "已整理的评论", "保留评论待审核状态及服务端消息");

            var submission = await client.SubmitSkinAsync(new SkinMarketSubmissionDraft(
                temporaryZip,
                "离线投稿介绍",
                "Ancimen",
                "保留所有权利。",
                submittedMinimumVersion,
                ["轻量", "浅色"]));
            Check(submission is { PostId: 987, Status: "pending", SkinId: var id } && id == package.Id,
                "上传后采用服务器返回的待审核状态");
            var multipart = Encoding.UTF8.GetString(uploadedBody ?? Array.Empty<byte>());
            Check(ContainsMultipartField(multipart, "package"), "投稿 multipart 使用 package 文件字段");
            Check(uploadedBody is not null && uploadedBody.AsSpan().IndexOf(zipBytes) >= 0,
                "投稿 multipart 包含已验证 ZIP 字节");
            Check(ContainsMultipartField(multipart, "description") &&
                  ContainsMultipartField(multipart, "licenseNotes") &&
                  ContainsMultipartField(multipart, "minManagerVersion") &&
                  ContainsMultipartField(multipart, "tags"),
                "投稿 multipart 使用服务端字段名");
            Check(ContainsMultipartValue(multipart, "minManagerVersion", expectedMinimumVersion),
                "投稿 multipart 发送按皮肤包内容调整后的最低管理器版本");
            Check(multipart.Contains(JsonSerializer.Serialize(new[] { "轻量", "浅色" }), StringComparison.Ordinal),
                "投稿 multipart 标签为名称数组");

            var writeRequests = requests.Where(request => request.Method == HttpMethod.Post).ToArray();
            Check(writeRequests.Length == 3 && writeRequests.All(request => request.Cookie == TestCookie && request.Nonce == TestNonce),
                "点赞、评论和上传 POST 均带同一会话认证头");
            Check(writeRequests.Any(request => request.Query.Contains("/like", StringComparison.Ordinal) &&
                  request.BodyText.Contains("\"liked\":false", StringComparison.Ordinal)), "点赞开关使用布尔 liked 请求体");
        }
        finally
        {
            if (temporaryZip is not null && File.Exists(temporaryZip))
            {
                File.Delete(temporaryZip);
            }
        }
    }

    private static async Task AnonymousCommunityReadAndUnauthenticatedWritesAsync()
    {
        var requestCount = 0;
        using var client = new SkinMarketClient(new StubHandler(request =>
        {
            requestCount++;
            var observed = ObserveRequest(request);
            Check(observed.Cookie is null && observed.Nonce is null, "匿名评论 GET 不携带账号认证头");
            return JsonResponse("""
                {"items":[],"pagination":{"page":1,"perPage":20,"total":0,"totalPages":0}}
                """);
        }));

        Check(await client.RefreshAccountAsync(null) is null && client.Account is null && requestCount == 0,
            "无 cookie 时清除账号且不请求 bootstrap");
        var comments = await client.GetCommentsAsync("Ayanami");
        Check(comments.Items.Count == 0, "匿名浏览公开评论");

        await ThrowsAsync<InvalidOperationException>(
            () => client.SetLikeAsync("Ayanami", true), "未登录禁止点赞写请求");
        await ThrowsAsync<InvalidOperationException>(
            () => client.CreateCommentAsync("Ayanami", "测试"), "未登录禁止评论写请求");
        await ThrowsAsync<InvalidOperationException>(
            () => client.SubmitSkinAsync(new SkinMarketSubmissionDraft("missing.zip", "", "", "", "", Array.Empty<string>())),
            "未登录禁止上传写请求");
        Check(requestCount == 1, "未登录写操作不会触发 HTTP 请求");
    }

    private static async Task RejectInvalidNonceWithoutRetryAsync()
    {
        var likePosts = 0;
        using var client = new SkinMarketClient(new StubHandler(request =>
        {
            var observed = ObserveRequest(request);
            if (observed.Query.Contains("page_id=39", StringComparison.Ordinal))
            {
                return HtmlResponse(BootstrapHtml(isLoggedIn: true, userId: 42, canSubmit: true));
            }

            if (observed.Query.Contains("/wp/v2/users/me", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("{\"id\":42,\"name\":\"离线测试用户\"}");
            }

            if (request.Method == HttpMethod.Post && observed.Query.Contains("/like", StringComparison.Ordinal))
            {
                likePosts++;
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(
                        "{\"code\":\"rest_cookie_invalid_nonce\",\"message\":\"安全验证已失效\",\"data\":{\"status\":403}}",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            throw new InvalidDataException($"未预期的离线市场请求：{observed.Query}");
        }));

        await client.RefreshAccountAsync(TestCookie);
        await ThrowsAsync<SkinMarketReauthenticationException>(
            () => client.SetLikeAsync("Ayanami", true), "nonce 失效时返回重新登录异常");
        Check(likePosts == 1 && client.Account is null, "nonce 失效清除当前会话且不重放 POST");
    }

    private static async Task DirectoryNonceExpiryClearsAccountAsync()
    {
        var catalogRequests = 0;
        using var client = new SkinMarketClient(new StubHandler(request =>
        {
            var observed = ObserveRequest(request);
            if (observed.Query.Contains("page_id=39", StringComparison.Ordinal))
            {
                return HtmlResponse(BootstrapHtml(isLoggedIn: true, userId: 42, canSubmit: false));
            }

            if (observed.Query.Contains("/wp/v2/users/me", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("{\"id\":42,\"name\":\"离线测试用户\"}");
            }

            if (observed.Query.Contains("/skin-manager/v1/skins", StringComparison.Ordinal))
            {
                catalogRequests++;
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(
                        "{\"code\":\"rest_cookie_invalid_nonce\",\"message\":\"安全验证已失效\",\"data\":{\"status\":403}}",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            throw new InvalidDataException($"未预期的离线市场请求：{observed.Query}");
        }));

        await client.RefreshAccountAsync(TestCookie);
        await ThrowsAsync<SkinMarketReauthenticationException>(
            () => client.GetPageAsync(), "目录 GET nonce 失效时提示重新登录");
        Check(catalogRequests == 1 && client.Account is null && client.LoginUri.Host == "blog.ancimen.top",
            "目录 nonce 失效清除匹配会话、不重试目录并保留登录入口");
    }

    private static async Task RejectCrossOriginAccountConfigAsync()
    {
        var requests = 0;
        using var client = new SkinMarketClient(new StubHandler(_ =>
        {
            requests++;
            return HtmlResponse(BootstrapHtml(
                isLoggedIn: true,
                userId: 42,
                canSubmit: true,
                loginUrl: "https://evil.example/login"));
        }));

        await ThrowsAsync<InvalidDataException>(
            () => client.RefreshAccountAsync(TestCookie), "拒绝 bootstrap 中的跨源登录地址");
        Check(requests == 1 && client.Account is null, "跨源登录地址未被请求且没有建立会话");
    }

    private static async Task CancelAccountRefreshAsync()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new SkinMarketClient(new AsyncStubHandler(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return HtmlResponse(BootstrapHtml(isLoggedIn: true, userId: 42, canSubmit: true));
        }));
        using var cancellation = new CancellationTokenSource();
        var refresh = client.RefreshAccountAsync(TestCookie, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => refresh, "取消账号 bootstrap 请求");
        Check(client.Account is null, "取消账号刷新后不保留半成品会话");
    }

    private static async Task IgnoreLateAccountRefreshAsync()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBootstrap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new SkinMarketClient(new AsyncStubHandler(async (request, cancellationToken) =>
        {
            var observed = ObserveRequest(request);
            if (observed.Query.Contains("page_id=39", StringComparison.Ordinal))
            {
                started.TrySetResult();
                await releaseBootstrap.Task.WaitAsync(cancellationToken);
                return HtmlResponse(BootstrapHtml(isLoggedIn: true, userId: 42, canSubmit: true));
            }

            return JsonResponse("{\"id\":42,\"name\":\"离线测试用户\"}");
        }));

        var refresh = client.RefreshAccountAsync(TestCookie);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        client.ClearAccount();
        releaseBootstrap.SetResult();
        var result = await refresh;
        Check(result is null && client.Account is null, "ClearAccount 后的迟到 bootstrap 响应不能恢复旧用户");
    }

    private static string BootstrapHtml(
        bool isLoggedIn,
        int userId,
        bool canSubmit,
        string? loginUrl = null)
    {
        var config = JsonSerializer.Serialize(new
        {
            restBase = "/?rest_route=/skin-manager/v1/",
            nonce = isLoggedIn ? TestNonce : string.Empty,
            isLoggedIn,
            userId,
            canSubmit,
            loginUrl = loginUrl ?? "https://blog.ancimen.top/wp-login.php?redirect_to=https%3A%2F%2Fblog.ancimen.top%2F%3Fpage_id%3D39",
            marketUrl = "https://blog.ancimen.top/?page_id=39"
        });
        return $"<section data-smm-config=\"{WebUtility.HtmlEncode(config)}\"></section>";
    }

    private static HttpResponseMessage HtmlResponse(string html) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(html, Encoding.UTF8, "text/html")
    };

    private static ObservedRequest ObserveRequest(HttpRequestMessage request)
    {
        request.Headers.TryGetValues("Cookie", out var cookieValues);
        request.Headers.TryGetValues("X-WP-Nonce", out var nonceValues);
        var body = request.Content?.ReadAsByteArrayAsync().GetAwaiter().GetResult() ?? Array.Empty<byte>();
        var rawQuery = request.RequestUri?.Query ?? string.Empty;
        var decodedQuery = Uri.UnescapeDataString(rawQuery.Replace('+', ' '));
        return new ObservedRequest(
            request.Method,
            decodedQuery,
            cookieValues?.SingleOrDefault(),
            nonceValues?.SingleOrDefault(),
            body,
            Encoding.Latin1.GetString(body));
    }

    private static bool ContainsMultipartField(string body, string name) =>
        body.Contains($"name=\"{name}\"", StringComparison.Ordinal) ||
        body.Contains($"name={name};", StringComparison.Ordinal) ||
        body.Contains($"name={name}\r", StringComparison.Ordinal);

    private static bool ContainsMultipartValue(string body, string name, string value) =>
        body.Contains($"name=\"{name}\"\r\n\r\n{value}\r\n", StringComparison.Ordinal) ||
        body.Contains($"name={name}\r\n\r\n{value}\r\n", StringComparison.Ordinal);

    private sealed record ObservedRequest(
        HttpMethod Method,
        string Query,
        string? Cookie,
        string? Nonce,
        byte[] Body,
        string BodyText);

    private sealed class AsyncStubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
