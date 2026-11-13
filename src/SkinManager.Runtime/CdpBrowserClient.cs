using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SkinManager.Core;

namespace SkinManager.Runtime;

internal sealed record BrowserProcessIdentity(int ProcessId, string ExecutablePath, DateTimeOffset? StartedAtUtc = null)
{
    public CodexProcessIdentity? ToCodexProcessIdentity() => StartedAtUtc is { } startedAtUtc
        ? new CodexProcessIdentity(ProcessId, startedAtUtc, Path.GetFullPath(ExecutablePath))
        : null;
}

internal interface ICodexEndpointIdentityVerifier
{
    Task<BrowserProcessIdentity> VerifyListenerAsync(Uri endpoint, CancellationToken cancellationToken);
}

internal sealed record CdpEvent(string Method, JsonElement Parameters, string? SessionId);

internal sealed class CdpProtocolException : Exception
{
    public int Code { get; }

    public CdpProtocolException(int code, string message) : base($"CDP {code}: {message}") => Code = code;
}

internal sealed class CdpBrowserClient : IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private readonly ClientWebSocket _socket;
    private readonly Channel<CdpEvent> _events = Channel.CreateUnbounded<CdpEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _reader;
    private int _nextCommandId;
    private Exception? _disconnectReason;

    public bool IsConnected => _socket.State == WebSocketState.Open && Volatile.Read(ref _disconnectReason) is null;
    public ChannelReader<CdpEvent> Events => _events.Reader;
    public CodexProcessIdentity? ConnectedInstance { get; }

    private CdpBrowserClient(ClientWebSocket socket, BrowserProcessIdentity connectedIdentity)
    {
        _socket = socket;
        ConnectedInstance = connectedIdentity.ToCodexProcessIdentity();
        _reader = ReadLoopAsync();
    }

    public static async Task<CdpBrowserClient> ConnectAsync(
        Uri endpoint,
        ICodexEndpointIdentityVerifier identityVerifier,
        CancellationToken cancellationToken,
        CodexProcessIdentity? expectedInstance = null)
    {
        ValidateEndpoint(endpoint);
        var beforeConnect = await identityVerifier.VerifyListenerAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (expectedInstance is not null && !MatchesExpectedInstance(beforeConnect, expectedInstance))
            throw new InvalidOperationException("调试端点实际属于另一个 Codex 实例，未建立连接。请刷新实例列表后重试。");

        var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5) };
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        using var response = await httpClient.GetAsync(new Uri(endpoint, "/json/version"), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException(AppLocalization.Format("调试端点 /json/version 返回 HTTP {0}。请确认端口属于使用调试参数启动的 Codex。", ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)));

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("Browser", out var browserValue) || browserValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(browserValue.GetString()))
            throw new InvalidOperationException("调试端点缺少有效的 Browser 身份字段。 ");
        if (!document.RootElement.TryGetProperty("webSocketDebuggerUrl", out var wsValue) || wsValue.ValueKind != JsonValueKind.String || !Uri.TryCreate(wsValue.GetString(), UriKind.Absolute, out var browserWebSocketUri))
            throw new InvalidOperationException("调试端点缺少有效的 browser WebSocket 地址。 ");
        ValidateBrowserWebSocket(endpoint, browserWebSocketUri);

        var socket = new ClientWebSocket();
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            await socket.ConnectAsync(browserWebSocketUri, connectTimeout.Token).ConfigureAwait(false);
            var afterConnect = await identityVerifier.VerifyListenerAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (!SameProcess(beforeConnect, afterConnect))
                throw new InvalidOperationException("调试端口所属进程在连接期间发生变化，已断开以避免连接到复用的端口。 ");
            if (expectedInstance is not null && !MatchesExpectedInstance(afterConnect, expectedInstance))
                throw new InvalidOperationException("调试端口在连接期间切换到了另一个 Codex 实例，已拒绝连接。");
            return new CdpBrowserClient(socket, afterConnect);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool SameProcess(BrowserProcessIdentity left, BrowserProcessIdentity right) =>
        left.ProcessId == right.ProcessId && left.StartedAtUtc == right.StartedAtUtc &&
        string.Equals(Path.GetFullPath(left.ExecutablePath), Path.GetFullPath(right.ExecutablePath), StringComparison.OrdinalIgnoreCase);

    private static bool MatchesExpectedInstance(BrowserProcessIdentity actual, CodexProcessIdentity expected) =>
        actual.ProcessId == expected.ProcessId && actual.StartedAtUtc is { } startedAtUtc &&
        startedAtUtc.UtcDateTime == expected.StartedAtUtc.UtcDateTime &&
        string.Equals(Path.GetFullPath(actual.ExecutablePath), Path.GetFullPath(expected.ExecutablePath), StringComparison.OrdinalIgnoreCase);

    public async Task<JsonElement> SendAsync(string method, object? parameters = null, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            throw new IOException("Codex browser WebSocket 已断开。 ", _disconnectReason);

        var id = Interlocked.Increment(ref _nextCommandId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
            throw new InvalidOperationException("CDP 命令 ID 冲突。 ");

        try
        {
            using var payload = new MemoryStream();
            using (var writer = new Utf8JsonWriter(payload))
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", id);
                writer.WriteString("method", method);
                if (parameters is not null)
                {
                    writer.WritePropertyName("params");
                    JsonSerializer.Serialize(writer, parameters, AgentPipeProtocol.JsonOptions);
                }
                if (!string.IsNullOrWhiteSpace(sessionId))
                    writer.WriteString("sessionId", sessionId);
                writer.WriteEndObject();
            }

            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(GetWrittenMemory(payload), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(CommandTimeout);
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException(AppLocalization.Format("CDP 命令 {0} 在 {1:0} 秒内未响应。", method, CommandTimeout.TotalSeconds));
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public void Abort(Exception reason)
    {
        Interlocked.CompareExchange(ref _disconnectReason, reason, null);
        try { _lifetime.Cancel(); }
        catch (ObjectDisposedException) { }
        try { _socket.Abort(); }
        catch (ObjectDisposedException) { }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        var buffer = new byte[8192];
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, _lifetime.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new IOException("Codex browser WebSocket 收到关闭帧。 ");
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                using var document = JsonDocument.Parse(GetWrittenMemory(message));
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var idValue) && idValue.TryGetInt32(out var id))
                {
                    if (!_pending.TryGetValue(id, out var pending))
                        continue;
                    if (root.TryGetProperty("error", out var error))
                    {
                        var code = error.TryGetProperty("code", out var codeValue) && codeValue.TryGetInt32(out var parsedCode) ? parsedCode : -1;
                        var messageText = error.TryGetProperty("message", out var messageValue) ? messageValue.GetString() ?? "命令失败" : "命令失败";
                        pending.TrySetException(new CdpProtocolException(code, messageText));
                    }
                    else
                    {
                        var response = root.TryGetProperty("result", out var responseValue) ? responseValue.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
                        pending.TrySetResult(response);
                    }
                    continue;
                }

                if (root.TryGetProperty("method", out var methodValue) && IsSessionEvent(methodValue))
                {
                    var parameters = root.TryGetProperty("params", out var paramsValue) ? paramsValue.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
                    var sessionId = root.TryGetProperty("sessionId", out var sessionValue) ? sessionValue.GetString() : null;
                    await _events.Writer.WriteAsync(new CdpEvent(methodValue.GetString()!, parameters, sessionId), _lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            Interlocked.CompareExchange(ref _disconnectReason, failure ?? new IOException("CDP WebSocket 已关闭。 "), null);
            foreach (var pending in _pending.Values)
                pending.TrySetException(new IOException("CDP WebSocket 已断开。 ", _disconnectReason));
            _events.Writer.TryComplete(_disconnectReason);
        }
    }

    internal static ReadOnlyMemory<byte> GetWrittenMemory(MemoryStream stream) =>
        stream.GetBuffer().AsMemory(0, checked((int)stream.Length));

    private static bool IsSessionEvent(JsonElement methodValue) =>
        methodValue.ValueEquals("Target.targetCreated") ||
        methodValue.ValueEquals("Target.targetInfoChanged") ||
        methodValue.ValueEquals("Target.targetDestroyed") ||
        methodValue.ValueEquals("Target.detachedFromTarget") ||
        methodValue.ValueEquals("Runtime.bindingCalled") ||
        methodValue.ValueEquals("Runtime.executionContextCreated") ||
        methodValue.ValueEquals("Page.loadEventFired") ||
        methodValue.ValueEquals("Page.frameNavigated");

    internal static void ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) || !IsLiteralLoopbackHost(endpoint) ||
            endpoint.Port is < 1 or > 65535 || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.AbsolutePath is not "/" and not "")
            throw new ArgumentException("调试端点必须是仅限本机 loopback 的 HTTP 地址，例如 http://127.0.0.1:9335。", nameof(endpoint));
    }

    internal static async Task<bool> IsSupportedEndpointAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        ValidateEndpoint(endpoint);
        using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeTimeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
        var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromMilliseconds(1200) };
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(1500) };
        try
        {
            using var response = await httpClient.GetAsync(new Uri(endpoint, "/json/version"), HttpCompletionOption.ResponseHeadersRead, probeTimeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return false;

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(probeTimeout.Token).ConfigureAwait(false),
                cancellationToken: probeTimeout.Token).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("Browser", out var browserValue) ||
                browserValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(browserValue.GetString()) ||
                !document.RootElement.TryGetProperty("webSocketDebuggerUrl", out var wsValue) ||
                wsValue.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(wsValue.GetString(), UriKind.Absolute, out var browserWebSocketUri))
                return false;

            try
            {
                ValidateBrowserWebSocket(endpoint, browserWebSocketUri);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ValidateBrowserWebSocket(Uri endpoint, Uri websocket)
    {
        var segments = websocket.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!string.Equals(websocket.Scheme, "ws", StringComparison.OrdinalIgnoreCase) || !IsLiteralLoopbackHost(websocket) || websocket.Port != endpoint.Port ||
            !IPAddress.TryParse(websocket.DnsSafeHost, out var websocketAddress) || !IPAddress.TryParse(endpoint.DnsSafeHost, out var endpointAddress) ||
            !websocketAddress.Equals(endpointAddress) ||
            websocket.UserInfo.Length != 0 || websocket.Query.Length != 0 || websocket.Fragment.Length != 0 ||
            segments.Length != 3 || segments[0] != "devtools" || segments[1] != "browser" || string.IsNullOrWhiteSpace(segments[2]))
            throw new InvalidOperationException("/json/version 返回的地址不是同一 loopback 端口上的 browser WebSocket。 ");
    }

    private static bool IsLiteralLoopbackHost(Uri uri)
    {
        if (!uri.IsLoopback || !IPAddress.TryParse(uri.DnsSafeHost, out var address))
            return false;
        return address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _socket.Dispose();
        try { await _reader.ConfigureAwait(false); }
        catch { }
        _sendGate.Dispose();
        _lifetime.Dispose();
    }
}
