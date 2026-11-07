using SkinManager.Runtime;

namespace SkinManager.Desktop;

internal sealed record RuntimeConnectionViewState(
    string StatusLabel,
    string BrushResourceKey,
    string Message,
    bool IsConnected,
    bool IsApplied)
{
    internal static RuntimeConnectionViewState From(RuntimeStatus? status)
    {
        if (status is null)
        {
            return new("未连接", "SecondaryText", "选择一个窗口，或在没有 Codex 实例时启动调试版。", false, false);
        }

        var isConnected = status.State == RuntimeStatusState.Connected;
        var isApplied = isConnected && status.AppliedTargetCount > 0;
        var (label, brush, fallback) = status.State switch
        {
            RuntimeStatusState.Connecting when string.IsNullOrWhiteSpace(status.SkinId) =>
                ("正在等待调试端口", "WarningText", "正在启动 Codex 调试版并等待本地端口就绪。"),
            RuntimeStatusState.Connecting =>
                ("正在连接", "WarningText", "正在核验调试端口身份并连接 Codex。"),
            RuntimeStatusState.Applying =>
                ("正在应用", "WarningText", "正在将当前皮肤应用到 Codex 页面。"),
            RuntimeStatusState.Connected when status.AppliedTargetCount > 0 =>
                ("已连接并已应用", "SuccessText", $"已应用到 {status.AppliedTargetCount} 个 Codex 页面。"),
            RuntimeStatusState.Connected =>
                ("已连接，尚未应用", "WarningText", "连接已建立，但没有匹配的 Codex 主窗口；皮肤尚未应用。"),
            RuntimeStatusState.Error =>
                ("本次操作未完成", "ErrorText", "本次操作未完成。请查看原因并重试。"),
            _ =>
                ("未连接", "SecondaryText", "选择一个窗口，或在没有 Codex 实例时启动调试版。")
        };

        var message = string.IsNullOrWhiteSpace(status.Message) ? fallback : status.Message;
        if (isConnected && status.AppliedTargetCount == 0 &&
            !message.Contains("尚未应用", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("未应用", StringComparison.OrdinalIgnoreCase))
        {
            message = $"连接已建立，皮肤尚未应用。{message}";
        }

        return new RuntimeConnectionViewState(label, brush, message, isConnected, isApplied);
    }

    internal static string GetPrimaryActionLabel(bool hasActiveConnection, bool hasSelectedTarget, bool hasSkin) =>
        !hasSkin ? "先选择皮肤" :
        hasActiveConnection ? "应用皮肤 / 更新" :
        hasSelectedTarget ? "连接并应用" :
        "启动并应用";
}
