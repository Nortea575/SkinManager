using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SkinManager.Runtime;

internal sealed record CodexListenerEndpoint(int ProcessId, IPAddress Address)
{
    public Uri? ToLoopbackEndpoint(int port)
    {
        var endpointAddress = Address.Equals(IPAddress.Any)
            ? IPAddress.Loopback
            : Address.Equals(IPAddress.IPv6Any)
                ? IPAddress.IPv6Loopback
                : Address;
        return IPAddress.IsLoopback(endpointAddress)
            ? new UriBuilder(Uri.UriSchemeHttp, endpointAddress.ToString(), port).Uri
            : null;
    }
}

internal sealed class WindowsCodexIdentityVerifier : ICodexEndpointIdentityVerifier
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private static readonly Regex PackageDirectoryPattern = new(
        "^OpenAI\\.Codex_[^\\\\]+_x64__2p2nqsd0c76g0$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public Task<BrowserProcessIdentity> VerifyListenerAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("只支持 Windows Store 安装的 Codex 桌面版。 ");
        CdpBrowserClient.ValidateEndpoint(endpoint);

        var processIds = GetListenerProcessIds(endpoint);
        if (processIds.Count != 1)
            throw new InvalidOperationException(processIds.Count == 0
                ? $"指定地址 {endpoint.Host} 的端口 {endpoint.Port} 没有 TCP listener。请使用调试参数启动 Codex 后重试。"
                : "指定 loopback 地址由多个进程共同监听，无法安全确认目标身份。 ");

        var processId = processIds.Single();
        using var process = Process.GetProcessById(processId);
        var executablePath = process.MainModule?.FileName;
        if (!IsKnownCodexExecutable(executablePath))
            throw new InvalidOperationException("指定端口并非由已识别的 Windows Store Codex 程序监听；为避免向普通网页执行脚本，连接已拒绝。 ");

        var productName = FileVersionInfo.GetVersionInfo(executablePath!).ProductName;
        if (!string.Equals(productName, "Codex", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Codex 安装路径中的可执行文件产品身份不匹配，连接已拒绝。 ");

        return Task.FromResult(new BrowserProcessIdentity(processId, Path.GetFullPath(executablePath!), new DateTimeOffset(process.StartTime.ToUniversalTime())));
    }

    internal static bool IsKnownCodexExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!string.Equals(Path.GetFileName(fullPath), "ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
                return false;

            var appDirectory = Directory.GetParent(fullPath);
            var packageDirectory = appDirectory?.Parent;
            if (appDirectory is null || packageDirectory is null || !string.Equals(appDirectory.Name, "app", StringComparison.OrdinalIgnoreCase) ||
                !PackageDirectoryPattern.IsMatch(packageDirectory.Name))
                return false;

            var windowsAppsRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"));
            var expectedPrefix = Path.TrimEndingDirectorySeparator(windowsAppsRoot) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<CodexListenerEndpoint> GetLoopbackListenerEndpoints(int port)
    {
        var listeners = new List<CodexListenerEndpoint>();
        AddTableListeners(AfInet, port, 24, stateOffset: 0, portOffset: 8, pidOffset: 20,
            addressOffset: 4, addressLength: 4, listeners);
        AddTableListeners(AfInet6, port, 56, stateOffset: 48, portOffset: 20, pidOffset: 52,
            addressOffset: 0, addressLength: 16, listeners);
        return listeners.Distinct().ToArray();
    }

    private static HashSet<int> GetListenerProcessIds(Uri endpoint)
    {
        if (!IPAddress.TryParse(endpoint.DnsSafeHost, out var wantedAddress))
            return [];
        var pids = new HashSet<int>();
        var wildcardAddress = wantedAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? IPAddress.Any
            : IPAddress.IPv6Any;
        foreach (var listener in GetLoopbackListenerEndpoints(endpoint.Port))
        {
            if (listener.Address.Equals(wantedAddress) ||
                wantedAddress.AddressFamily == listener.Address.AddressFamily && listener.Address.Equals(wildcardAddress))
                pids.Add(listener.ProcessId);
        }
        return pids;
    }

    private static void AddTableListeners(int addressFamily, int wantedPort, int rowSize, int stateOffset, int portOffset, int pidOffset,
        int addressOffset, int addressLength, List<CodexListenerEndpoint> listeners)
    {
        var bufferLength = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref bufferLength, order: true, addressFamily, TcpTableOwnerPidListener, 0);
        if (result != ErrorInsufficientBuffer && result != 0)
            throw new InvalidOperationException($"无法读取本机 TCP listener 表（错误 {result}）。");
        if (bufferLength <= sizeof(uint))
            return;

        var buffer = Marshal.AllocHGlobal(bufferLength);
        try
        {
            result = GetExtendedTcpTable(buffer, ref bufferLength, order: true, addressFamily, TcpTableOwnerPidListener, 0);
            if (result != 0)
                throw new InvalidOperationException($"无法读取本机 TCP listener 表（错误 {result}）。");

            var rows = Marshal.ReadInt32(buffer);
            var bytes = new byte[bufferLength];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            for (var index = 0; index < rows; index++)
            {
                var offset = sizeof(uint) + (index * rowSize);
                if (offset + rowSize > bytes.Length)
                    break;
                if (BitConverter.ToInt32(bytes, offset + stateOffset) != 2)
                    continue;
                var rawPort = BitConverter.ToUInt32(bytes, offset + portOffset);
                var networkPort = ((rawPort & 0xff) << 8) | ((rawPort >> 8) & 0xff);
                if (networkPort == wantedPort)
                {
                    var pid = BitConverter.ToInt32(bytes, offset + pidOffset);
                    if (pid > 0)
                    {
                        var addressBytes = bytes.AsSpan(offset + addressOffset, addressLength);
                        var address = new IPAddress(addressBytes);
                        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                            listeners.Add(new CodexListenerEndpoint(pid, address));
                    }
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(IntPtr tcpTable, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily, int tableClass, uint reserved);
}
