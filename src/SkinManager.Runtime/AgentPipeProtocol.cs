using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkinManager.Runtime;

internal enum AgentOperation
{
    Apply,
    Remove,
    Stop,
    GetStatus,
    ConfigureCompatibility
}

internal sealed class AgentCommand
{
    public Guid RequestId { get; init; }
    public AgentOperation Operation { get; init; }
    public RuntimeApplyRequest? ApplyRequest { get; init; }
    public RuntimeCompatibilityRequest? CompatibilityRequest { get; init; }
}

internal sealed class AgentResponse
{
    public Guid RequestId { get; init; }
    public RuntimeStatusState State { get; init; }
    public string? SkinId { get; init; }
    public string? Endpoint { get; init; }
    public string? Message { get; init; }
    public bool? IsDebugPortOpen { get; init; }
    public int AppliedTargetCount { get; init; }
    public CodexProcessIdentity? ConnectedInstance { get; init; }
    public RuntimeCompatibilityResult? CompatibilityResult { get; init; }

    public RuntimeStatus ToStatus() => new(State, SkinId, Endpoint, Message, IsDebugPortOpen, AppliedTargetCount, ConnectedInstance, CompatibilityResult);

    public static AgentResponse From(Guid requestId, RuntimeStatus status) => new()
    {
        RequestId = requestId,
        State = status.State,
        SkinId = status.SkinId,
        Endpoint = status.Endpoint,
        Message = status.Message,
        IsDebugPortOpen = status.IsDebugPortOpen,
        AppliedTargetCount = status.AppliedTargetCount,
        ConnectedInstance = status.ConnectedInstance,
        CompatibilityResult = status.CompatibilityResult
    };

    public static AgentResponse From(Guid requestId, RuntimeStatus status, RuntimeCompatibilityResult compatibilityResult) => new()
    {
        RequestId = requestId,
        State = status.State,
        SkinId = status.SkinId,
        Endpoint = status.Endpoint,
        Message = status.Message,
        IsDebugPortOpen = status.IsDebugPortOpen,
        AppliedTargetCount = status.AppliedTargetCount,
        ConnectedInstance = status.ConnectedInstance,
        CompatibilityResult = compatibilityResult
    };
}

internal static class AgentPipeProtocol
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string GetPipeName(string agentExecutablePath)
    {
        var identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var normalizedAgentPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(agentExecutablePath)).ToUpperInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity + "\n" + normalizedAgentPath));
        return "SkinManager.Agent." + Convert.ToHexString(digest.AsSpan(0, 10));
    }
}
