using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SkinManager.Runtime;

internal static class AgentHost
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NoSessionIdleTimeout = TimeSpan.FromSeconds(15);

    public static async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await using var coordinator = new RuntimeCoordinator(new WindowsCodexIdentityVerifier());
        using var hostLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = coordinator.SessionTerminated.ContinueWith(_ => hostLifetime.Cancel(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var agentPath = Path.Combine(AppContext.BaseDirectory, "SkinManager.Agent.exe");
        var pipeName = AgentPipeProtocol.GetPipeName(agentPath);
        while (!hostLifetime.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                if (coordinator.HasActiveSession)
                {
                    await pipe.WaitForConnectionAsync(hostLifetime.Token).ConfigureAwait(false);
                }
                else
                {
                    using var startupWait = CancellationTokenSource.CreateLinkedTokenSource(hostLifetime.Token);
                    startupWait.CancelAfter(NoSessionIdleTimeout);
                    try { await pipe.WaitForConnectionAsync(startupWait.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!hostLifetime.IsCancellationRequested)
                    {
                        return;
                    }
                }

                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(hostLifetime.Token);
                requestTimeout.CancelAfter(RequestTimeout);
                var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                string? line = await reader.ReadLineAsync(requestTimeout.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                {
                    line = null;
                    continue;
                }

                AgentCommand? command;
                try
                {
                    command = JsonSerializer.Deserialize<AgentCommand>(line, AgentPipeProtocol.JsonOptions);
                }
                catch (JsonException)
                {
                    line = null;
                    continue;
                }
                line = null;
                if (command is null || command.RequestId == Guid.Empty)
                {
                    command = null;
                    continue;
                }

                var operation = command.Operation;
                RuntimeStatus status;
                try
                {
                    status = await coordinator.HandleAsync(command, requestTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (hostLifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    status = RuntimeStatus.Error($"后台处理失败：{ex.Message}");
                }

                using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(hostLifetime.Token);
                responseTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var responseLine = JsonSerializer.Serialize(AgentResponse.From(command.RequestId, status), AgentPipeProtocol.JsonOptions);
                try
                {
                    await writer.WriteLineAsync(responseLine.AsMemory(), responseTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    if (hostLifetime.IsCancellationRequested)
                        return;
                }

                responseLine = string.Empty;
                var shouldExit = operation is AgentOperation.Remove or AgentOperation.Stop ||
                    operation == AgentOperation.Apply && !coordinator.HasActiveSession;
                command = null;
                if (shouldExit)
                    return;
            }
            catch (OperationCanceledException) when (hostLifetime.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // A stalled or abandoned client only ends its own request; an applied skin keeps running.
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                if (hostLifetime.IsCancellationRequested)
                    return;
            }
        }
    }
}
