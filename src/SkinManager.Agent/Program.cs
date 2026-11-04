using System.Diagnostics;
using SkinManager.Runtime;

namespace SkinManager.Agent;

internal static class Program
{
    private static readonly TimeSpan StopCommandTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan AgentExitTimeout = TimeSpan.FromSeconds(8);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && string.Equals(args[0], "--stop", StringComparison.OrdinalIgnoreCase))
            return StopAgentAsync().GetAwaiter().GetResult();
        if (args.Length != 0)
            return 64;

        using var singleInstanceMutex = new Mutex(initiallyOwned: false, "Local\\" + GetPipeName());
        var ownsMutex = false;
        try
        {
            ownsMutex = singleInstanceMutex.WaitOne(0);
            if (!ownsMutex)
                return 0;

            AgentEntry.RunAsync().GetAwaiter().GetResult();
            return 0;
        }
        catch
        {
            return 1;
        }
        finally
        {
            if (ownsMutex)
                singleInstanceMutex.ReleaseMutex();
        }
    }

    private static string GetPipeName() => AgentPipeProtocol.GetPipeName(Path.Combine(AppContext.BaseDirectory, "SkinManager.Agent.exe"));

    private static async Task<int> StopAgentAsync()
    {
        var agentExecutablePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "SkinManager.Agent.exe"));
        if (!HasMatchingAgentProcess(agentExecutablePath, Environment.ProcessId))
            return 0;

        using var timeout = new CancellationTokenSource(StopCommandTimeout);
        try
        {
            var status = await new RuntimeController(agentExecutablePath).StopAsync(timeout.Token).ConfigureAwait(false);
            if (status.State == RuntimeStatusState.Error)
                return Fail(2, status.Message ?? "后台程序未能恢复当前皮肤并停止连接。");

            var exitDeadline = Stopwatch.GetTimestamp() + (long)(AgentExitTimeout.TotalSeconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < exitDeadline)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (!HasMatchingAgentProcess(agentExecutablePath, Environment.ProcessId))
                    return 0;
                await Task.Delay(200, timeout.Token).ConfigureAwait(false);
            }

            return Fail(3, "等待皮肤后台退出超时。请关闭 SkinManager 后重试；未强制结束任何进程。");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return Fail(3, "停止命令超时。请确认 SkinManager 已关闭后重试；未强制结束任何进程。");
        }
        catch (Exception exception)
        {
            return Fail(2, $"无法停止皮肤后台：{exception.Message}");
        }
    }

    private static bool HasMatchingAgentProcess(string executablePath, int excludedProcessId)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executablePath)))
        {
            try
            {
                if (process.Id == excludedProcessId)
                    continue;
                if (string.Equals(process.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
        return false;
    }

    private static int Fail(int exitCode, string message)
    {
        Console.Error.WriteLine(message);
        return exitCode;
    }
}

internal static class AgentEntry
{
    public static Task RunAsync() => AgentHost.RunAsync();
}
