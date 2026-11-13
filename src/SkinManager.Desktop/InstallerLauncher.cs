using System.Diagnostics;
using System.IO;

namespace SkinManager.Desktop;

public interface IInstallerLauncher
{
    bool Launch(string installerPath, string installDirectory);
}

public sealed class ProcessInstallerLauncher : IInstallerLauncher
{
    public bool Launch(string installerPath, string installDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        var fullInstallerPath = Path.GetFullPath(installerPath);
        var fullInstallDirectory = Path.GetFullPath(installDirectory);
        if (!File.Exists(fullInstallerPath))
        {
            throw new FileNotFoundException(AppLocalization.Get("已下载的安装包不存在。"), fullInstallerPath);
        }

        using var process = Process.Start(CreateStartInfo(fullInstallerPath, fullInstallDirectory, AppLocalization.IsEnglish));
        return process is not null;
    }

    internal static ProcessStartInfo CreateStartInfo(string installerPath, string installDirectory, bool isEnglish)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(installerPath),
            WorkingDirectory = Path.GetFullPath(installDirectory),
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("/SILENT");
        startInfo.ArgumentList.Add("/NORESTART");
        startInfo.ArgumentList.Add("/SP-");
        startInfo.ArgumentList.Add("/SKINMANAGERUPDATE=1");
        startInfo.ArgumentList.Add($"/LANG={(isEnglish ? "english" : "chinesesimp")}");
        startInfo.ArgumentList.Add($"/DIR={startInfo.WorkingDirectory}");
        return startInfo;
    }
}
