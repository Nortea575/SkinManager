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

internal sealed class SkinMarketRow(SkinMarketItem item, Version managerVersion) : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;
    private string _thumbnailLabel = string.IsNullOrWhiteSpace(item.EffectiveThumbnailUrl)
        ? "未提供封面缩略图"
        : "正在加载封面缩略图…";
    private string? _thumbnailError;
    private bool _hasThumbnailError;
    private bool _isThumbnailLoading;
    private int _likeCount = item.LikeCount;
    private bool _likedByMe = item.LikedByMe;
    private int _commentCount = item.CommentCount;
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
    public string Description => string.IsNullOrWhiteSpace(Item.EffectiveDescription)
        ? "作者没有提供皮肤说明。"
        : Item.EffectiveDescription;
    public string ShortDescription => string.IsNullOrWhiteSpace(Item.Description)
        ? "作者没有提供简介。"
        : Item.Description;
    public int LikeCount
    {
        get => _likeCount;
        private set
        {
            if (SetField(ref _likeCount, value))
            {
                OnPropertyChanged(nameof(AuthorAndSocialLabel));
            }
        }
    }
    public bool LikedByMe
    {
        get => _likedByMe;
        private set => SetField(ref _likedByMe, value);
    }
    public int CommentCount
    {
        get => _commentCount;
        private set
        {
            if (SetField(ref _commentCount, value))
            {
                OnPropertyChanged(nameof(AuthorAndSocialLabel));
            }
        }
    }
    public string AuthorAndSocialLabel => string.Join(" · ", new[]
    {
        string.IsNullOrWhiteSpace(Item.Author) ? "作者未提供" : $"作者 {Item.Author}",
        $"赞 {LikeCount}",
        $"评论 {CommentCount}"
    });
    public string CompatibilityLabel => SkinMarketClient.IsManagerCompatible(
        Item.MinimumManagerVersion, managerVersion, out var message)
        ? "兼容管理器"
        : message;
    public string DetailTechnical => string.Join("\n", new[]
    {
        $"外观：{AppearanceLabel}",
        $"皮肤版本：v{Item.Version} · skin 格式 v{Item.SkinFormatVersion}",
        $"最低管理器版本：{Item.MinimumManagerVersion}",
        $"安装包：{SkinMarketWindow.FormatSize(Item.Size)}",
        $"兼容状态：{CompatibilityLabel}",
        string.IsNullOrWhiteSpace(Item.UpdatedAt) ? string.Empty : $"更新时间：{SkinMarketWindow.FormatUpdatedAt(Item.UpdatedAt)}",
        Item.Tags is { Count: > 0 } ? $"标签：{string.Join("、", Item.Tags.Select(tag => tag.Name))}" : string.Empty
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string UpdatedLabel => string.IsNullOrWhiteSpace(Item.UpdatedAt)
        ? string.Empty
        : $"更新于 {SkinMarketWindow.FormatUpdatedAt(Item.UpdatedAt)}";
    public bool HasWebsiteDetail => !string.IsNullOrWhiteSpace(Item.DetailUrl);

    public string InstallStatus
    {
        get => _installStatus;
        set
        {
            if (SetField(ref _installStatus, value))
            {
                OnPropertyChanged(nameof(InstallStatusLabel));
            }
        }
    }
    public string InstallStatusLabel => $"{InstallStatus} · {CompatibilityLabel}";

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
    public string ThumbnailLabel
    {
        get => _thumbnailLabel;
        private set => SetField(ref _thumbnailLabel, value);
    }
    public string? ThumbnailError
    {
        get => _thumbnailError;
        private set => SetField(ref _thumbnailError, value);
    }
    public bool HasThumbnailError
    {
        get => _hasThumbnailError;
        private set
        {
            if (SetField(ref _hasThumbnailError, value))
            {
                OnPropertyChanged(nameof(ThumbnailRetryVisibility));
            }
        }
    }
    public bool IsThumbnailLoading
    {
        get => _isThumbnailLoading;
        private set => SetField(ref _isThumbnailLoading, value);
    }
    public Visibility ThumbnailRetryVisibility => HasThumbnailError ? Visibility.Visible : Visibility.Collapsed;
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

    public void SetThumbnailLoading()
    {
        Thumbnail = null;
        ThumbnailError = null;
        HasThumbnailError = false;
        IsThumbnailLoading = true;
        ThumbnailLabel = "正在加载封面缩略图…";
    }

    public void SetThumbnail(BitmapSource image)
    {
        Thumbnail = image;
        ThumbnailError = null;
        HasThumbnailError = false;
        IsThumbnailLoading = false;
        ThumbnailLabel = string.Empty;
    }

    public void SetThumbnailFailure(string message)
    {
        Thumbnail = null;
        ThumbnailError = message;
        HasThumbnailError = true;
        IsThumbnailLoading = false;
        ThumbnailLabel = "封面缩略图暂不可用";
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

    public void SetLikeState(bool liked, int likeCount)
    {
        LikedByMe = liked;
        LikeCount = Math.Max(0, likeCount);
    }

    public void SetCommentCount(int commentCount) => CommentCount = Math.Max(0, commentCount);

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
    private readonly Func<SkinPackage, Task<bool>> _installPackage;
    private readonly Func<IReadOnlyDictionary<string, string>> _readInstalledVersions;
    private readonly ObservableCollection<SkinMarketRow> _rows = [];
    private readonly SemaphoreSlim _thumbnailGate = new(4, 4);
    private CancellationTokenSource? _catalogCancellation;
    private CancellationTokenSource? _thumbnailCancellation;
    private CancellationTokenSource? _downloadCancellation;
    private SkinMarketRow? _selectedRow;
    private SkinMarketRow? _downloadingRow;
    private string? _failedItemId;
    private int _page = 1;
    private int _totalPages;
    private long _catalogSequence;
    private double _catalogScrollOffset;
    private bool _isLoading;
    private bool _isDownloading;
    private bool _canCancelDownload;
    private bool _isClosed;
    private bool _isUpdatingTags;

    internal SkinMarketWindow(
        SkinMarketClient client,
        Version managerVersion,
        BasePackage basePackage,
        string unusedProfilePath,
        Func<SkinPackage, Task<bool>> installPackage,
        Func<IReadOnlyDictionary<string, string>> readInstalledVersions,
        IMarketAccountAuthProvider? accountAuthProvider = null,
        bool useDefaultAccountAuth = true,
        string? accountProfileDirectory = null,
        Func<IReadOnlyList<SkinCatalogEntry>>? readLocalSkinCatalog = null,
        Action<string, string>? exportLocalSkinPackage = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _managerVersion = managerVersion ?? throw new ArgumentNullException(nameof(managerVersion));
        ArgumentNullException.ThrowIfNull(basePackage);
        _ = unusedProfilePath;
        _installPackage = installPackage ?? throw new ArgumentNullException(nameof(installPackage));
        _readInstalledVersions = readInstalledVersions ?? throw new ArgumentNullException(nameof(readInstalledVersions));
        InitializeComponent();
        InitializeCommunity(accountAuthProvider, useDefaultAccountAuth, accountProfileDirectory,
            readLocalSkinCatalog, exportLocalSkinPackage);
        Icon = App.ReadApplicationIcon();
        WindowMotion.Attach(this, animateWindow: false, animateClose: false);
        GridCards.ItemsSource = _rows;
        ListCards.ItemsSource = _rows;
        Loaded += (_, _) =>
        {
            _ = RefreshAsync(resetPage: true);
            _ = InitializeAccountSessionAsync();
        };
        Closing += SkinMarketWindow_Closing;
        Closed += SkinMarketWindow_Closed;
        UpdateControls();
    }

    internal IReadOnlyList<SkinMarketRow> Rows => _rows;
    internal SkinMarketRow? SelectedRow => _selectedRow;
    internal bool IsDetailsVisible => DetailsPane.Visibility == Visibility.Visible;
    internal bool IsCatalogVisible => CatalogPane.Visibility == Visibility.Visible;
    internal bool IsLoading => _isLoading;
    internal bool IsDownloading => _isDownloading;
    internal bool IsListView => ListCards.Visibility == Visibility.Visible;
    internal double CatalogScrollOffset => CatalogScrollViewer.VerticalOffset;
    internal ScrollViewer CatalogScrollViewerForTests => CatalogScrollViewer;
    internal RadioButton DarkFilterButtonForTests => FilterDarkButton;
    internal RadioButton FilterAllButtonForTests => FilterAllButton;
    internal TextBox SearchBoxForTests => SearchBox;
    internal ComboBox SortBoxForTests => SortBox;
    internal ComboBox TagBoxForTests => TagBox;
    internal RadioButton GridViewButtonForTests => GridViewButton;
    internal RadioButton ListViewButtonForTests => ListViewButton;
    internal ItemsControl GridCardsForTests => GridCards;
    internal ItemsControl ListCardsForTests => ListCards;
    internal Button DetailsDownloadButtonForTests => DetailsDownloadButton;
    internal Button LoginButtonForTests => LoginButton;
    internal Button RegisterButtonForTests => RegisterButton;
    internal Button LogoutButtonForTests => LogoutButton;
    internal Button OpenUploadButtonForTests => OpenUploadButton;
    internal Button DetailsLikeButtonForTests => DetailsLikeButton;
    internal TextBox CommentInputForTests => CommentInput;
    internal Button SendCommentButtonForTests => SendCommentButton;
    internal ItemsControl CommentsListForTests => CommentsList;
    internal TextBlock CommentStatusTextForTests => CommentStatusText;
    internal Button NextCommentsButtonForTests => NextCommentsButton;
    internal Button PickUploadZipButtonForTests => PickUploadZipButton;
    internal Button ExportLocalSkinButtonForTests => ExportLocalSkinButton;
    internal Button SubmitUploadButtonForTests => SubmitUploadButton;
    internal Button CancelSubmissionButtonForTests => CancelSubmissionButton;
    internal TextBox UploadDescriptionBoxForTests => UploadDescriptionBox;
    internal TextBox UploadAuthorBoxForTests => UploadAuthorBox;
    internal TextBox UploadLicenseBoxForTests => UploadLicenseBox;
    internal TextBox UploadMinimumVersionBoxForTests => UploadMinimumVersionBox;
    internal TextBox UploadTagsBoxForTests => UploadTagsBox;
    internal TextBlock UploadStatusTextForTests => UploadStatusText;
    internal TextBlock UploadPackageSummaryForTests => UploadPackageSummary;
    internal ComboBox LocalSkinBoxForTests => LocalSkinBox;
    internal bool IsUploadVisibleForTests => UploadPane.Visibility == Visibility.Visible;
    internal WebView2 AccountCookieBridgeForTests => AccountCookieBridge;
    internal string? UploadPackagePathForTests => _uploadPackagePath;
    internal bool IsSubmittingForTests => _isSubmitting;
    internal Task LoadUploadPackageForTestsAsync(string path) => PrepareUploadPackageAsync(path, isTemporary: false);
    internal Task SubmitUploadForTestsAsync() => SubmitSkinAsync();

    internal async Task RefreshAsync(bool resetPage = false)
    {
        if (_isClosed || _isDownloading || IsDetailsVisible)
        {
            return;
        }

        if (resetPage)
        {
            _page = 1;
            _catalogScrollOffset = 0;
        }

        _catalogCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _catalogCancellation = cancellation;
        var sequence = Interlocked.Increment(ref _catalogSequence);
        var requestUserId = _client.Account?.UserId ?? 0;
        var requestAccountGeneration = _accountGeneration;
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
                cancellation.Token,
                GetSelectedSort(),
                GetSelectedTag());
            if (!IsCurrentCatalogRequest(sequence, cancellation))
            {
                return;
            }

            _page = result.Pagination.Page;
            _totalPages = result.Pagination.TotalPages;
            _thumbnailCancellation?.Cancel();
            _thumbnailCancellation = new CancellationTokenSource();
            var thumbnailToken = _thumbnailCancellation.Token;
            _rows.Clear();
            foreach (var item in result.Items)
            {
                _rows.Add(new SkinMarketRow(item, _managerVersion));
            }

            RefreshInstalledVersions();
            var selectedTagStillAvailable = UpdateAvailableTags(result.AvailableTags ?? Array.Empty<SkinMarketTag>());
            UpdatePager(result.Pagination.Total);
            CatalogEmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!selectedTagStillAvailable)
            {
                _page = 1;
                _ = RefreshAsync(resetPage: true);
                return;
            }

            if (_rows.Count == 0)
            {
                CatalogEmptyTitle.Text = "没有找到符合条件的皮肤";
                CatalogEmptyMessage.Text = "调整搜索、外观或标签筛选，再刷新目录。";
                EmptyRetryButton.Visibility = Visibility.Collapsed;
                SetCatalogStatus("市场目录中没有匹配结果。");
            }
            else
            {
                SetCatalogStatus($"共读取 {result.Items.Count} 款皮肤。");
                foreach (var row in _rows.ToArray())
                {
                    _ = LoadThumbnailAsync(row, thumbnailToken);
                }
            }

            _ = Dispatcher.BeginInvoke(new Action(() => CatalogScrollViewer.ScrollToVerticalOffset(_catalogScrollOffset)),
                DispatcherPriority.Loaded);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (SkinMarketReauthenticationException exception)
        {
            if (!IsCurrentCatalogRequest(sequence, cancellation))
            {
                return;
            }

            var handled = HandleReauthenticationFailure(
                exception,
                requestUserId,
                requestAccountGeneration,
                "目录刷新");
            if (!IsCurrentCatalogRequest(sequence, cancellation))
            {
                return;
            }

            var message = handled
                ? "市场登录状态已过期；请重新登录后同步个人点赞状态。"
                : DescribeError(exception);
            SetCatalogStatus(message, isError: true);
            if (_rows.Count == 0)
            {
                CatalogEmptyState.Visibility = Visibility.Visible;
                CatalogEmptyTitle.Text = "无法读取市场目录";
                CatalogEmptyMessage.Text = message;
                EmptyRetryButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception exception)
        {
            if (!IsCurrentCatalogRequest(sequence, cancellation))
            {
                return;
            }

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
                _isLoading = false;
                UpdateControls();
            }

            cancellation.Dispose();
        }
    }

    private bool IsCurrentCatalogRequest(long sequence, CancellationTokenSource cancellation) =>
        !_isClosed && !cancellation.IsCancellationRequested && sequence == Volatile.Read(ref _catalogSequence) &&
        ReferenceEquals(_catalogCancellation, cancellation);

    private bool UpdateAvailableTags(IReadOnlyList<SkinMarketTag> tags)
    {
        var selectedTag = GetSelectedTag();
        _isUpdatingTags = true;
        try
        {
            TagBox.Items.Clear();
            TagBox.Items.Add(new ComboBoxItem { Content = "全部标签", Tag = string.Empty });
            foreach (var tag in tags)
            {
                TagBox.Items.Add(new ComboBoxItem { Content = tag.Name, Tag = tag.Id });
            }

            TagFilterLabel.Visibility = tags.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            TagBox.Visibility = tags.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            var match = TagBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, selectedTag, StringComparison.Ordinal));
            var selectedStillAvailable = selectedTag.Length == 0 || match is not null;
            TagBox.SelectedItem = selectedStillAvailable ? match ?? TagBox.Items[0] : TagBox.Items[0];
            return selectedStillAvailable;
        }
        finally
        {
            _isUpdatingTags = false;
        }
    }

    private async Task LoadThumbnailAsync(SkinMarketRow row, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(row.Item.EffectiveThumbnailUrl) || row.IsThumbnailLoading || _isClosed)
        {
            return;
        }

        row.SetThumbnailLoading();
        var gateEntered = false;
        try
        {
            await _thumbnailGate.WaitAsync(cancellationToken);
            gateEntered = true;
            var bytes = await _client.GetThumbnailImageAsync(row.Item, cancellationToken);
            if (bytes is null || bytes.Length == 0)
            {
                throw new InvalidDataException("服务器没有返回封面缩略图。");
            }

            if (cancellationToken.IsCancellationRequested || _isClosed || !_rows.Contains(row))
            {
                return;
            }

            var image = DecodeThumbnail(bytes);
            if (!cancellationToken.IsCancellationRequested && !_isClosed && _rows.Contains(row))
            {
                row.SetThumbnail(image);
                if (ReferenceEquals(_selectedRow, row))
                {
                    UpdateDetails(row);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"市场封面缩略图读取失败：{row.Id}\n{exception}");
            if (!cancellationToken.IsCancellationRequested && !_isClosed && _rows.Contains(row))
            {
                row.SetThumbnailFailure(DescribeThumbnailError(exception));
                if (ReferenceEquals(_selectedRow, row))
                {
                    UpdateDetails(row);
                }
            }
        }
        finally
        {
            if (gateEntered)
            {
                _thumbnailGate.Release();
            }
        }
    }

    internal static BitmapSource DecodeThumbnail(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("封面缩略图没有可读取的帧。");
        }

        var frame = decoder.Frames[0];
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        if (width <= 0 || height <= 0 || width > 16_000 || height > 16_000 || (long)width * height > 50_000_000)
        {
            throw new InvalidDataException("封面缩略图尺寸无效或过大。");
        }

        var scale = Math.Min(1d, Math.Min(640d / width, 420d / height));
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = Math.Max(1, (int)Math.Round(width * scale));
        image.DecodePixelHeight = Math.Max(1, (int)Math.Round(height * scale));
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    internal void OpenDetails(SkinMarketRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_isClosed || !_rows.Contains(row))
        {
            return;
        }

        _catalogScrollOffset = CatalogScrollViewer.VerticalOffset;
        if (_selectedRow is not null)
        {
            _selectedRow.PropertyChanged -= SelectedRow_PropertyChanged;
        }

        _selectedRow = row;
        _selectedRow.PropertyChanged += SelectedRow_PropertyChanged;
        UpdateDetails(row);
        DetailsPane.Visibility = Visibility.Visible;
        CatalogPane.Visibility = Visibility.Collapsed;
        UpdateControls();
        BeginDetailsCommunityLoad(row);
    }

    private void UpdateDetails(SkinMarketRow row)
    {
        DetailsTitle.Text = row.Name;
        DetailsSummary.Text = row.SummaryLabel;
        DetailsInstallState.Text = row.InstallStatus;
        DetailsDescription.Text = row.Description;
        DetailsTechnical.Text = row.DetailTechnical;
        DetailsCounts.Text = row.AuthorAndSocialLabel;
        DetailsAuthor.Text = string.IsNullOrWhiteSpace(row.Item.Author) ? "作者：未提供" : $"作者：{row.Item.Author}";
        DetailsLicense.Text = string.IsNullOrWhiteSpace(row.Item.LicenseNotes)
            ? "版权与授权：未提供说明"
            : $"版权与授权：{row.Item.LicenseNotes}";
        DetailsSubmitter.Text = string.IsNullOrWhiteSpace(row.Item.Submitter)
            ? string.Empty
            : $"投稿人：{row.Item.Submitter}";
        DetailsThumbnail.Source = row.Thumbnail;
        DetailsThumbnailPlaceholder.Visibility = row.HasThumbnail ? Visibility.Collapsed : Visibility.Visible;
        WebsiteDetailButton.Visibility = row.HasWebsiteDetail ? Visibility.Visible : Visibility.Collapsed;
        DetailsOperationStatus.Text = row.OperationStatus;
        DetailsProgress.Value = row.DownloadProgress;
        DetailsProgress.Visibility = row.ProgressVisibility;
        DetailsCancelButton.Visibility = row.CancelVisibility;
        DetailsStatusText.Text = row.OperationStatus;
        DetailsStatusText.SetResourceReference(TextBlock.ForegroundProperty,
            row.OperationIsError ? "ErrorText" : "SecondaryText");
        DetailsOperationStatus.SetResourceReference(TextBlock.ForegroundProperty,
            row.OperationIsError ? "ErrorText" : "SecondaryText");
        DetailsDownloadButton.Content = row.DownloadButtonText;
        DetailsDownloadButton.IsEnabled = row.CanDownload && !_isDownloading;
        UpdateCommunityDetails(row);
    }

    private void SelectedRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is SkinMarketRow row && ReferenceEquals(_selectedRow, row))
        {
            UpdateDetails(row);
        }
    }

    internal void BackToCatalog()
    {
        if (_selectedRow is not null)
        {
            _selectedRow.PropertyChanged -= SelectedRow_PropertyChanged;
        }

        _selectedRow = null;
        DetailsPane.Visibility = Visibility.Collapsed;
        CatalogPane.Visibility = Visibility.Visible;
        LeaveDetailsCommunityView();
        UpdateControls();
        _ = Dispatcher.BeginInvoke(new Action(() => CatalogScrollViewer.ScrollToVerticalOffset(_catalogScrollOffset)),
            DispatcherPriority.Loaded);
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
            var package = await _client.DownloadPackageAsync(row.Item, _managerVersion, progress, cancellation.Token);
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
                SetCatalogStatus("皮肤已安装到本地库。");
            }
            else
            {
                row.SetOperationStatus("已取消替换，现有皮肤未更改。");
                SetCatalogStatus("替换已取消；现有皮肤保持不变。");
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
        SortBox.IsEnabled = browsingEnabled;
        TagBox.IsEnabled = browsingEnabled;
        GridViewButton.IsEnabled = browsingEnabled;
        ListViewButton.IsEnabled = browsingEnabled;
        SearchButton.IsEnabled = browsingEnabled;
        RefreshButton.IsEnabled = browsingEnabled && !IsDetailsVisible;
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

        UpdateCommunityControls();

        if (_selectedRow is not null)
        {
            UpdateDetails(_selectedRow);
        }
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

    private static bool HasValidSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

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
        CatalogStatus.SetResourceReference(TextBlock.ForegroundProperty, isError ? "ErrorText" : "SecondaryText");
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

    private static string DescribeThumbnailError(Exception exception)
    {
        IEnumerable<Exception> inners = exception is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions
            : [exception];
        var effective = inners.LastOrDefault() ?? exception;
        return effective switch
        {
            HttpRequestException { StatusCode: { } statusCode } => $"缩略图请求失败（HTTP {(int)statusCode}）",
            HttpRequestException or IOException => "图片连接中断，点击重试图片",
            TimeoutException or TaskCanceledException => "图片读取超时，点击重试图片",
            InvalidDataException => effective.Message,
            _ => "封面缩略图暂时无法读取，点击重试图片"
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

    private string GetSelectedSort() => SortBox.SelectedValue as string ?? "latest";

    private string GetSelectedTag() => (TagBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;

    private void MarketCard_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject card)
        {
            WindowMotion.AttachBehaviors(card);
        }
    }

    private void OpenDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SkinMarketRow row })
        {
            OpenDetails(row);
        }
    }

    private void RetryThumbnailButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SkinMarketRow row } &&
            _thumbnailCancellation is { IsCancellationRequested: false } cancellation)
        {
            _ = LoadThumbnailAsync(row, cancellation.Token);
        }
    }

    private async void DetailsDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRow is { } row)
        {
            await DownloadRowAsync(row);
        }
    }

    private void WebsiteDetailButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRow is not { } row || string.IsNullOrWhiteSpace(row.Item.DetailUrl))
        {
            return;
        }

        try
        {
            var uri = SkinMarketClient.GetDetailUri(row.Item.DetailUrl);
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidDataException or Win32Exception or InvalidOperationException)
        {
            DetailsStatusText.Text = exception is InvalidDataException
                ? "网站详情地址无效。"
                : "无法打开网站详情，请检查系统浏览器设置。";
            DetailsStatusText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorText");
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
            _downloadingRow?.SetOperationStatus("正在取消下载…");
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

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_isLoading)
        {
            _ = RefreshAsync(resetPage: true);
        }
    }

    private void TagBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_isLoading && !_isUpdatingTags)
        {
            _ = RefreshAsync(resetPage: true);
        }
    }

    private void GridView_Checked(object sender, RoutedEventArgs e) => SetListView(false);

    private void ListView_Checked(object sender, RoutedEventArgs e) => SetListView(true);

    private void SetListView(bool list)
    {
        if (GridCards is null)
        {
            return;
        }

        GridCards.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
        ListCards.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        GridViewButton.IsChecked = !list;
        ListViewButton.IsChecked = list;
        CatalogScrollViewer.ScrollToTop();
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync(resetPage: true);

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void PreviousPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_page > 1)
        {
            _page--;
            _catalogScrollOffset = 0;
            _ = RefreshAsync();
        }
    }

    private void NextPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_totalPages > 0 && _page < _totalPages)
        {
            _page++;
            _catalogScrollOffset = 0;
            _ = RefreshAsync();
        }
    }

    private void SkinMarketWindow_Closing(object? sender, CancelEventArgs e)
    {
        _isClosed = true;
        CancelCommunityOperations();
        Interlocked.Increment(ref _catalogSequence);
        _catalogCancellation?.Cancel();
        _thumbnailCancellation?.Cancel();
        _downloadCancellation?.Cancel();
    }

    private void SkinMarketWindow_Closed(object? sender, EventArgs e)
    {
        _ = DisposeCommunityAsync();
        _client.Dispose();
        _catalogCancellation?.Dispose();
        _thumbnailCancellation?.Dispose();
        _downloadCancellation?.Dispose();
    }
}
