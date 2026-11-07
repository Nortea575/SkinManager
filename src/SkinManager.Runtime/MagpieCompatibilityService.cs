using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace SkinManager.Runtime;

public enum MagpieCompatibilitySupport
{
    NotInstalled,
    Unsupported,
    Unverified,
    InvalidExecutable,
    UnsupportedPlatform
}

public sealed record MagpieCompatibilityResult(
    bool IsInstalled,
    bool IsRunning,
    string? ExecutablePath,
    string? ProductVersion,
    MagpieCompatibilitySupport Support,
    bool CanLaunch,
    string Evidence,
    string Message);

internal sealed record MagpieExecutableMetadata(
    string? CompanyName,
    string? ProductName,
    string? OriginalFilename,
    string? ProductVersion);

internal interface IMagpieCompatibilityPlatform
{
    bool IsWindows { get; }
    string? FindDefaultExecutable();
    bool FileExists(string path);
    MagpieExecutableMetadata ReadMetadata(string path);
    bool IsRunning();
    Task LaunchAsync(string path, CancellationToken cancellationToken);
}

public sealed class MagpieCompatibilityService
{
    private const string VerifiedProductVersion = "0.1.3237";
    private const string VerifiedEvidence =
        "已核对官方 Magpie v0.1.3237 源码：Windows 重开 Codex 时通过应用标识启动，未传递远程调试参数。";

    private readonly IMagpieCompatibilityPlatform _platform;

    public MagpieCompatibilityService()
        : this(WindowsMagpieCompatibilityPlatform.Instance)
    {
    }

    internal MagpieCompatibilityService(IMagpieCompatibilityPlatform platform)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    public MagpieCompatibilityResult Inspect(string? selectedExecutablePath = null)
    {
        if (!_platform.IsWindows)
        {
            return new MagpieCompatibilityResult(
                false,
                false,
                null,
                null,
                MagpieCompatibilitySupport.UnsupportedPlatform,
                false,
                "Magpie 与 Codex 的启动流程仅在 Windows 上核对。",
                "Magpie 检测与启动流程仅支持 Windows。");
        }

        var hasSelection = !string.IsNullOrWhiteSpace(selectedExecutablePath);
        var candidate = hasSelection ? selectedExecutablePath : _platform.FindDefaultExecutable();
        if (string.IsNullOrWhiteSpace(candidate))
            return NotInstalled();

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return InvalidExecutable(candidate);
        }

        if (!_platform.FileExists(fullPath))
            return hasSelection ? InvalidExecutable(fullPath) : NotInstalled();

        MagpieExecutableMetadata metadata;
        try
        {
            metadata = _platform.ReadMetadata(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            return InvalidExecutable(fullPath);
        }

        if (!IsOfficialMagpieProduct(metadata))
            return InvalidExecutable(fullPath);

        var productVersion = NormalizeVersion(metadata.ProductVersion);
        var support = productVersion == VerifiedProductVersion
            ? MagpieCompatibilitySupport.Unsupported
            : MagpieCompatibilitySupport.Unverified;
        var evidence = support == MagpieCompatibilitySupport.Unsupported
            ? VerifiedEvidence
            : "仅 Magpie v0.1.3237 的启动行为已与官方源码核对；其他版本的行为未验证。";
        var message = support == MagpieCompatibilitySupport.Unsupported
            ? "已核对的 Magpie 0.1.3237 在自身重新打开 Codex 时不附带远程调试参数。需要换肤时从 SkinManager 启动；网关模型配置与皮肤启动参数相互独立。"
            : "此版本的 Magpie 自行重新打开 Codex 时是否保留远程调试参数尚未核实。需要换肤时可从 SkinManager 启动；网关模型配置与皮肤启动参数相互独立。";

        return new MagpieCompatibilityResult(
            true,
            _platform.IsRunning(),
            fullPath,
            metadata.ProductVersion,
            support,
            true,
            evidence,
            message);
    }

    public async Task LaunchAsync(string? selectedExecutablePath = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = Inspect(selectedExecutablePath);
        if (!result.IsInstalled || !result.CanLaunch || result.ExecutablePath is null)
            throw new InvalidOperationException(result.Message);

        await _platform.LaunchAsync(result.ExecutablePath, cancellationToken).ConfigureAwait(false);
    }

    internal static bool IsOfficialMagpieProduct(MagpieExecutableMetadata metadata) =>
        string.Equals(metadata.CompanyName?.Trim(), "magpie", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(metadata.ProductName?.Trim(), "magpie", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(metadata.OriginalFilename?.Trim(), "magpie.exe", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var version = value.Trim().TrimStart('v', 'V');
        var metadataSeparator = version.IndexOf('+');
        if (metadataSeparator >= 0)
            version = version[..metadataSeparator];
        return version;
    }

    private static MagpieCompatibilityResult NotInstalled() => new(
        false,
        false,
        null,
        null,
        MagpieCompatibilitySupport.NotInstalled,
        false,
        "未发现带有 Magpie 官方产品信息的可执行文件。",
        "未检测到 Magpie。可以手动选择已安装或便携版的 magpie.exe。");

    private static MagpieCompatibilityResult InvalidExecutable(string path) => new(
        false,
        false,
        path,
        null,
        MagpieCompatibilitySupport.InvalidExecutable,
        false,
        "已检查可执行文件的产品信息；它与官方 yetone/magpie 的标识不匹配。",
        "所选文件无法确认为 Magpie，请选择官方 Magpie 的可执行文件。");
}

internal sealed class WindowsMagpieCompatibilityPlatform : IMagpieCompatibilityPlatform
{
    public static WindowsMagpieCompatibilityPlatform Instance { get; } = new();

    private WindowsMagpieCompatibilityPlatform() { }

    public bool IsWindows => OperatingSystem.IsWindows();

    public string? FindDefaultExecutable()
    {
        var runningPath = FindRunningExecutable();
        if (runningPath is not null)
            return runningPath;

        var candidates = new List<string>();
        var appPath = ReadCurrentUserAppPath();
        if (!string.IsNullOrWhiteSpace(appPath))
            candidates.Add(appPath);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            candidates.Add(Path.Combine(localAppData, "Programs", "magpie", "magpie.exe"));

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            candidates.Add(Path.Combine(programFiles, "magpie", "magpie.exe"));

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            candidates.Add(Path.Combine(programFilesX86, "magpie", "magpie.exe"));

        foreach (var path in candidates)
        {
            var verifiedPath = VerifyExecutable(path);
            if (verifiedPath is not null)
                return verifiedPath;
        }

        return null;
    }

    private static string? FindRunningExecutable()
    {
        foreach (var process in Process.GetProcessesByName("magpie"))
        {
            try
            {
                var verifiedPath = VerifyExecutable(process.MainModule?.FileName);
                if (verifiedPath is not null)
                    return verifiedPath;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or IOException or System.Security.SecurityException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    private static string? ReadCurrentUserAppPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\magpie.exe",
                writable: false);
            return key?.GetValue(null) is string path
                ? Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string? VerifyExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var fullPath = Path.GetFullPath(path);
            return MagpieCompatibilityService.IsOfficialMagpieProduct(ReadMetadataFor(path)) ? fullPath : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or Win32Exception or System.Security.SecurityException)
        {
            return null;
        }
    }

    public bool FileExists(string path) => File.Exists(path);

    public MagpieExecutableMetadata ReadMetadata(string path) => ReadMetadataFor(path);

    private static MagpieExecutableMetadata ReadMetadataFor(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return new MagpieExecutableMetadata(info.CompanyName, info.ProductName, info.OriginalFilename, info.ProductVersion);
    }

    public bool IsRunning() => FindRunningExecutable() is not null;

    public Task LaunchAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
