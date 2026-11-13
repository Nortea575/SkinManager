using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using SkinManager.Updating;

namespace SkinManager.Desktop;

public partial class UpdatesWindow : Window
{
    private readonly UpdateCoordinator? _coordinator;
    private readonly Func<bool> _canInstall;
    private readonly DispatcherTimer _availabilityTimer;
    private bool _initializing = true;
    private bool _installPending;

    public UpdatesWindow(UpdateCoordinator? coordinator, Func<bool>? canInstall = null)
    {
        _coordinator = coordinator;
        _canInstall = canInstall ?? (() => true);
        InitializeComponent();
        AppLocalization.LanguageChanged += Localization_LanguageChanged;
        Icon = App.ReadApplicationIcon();
        WindowMotion.Attach(this);
        HistoryEntries.ItemsSource = ChangelogReader.ReadEmbedded();
        _availabilityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _availabilityTimer.Tick += (_, _) => RefreshState();
        if (_coordinator is not null)
        {
            _coordinator.PropertyChanged += Coordinator_PropertyChanged;
            CurrentVersionText.Text = AppLocalization.Format(
                "当前版本：{0}{1}", _coordinator.CurrentVersionLabel,
                _coordinator.CurrentVersionIsPrerelease ? AppLocalization.Get("（测试版）") : string.Empty);
            AutomaticCheckBox.IsChecked = _coordinator.AutomaticCheckAndDownload;
            PrereleaseCheckBox.IsChecked = _coordinator.IncludePrereleases;
        }
        else
        {
            CurrentVersionText.Text = AppLocalization.Format("当前版本：{0}{1}",
                UpdateCoordinator.ReadCurrentVersionTag(), string.Empty);
            ChannelText.Text = AppLocalization.Get("更新通道：不可用");
            StatusText.Text = AppLocalization.Get("当前没有可用的更新服务。");
            AutomaticCheckBox.IsEnabled = false;
            PrereleaseCheckBox.IsEnabled = false;
            CheckButton.IsEnabled = false;
        }

        _initializing = false;
        SetActiveView(history: false);
        Loaded += (_, _) => _availabilityTimer.Start();
        Closed += (_, _) =>
        {
            _availabilityTimer.Stop();
            if (_coordinator is not null)
            {
                _coordinator.PropertyChanged -= Coordinator_PropertyChanged;
            }
            AppLocalization.LanguageChanged -= Localization_LanguageChanged;
        };
        RefreshState();
    }

    public event Func<Task<bool>>? InstallRequested;

    private void Coordinator_PropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshState();

    private void Localization_LanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => Localization_LanguageChanged(sender, e));
            return;
        }

        HistoryEntries.ItemsSource = ChangelogReader.ReadEmbedded();
        RefreshState();
    }

    private void ViewTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
        {
            SetActiveView(HistoryTab.IsChecked == true);
        }
    }

    private void SetActiveView(bool history)
    {
        UpdateView.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
        HistoryView.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        UpdateActions.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshState()
    {
        if (_coordinator is null)
        {
            return;
        }

        CurrentVersionText.Text = AppLocalization.Format(
            "当前版本：{0}{1}", _coordinator.CurrentVersionLabel,
            _coordinator.CurrentVersionIsPrerelease ? AppLocalization.Get("（测试版）") : string.Empty);
        ChannelText.Text = AppLocalization.Get(_coordinator.IncludePrereleases
            ? "更新通道：正式版与测试版"
            : "更新通道：仅正式版");
        StatusText.Text = _coordinator.StatusMessage;
        ReleaseVersionText.Text = _coordinator.Release is { } release
            ? AppLocalization.Format("可用版本：{0}{1}", release.DisplayVersion,
                AppLocalization.Get(release.IsPrerelease ? "（测试版）" : "（正式版）"))
            : _coordinator.CheckState switch
            {
                UpdateCheckState.UpToDate => AppLocalization.Get("没有可用的新版本。"),
                UpdateCheckState.NotPublished => AppLocalization.Get("此版本尚未发布可用的更新。"),
                UpdateCheckState.PackageNotReady => AppLocalization.Get("发现新版本，但安装包尚未准备好。"),
                _ => ""
            };
        ReleaseSizeText.Text = _coordinator.Release is { } releaseWithSize
            ? AppLocalization.Format("安装包大小：{0}", FormatBytes(releaseWithSize.Size))
            : "";
        ReleaseNotesText.Text = _coordinator.Release?.Notes ?? AppLocalization.Get("检查更新后，发布说明会显示在这里。");
        AutomaticCheckBox.IsChecked = _coordinator.AutomaticCheckAndDownload;
        PrereleaseCheckBox.IsChecked = _coordinator.IncludePrereleases;
        PrereleaseCheckBox.IsEnabled = !_coordinator.IsWorking && !_installPending;
        CheckButton.IsEnabled = !_coordinator.IsWorking && !_installPending;
        DownloadButton.Visibility = _coordinator.HasAvailableUpdate ? Visibility.Visible : Visibility.Collapsed;
        DownloadButton.Content = AppLocalization.Get(_coordinator.HasDownloadedUpdate ? "重新下载" : "下载更新");
        DownloadButton.IsEnabled = !_coordinator.IsWorking && !_installPending;
        CancelDownloadButton.Visibility = _coordinator.IsDownloading ? Visibility.Visible : Visibility.Collapsed;
        CancelDownloadButton.IsEnabled = _coordinator.IsDownloading;
        InstallButton.Visibility = _coordinator.HasDownloadedUpdate ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.IsEnabled = _coordinator.HasDownloadedUpdate && !_coordinator.IsWorking &&
                                  !_installPending && CanInstallNow();
        DownloadProgress.Visibility = _coordinator.IsDownloading ? Visibility.Visible : Visibility.Collapsed;
        ProgressText.Visibility = _coordinator.IsDownloading ? Visibility.Visible : Visibility.Collapsed;
        if (_coordinator.Progress is { } progress)
        {
            DownloadProgress.IsIndeterminate = progress.Percent is null;
            DownloadProgress.Value = progress.Percent ?? 0;
            ProgressText.Text = string.Format(AppLocalization.Culture,
                "{0} / {1}", FormatBytes(progress.BytesReceived), FormatBytes(progress.TotalBytes));
        }
    }

    private bool CanInstallNow()
    {
        try
        {
            return _canInstall();
        }
        catch
        {
            return false;
        }
    }

    private async void CheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator is not null)
        {
            await _coordinator.CheckAsync();
        }
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator is not null)
        {
            await _coordinator.DownloadAsync();
        }
    }

    private void CancelDownloadButton_Click(object sender, RoutedEventArgs e) => _coordinator?.CancelDownload();

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator?.HasDownloadedUpdate != true || !CanInstallNow() || _installPending)
        {
            RefreshState();
            return;
        }

        _installPending = true;
        RefreshState();
        Close();
        try
        {
            var handler = InstallRequested;
            if (handler is not null)
            {
                foreach (Func<Task<bool>> callback in handler.GetInvocationList())
                {
                    if (await callback())
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            _installPending = false;
            if (IsVisible)
            {
                RefreshState();
            }
        }
    }

    private void AutomaticCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initializing && _coordinator is not null)
        {
            _coordinator.AutomaticCheckAndDownload = AutomaticCheckBox.IsChecked == true;
        }
    }

    private void PrereleaseCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initializing && _coordinator is not null)
        {
            _coordinator.IncludePrereleases = PrereleaseCheckBox.IsChecked == true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var kibibytes = bytes / 1024d;
        if (kibibytes < 1024)
        {
            return kibibytes.ToString("0.#", AppLocalization.Culture) + " KB";
        }

        return (kibibytes / 1024).ToString("0.##", AppLocalization.Culture) + " MB";
    }
}
