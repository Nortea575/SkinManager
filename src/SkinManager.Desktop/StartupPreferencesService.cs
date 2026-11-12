using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace SkinManager.Desktop;

public sealed record StartupPreferencesState(
    bool ManagerStartsAtWindowsSignIn,
    bool ApplyLastSelectedSkin,
    string? LastSelectedSkinId)
{
    public bool ShouldApplyLastSelectedSkinOnAutostart =>
        ManagerStartsAtWindowsSignIn && ApplyLastSelectedSkin;
}

public interface IStartupPreferencesService
{
    StartupPreferencesState Read();

    StartupPreferencesState SetManagerStartsAtWindowsSignIn(bool enabled);

    StartupPreferencesState SetApplyLastSelectedSkin(bool enabled);

    void RecordLastSelectedSkin(string skinId);
}

public interface IStartupRunRegistration
{
    bool IsEnabled();

    void SetEnabled(bool enabled);
}

public sealed class StartupPreferencesService : IStartupPreferencesService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _preferencesPath;
    private readonly IStartupRunRegistration _runRegistration;

    public StartupPreferencesService(string preferencesPath, IStartupRunRegistration runRegistration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preferencesPath);
        ArgumentNullException.ThrowIfNull(runRegistration);
        _preferencesPath = Path.GetFullPath(preferencesPath);
        _runRegistration = runRegistration;
    }

    public static StartupPreferencesService CreateForCurrentUser()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var preferencesPath = Path.Combine(localApplicationData, "SkinManager", "startup-preferences.json");
        return new StartupPreferencesService(
            preferencesPath,
            new WindowsCurrentUserRunRegistration(() => Environment.ProcessPath));
    }

    public static StartupPreferencesService CreatePreview(string profileDirectory) => new(
        Path.Combine(Path.GetFullPath(profileDirectory), "startup-preferences.json"),
        new DisabledStartupRunRegistration());

    public StartupPreferencesState Read()
    {
        var preferences = LoadPreferences();
        return new StartupPreferencesState(
            _runRegistration.IsEnabled(),
            preferences.ApplyLastSelectedSkin,
            preferences.LastSelectedSkinId);
    }

    public StartupPreferencesState SetManagerStartsAtWindowsSignIn(bool enabled)
    {
        var preferences = LoadPreferences();
        _runRegistration.SetEnabled(enabled);
        return new StartupPreferencesState(enabled, preferences.ApplyLastSelectedSkin, preferences.LastSelectedSkinId);
    }

    public StartupPreferencesState SetApplyLastSelectedSkin(bool enabled)
    {
        var current = LoadPreferences();
        var managerStartsAtWindowsSignIn = _runRegistration.IsEnabled();
        if (enabled && !managerStartsAtWindowsSignIn)
        {
            throw new InvalidOperationException("请先开启“管理器随 Windows 登录启动”。");
        }

        SavePreferences(current with { ApplyLastSelectedSkin = enabled });
        return new StartupPreferencesState(managerStartsAtWindowsSignIn, enabled, current.LastSelectedSkinId);
    }

    public void RecordLastSelectedSkin(string skinId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skinId);
        var current = LoadPreferences();
        SavePreferences(current with { LastSelectedSkinId = skinId.Trim() });
    }

    private StoredStartupPreferences LoadPreferences()
    {
        if (!File.Exists(_preferencesPath))
        {
            return new StoredStartupPreferences();
        }

        var content = File.ReadAllText(_preferencesPath);
        return JsonSerializer.Deserialize<StoredStartupPreferences>(content)
            ?? throw new InvalidDataException("开机启动设置文件为空或格式无效。");
    }

    private void SavePreferences(StoredStartupPreferences preferences)
    {
        var directory = Path.GetDirectoryName(_preferencesPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("开机启动设置路径无效。");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = _preferencesPath + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(preferences, JsonOptions));
            File.Move(temporaryPath, _preferencesPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record StoredStartupPreferences(bool ApplyLastSelectedSkin = false, string? LastSelectedSkinId = null);

    private sealed class DisabledStartupRunRegistration : IStartupRunRegistration
    {
        public bool IsEnabled() => false;

        public void SetEnabled(bool enabled)
        {
            if (enabled)
            {
                throw new InvalidOperationException("此预览窗口不会写入 Windows 开机启动设置。");
            }
        }
    }
}

public sealed class WindowsCurrentUserRunRegistration : IStartupRunRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SkinManager";
    private readonly Func<string?> _executablePathProvider;

    public WindowsCurrentUserRunRegistration(Func<string?> executablePathProvider)
    {
        ArgumentNullException.ThrowIfNull(executablePathProvider);
        _executablePathProvider = executablePathProvider;
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);
    }

    public void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows 登录自启仅适用于 Windows。");
        }

        if (!enabled)
        {
            using var existingKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            existingKey?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = _executablePathProvider();
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !string.Equals(Path.GetFileNameWithoutExtension(executablePath), "SkinManager", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("只能从 SkinManager.exe 开启 Windows 登录自启；当前进程不是管理器程序。");
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的 Windows 登录自启项。");
        key.SetValue(ValueName, BuildCommandLine(executablePath), RegistryValueKind.String);
    }

    internal static string BuildCommandLine(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var fullPath = Path.GetFullPath(executablePath);
        if (fullPath.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("可执行文件路径包含无效引号。", nameof(executablePath));
        }

        return $"\"{fullPath}\" --autostart";
    }
}
