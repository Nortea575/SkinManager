using System.Diagnostics;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SkinManager.Runtime;

public enum RuntimeStatusState
{
    Stopped,
    Connecting,
    Connected,
    Applying,
    Error
}

public sealed class RuntimeStatus : EventArgs
{
    public RuntimeStatusState State { get; }
    public string? SkinId { get; }
    public string? Endpoint { get; }
    public string? Message { get; }
    public bool? IsDebugPortOpen { get; }
    public int AppliedTargetCount { get; }
    public CodexProcessIdentity? ConnectedInstance { get; }
    public RuntimeCompatibilityResult? CompatibilityResult { get; }

    public RuntimeStatus(RuntimeStatusState state, string? skinId, string? endpoint, string? message, bool? isDebugPortOpen, int appliedTargetCount = 0, CodexProcessIdentity? connectedInstance = null, RuntimeCompatibilityResult? compatibilityResult = null)
    {
        State = state;
        SkinId = skinId;
        Endpoint = endpoint;
        Message = message;
        IsDebugPortOpen = isDebugPortOpen;
        AppliedTargetCount = appliedTargetCount;
        ConnectedInstance = connectedInstance;
        CompatibilityResult = compatibilityResult;
    }

    internal static RuntimeStatus Stopped(string? message = null) => new(RuntimeStatusState.Stopped, null, null, message, null);
    internal static RuntimeStatus Error(string message, string? skinId = null, string? endpoint = null) => new(RuntimeStatusState.Error, skinId, endpoint, message, null);
}

public sealed record RuntimeApplyRequest
{
    public required string SkinId { get; init; }
    public required string RuntimeScript { get; init; }
    public required string RemoveScript { get; init; }
    public string Endpoint { get; init; } = "http://127.0.0.1:9335";
    public CodexProcessIdentity? ExpectedInstance { get; init; }
    public RuntimeCompatibilityOptions? Compatibility { get; init; }
}

public sealed record DebugClientLaunchOptions(int Port = 9335);

internal interface IRuntimeLaunchPlatform
{
    bool IsWindows { get; }
    bool IsCodexProcessRunning();
    Task<uint> ActivateStoreAppAsync(string appUserModelId, string arguments, CancellationToken cancellationToken);
}

public sealed class RuntimeController
{
    private const string PackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";
    private const string AppUserModelId = PackageFamily + "!App";
    private static readonly TimeSpan AgentResponseTimeout = TimeSpan.FromSeconds(60);
    private readonly string _agentExecutablePath;
    private readonly string _pipeName;
    private readonly IRuntimeLaunchPlatform _launchPlatform;
    private readonly Func<bool>? _agentRunningProbe;
    private readonly Action? _startAgentOverride;
    private readonly TimeSpan _agentPipeConnectTimeout;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private RuntimeStatus _lastStatus = RuntimeStatus.Stopped();

    public event EventHandler<RuntimeStatus>? StatusChanged;

    public RuntimeController(string? agentExecutablePath = null)
        : this(agentExecutablePath ?? Path.Combine(AppContext.BaseDirectory, "SkinManager.Agent.exe"), null, null)
    {
    }

    internal RuntimeController(
        string agentExecutablePath,
        string? pipeName,
        IRuntimeLaunchPlatform? launchPlatform = null,
        Func<bool>? agentRunningProbe = null,
        Action? startAgentOverride = null,
        TimeSpan? agentPipeConnectTimeout = null)
    {
        _agentExecutablePath = Path.GetFullPath(agentExecutablePath);
        _pipeName = pipeName ?? AgentPipeProtocol.GetPipeName(_agentExecutablePath);
        _launchPlatform = launchPlatform ?? WindowsRuntimeLaunchPlatform.Instance;
        _agentRunningProbe = agentRunningProbe;
        _startAgentOverride = startAgentOverride;
        _agentPipeConnectTimeout = agentPipeConnectTimeout ?? TimeSpan.FromSeconds(12);
    }

    public async Task<RuntimeStatus> ApplyAsync(RuntimeApplyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SkinId) || string.IsNullOrWhiteSpace(request.RuntimeScript) || string.IsNullOrWhiteSpace(request.RemoveScript))
            return Publish(RuntimeStatus.Error("皮肤运行脚本、恢复脚本和皮肤 ID 均不能为空。", request.SkinId, request.Endpoint));

        return await SendAsync(AgentOperation.Apply, request, startAgent: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RuntimeCompatibilityResult> ConfigureCompatibilityAsync(
        RuntimeCompatibilityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Uri endpoint;
        try
        {
            if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out endpoint!))
                throw new ArgumentException("调试端点不是有效的绝对 URI。", nameof(request));
            CdpBrowserClient.ValidateEndpoint(endpoint);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Failed, request.Endpoint, ex.Message);
        }

        using var commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        commandTimeout.CancelAfter(AgentResponseTimeout);
        var commandToken = commandTimeout.Token;
        var acquiredGate = false;
        try
        {
            await _commandGate.WaitAsync(commandToken).ConfigureAwait(false);
            acquiredGate = true;
            NamedPipeClientStream? pipe = await TryConnectAsync(TimeSpan.FromMilliseconds(250), commandToken).ConfigureAwait(false);
            if (pipe is null)
            {
                if (!File.Exists(_agentExecutablePath))
                    return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Failed, endpoint.ToString(), $"找不到后台程序：{_agentExecutablePath}");
                StartAgentIfNeeded();
                pipe = await TryConnectAsync(TimeSpan.FromSeconds(12), commandToken).ConfigureAwait(false);
            }

            if (pipe is null)
                return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Pending, endpoint.ToString(), "语言设置等待后台连接就绪。", false);

            await using (pipe.ConfigureAwait(false))
            {
                var requestId = Guid.NewGuid();
                var command = new AgentCommand
                {
                    RequestId = requestId,
                    Operation = AgentOperation.ConfigureCompatibility,
                    CompatibilityRequest = request with { Endpoint = endpoint.ToString() }
                };
                var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                await writer.WriteLineAsync(JsonSerializer.Serialize(command, AgentPipeProtocol.JsonOptions).AsMemory(), commandToken).ConfigureAwait(false);
                var line = await reader.ReadLineAsync(commandToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                    return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Failed, endpoint.ToString(), "后台程序已断开，未收到语言设置结果。");
                var response = JsonSerializer.Deserialize<AgentResponse>(line, AgentPipeProtocol.JsonOptions);
                if (response is null || response.RequestId != requestId)
                    return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Failed, endpoint.ToString(), "后台程序返回了不匹配的语言设置响应。");
                return response.CompatibilityResult ?? new RuntimeCompatibilityResult(
                    RuntimeCompatibilityState.Failed,
                    endpoint.ToString(),
                    "后台程序未返回语言设置状态。",
                    response.IsDebugPortOpen,
                    response.ConnectedInstance);
            }
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;
            return new RuntimeCompatibilityResult(RuntimeCompatibilityState.TimedOut, endpoint.ToString(), $"语言设置操作在 {AgentResponseTimeout.TotalSeconds:0} 秒内未完成。");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or JsonException or InvalidOperationException or Win32Exception)
        {
            return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Failed, endpoint.ToString(), $"语言设置后台通信失败：{ex.Message}");
        }
        finally
        {
            if (acquiredGate)
                _commandGate.Release();
        }
    }

    public Task<RuntimeStatus> RemoveAsync(CancellationToken cancellationToken = default) =>
        SendAsync(AgentOperation.Remove, null, startAgent: false, cancellationToken);

    public Task<RuntimeStatus> StopAsync(CancellationToken cancellationToken = default) =>
        SendAsync(AgentOperation.Stop, null, startAgent: false, cancellationToken);

    public Task<RuntimeStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        SendAsync(AgentOperation.GetStatus, null, startAgent: false, cancellationToken);

    public async Task<RuntimeStatus> LaunchDebugClientAsync(DebugClientLaunchOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new DebugClientLaunchOptions();
        if (options.Port is < 1 or > 65535)
            return Publish(RuntimeStatus.Error("调试端口必须在 1 到 65535 之间。"));
        if (!_launchPlatform.IsWindows)
            return Publish(RuntimeStatus.Error("调试版启动仅支持 Windows Store 安装的 Codex 桌面版。"));
        cancellationToken.ThrowIfCancellationRequested();

        if (_launchPlatform.IsCodexProcessRunning())
            return Publish(RuntimeStatus.Error("检测到 Codex 正在运行。请手动完全退出后，再启动调试版；管理器不会关闭或重启 Codex。"));

        try
        {
            var arguments = string.Join(' ',
                "--remote-debugging-address=127.0.0.1",
                $"--remote-debugging-port={options.Port}");
            await _launchPlatform.ActivateStoreAppAsync(AppUserModelId, arguments, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return Publish(new RuntimeStatus(
                RuntimeStatusState.Connecting,
                null,
                $"http://127.0.0.1:{options.Port}",
                "已请求启动 Codex 调试版，正在自动检测实例与端口就绪状态。",
                null));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or Win32Exception)
        {
            return Publish(RuntimeStatus.Error($"无法启动 Codex 调试版：{ex.Message}"));
        }
    }

    private async Task<RuntimeStatus> SendAsync(AgentOperation operation, RuntimeApplyRequest? request, bool startAgent, CancellationToken cancellationToken)
    {
        using var commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        commandTimeout.CancelAfter(AgentResponseTimeout);
        var commandToken = commandTimeout.Token;
        var acquiredGate = false;
        try
        {
            await _commandGate.WaitAsync(commandToken).ConfigureAwait(false);
            acquiredGate = true;
            NamedPipeClientStream? pipe = await TryConnectAsync(TimeSpan.FromMilliseconds(250), commandToken).ConfigureAwait(false);
            if (pipe is null && !startAgent && IsAgentRunning())
                pipe = await TryConnectAsync(TimeSpan.FromSeconds(12), commandToken).ConfigureAwait(false);
            if (pipe is null && startAgent)
            {
                if (!File.Exists(_agentExecutablePath))
                    return Publish(RuntimeStatus.Error($"找不到后台程序：{_agentExecutablePath}"));
                StartAgentIfNeeded();
                pipe = await TryConnectAsync(_agentPipeConnectTimeout, commandToken).ConfigureAwait(false);
                if (pipe is null && !IsAgentRunning())
                {
                    StartAgentIfNeeded();
                    pipe = await TryConnectAsync(_agentPipeConnectTimeout, commandToken).ConfigureAwait(false);
                }
            }

            if (pipe is null)
            {
                if (startAgent)
                {
                    var detail = IsAgentRunning()
                        ? "后台程序仍在运行，但通信管道未就绪。"
                        : "后台程序已退出或无法启动。";
                    return Publish(RuntimeStatus.Error($"{detail}未发送本次皮肤请求，请重试。"));
                }
                var stopped = RuntimeStatus.Stopped(operation == AgentOperation.GetStatus
                    ? "当前没有运行中的皮肤连接。"
                    : "当前没有运行中的皮肤连接。调试端口由 Codex 进程管理，停止连接不会关闭它。");
                return Publish(stopped);
            }

            await using (pipe.ConfigureAwait(false))
            {
                var requestId = Guid.NewGuid();
                var command = new AgentCommand { RequestId = requestId, Operation = operation, ApplyRequest = request };
                var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                await writer.WriteLineAsync(JsonSerializer.Serialize(command, AgentPipeProtocol.JsonOptions).AsMemory(), commandToken).ConfigureAwait(false);
                var line = await reader.ReadLineAsync(commandToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                    return Publish(RuntimeStatus.Error("后台程序已断开，未收到状态响应。"));
                var response = JsonSerializer.Deserialize<AgentResponse>(line, AgentPipeProtocol.JsonOptions);
                if (response is null || response.RequestId != requestId)
                    return Publish(RuntimeStatus.Error("后台程序返回了不匹配的命令响应。"));
                var status = response.ToStatus();
                _lastStatus = status;
                return Publish(status);
            }
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;
            var timeout = RuntimeStatus.Error($"后台命令在 {AgentResponseTimeout.TotalSeconds:0} 秒内未完成。");
            _lastStatus = timeout;
            return Publish(timeout);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or JsonException or InvalidOperationException or Win32Exception)
        {
            var error = RuntimeStatus.Error($"皮肤后台通信失败：{ex.Message}");
            _lastStatus = error;
            return Publish(error);
        }
        finally
        {
            if (acquiredGate)
                _commandGate.Release();
        }
    }

    private async Task<NamedPipeClientStream?> TryConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            return pipe;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            if (ex is OperationCanceledException)
                throw;
            return null;
        }
    }

    private void StartAgentIfNeeded()
    {
        using var mutex = new Mutex(false, $"Local\\SkinManager.Agent.Start.{_pipeName}");
        var ownsMutex = false;
        try
        {
            ownsMutex = mutex.WaitOne(0);
            if (!ownsMutex)
                return;
            if (IsAgentRunning())
                return;
            if (_startAgentOverride is not null)
            {
                _startAgentOverride();
                return;
            }
            using var startedAgent = Process.Start(new ProcessStartInfo
            {
                FileName = _agentExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(_agentExecutablePath)!,
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        finally
        {
            if (ownsMutex)
                mutex.ReleaseMutex();
        }
    }

    private bool IsAgentRunning()
    {
        if (_agentRunningProbe is not null)
            return _agentRunningProbe();

        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_agentExecutablePath)))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, _agentExecutablePath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
        return false;
    }

    private RuntimeStatus Publish(RuntimeStatus status)
    {
        _lastStatus = status;
        var handlers = StatusChanged;
        if (handlers is not null)
        {
            foreach (EventHandler<RuntimeStatus> handler in handlers.GetInvocationList())
            {
                try { handler(this, status); }
                catch { }
            }
        }
        return status;
    }

}

internal sealed class WindowsRuntimeLaunchPlatform : IRuntimeLaunchPlatform
{
    public static WindowsRuntimeLaunchPlatform Instance { get; } = new();

    private WindowsRuntimeLaunchPlatform() { }

    public bool IsWindows => OperatingSystem.IsWindows();

    public bool IsCodexProcessRunning()
    {
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (WindowsCodexIdentityVerifier.IsKnownCodexExecutable(path))
                    return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
        return false;
    }

    public Task<uint> ActivateStoreAppAsync(string appUserModelId, string arguments, CancellationToken cancellationToken) =>
        Task.Run(() => ActivateStoreApp(appUserModelId, arguments), cancellationToken);

    private static uint ActivateStoreApp(string appUserModelId, string arguments)
    {
        var activationType = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), throwOnError: true)!;
        var activation = (IApplicationActivationManager)Activator.CreateInstance(activationType)!;
        try
        {
            var result = activation.ActivateApplication(appUserModelId, arguments, 0, out var processId);
            Marshal.ThrowExceptionForHR(result);
            return processId;
        }
        finally
        {
            if (Marshal.IsComObject(activation))
                Marshal.ReleaseComObject(activation);
        }
    }

    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
    }
}
