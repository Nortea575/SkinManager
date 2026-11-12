using System.IO;
using SkinManager.Desktop;

namespace SkinManager.Preview.Tests;

internal static class StartupPreferencesChecks
{
    internal static void RunServiceChecks(string testRoot)
    {
        var path = Path.Combine(testRoot, "startup-preferences", "startup-preferences.json");
        var runRegistration = new FakeStartupRunRegistration();
        var service = new StartupPreferencesService(path, runRegistration);

        var initial = service.Read();
        Require(!initial.ManagerStartsAtWindowsSignIn && !initial.ApplyLastSelectedSkin &&
                initial.LastSelectedSkinId is null && !initial.ShouldApplyLastSelectedSkinOnAutostart,
            "开机偏好首次读取使用关闭状态且不要求自动应用");

        var rejected = false;
        try
        {
            service.SetApplyLastSelectedSkin(true);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        Require(rejected && !File.Exists(path), "管理器自启关闭时拒绝启用上次皮肤自动应用");
        service.SetManagerStartsAtWindowsSignIn(true);
        service.SetApplyLastSelectedSkin(true);
        service.RecordLastSelectedSkin("Saya");
        var enabled = service.Read();
        Require(enabled.ManagerStartsAtWindowsSignIn && enabled.ApplyLastSelectedSkin &&
                enabled.LastSelectedSkinId == "Saya" && enabled.ShouldApplyLastSelectedSkinOnAutostart,
            "当前用户 Run 状态、自动应用偏好与上次皮肤 ID 可独立保存并恢复");

        var disabled = service.SetManagerStartsAtWindowsSignIn(false);
        Require(!disabled.ManagerStartsAtWindowsSignIn && disabled.ApplyLastSelectedSkin &&
                disabled.LastSelectedSkinId == "Saya" && !disabled.ShouldApplyLastSelectedSkinOnAutostart,
            "关闭管理器自启后保留第二项偏好，但自动应用立即失效");
        Require(runRegistration.WriteCalls == 2 && runRegistration.IsEnabled() == false,
            "测试只写入假 Run 注册，不访问当前用户注册表");

        var commandLine = WindowsCurrentUserRunRegistration.BuildCommandLine(
            @"C:\Program Files\SkinManager\SkinManager.exe");
        Require(commandLine == "\"C:\\Program Files\\SkinManager\\SkinManager.exe\" --autostart",
            "Run 命令行引用管理器路径并附带明确的 --autostart 参数");
        Require(App.IsAutostartInvocation(["--autostart"]) &&
                App.IsAutostartInvocation(["--other", "--AUTOSTART"]) &&
                !App.IsAutostartInvocation(["--autostart=true"]) &&
                !App.IsAutostartInvocation(Array.Empty<string>()),
            "只有完整的 --autostart 参数会标记 Windows 登录启动");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"开机偏好检查失败：{message}");
        }

        Console.WriteLine($"通过：{message}");
    }
}

internal sealed class FakeStartupRunRegistration : IStartupRunRegistration
{
    public bool Enabled { get; private set; }

    public int WriteCalls { get; private set; }

    public bool IsEnabled() => Enabled;

    public void SetEnabled(bool enabled)
    {
        WriteCalls++;
        Enabled = enabled;
    }
}
