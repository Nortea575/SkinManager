using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;
using SkinManager.Core;
using SkinManager.Runtime;
using SkinManager.Updating;

namespace SkinManager.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppThemeService.Initialize();
        UpdateCoordinator? updateCoordinator = null;
        try
        {
            var basePackage = ReadEmbeddedBasePackage();
            var skinsDirectory = Path.Combine(AppContext.BaseDirectory, "skin");
            var store = new SkinStore(skinsDirectory);
            store.Initialize();
            var updatesDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SkinManager",
                "updates");
            var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            updateCoordinator = new UpdateCoordinator(
                new GitHubUpdateClient(httpClient, Path.Combine(updatesDirectory, "cache")),
                updatesDirectory,
                UpdateCoordinator.ReadCurrentVersion(),
                ownedHttpClient: httpClient,
                currentVersionTag: UpdateCoordinator.ReadCurrentVersionTag());
            var window = new MainWindow(
                store,
                basePackage,
                updateCoordinator: updateCoordinator,
                installerLauncher: new ProcessInstallerLauncher(),
                codexInstanceService: new WindowsCodexInstanceService(),
                startupPreferencesService: StartupPreferencesService.CreateForCurrentUser(),
                launchedByAutostart: IsAutostartInvocation(e.Args));
            window.Icon = ReadApplicationIcon();
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            updateCoordinator?.Dispose();
            MessageBox.Show(
                $"皮肤库启动失败。\n\n{exception.Message}",
                "ChatGPT 皮肤管理器",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    internal static bool IsAutostartInvocation(IEnumerable<string> arguments) =>
        arguments.Any(argument => string.Equals(argument, "--autostart", StringComparison.OrdinalIgnoreCase));

    internal static BasePackage ReadEmbeddedBasePackage()
    {
        const string resourceName = "SkinManager.Desktop.Resources.CodexBase-26.1027.3384.0.zip";
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"内置原生预览资源不可用：{resourceName}");
        using var destination = new MemoryStream();
        source.CopyTo(destination);
        return BasePackageReader.ReadZipBytes(destination.ToArray());
    }

    internal static BitmapFrame ReadApplicationIcon()
    {
        var resource = Application.GetResourceStream(new Uri(
            "pack://application:,,,/SkinManager;component/SkinManager.ico", UriKind.Absolute))
            ?? throw new InvalidOperationException("应用程序图标资源不可用。");
        using (resource.Stream)
        {
            var decoder = BitmapDecoder.Create(
                resource.Stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();
        }
    }
}
