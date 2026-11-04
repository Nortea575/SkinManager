using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SkinManager.Runtime;

internal sealed class WindowsCodexIdentityVerifier : ICodexEndpointIdentityVerifier
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private static readonly Regex PackageDirectoryPattern = new(
        "^OpenAI\\.Codex_[^\\\\]+_x64__2p2nqsd0c76g0$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public Task<BrowserProcessIdentity> VerifyListenerAsync(int port, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("只支持 Windows Store 安装的 Codex 桌面版。 ");

        var processIds = GetListenerProcessIds(port);
        if (processIds.Count != 1)
            throw new InvalidOperationException(processIds.Count == 0
                ? "指定端口没有 TCP listener。请使用调试参数启动 Codex 后重试。"
                : "指定端口由多个进程共同监听，无法安全确认目标身份。 ");

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

    internal static bool IsUniqueListenerOwnedByProcess(int port, int processId)
    {
        var processIds = GetListenerProcessIds(port);
        return processIds.Count == 1 && processIds.Contains(processId);
    }

    private static HashSet<int> GetListenerProcessIds(int port)
    {
        var pids = new HashSet<int>();
        AddTableListeners(AfInet, port, 24, stateOffset: 0, portOffset: 8, pidOffset: 20, pids);
        AddTableListeners(AfInet6, port, 56, stateOffset: 48, portOffset: 20, pidOffset: 52, pids);
        return pids;
    }

    private static void AddTableListeners(int addressFamily, int wantedPort, int rowSize, int stateOffset, int portOffset, int pidOffset, HashSet<int> processIds)
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
                        processIds.Add(pid);
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
