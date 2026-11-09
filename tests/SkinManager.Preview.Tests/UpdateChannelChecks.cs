using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SkinManager.Desktop;
using SkinManager.Updating;

namespace SkinManager.Preview.Tests;

internal static class UpdateChannelChecks
{
    public static async Task RunAsync()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "SkinManagerUpdateChannel", Guid.NewGuid().ToString("N"));
        var screenshotDirectory = Path.Combine(Program.FindTestOutputDirectory(), "update-channel");
        Directory.CreateDirectory(testRoot);
        Directory.CreateDirectory(screenshotDirectory);
        try
        {
            await VerifyDefaultsAndPersistenceAsync(testRoot);
            await VerifyRequestAndStaleCheckAsync(testRoot);
            await VerifyStaleDownloadAndInstallGuardAsync(testRoot);
            await CaptureUpdateChannelScreenshotsAsync(testRoot, screenshotDirectory);
            Console.WriteLine($"更新通道离线回归与页面截图已输出：{screenshotDirectory}");
        }
        finally
        {
            TryDeleteDirectory(testRoot);
        }
    }

    private static async Task VerifyDefaultsAndPersistenceAsync(string testRoot)
    {
        var updatesDirectory = Path.Combine(testRoot, "preferences");
        using (var coordinator = new UpdateCoordinator(
                   new FakeUpdateClient(Path.Combine(testRoot, "preference-downloads")),
                   updatesDirectory,
                   new Version(1, 1, 2)))
        {
            Check(coordinator.AutomaticCheckAndDownload && !coordinator.IncludePrereleases,
                "默认保留启动自动检查，测试版通道关闭");
            coordinator.AutomaticCheckAndDownload = false;
            await coordinator.CheckAsync();
            Check(coordinator.Release is null && coordinator.CheckState == UpdateCheckState.NotPublished,
                "默认通道忽略客户端意外返回的测试版");
            coordinator.IncludePrereleases = true;
            var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(updatesDirectory, "settings.json")));
            Check(saved.RootElement.GetProperty("IncludePrereleases").GetBoolean() &&
                  !saved.RootElement.GetProperty("AutomaticCheckAndDownload").GetBoolean(),
                "两个更新偏好写入 settings.json");
        }

        using (var reloaded = new UpdateCoordinator(
                   new FakeUpdateClient(Path.Combine(testRoot, "reload-downloads")),
                   updatesDirectory,
                   new Version(1, 1, 2)))
        {
            Check(!reloaded.AutomaticCheckAndDownload && reloaded.IncludePrereleases,
                "重开后保留更新通道和自动检查偏好");
        }

        var legacyDirectory = Path.Combine(testRoot, "legacy-preferences");
        Directory.CreateDirectory(legacyDirectory);
        File.WriteAllText(
            Path.Combine(legacyDirectory, "settings.json"),
            "{\"AutomaticCheckAndDownload\":false}");
        using var legacy = new UpdateCoordinator(
            new FakeUpdateClient(Path.Combine(testRoot, "legacy-downloads")),
            legacyDirectory,
            new Version(1, 1, 2));
        Check(!legacy.AutomaticCheckAndDownload && !legacy.IncludePrereleases,
            "旧设置缺少通道字段时仍兼容并默认关闭测试版");
    }

    private static async Task VerifyRequestAndStaleCheckAsync(string testRoot)
    {
        var client = new FakeUpdateClient(Path.Combine(testRoot, "request-downloads"));
        var updatesDirectory = Path.Combine(testRoot, "request");
        using var coordinator = new UpdateCoordinator(
            client,
            updatesDirectory,
            new Version(1, 1, 3),
            currentVersionTag: "1.1.3-beta.1");
        coordinator.AutomaticCheckAndDownload = false;
        coordinator.IncludePrereleases = true;
        await coordinator.CheckAsync();
        Check(client.LastRequest is { IncludePrereleases: true, CurrentVersionTag: "1.1.3-beta.1" },
            "检查请求携带测试版通道及完整当前版本标签");
        Check(coordinator.CurrentVersionLabel == "1.1.3-beta.1" &&
              client.Release.DisplayVersion == "1.1.4-beta.2" && client.Release.IsPrerelease,
            "beta 当前版本和可用版本保留完整显示标签");

        var lateClient = new FakeUpdateClient(Path.Combine(testRoot, "late-check-downloads"));
        var lateCheck = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lateClient.CheckHandler = (request, cancellationToken) =>
        {
            lateClient.LastRequest = request;
            lateClient.LastCheckCancellationToken = cancellationToken;
            checkStarted.TrySetResult();
            return lateCheck.Task;
        };
        using var lateCoordinator = new UpdateCoordinator(
            lateClient,
            Path.Combine(testRoot, "late-check"),
            new Version(1, 1, 2));
        lateCoordinator.AutomaticCheckAndDownload = false;
        var checkTask = lateCoordinator.CheckAsync();
        await checkStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var busyWindow = new UpdatesWindow(lateCoordinator);
        Check(busyWindow.FindName("PrereleaseCheckBox") is CheckBox { IsEnabled: false },
            "更新检查期间禁用通道开关");
        lateCoordinator.IncludePrereleases = true;
        Check(lateClient.LastCheckCancellationToken.IsCancellationRequested && lateCoordinator.Release is null &&
              lateCoordinator.DownloadedUpdate is null && lateCoordinator.CheckState is null,
            "切换通道取消检查并立即清除旧更新结果");
        lateCheck.TrySetResult(new UpdateCheckResult(UpdateCheckState.Available, client.Release));
        await checkTask;
        Check(lateCoordinator.Release is null && lateCoordinator.DownloadedUpdate is null &&
              lateCoordinator.StatusMessage == "更新通道已更改，请重新检查更新。",
            "旧通道的晚到检查结果不会写回状态");
    }

    private static async Task VerifyStaleDownloadAndInstallGuardAsync(string testRoot)
    {
        var client = new FakeUpdateClient(Path.Combine(testRoot, "late-downloads"));
        var downloadResult = new TaskCompletionSource<DownloadedUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DownloadHandler = (_, cancellationToken) =>
        {
            client.LastDownloadCancellationToken = cancellationToken;
            downloadStarted.TrySetResult();
            return downloadResult.Task;
        };
        using var coordinator = new UpdateCoordinator(
            client,
            Path.Combine(testRoot, "late-download"),
            new Version(1, 1, 2));
        coordinator.AutomaticCheckAndDownload = false;
        coordinator.IncludePrereleases = true;
        await coordinator.CheckAsync();
        var downloadTask = coordinator.DownloadAsync();
        await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var staleDownload = new DownloadedUpdate(client.Release, Path.Combine(testRoot, "fake-beta-setup.exe"));
        coordinator.IncludePrereleases = false;
        Check(client.LastDownloadCancellationToken.IsCancellationRequested && coordinator.Release is null &&
              !coordinator.HasDownloadedUpdate,
            "关闭测试版通道时取消下载并清空可安装更新");
        downloadResult.TrySetResult(staleDownload);
        await downloadTask;
        Check(coordinator.DownloadedUpdate is null && !coordinator.HasDownloadedUpdate,
            "下载器忽略取消时，旧测试包也不会变成可安装状态");
        await ExpectInvalidOperationAsync(
            coordinator.ValidateAsync(staleDownload),
            "旧通道中捕获的测试包不能通过安装前校验");
    }

    private static async Task CaptureUpdateChannelScreenshotsAsync(string testRoot, string screenshotDirectory)
    {
        var defaultCoordinator = new UpdateCoordinator(
            new FakeUpdateClient(Path.Combine(testRoot, "default-downloads")),
            Path.Combine(testRoot, "screenshot-default"),
            new Version(1, 1, 2));
        Check(!defaultCoordinator.IncludePrereleases, "截图默认状态只接收正式版");
        await CaptureWindowAsync(
            new UpdatesWindow(defaultCoordinator) { Width = 560, Height = 640 },
            Path.Combine(screenshotDirectory, "01-默认稳定通道.png"));
        defaultCoordinator.Dispose();

        var enabledCoordinator = new UpdateCoordinator(
            new FakeUpdateClient(Path.Combine(testRoot, "enabled-downloads")),
            Path.Combine(testRoot, "screenshot-enabled"),
            new Version(1, 1, 2));
        enabledCoordinator.IncludePrereleases = true;
        await CaptureWindowAsync(
            new UpdatesWindow(enabledCoordinator) { Width = 560, Height = 640 },
            Path.Combine(screenshotDirectory, "02-接收测试版更新.png"));
        enabledCoordinator.Dispose();

        var betaClient = new FakeUpdateClient(Path.Combine(testRoot, "beta-downloads"));
        var betaCoordinator = new UpdateCoordinator(
            betaClient,
            Path.Combine(testRoot, "screenshot-beta"),
            new Version(1, 1, 3),
            currentVersionTag: "1.1.3-beta.1");
        betaCoordinator.IncludePrereleases = true;
        await betaCoordinator.CheckAsync();
        await betaCoordinator.DownloadAsync();
        var betaWindow = new UpdatesWindow(betaCoordinator) { Width = 560, Height = 640 };
        await CaptureWindowAsync(betaWindow, Path.Combine(screenshotDirectory, "03-beta更新可安装.png"));
        var installButton = betaWindow.FindName("InstallButton") as Button;
        var releaseText = betaWindow.FindName("ReleaseVersionText") as TextBlock;
        Check(betaCoordinator.HasDownloadedUpdate &&
              installButton is { Visibility: Visibility.Visible, IsEnabled: true } &&
              releaseText?.Text == "可用版本：1.1.4-beta.2（测试版）",
            "测试版下载就绪时显示完整标签且保留显式安装按钮");
        var blockedInstallWindow = new UpdatesWindow(betaCoordinator, () => false);
        var blockedInstallButton = blockedInstallWindow.FindName("InstallButton") as Button;
        Check(blockedInstallButton is { Visibility: Visibility.Visible, IsEnabled: false },
            "现有安装资格回调仍可阻止安装按钮");
        betaWindow.Close();
        betaCoordinator.Dispose();
    }

    private static async Task CaptureWindowAsync(UpdatesWindow window, string path)
    {
        window.Show();
        try
        {
            await WaitForWindowAsync(window);
            await Program.SaveWindowScreenshotAsync(window, path);
        }
        finally
        {
            window.Close();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    private static async Task WaitForWindowAsync(Window window)
    {
        var limit = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < limit)
        {
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout());
            if (window.IsLoaded && window.Opacity >= 0.999)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("等待更新窗口完成淡入以捕获离线截图超时。");
    }

    private static async Task ExpectInvalidOperationAsync(Task action, string label)
    {
        try
        {
            await action;
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine($"通过：{label}");
            return;
        }

        throw new InvalidDataException($"{label}失败。");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidDataException($"{label}失败。");
        }

        Console.WriteLine($"通过：{label}");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FakeUpdateClient(string downloadDirectory) : IUpdateClient
    {
        public UpdateRelease Release { get; } = new(
            new Version(1, 1, 4),
            "v1.1.4-beta.2",
            "此处为离线测试版发布说明。\n修复版本与更新页面展示。",
            new Uri("https://updates.invalid/SkinManager-Setup-1.1.4-beta.2.exe"),
            4,
            new string('0', 64))
        {
            IsPrerelease = true
        };

        public UpdateCheckResult CheckResult { get; set; } = new(UpdateCheckState.Available);

        public Func<UpdateCheckRequest, CancellationToken, Task<UpdateCheckResult>>? CheckHandler { get; set; }

        public Func<UpdateRelease, CancellationToken, Task<DownloadedUpdate>>? DownloadHandler { get; set; }

        public UpdateCheckRequest? LastRequest { get; set; }

        public CancellationToken LastCheckCancellationToken { get; set; }

        public CancellationToken LastDownloadCancellationToken { get; set; }

        public Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(CheckResult);

        public Task<UpdateCheckResult> CheckAsync(UpdateCheckRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return CheckHandler is { } handler
                ? handler(request, cancellationToken)
                : Task.FromResult(new UpdateCheckResult(UpdateCheckState.Available, Release));
        }

        public async Task<DownloadedUpdate> DownloadAsync(
            UpdateRelease release,
            IProgress<UpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            LastDownloadCancellationToken = cancellationToken;
            if (DownloadHandler is { } handler)
            {
                return await handler(release, cancellationToken);
            }

            Directory.CreateDirectory(downloadDirectory);
            var path = Path.Combine(downloadDirectory, "fake-update.exe");
            await File.WriteAllBytesAsync(path, [0x4D, 0x5A, 0x00, 0x00], cancellationToken);
            progress?.Report(new UpdateDownloadProgress(release.Size, release.Size));
            return new DownloadedUpdate(release, path);
        }

        public Task ValidateAsync(DownloadedUpdate update, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
