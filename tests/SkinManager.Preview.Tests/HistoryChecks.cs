using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using SkinManager.Desktop;
using SkinManager.Updating;

namespace SkinManager.Preview.Tests;

internal static class HistoryChecks
{
    internal static async Task RunAsync()
    {
        var releases = ChangelogReader.ReadEmbedded();
        var currentVersionTag = UpdateCoordinator.ReadCurrentVersionTag();
        var previousBetaVersionTag = GetPreviousBetaVersionTag(currentVersionTag);
        var expectedPriorVersions = new List<string>();
        if (previousBetaVersionTag is not null)
        {
            expectedPriorVersions.Add(previousBetaVersionTag);
        }
        expectedPriorVersions.Add("1.1.3");
        expectedPriorVersions.Add("1.1.3-beta.1");
        expectedPriorVersions.Add("1.1.2");
        Check(releases.Count >= 4, "内嵌更新日志包含多个公开版本");
        var releaseVersions = releases.Select(release => release.Version).ToArray();
        var priorVersionIndices = expectedPriorVersions
            .Select(version => Array.IndexOf(releaseVersions, version))
            .ToArray();
        Check(releases[0].Version == currentVersionTag &&
              priorVersionIndices.All(index => index > 0) &&
              priorVersionIndices.SequenceEqual(priorVersionIndices.OrderBy(index => index)),
            "历史版本按 CHANGELOG 顺序显示当前测试版及既有正式版、测试版");
        var currentRelease = releases.Single(release => release.Version == currentVersionTag);
        Check(!string.IsNullOrWhiteSpace(currentRelease.DateLabel) &&
              currentRelease.Body.Length > 0 &&
              !currentRelease.Body.Contains("**", StringComparison.Ordinal),
            "当前版本历史保留发布日期与正文并转成原生纯文本");
        var gifBetaRelease = releases.Single(release => release.Version == "1.1.4-beta.1");
        Check(gifBetaRelease.DateLabel == "2026-11-12" &&
              gifBetaRelease.Body.Contains("GIF", StringComparison.Ordinal) &&
              gifBetaRelease.Body.Contains("自由调色", StringComparison.Ordinal) &&
              !gifBetaRelease.Body.Contains("**", StringComparison.Ordinal),
            "上一测试版保留 GIF 与自由调色功能条目");
        var stableRelease = releases.Single(release => release.Version == "1.1.3");
        Check(stableRelease.DateLabel == "2026-11-10" &&
              stableRelease.Body.Contains("保持活动连接", StringComparison.Ordinal) &&
              stableRelease.Body.Contains("直接拖动皮肤排序", StringComparison.Ordinal),
            "1.1.3 正式版历史仍保留连接与排序功能条目");
        var betaOneRelease = releases.Single(release => release.Version == "1.1.3-beta.1");
        Check(betaOneRelease.DateLabel == "2026-11-10" &&
              betaOneRelease.Body.Contains("离线查看历史更新日志", StringComparison.Ordinal) &&
              !betaOneRelease.Body.Contains("**", StringComparison.Ordinal),
            "beta.1 历史正文仍保留历史日志功能条目");
        Check(releases.Single(release => release.Version == "1.1.2").Body
                  .Contains("可选测试版更新", StringComparison.Ordinal),
            "正式版历史正文完整保留功能条目");

        var testRoot = Path.Combine(Path.GetTempPath(), "SkinManagerHistory", Guid.NewGuid().ToString("N"));
        var screenshotDirectory = Program.FindTestOutputDirectory();
        Directory.CreateDirectory(screenshotDirectory);
        var screenshots = new Dictionary<string, string>
        {
            ["update-normal"] = Path.Combine(screenshotDirectory, "版本与更新-检查更新-常规窗口.png"),
            ["history-normal"] = Path.Combine(screenshotDirectory, "版本与更新-历史日志-常规窗口.png"),
            ["update-minimum"] = Path.Combine(screenshotDirectory, "版本与更新-检查更新-最小窗口.png"),
            ["history-minimum"] = Path.Combine(screenshotDirectory, "版本与更新-历史日志-最小窗口.png")
        };
        Directory.CreateDirectory(testRoot);

        var client = new CountingUpdateClient(Path.Combine(testRoot, "downloads"));
        using var coordinator = new UpdateCoordinator(
            client,
            Path.Combine(testRoot, "preferences"),
            UpdateCoordinator.ReadCurrentVersion(),
            currentVersionTag: currentVersionTag);
        var window = new UpdatesWindow(coordinator)
        {
            Width = 580,
            Height = 650,
            Left = -2000,
            Top = -2000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };
        try
        {
            Check(!coordinator.IncludePrereleases &&
                  window.FindName("PrereleaseCheckBox") is CheckBox { IsChecked: false },
                "测试版开关默认关闭且与旧更新通道偏好一致");

            coordinator.IncludePrereleases = true;
            await coordinator.CheckAsync();
            await coordinator.DownloadAsync();
            Check(client.CheckCount == 1 && client.DownloadCount == 1 && coordinator.HasDownloadedUpdate,
                "打开历史前已有候选测试版和已下载的安装包");

            window.Show();
            await WaitForWindowAsync(window);
            await Program.SaveWindowScreenshotAsync(window, screenshots["update-normal"]);

            var historyTab = (RadioButton)window.FindName("HistoryTab")!;
            historyTab.IsChecked = true;
            window.UpdateLayout();
            var historyEntries = (ItemsControl)window.FindName("HistoryEntries")!;
            var historyScrollViewer = (ScrollViewer)window.FindName("HistoryScrollViewer")!;
            await Program.SaveWindowScreenshotAsync(window, screenshots["history-normal"]);
            Check(historyEntries.Items.Count == releases.Count &&
                  historyScrollViewer.ActualHeight > 100 && historyScrollViewer.ScrollableHeight > 0,
                "常规窗口内历史条目进入可滚动区域");
            Check(client.CheckCount == 1 && client.DownloadCount == 1 &&
                  coordinator.Release?.DisplayVersion == "1.1.5-beta.1" && coordinator.HasDownloadedUpdate,
                "切换到离线历史页不请求网络且保留更新候选和下载结果");
            Check(((StackPanel)window.FindName("UpdateActions")!).Visibility == Visibility.Collapsed &&
                  ((Button)window.FindName("CloseButton")!).Visibility == Visibility.Visible,
                "历史页隐藏更新操作并保留关闭入口");

            window.Width = 500;
            window.Height = 560;
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Check(historyScrollViewer.ActualHeight > 100 && historyScrollViewer.ScrollableHeight > 0,
                "最小窗口内历史条目仍位于可滚动区域，没有被窗口边界截断");
            await Program.SaveWindowScreenshotAsync(window, screenshots["history-minimum"]);

            ((RadioButton)window.FindName("CheckUpdatesTab")!).IsChecked = true;
            window.UpdateLayout();
            var updateScrollViewer = (ScrollViewer)window.FindName("UpdateScrollViewer")!;
            Check(((Grid)window.FindName("UpdateView")!).Visibility == Visibility.Visible &&
                  ((Grid)window.FindName("HistoryView")!).Visibility == Visibility.Collapsed &&
                  ((TextBlock)window.FindName("ReleaseVersionText")!).Text == "可用版本：1.1.5-beta.1（测试版）" &&
                  ((Button)window.FindName("InstallButton")!).Visibility == Visibility.Visible &&
                  ((Button)window.FindName("InstallButton")!).IsEnabled,
                "切回检查更新后恢复候选版本信息与安装操作");
            Check(updateScrollViewer.ActualHeight > 100 &&
                  ((StackPanel)window.FindName("UpdateActions")!).Visibility == Visibility.Visible,
                "最小窗口内检查更新页保留滚动区域和固定操作栏");
            await Program.SaveWindowScreenshotAsync(window, screenshots["update-minimum"]);
            Check(client.CheckCount == 1 && client.DownloadCount == 1,
                "在两个视图间往返不会重复检查或下载");
            Console.WriteLine($"版本与更新页截图已输出：{screenshotDirectory}");
        }
        finally
        {
            window.Close();
            TryDeleteDirectory(testRoot);
        }
    }

    private static async Task WaitForWindowAsync(Window window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            if (window.IsLoaded && window.Opacity >= 0.999)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("等待历史日志窗口完成淡入以捕获离线截图超时。");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidDataException($"{label}失败。");
        }

        Console.WriteLine($"通过：{label}");
    }

    private static string? GetPreviousBetaVersionTag(string currentVersionTag)
    {
        const string betaMarker = "-beta.";
        var markerIndex = currentVersionTag.LastIndexOf(betaMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        var betaNumberStart = markerIndex + betaMarker.Length;
        if (!int.TryParse(currentVersionTag[betaNumberStart..], NumberStyles.None, CultureInfo.InvariantCulture, out var betaNumber) ||
            betaNumber <= 1)
        {
            return null;
        }

        return $"{currentVersionTag[..betaNumberStart]}{(betaNumber - 1).ToString(CultureInfo.InvariantCulture)}";
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

    private sealed class CountingUpdateClient(string downloadDirectory) : IUpdateClient
    {
        private readonly UpdateRelease _release = new(
            new Version(1, 1, 5),
            "v1.1.5-beta.1",
            "离线测试候选更新说明。",
            new Uri("https://updates.invalid/SkinManager-Setup-1.1.5-beta.1.exe"),
            4,
            new string('0', 64))
        {
            IsPrerelease = true
        };

        internal int CheckCount { get; private set; }
        internal int DownloadCount { get; private set; }

        public Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default) =>
            CheckAsync(new UpdateCheckRequest(currentVersion), cancellationToken);

        public Task<UpdateCheckResult> CheckAsync(UpdateCheckRequest request, CancellationToken cancellationToken = default)
        {
            CheckCount++;
            return Task.FromResult(new UpdateCheckResult(UpdateCheckState.Available, _release));
        }

        public async Task<DownloadedUpdate> DownloadAsync(
            UpdateRelease release,
            IProgress<UpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadCount++;
            Directory.CreateDirectory(downloadDirectory);
            var path = Path.Combine(downloadDirectory, "preview-update.exe");
            await File.WriteAllBytesAsync(path, [0x4D, 0x5A, 0x00, 0x00], cancellationToken);
            progress?.Report(new UpdateDownloadProgress(release.Size, release.Size));
            return new DownloadedUpdate(release, path);
        }

        public Task ValidateAsync(DownloadedUpdate update, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
