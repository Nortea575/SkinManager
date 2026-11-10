using SkinManager.Runtime;

internal static class CodexConfigReadabilityServiceTests
{
    public static void Run()
    {
        const string home = @"C:\fixture\codex-home";
        var exists = true;
        var readable = true;
        var inspectedPath = string.Empty;
        var service = new CodexConfigReadabilityService(
            () => home,
            path =>
            {
                inspectedPath = path;
                return exists;
            },
            path =>
            {
                inspectedPath = path;
                return readable;
            });

        var result = service.Inspect();
        var expectedPath = Path.GetFullPath(Path.Combine(home, "config.toml"));
        Assert(result.State == CodexConfigReadabilityState.Readable && result.IsReadable &&
            result.Path == expectedPath && inspectedPath == expectedPath,
            "配置状态只检查注入路径下 config.toml 是否存在且能打开读取");

        exists = false;
        result = service.Inspect();
        Assert(result.State == CodexConfigReadabilityState.Missing && !result.IsReadable,
            "缺少 config.toml 时报告 Missing");

        exists = true;
        readable = false;
        result = service.Inspect();
        Assert(result.State == CodexConfigReadabilityState.Unreadable && !result.IsReadable,
            "存在但不可读的 config.toml 不会被报告为可读取");

        var invalid = new CodexConfigReadabilityService(() => null, _ => throw new InvalidOperationException(), _ => true).Inspect();
        Assert(invalid.State == CodexConfigReadabilityState.InvalidPath && invalid.Path.Length == 0,
            "无有效 Codex 配置目录时报告 InvalidPath");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Codex config readability test failed: " + message);
    }
}
