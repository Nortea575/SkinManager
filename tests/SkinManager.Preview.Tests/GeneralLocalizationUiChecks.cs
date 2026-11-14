using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using SkinManager.Core;
using SkinManager.Desktop;
using SkinManager.Runtime;
using SkinManager.Updating;

namespace SkinManager.Preview.Tests;

internal static class GeneralLocalizationUiChecks
{
    internal static async Task RunAsync()
    {
        var previousLanguage = AppLocalization.Preference;
        var testRoot = Path.Combine(Path.GetTempPath(), $"SkinManager-localization-ui-{Guid.NewGuid():N}");
        var screenshotDirectory = Path.Combine(Program.FindTestOutputDirectory(), "localization");
        Directory.CreateDirectory(testRoot);
        Directory.CreateDirectory(screenshotDirectory);

        InstancesWindow? instancesWindow = null;
        UpdatesWindow? updatesWindow = null;
        SupportAuthorWindow? supportWindow = null;
        UpdateCoordinator? coordinator = null;
        try
        {
            AppLocalization.SetLanguage(AppLanguage.English);
            instancesWindow = await VerifyInstancesWindowAsync(screenshotDirectory);
            AppLocalization.SetLanguage(AppLanguage.English);

            var client = new FixtureUpdateClient();
            coordinator = new UpdateCoordinator(
                client,
                Path.Combine(testRoot, "updates"),
                UpdateCoordinator.ReadCurrentVersion(),
                currentVersionTag: UpdateCoordinator.ReadCurrentVersionTag());
            coordinator.AutomaticCheckAndDownload = false;
            coordinator.IncludePrereleases = true;
            await coordinator.CheckAsync();
            updatesWindow = new UpdatesWindow(coordinator)
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
            updatesWindow.Show();
            await WaitForWindowAsync(updatesWindow, () => updatesWindow.StatusText.Text.Length > 0);

            Require(updatesWindow.Title == "Version and updates" &&
                    updatesWindow.StatusText.Text == "Version 1.1.5-beta.1 is available" &&
                    updatesWindow.ChannelText.Text == "Update channel: stable and beta",
                "Updates window title, channel, and live status render in English");

            var historyTab = (RadioButton)updatesWindow.FindName("HistoryTab")!;
            historyTab.IsChecked = true;
            updatesWindow.UpdateLayout();
            var englishHistory = GetHistoryEntries(updatesWindow);
            Require(englishHistory.Count > 0 && englishHistory[0].Version == UpdateCoordinator.ReadCurrentVersionTag() &&
                    englishHistory[0].Body.Contains("Submit suggestions", StringComparison.Ordinal) &&
                    !ContainsCjk(englishHistory[0].Body),
                "English history log is loaded from the embedded English changelog");
            await Program.SaveWindowScreenshotAsync(updatesWindow,
                Path.Combine(screenshotDirectory, "updates-en-history.png"));

            updatesWindow.Width = updatesWindow.MinWidth;
            updatesWindow.Height = updatesWindow.MinHeight;
            await updatesWindow.Dispatcher.InvokeAsync(updatesWindow.UpdateLayout, DispatcherPriority.Background);
            await Program.SaveWindowScreenshotAsync(updatesWindow,
                Path.Combine(screenshotDirectory, "updates-en-minimum.png"));

            supportWindow = new SupportAuthorWindow
            {
                Width = 620,
                Height = 380,
                Left = -2000,
                Top = -2000,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };
            supportWindow.Show();
            await WaitForWindowAsync(supportWindow);
            Require(supportWindow.Title == "Suggestions" &&
                    FindNamed<TextBlock>(supportWindow, "FreeDistributionText").Text.Contains("free", StringComparison.OrdinalIgnoreCase) &&
                    supportWindow.FontFamily.Source.Contains("Space Grotesk", StringComparison.OrdinalIgnoreCase) &&
                    supportWindow.FontFamily.Source.Contains("Noto Sans SC", StringComparison.OrdinalIgnoreCase),
                "Support panel renders English text with the packaged Latin and Chinese font fallbacks");
            await Program.SaveWindowScreenshotAsync(supportWindow,
                Path.Combine(screenshotDirectory, "support-author-en.png"));

            Uri? sponsorUri = null;
            Uri? githubUri = null;
            var sponsorLaunchCount = 0;
            supportWindow.ExternalUriLauncher = uri =>
            {
                sponsorUri = uri;
                sponsorLaunchCount++;
            };
            FindNamed<Button>(supportWindow, "SupportAuthorLinkButton")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(sponsorUri is not null && GetQueryValue(sponsorUri, "page_id") == "170" &&
                    GetQueryValue(sponsorUri, "lang") == "en",
                "English support link preserves page_id=170 and requests the English market page");
            supportWindow.ExternalUriLauncher = uri => githubUri = uri;
            FindNamed<Button>(supportWindow, "GitHubLinkButton")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(githubUri?.AbsoluteUri == "https://github.com/Nortea575/SkinManager",
                "GitHub link remains unchanged when the interface is English");

            var englishStartInfo = ProcessInstallerLauncher.CreateStartInfo(
                Path.Combine(testRoot, "installer.exe"),
                Path.Combine(testRoot, "Program Files", "SkinManager"),
                isEnglish: true);
            var chineseStartInfo = ProcessInstallerLauncher.CreateStartInfo(
                Path.Combine(testRoot, "installer.exe"),
                Path.Combine(testRoot, "Program Files", "SkinManager"),
                isEnglish: false);
            var expectedInstallerArguments = new[]
            {
                "/SILENT", "/NORESTART", "/SP-", "/SKINMANAGERUPDATE=1"
            };
            Require(englishStartInfo.ArgumentList.Take(4).SequenceEqual(expectedInstallerArguments) &&
                    englishStartInfo.ArgumentList[4] == "/LANG=english" &&
                    englishStartInfo.ArgumentList[5] == $"/DIR={englishStartInfo.WorkingDirectory}" &&
                    chineseStartInfo.ArgumentList.Take(4).SequenceEqual(expectedInstallerArguments) &&
                    chineseStartInfo.ArgumentList[4] == "/LANG=chinesesimp" &&
                    chineseStartInfo.ArgumentList[5] == $"/DIR={chineseStartInfo.WorkingDirectory}" &&
                    !englishStartInfo.UseShellExecute && !chineseStartInfo.UseShellExecute,
                "Installer launch arguments preserve silent/update flow and carry the selected language without launching");

            AppLocalization.SetLanguage(AppLanguage.SimplifiedChinese);
            await updatesWindow.Dispatcher.InvokeAsync(updatesWindow.UpdateLayout, DispatcherPriority.Background);
            await supportWindow.Dispatcher.InvokeAsync(supportWindow.UpdateLayout, DispatcherPriority.Background);
            await instancesWindow!.Dispatcher.InvokeAsync(instancesWindow.UpdateLayout, DispatcherPriority.Background);
            var chineseSupportButton = FindNamed<Button>(supportWindow, "SupportAuthorLinkButton");

            Require(updatesWindow.Title == "版本与更新" &&
                    updatesWindow.StatusText.Text == "发现新版本 1.1.5-beta.1" &&
                    GetHistoryEntries(updatesWindow)[0].Body.Contains("提出建议", StringComparison.Ordinal) &&
                    FindNamed<TextBox>(updatesWindow, "ReleaseNotesText").Text == "Fixture release notes remain unchanged.",
                "Open update and history window refreshes to Chinese immediately");
            Require(supportWindow.Title == "提出建议" &&
                    FindNamed<TextBlock>(supportWindow, "FreeDistributionText").Text.Contains("免费", StringComparison.Ordinal),
                "Open support author panel refreshes to Chinese immediately");

            sponsorUri = null;
            supportWindow.ExternalUriLauncher = uri =>
            {
                sponsorUri = uri;
                sponsorLaunchCount++;
            };
            Console.WriteLine($"Support-link diagnostic before Chinese click: preference={AppLocalization.Preference}, code={AppLocalization.LanguageCode}, enabled={chineseSupportButton.IsEnabled}, visible={chineseSupportButton.IsVisible}, launcherCalls={sponsorLaunchCount}");
            chineseSupportButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await supportWindow.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            var chineseSponsorUri = sponsorUri?.AbsoluteUri ?? "<not invoked>";
            Console.WriteLine($"Support-link diagnostic after Chinese click: uri={chineseSponsorUri}, preference={AppLocalization.Preference}, code={AppLocalization.LanguageCode}, enabled={chineseSupportButton.IsEnabled}, visible={chineseSupportButton.IsVisible}, launcherCalls={sponsorLaunchCount}");
            Require(sponsorUri is not null && GetQueryValue(sponsorUri, "page_id") == "170" &&
                    GetQueryValue(sponsorUri, "lang") == "zh",
                $"Chinese support link preserves page_id=170 and requests the Chinese market page (uri={chineseSponsorUri}, preference={AppLocalization.Preference}, code={AppLocalization.LanguageCode}, enabled={chineseSupportButton.IsEnabled}, visible={chineseSupportButton.IsVisible}, launcherCalls={sponsorLaunchCount})");
            Console.WriteLine($"Localization UI screenshots saved to: {screenshotDirectory}");
        }
        finally
        {
            if (supportWindow?.IsVisible == true)
            {
                supportWindow.Close();
            }
            if (updatesWindow?.IsVisible == true)
            {
                updatesWindow.Close();
            }
            if (instancesWindow?.IsVisible == true)
            {
                instancesWindow.Close();
            }
            coordinator?.Dispose();
            AppLocalization.SetLanguage(previousLanguage);
            TryDeleteDirectory(testRoot);
        }
    }

    private static async Task<InstancesWindow> VerifyInstancesWindowAsync(string screenshotDirectory)
    {
        var debugIdentity = new CodexProcessIdentity(
            53_481,
            new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero),
            @"C:\Fixtures\Codex debug.exe");
        var regularIdentity = new CodexProcessIdentity(
            53_482,
            new DateTimeOffset(2026, 11, 13, 0, 1, 0, TimeSpan.Zero),
            @"C:\Fixtures\Codex.exe");
        var debugInstance = new CodexInstance(
            debugIdentity,
            "Codex 用户自定义标题 {draft}",
            1,
            9335,
            @"C:\Fixtures\Profile")
        {
            Kind = CodexInstanceKind.Debug,
            CanConnect = true,
            DebugConnectionState = CodexDebugConnectionState.Ready,
            DebugEndpoint = "http://127.0.0.1:9335/"
        };
        var regularInstance = new CodexInstance(
            regularIdentity,
            "Codex project",
            2,
            null,
            @"C:\Fixtures\RegularProfile")
        {
            Kind = CodexInstanceKind.Normal,
            CanConnect = false
        };

        var window = new InstancesWindow(
            new FixtureCodexInstanceService([debugInstance, regularInstance]),
            connectedIdentity: debugIdentity)
        {
            Width = 920,
            Height = 560,
            Left = -2000,
            Top = -2000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };
        var verified = false;
        try
        {
            window.Show();
            await WaitForWindowAsync(window, () => window.Instances.Count == 2);

            var list = FindNamed<ListView>(window, "InstanceList");
            var grid = list.View as GridView ?? throw new InvalidOperationException("Instance list has no GridView.");
            Require(grid.Columns.Select(column => column.Header?.ToString()).SequenceEqual(
                        ["Window title", "PID", "Type", "Debug port", "Connection status"], StringComparer.Ordinal) &&
                    FindNamed<TextBlock>(window, "ActionStatus").Text == "Found 2 Codex main instances.",
                "Instance window headers and initial status render in English");

            list.SelectedItem = list.Items.Cast<object>().Single(item => GetInstance(item).Identity == debugIdentity);
            var selectedIdentity = GetSelectedIdentity(list);
            Require(selectedIdentity == debugIdentity &&
                    FindNamed<TextBlock>(window, "ActionStatus").Text.Contains("Verified debug endpoint", StringComparison.Ordinal),
                "Selecting an instance shows an English status message");
            Require(FindRowText(list, "WindowTitle") == "Codex 用户自定义标题 {draft}",
                "User-provided window titles remain unchanged in English UI");
            await Program.SaveWindowScreenshotAsync(window,
                Path.Combine(screenshotDirectory, "instances-en.png"));

            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            await Program.SaveWindowScreenshotAsync(window,
                Path.Combine(screenshotDirectory, "instances-en-minimum.png"));

            AppLocalization.SetLanguage(AppLanguage.SimplifiedChinese);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Require(GetSelectedIdentity(list) == selectedIdentity &&
                    grid.Columns.Select(column => column.Header?.ToString()).SequenceEqual(
                        ["窗口标题", "PID", "类型", "调试端口", "连接状态"], StringComparer.Ordinal) &&
                    FindNamed<TextBlock>(window, "ActionStatus").Text.Contains("已核验调试连接端点", StringComparison.Ordinal),
                "Instance window switches to Chinese while preserving the selected process and status");
            Require(FindRowText(list, "WindowTitle") == "Codex 用户自定义标题 {draft}",
                "User-provided window titles remain unchanged after switching languages");

            verified = true;
            return window;
        }
        finally
        {
            if (!verified && window.IsVisible)
            {
                window.Close();
            }
        }
    }

    private static List<ChangelogEntry> GetHistoryEntries(UpdatesWindow window) =>
        ((IEnumerable<ChangelogEntry>)((ItemsControl)window.FindName("HistoryEntries")!).ItemsSource!).ToList();

    private static CodexProcessIdentity? GetSelectedIdentity(ListView list)
    {
        var selected = list.SelectedItem;
        return selected is null ? null : GetInstance(selected).Identity;
    }

    private static CodexInstance GetInstance(object row) =>
        (CodexInstance)(row.GetType().GetProperty("Instance")?.GetValue(row)
            ?? throw new InvalidOperationException("The selected instance row has no Instance value."));

    private static string FindRowText(ListView list, string propertyName)
    {
        var selected = list.SelectedItem ?? throw new InvalidOperationException("No instance row is selected.");
        var propertyValue = selected.GetType().GetProperty(propertyName)?.GetValue(selected);
        return propertyValue?.ToString() ?? string.Empty;
    }

    private static T FindNamed<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        root.FindName(name) as T ?? throw new InvalidOperationException($"Missing {typeof(T).Name} '{name}'.");

    private static async Task WaitForWindowAsync(Window window, Func<bool>? ready = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            if (window.IsLoaded && window.Opacity >= 0.999 && (ready?.Invoke() ?? true))
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Timed out waiting for {window.GetType().Name} to become ready.");
    }

    private static string? GetQueryValue(Uri uri, string key)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var partKey = Uri.UnescapeDataString(separator < 0 ? part : part[..separator]);
            if (partKey.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(separator < 0 ? string.Empty : part[(separator + 1)..]);
            }
        }

        return null;
    }

    private static bool ContainsCjk(string text) => text.Any(character =>
        character is >= '\u3400' and <= '\u9FFF');

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException($"Localization UI check failed: {message}");
        }

        Console.WriteLine($"Passed: {message}");
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

    private sealed class FixtureCodexInstanceService(IReadOnlyList<CodexInstance> instances) : ICodexInstanceService
    {
        public Task<IReadOnlyList<CodexInstance>> GetInstancesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(instances);

        public Task<CodexLocateResult> LocateAsync(CodexInstance instance, CancellationToken cancellationToken = default) =>
            Task.FromResult(CodexLocateResult.Located);

        public Task<CodexCloseOperationResult> CloseAsync(
            CodexInstance instance,
            bool force = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodexCloseOperationResult(CodexCloseResult.Closed, instance, "Fixture only."));
    }

    private sealed class FixtureUpdateClient : IUpdateClient
    {
        private readonly UpdateRelease _release = new(
            new Version(1, 1, 5),
            "v1.1.5-beta.1",
            "Fixture release notes remain unchanged.",
            new Uri("https://updates.invalid/SkinManager-Setup-1.1.5-beta.1.exe"),
            1024,
            new string('0', 64))
        {
            IsPrerelease = true
        };

        public Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(new UpdateCheckResult(UpdateCheckState.Available, _release));

        public Task<UpdateCheckResult> CheckAsync(UpdateCheckRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new UpdateCheckResult(UpdateCheckState.Available, _release));

        public Task<DownloadedUpdate> DownloadAsync(
            UpdateRelease release,
            IProgress<UpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DownloadedUpdate(release, "fixture-installer.exe"));

        public Task ValidateAsync(DownloadedUpdate update, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
