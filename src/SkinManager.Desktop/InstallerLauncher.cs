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
            throw new FileNotFoundException("已下载的安装包不存在。", fullInstallerPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fullInstallerPath,
            WorkingDirectory = fullInstallDirectory,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("/SILENT");
        startInfo.ArgumentList.Add("/NORESTART");
        startInfo.ArgumentList.Add("/SP-");
        startInfo.ArgumentList.Add("/SKINMANAGERUPDATE=1");
        startInfo.ArgumentList.Add($"/DIR={fullInstallDirectory}");
        using var process = Process.Start(startInfo);
        return process is not null;
    }
}
