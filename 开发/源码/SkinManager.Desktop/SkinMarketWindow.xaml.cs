using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using SkinManager.Core;
using SkinManager.Market;

namespace SkinManager.Desktop;

internal sealed class SkinMarketRow(SkinMarketItem item) : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;
    private string _previewLabel = string.IsNullOrWhiteSpace(item.PreviewUrl) ? "作者未提供预览图片" : "正在加载预览图片…";
    private string? _previewError;
    private bool _hasPreviewError;
    private bool _isPreviewLoading;
    private string _installStatus = "未安装";
    private bool _canDownload;
    private string _downloadButtonText = "下载并安装";
    private string _operationStatus = string.Empty;
    private bool _operationIsError;
    private double _downloadProgress;
    private Visibility _progressVisibility = Visibility.Collapsed;
    private Visibility _cancelVisibility = Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
    public SkinMarketItem Item { get; } = item;
    public string Id => Item.Id;
    public string Name => Item.Name;
    public string AppearanceLabel => Item.Appearance switch
    {
        "light" => "浅色",
        "dark" => "深色",
        _ => "系统"
    };
    public string SummaryLabel => $"{AppearanceLabel}外观 · skin v{Item.SkinFormatVersion} · v{Item.Version}";
    public string Description => string.IsNullOrWhiteSpace(Item.Description) ? "作者没有提供简介。" : Item.Description;
    public string ShortDescription => Description;
    public string DetailMetadata => string.Join("\n", new[]
    {
        string.IsNullOrWhiteSpace(Item.Author) ? "作者：未提供" : $"作者：{Item.Author}",
        string.IsNullOrWhiteSpace(Item.LicenseNotes) ? "授权说明：未提供" : $"授权说明：{Item.LicenseNotes}",
        $"皮肤格式：v{Item.SkinFormatVersion} · 包大小：{SkinMarketWindow.FormatSize(Item.Size)}",
        $"最低管理器版本：{Item.MinimumManagerVersion}",
        $"SHA-256：{Item.Sha256}"
    });
    public string UpdatedLabel => string.IsNullOrWhiteSpace(Item.UpdatedAt)
        ? string.Empty
        : $"更新于 {SkinMarketWindow.FormatUpdatedAt(Item.UpdatedAt)}";

    public string InstallStatus
    {
        get => _installStatus;
        set => SetField(ref _installStatus, value);
    }

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            if (SetField(ref _thumbnail, value))
            {
                OnPropertyChanged(nameof(HasThumbnail));
            }
        }
    }

    public bool HasThumbnail => Thumbnail is not null;
    public string PreviewLabel
    {
        get => _previewLabel;
        private set => SetField(ref _previewLabel, value);
    }
    public string? PreviewError
    {
        get => _previewError;
        private set => SetField(ref _previewError, value);
    }
    public bool HasPreviewError
    {
        get => _hasPreviewError;
        private set => SetField(ref _hasPreviewError, value);
    }
    public bool IsPreviewLoading
    {
        get => _isPreviewLoading;
        private set => SetField(ref _isPreviewLoading, value);
    }
    public bool CanDownload
    {
        get => _canDownload;
        private set => SetField(ref _canDownload, value);
    }
    public string DownloadButtonText
    {
        get => _downloadButtonText;
        private set => SetField(ref _downloadButtonText, value);
    }
    public string OperationStatus
    {
        get => _operationStatus;
        private set => SetField(ref _operationStatus, value);
    }
    public bool OperationIsError
    {
        get => _operationIsError;
        private set => SetField(ref _operationIsError, value);
    }
    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => SetField(ref _downloadProgress, value);
    }
    public Visibility ProgressVisibility
    {
        get => _progressVisibility;
        private set => SetField(ref _progressVisibility, value);
    }
    public Visibility CancelVisibility
    {
        get => _cancelVisibility;
        private set => SetField(ref _cancelVisibility, value);
    }

    public void SetPreviewLoading()
    {
        Thumbnail = null;
        PreviewError = null;
        HasPreviewError = false;
        IsPreviewLoading = true;
        PreviewLabel = "正在加载预览图片…";
    }

    public void SetPreview(BitmapSource image)
    {
        Thumbnail = image;
        PreviewError = null;
        HasPreviewError = false;
        IsPreviewLoading = false;
        PreviewLabel = string.Empty;
    }

    public void SetPreviewFailure(string message)
    {
        Thumbnail = null;
        PreviewError = message;
        HasPreviewError = true;
        IsPreviewLoading = false;
        PreviewLabel = "预览图片暂不可用";
    }

    public void SetDownloadControls(bool canDownload, string buttonText, bool canCancel)
    {
        CanDownload = canDownload;
        DownloadButtonText = buttonText;
        CancelVisibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetOperationStatus(string text, bool isError = false)
    {
        OperationStatus = text;
        OperationIsError = isError;
    }

    public void SetDownloadProgress(double percentage, bool visible)
    {
        DownloadProgress = Math.Clamp(percentage, 0, 100);
        ProgressVisibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public partial class SkinMarketWindow : Window
{
    private const int ItemsPerPage = 12;
    private readonly SkinMarketClient _client;
    private readonly Version _managerVersion;
    private readonly BasePackage _basePackage;
    private readonly string _previewProfileDirectory;
    private readonly Func<SkinPackage, Task<bool>> _installPackage;
    private readonly Func<IReadOnlyDictionary<string, string>> _readInstalledVersions;
    private readonly ObservableCollection<SkinMarketRow> _rows = [];
    private readonly SemaphoreSlim _previewGate = new(4, 4);
    private CancellationTokenSource? _catalogCancellation;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _downloadCancellation;
    private CancellationTokenSource? _packagePreviewCancellation;
    private SkinMarketRow? _previewRow;
    private SkinMarketRow? _downloadingRow;
    private SkinPackage? _previewPackage;
    private ThemePreviewRenderer? _previewRenderer;
    private WebView2? _previewWebView;
    private string? _failedItemId;
    private int _page = 1;
    private int _totalPages;
    private long _previewSequence;
    private double _catalogScrollOffset;
    private bool _isLoading;
    private bool _isDownloading;
    private bool _canCancelDownload;
    private bool _isClosed;

    internal SkinMarketWindow(
        SkinMarketClient client,
        Version managerVersion,
        BasePackage basePackage,
        string previewProfileDirectory,
        Func<SkinPackage, Task<bool>> installPackage,
        Func<IReadOnlyDictionary<string, string>> readInstalledVersions)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _managerVersion = managerVersion ?? throw new ArgumentNullException(nameof(managerVersion));
        _basePackage = basePackage ?? throw new ArgumentNullException(nameof(basePackage));
        _previewProfileDirectory = Path.GetFullPath(previewProfileDirectory ?? throw new ArgumentNullException(nameof(previewProfileDirectory)));
        _installPackage = installPackage ?? throw new ArgumentNullException(nameof(installPackage));
        _readInstalledVersions = readInstalledVersions ?? throw new ArgumentNullException(nameof(readInstalledVersions));
        InitializeComponent();
        Icon = App.ReadApplicationIcon();
        WindowMotion.Attach(this, animateWindow: false, animateClose: false);
        CatalogCards.ItemsSource = _rows;
        Loaded += async (_, _) => await RefreshAsync(resetPage: true);
        Closing += SkinMarketWindow_Closing;
        Closed += SkinMarketWindow_Closed;
        UpdateControls();
    }

    internal IReadOnlyList<SkinMarketRow> Rows => _rows;
    internal SkinMarketRow? SelectedRow => _previewRow;
    internal SkinPackage? PreviewPackage => _previewPackage;
    internal ThemePreviewRenderer? PreviewRenderer => _previewRenderer;
    internal ContentControl PreviewBrowserHostForTests => PreviewBrowserHost;
    internal ScrollViewer CatalogScrollViewerForTests => CatalogScrollViewer;
    internal RadioButton DarkFilterButtonForTests => FilterDarkButton;
    internal RadioButton FilterAllButtonForTests => FilterAllButton;
    internal bool PreviewInstallButtonIsEnabledForTests => PreviewInstallButton.IsEnabled;
    internal bool IsPreviewPageVisible => PreviewPane.Visibility == Visibility.Visible;
    internal bool IsPackagePreviewLoading => _packagePreviewCancellation is not null;
    internal bool IsDownloading => _isDownloading;
    internal double CatalogScrollOffset => CatalogScrollViewer.VerticalOffset;

    internal async Task RefreshAsync(bool resetPage = false)
    {
        if (_isClosed || _isLoading || _isDownloading)
        {
            return;
        }

        if (resetPage)
        {
            _page = 1;
            _catalogScrollOffset = 0;
        }

        var selectionToRestore = _previewRow?.Id;
        _catalogCancellation?.Cancel();
        _catalogCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _catalogCancellation = cancellation;
        _isLoading = true;
        SetCatalogStatus("正在读取市场目录…");
        CatalogEmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_rows.Count == 0)
        {
            CatalogEmptyTitle.Text = "正在读取市场目录";
            CatalogEmptyMessage.Text = "请稍候。";
            EmptyRetryButton.Visibility = Visibility.Collapsed;
        }
        UpdateControls();

        try
        {
            var result = await _client.GetPageAsync(
                _page,
                ItemsPerPage,
                SearchBox.Text.Trim(),
                GetSelectedAppearance(),
                cancellation.Token);
            if (cancellation.IsCancellationRequested || _isClosed)
            {
                return;
            }

            _page = result.Pagination.Page;
            _totalPages = result.Pagination.TotalPages;
            _previewCancellation?.Cancel();
            _previewCancellation?.Dispose();
            var previewCancellation = new CancellationTokenSource();
            _previewCancellation = previewCancellation;

            _rows.Clear();
            foreach (var item in result.Items)
            {
                _rows.Add(new SkinMarketRow(item));
            }

            RefreshInstalledVersions();
            UpdatePager(result.Pagination.Total);
            CatalogEmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_rows.Count == 0)
            {
                CatalogEmptyTitle.Text = "没有找到符合条件的皮肤";
                CatalogEmptyMessage.Text = "调整搜索内容或外观筛选，再刷新目录。";
                EmptyRetryButton.Visibility = Visibility.Collapsed;
                SetCatalogStatus("市场目录中没有匹配结果。");
            }
            else
            {
                SetCatalogStatus($"共读取 {result.Items.Count} 款皮肤。" );
                foreach (var row in _rows.ToArray())
                {
                    _ = LoadPreviewAsync(row, previewCancellation.Token);
                }
            }

            if (selectionToRestore is not null)
            {
                _previewRow = _rows.FirstOrDefault(row => row.Id.Equals(selectionToRestore, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetCatalogStatus(DescribeError(exception), isError: true);
            if (_rows.Count == 0)
            {
                CatalogEmptyState.Visibility = Visibility.Visible;
                CatalogEmptyTitle.Text = "无法读取市场目录";
                CatalogEmptyMessage.Text = "检查网络连接后刷新目录。";
                EmptyRetryButton.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            if (ReferenceEquals(_catalogCancellation, cancellation))
            {
                _catalogCancellation = null;
            }

            cancellation.Dispose();
            _isLoading = false;
            UpdateControls();
        }
    }

    private async Task LoadPreviewAsync(SkinMarketRow row, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(row.Item.PreviewUrl) || row.IsPreviewLoading || _isClosed)
        {
            return;
        }

        row.SetPreviewLoading();
        var gateEntered = false;
        try
        {
            await _previewGate.WaitAsync(cancellationToken);
            gateEntered = true;
            var bytes = await _client.GetPreviewImageAsync(row.Item, cancellationToken);
            if (bytes is null || bytes.Length == 0)
            {
                throw new InvalidDataException("服务器没有返回预览图片。");
            }

            if (cancellationToken.IsCancellationRequested || _isClosed || !_rows.Contains(row))
            {
                return;
            }

            var image = DecodePreview(bytes);
            if (!cancellationToken.IsCancellationRequested && !_isClosed && _rows.Contains(row))
            {
                row.SetPreview(image);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"市场预览图片读取失败：{row.Id}\n{exception}");
            if (!cancellationToken.IsCancellationRequested && !_isClosed && _rows.Contains(row))
            {
                row.SetPreviewFailure(DescribePreviewImageError(exception));
            }
        }
        finally
        {
            if (gateEntered)
            {
                _previewGate.Release();
            }
        }
    }

    internal static BitmapSource DecodePreview(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("预览图片没有可读取的帧。");
        }

        var frame = decoder.Frames[0];
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        if (width <= 0 || height <= 0 || width > 16_000 || height > 16_000 || (long)width * height > 50_000_000)
        {
            throw new InvalidDataException("预览图片尺寸无效或过大。");
        }

        var scale = Math.Min(1d, Math.Min(640d / width, 420d / height));
        var decodeWidth = Math.Max(1, (int)Math.Round(width * scale));
        var decodeHeight = Math.Max(1, (int)Math.Round(height * scale));
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = decodeWidth;
        image.DecodePixelHeight = decodeHeight;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    internal async Task OpenPreviewAsync(SkinMarketRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_isClosed || !_rows.Contains(row))
        {
            return;
        }

        _catalogScrollOffset = CatalogScrollViewer.VerticalOffset;
        var requestSequence = Interlocked.Increment(ref _previewSequence);
        _packagePreviewCancellation?.Cancel();
        _packagePreviewCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _packagePreviewCancellation = cancellation;
        _previewRow = row;
        _previewPackage = null;
        DisposePreviewRenderer();
        PreviewPageTitle.Text = row.Name;
        PreviewPageSummary.Text = row.SummaryLabel;
        PreviewPageInstallState.Text = row.InstallStatus;
        PreviewStatusText.Text = string.Empty;
        PreviewPane.Visibility = Visibility.Visible;
        CatalogPane.Visibility = Visibility.Collapsed;
        RefreshButton.IsEnabled = false;
        ShowPreviewWaiting("正在读取皮肤预览…", $"正在下载并校验 {FormatSize(row.Item.Size)} 的皮肤包。", showRetry: false);
        UpdateControls();

        try
        {
            var progress = new Progress<SkinMarketDownloadProgress>(value =>
            {
                if (!IsCurrentPreviewRequest(requestSequence, cancellation))
                {
                    return;
                }

                var total = value.TotalBytes > 0 ? value.TotalBytes : row.Item.Size;
                PreviewStateDetail.Text = $"正在读取皮肤预览… {FormatSize(value.BytesReceived)} / {FormatSize(total)}";
            });
            var package = _previewPackage ?? await _client.DownloadPackageAsync(
                row.Item,
                _managerVersion,
                progress,
                cancellation.Token);
            if (!IsCurrentPreviewRequest(requestSequence, cancellation))
            {
                return;
            }

            _previewPackage = package;
            UpdateControls();
            ShowPreviewWaiting("正在显示皮肤预览…", "正在依据已校验的皮肤包生成静态效果。", showRetry: false);
            var renderer = EnsurePreviewRenderer();
            var displayed = await renderer.DisplayPackageAsync(package);
            if (!IsCurrentPreviewRequest(requestSequence, cancellation))
            {
                return;
            }

            if (!displayed)
            {
                throw new InvalidOperationException("静态预览没有完成渲染。");
            }

            PreviewStateOverlay.Visibility = Visibility.Collapsed;
            PreviewStatusText.Foreground = (Brush)FindResource("SecondaryText");
            PreviewStatusText.Text = $"{row.Name} v{row.Item.Version} · 静态效果预览";
            PreviewPageInstallState.Text = row.InstallStatus;
            UpdateControls();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!IsCurrentPreviewRequest(requestSequence, cancellation))
            {
                return;
            }

            Debug.WriteLine($"市场皮肤效果预览失败：{row.Id}\n{exception}");
            ShowPreviewFailure(DescribePackagePreviewError(exception));
        }
        finally
        {
            if (ReferenceEquals(_packagePreviewCancellation, cancellation))
            {
                _packagePreviewCancellation = null;
            }

            cancellation.Dispose();
            UpdateControls();
        }
    }

    private bool IsCurrentPreviewRequest(long requestSequence, CancellationTokenSource cancellation) =>
        !_isClosed && !cancellation.IsCancellationRequested && requestSequence == Volatile.Read(ref _previewSequence) &&
        ReferenceEquals(_packagePreviewCancellation, cancellation);

    private ThemePreviewRenderer EnsurePreviewRenderer()
    {
        if (_previewRenderer is not null)
        {
            return _previewRenderer;
        }

        _previewWebView = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        PreviewBrowserHost.Content = _previewWebView;
        _previewRenderer = new ThemePreviewRenderer(_previewWebView, _previewProfileDirectory, _basePackage);
        return _previewRenderer;
    }

    internal void BackToCatalog()
    {
        Interlocked.Increment(ref _previewSequence);
        _packagePreviewCancellation?.Cancel();
        _packagePreviewCancellation?.Dispose();
        _packagePreviewCancellation = null;
        DisposePreviewRenderer();
        _previewPackage = null;
        _previewRow = null;
        PreviewPane.Visibility = Visibility.Collapsed;
        CatalogPane.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = !_isLoading && !_isDownloading;
        UpdateControls();
        _ = Dispatcher.BeginInvoke(new Action(() => CatalogScrollViewer.ScrollToVerticalOffset(_catalogScrollOffset)),
            DispatcherPriority.Loaded);
    }

    private void DisposePreviewRenderer()
    {
        try
        {
            _previewRenderer?.Dispose();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"释放市场预览渲染器失败：{exception.Message}");
        }

        _previewRenderer = null;
        _previewWebView = null;
        PreviewBrowserHost.Content = null;
    }

    private async Task DownloadRowAsync(SkinMarketRow row)
    {
        if (_isClosed || _isDownloading)
        {
            return;
        }

        if (!CanDownload(row, out var reason))
        {
            row.SetOperationStatus(reason, isError: true);
            return;
        }

        _failedItemId = null;
        var cancellation = new CancellationTokenSource();
        _downloadCancellation = cancellation;
        _downloadingRow = row;
        _isDownloading = true;
        _canCancelDownload = true;
        row.SetDownloadProgress(0, visible: true);
        row.SetOperationStatus($"正在下载 {FormatSize(row.Item.Size)}…");
        SetCatalogStatus($"正在下载“{row.Name}”…");
        UpdateControls();

        var progress = new Progress<SkinMarketDownloadProgress>(value =>
        {
            if (_isClosed || !ReferenceEquals(_downloadCancellation, cancellation))
            {
                return;
            }

            var total = value.TotalBytes > 0 ? value.TotalBytes : row.Item.Size;
            var percentage = Math.Clamp(value.BytesReceived * 100d / total, 0, 100);
            row.SetDownloadProgress(percentage, visible: true);
            row.SetOperationStatus($"正在下载 {FormatSize(value.BytesReceived)} / {FormatSize(total)}…");
        });

        try
        {
            var package = ReferenceEquals(_previewRow, row) && _previewPackage is { } previewPackage
                ? previewPackage
                : await _client.DownloadPackageAsync(row.Item, _managerVersion, progress, cancellation.Token);
            if (cancellation.IsCancellationRequested || _isClosed)
            {
                return;
            }

            _canCancelDownload = false;
            row.SetDownloadProgress(100, visible: false);
            row.SetOperationStatus("文件已通过校验，正在保存到本地皮肤库…");
            UpdateControls();
            var installed = await _installPackage(package);
            if (_isClosed)
            {
                return;
            }

            if (installed)
            {
                _failedItemId = null;
                RefreshInstalledVersions();
                row.SetOperationStatus($"已将“{row.Name}” v{row.Item.Version}加入本地皮肤库。需要时可手动连接并应用。");
                SetCatalogStatus("皮肤已安装到本地库。", isError: false);
            }
            else
            {
                row.SetOperationStatus("已取消替换，现有皮肤未更改。");
                SetCatalogStatus("替换已取消；现有皮肤保持不变。");
            }

            if (ReferenceEquals(_previewRow, row))
            {
                PreviewPageInstallState.Text = row.InstallStatus;
                PreviewStatusText.Text = row.OperationStatus;
                PreviewStatusText.Foreground = (Brush)FindResource("SecondaryText");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _failedItemId = row.Id;
            row.SetOperationStatus("下载已取消，可以重试。");
            SetCatalogStatus("下载已取消。");
        }
        catch (Exception exception)
        {
            _failedItemId = row.Id;
            row.SetOperationStatus(DescribeError(exception), isError: true);
            SetCatalogStatus("下载或验证失败，可重试。", isError: true);
        }
        finally
        {
            if (ReferenceEquals(_downloadCancellation, cancellation))
            {
                _downloadCancellation = null;
            }

            cancellation.Dispose();
            _isDownloading = false;
            _canCancelDownload = false;
            _downloadingRow = null;
            row.SetDownloadProgress(0, visible: false);
            RefreshInstalledVersions();
            UpdateControls();
        }
    }

    private bool CanDownload(SkinMarketRow row, out string reason)
    {
        if (row.Item.SkinFormatVersion != 1)
        {
            reason = $"该皮肤格式版本 {row.Item.SkinFormatVersion} 当前不受支持。";
            return false;
        }

        if (!SkinMarketClient.IsManagerCompatible(row.Item.MinimumManagerVersion, _managerVersion, out reason))
        {
            return false;
        }

        if (row.Item.Size is <= 0 or > SkinMarketClient.MaxDownloadBytes)
        {
            reason = "皮肤包大小无效或超过 64 MB 上限。";
            return false;
        }

        if (!HasValidSha256(row.Item.Sha256))
        {
            reason = "市场条目的校验信息无效，刷新目录后重试。";
            return false;
        }

        if (row.InstallStatus.Equals($"已安装 v{row.Item.Version}", StringComparison.Ordinal))
        {
            reason = "此版本已安装。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private void UpdateControls()
    {
        if (SearchHint is not null)
        {
            SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        if (RefreshButton is null)
        {
            return;
        }

        var browsingEnabled = !_isLoading && !_isDownloading;
        SearchBox.IsEnabled = browsingEnabled;
        FilterAllButton.IsEnabled = browsingEnabled;
        FilterLightButton.IsEnabled = browsingEnabled;
        FilterDarkButton.IsEnabled = browsingEnabled;
        SearchButton.IsEnabled = browsingEnabled;
        RefreshButton.IsEnabled = browsingEnabled && PreviewPane.Visibility != Visibility.Visible;
        EmptyRetryButton.IsEnabled = browsingEnabled;
        PreviousPageButton.IsEnabled = browsingEnabled && _page > 1;
        NextPageButton.IsEnabled = browsingEnabled && _totalPages > 0 && _page < _totalPages;

        foreach (var row in _rows)
        {
            var downloadable = CanDownload(row, out _);
            var buttonText = _isDownloading && ReferenceEquals(_downloadingRow, row)
                ? "正在下载…"
                : _failedItemId?.Equals(row.Id, StringComparison.OrdinalIgnoreCase) == true
                    ? "重试下载"
                    : row.InstallStatus.Equals($"已安装 v{row.Item.Version}", StringComparison.Ordinal)
                        ? "已安装"
                        : row.InstallStatus.StartsWith("有更新", StringComparison.Ordinal)
                            ? $"更新到 v{row.Item.Version}"
                            : "下载并安装";
            row.SetDownloadControls(downloadable && browsingEnabled, buttonText,
                _canCancelDownload && ReferenceEquals(_downloadingRow, row));
        }

        if (_previewRow is null)
        {
            PreviewInstallButton.IsEnabled = false;
            return;
        }

        PreviewPageInstallState.Text = _previewRow.InstallStatus;
        PreviewInstallButton.Content = _previewRow.DownloadButtonText;
        PreviewInstallButton.IsEnabled = !_isDownloading && _previewPackage is not null &&
            CanDownload(_previewRow, out _);
    }

    private void RefreshInstalledVersions()
    {
        IReadOnlyDictionary<string, string> installed;
        try
        {
            installed = _readInstalledVersions();
        }
        catch (Exception exception)
        {
            SetCatalogStatus($"目录已读取，但无法读取本地安装状态：{exception.Message}", isError: true);
            installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var row in _rows)
        {
            if (!installed.TryGetValue(row.Id, out var installedVersion))
            {
                row.InstallStatus = "未安装";
            }
            else if (installedVersion.Equals(row.Item.Version, StringComparison.Ordinal))
            {
                row.InstallStatus = $"已安装 v{installedVersion}";
            }
            else if (Version.TryParse(installedVersion, out var localVersion) &&
                     Version.TryParse(row.Item.Version, out var marketVersion) && marketVersion > localVersion)
            {
                row.InstallStatus = $"有更新 · 本地 v{installedVersion}";
            }
            else
            {
                row.InstallStatus = $"本地 v{installedVersion}";
            }
        }

        UpdateControls();
    }

    private static bool HasValidSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private void UpdatePager(int total)
    {
        CatalogCount.Text = $"{_rows.Count} 款";
        PageText.Text = total == 0
            ? "共 0 款"
            : _totalPages > 0
                ? $"第 {_page} / {_totalPages} 页 · 共 {total} 款"
                : $"第 {_page} 页 · 共 {total} 款";
        UpdateControls();
    }

    private void SetCatalogStatus(string text, bool isError = false)
    {
        CatalogStatus.Text = text;
        CatalogStatus.Foreground = (Brush)FindResource(isError ? "ErrorText" : "SecondaryText");
        CatalogStatus.ToolTip = text;
    }

    private static string DescribeError(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } statusCode } => $"市场请求失败（HTTP {(int)statusCode}）。刷新目录后重试。",
        HttpRequestException => "无法连接市场服务器。检查网络连接后重试。",
        TimeoutException => "市场请求超时，可以重试。",
        InvalidDataException => exception.Message,
        OperationCanceledException => "操作已取消。",
        _ => $"操作失败：{exception.Message}"
    };

    private static string DescribePreviewImageError(Exception exception)
    {
        IEnumerable<Exception> inners = exception is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions
            : new[] { exception };
        var effective = inners.LastOrDefault() ?? exception;
        return effective switch
        {
            HttpRequestException { StatusCode: { } statusCode } => $"图片请求失败（HTTP {(int)statusCode}）",
            HttpRequestException or IOException => "图片连接中断，点击重试图片",
            TimeoutException or TaskCanceledException => "图片读取超时，点击重试图片",
            InvalidDataException => effective.Message,
            _ => "图片暂时无法读取，点击重试图片"
        };
    }

    private static string DescribePackagePreviewError(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            var inners = aggregate.Flatten().InnerExceptions;
            var effective = inners.LastOrDefault();
            if (effective is not null)
            {
                exception = effective;
            }
        }

        return exception switch
        {
            HttpRequestException { StatusCode: { } statusCode } => $"皮肤包请求失败（HTTP {(int)statusCode}），可以重试。",
            HttpRequestException or IOException => "读取皮肤包时网络连接中断，可以重试。",
            TimeoutException or TaskCanceledException => "读取皮肤包超时，可以重试。",
            InvalidDataException => exception.Message,
            _ => "无法生成皮肤静态预览，可以重试。"
        };
    }

    internal static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024
            ? $"{bytes / 1024d / 1024d:0.0} MB"
            : $"{bytes / 1024d:0} KB";

    internal static string FormatUpdatedAt(string value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
        {
            return timestamp.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
        }

        return value;
    }

    private string GetSelectedAppearance() => FilterLightButton.IsChecked == true
        ? "light"
        : FilterDarkButton.IsChecked == true
            ? "dark"
            : string.Empty;

    private void ShowPreviewWaiting(string title, string detail, bool showRetry)
    {
        PreviewStateTitle.Text = title;
        PreviewStateDetail.Text = detail;
        PreviewStateTitle.Foreground = (Brush)FindResource("PrimaryText");
        PreviewStateDetail.Foreground = (Brush)FindResource("SecondaryText");
        PreviewStateOverlay.Visibility = Visibility.Visible;
        PreviewStatusText.Text = string.Empty;
        PreviewStatusText.Foreground = (Brush)FindResource("SecondaryText");
        PreviewRetryButton.Visibility = showRetry ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowPreviewFailure(string message)
    {
        PreviewStateTitle.Text = "暂时无法显示皮肤预览";
        PreviewStateDetail.Text = message;
        PreviewStateTitle.Foreground = (Brush)FindResource("ErrorText");
        PreviewStateDetail.Foreground = (Brush)FindResource("ErrorText");
        PreviewStateOverlay.Visibility = Visibility.Visible;
        PreviewStatusText.Text = message;
        PreviewStatusText.Foreground = (Brush)FindResource("ErrorText");
        PreviewRetryButton.Visibility = Visibility.Visible;
        UpdateControls();
    }

    private void MarketCard_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject card)
        {
            WindowMotion.AttachBehaviors(card);
        }
    }

    private void PreviewFrame_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Grid frame && frame.ActualWidth > 0)
        {
            var height = frame.ActualWidth * 9d / 16d;
            if (double.IsNaN(frame.Height) || Math.Abs(frame.Height - height) > 0.5)
            {
                frame.Height = height;
            }
        }
    }

    private void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SkinMarketRow row })
        {
            _ = OpenPreviewAsync(row);
        }
    }

    private void RetryPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SkinMarketRow row } && _previewCancellation is { IsCancellationRequested: false } cancellation)
        {
            _ = LoadPreviewAsync(row, cancellation.Token);
        }
    }

    private async void PreviewRetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previewRow is { } row)
        {
            await OpenPreviewAsync(row);
        }
    }

    private async void PreviewInstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previewRow is { } row)
        {
            await DownloadRowAsync(row);
        }
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SkinMarketRow row })
        {
            await DownloadRowAsync(row);
        }
    }

    private void CancelDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_canCancelDownload)
        {
            if (_downloadingRow is { } row)
            {
                row.SetOperationStatus("正在取消下载…");
            }

            _downloadCancellation?.Cancel();
        }
    }

    private void BackToCatalogButton_Click(object sender, RoutedEventArgs e) => BackToCatalog();

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = RefreshAsync(resetPage: true);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchHint is not null)
        {
            SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void AppearanceFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && !_isLoading)
        {
            _ = RefreshAsync(resetPage: true);
        }
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync(resetPage: true);

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void PreviousPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_page > 1)
        {
            _page--;
            _ = RefreshAsync();
        }
    }

    private void NextPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_totalPages > 0 && _page < _totalPages)
        {
            _page++;
            _ = RefreshAsync();
        }
    }

    private void SkinMarketWindow_Closing(object? sender, CancelEventArgs e)
    {
        _isClosed = true;
        Interlocked.Increment(ref _previewSequence);
        _catalogCancellation?.Cancel();
        _previewCancellation?.Cancel();
        _downloadCancellation?.Cancel();
        _packagePreviewCancellation?.Cancel();
        DisposePreviewRenderer();
    }

    private void SkinMarketWindow_Closed(object? sender, EventArgs e)
    {
        _client.Dispose();
        _catalogCancellation?.Dispose();
        _previewCancellation?.Dispose();
        _downloadCancellation?.Dispose();
        _packagePreviewCancellation?.Dispose();
    }
}
