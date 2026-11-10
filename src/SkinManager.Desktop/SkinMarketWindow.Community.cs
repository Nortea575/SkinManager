using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SkinManager.Core;
using SkinManager.Market;

namespace SkinManager.Desktop;

internal sealed record MarketLocalSkinOption(SkinCatalogEntry Entry)
{
    public string DisplayLabel => $"{Entry.Name} · {Entry.Id} · v{Entry.Version}";
}

internal sealed record MarketCommentRow(string Id, string Author, string Content, string DateLabel)
{
    internal static MarketCommentRow From(SkinMarketComment comment) => new(
        comment.Id.ToString(CultureInfo.InvariantCulture),
        string.IsNullOrWhiteSpace(comment.Author) ? "匿名用户" : comment.Author,
        comment.Content,
        FormatDate(comment.Date));

    private static string FormatDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var date)
            ? date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            : value;
}

public partial class SkinMarketWindow
{
    private const int CommentsPerPage = 20;
    private readonly ObservableCollection<MarketCommentRow> _comments = [];
    private IMarketAccountAuthProvider? _accountAuthProvider;
    private Func<IReadOnlyList<SkinCatalogEntry>>? _readLocalSkinCatalog;
    private Action<string, string>? _exportLocalSkinPackage;
    private CancellationTokenSource? _accountLifetimeCancellation;
    private CancellationTokenSource? _commentsCancellation;
    private CancellationTokenSource? _communityMutationCancellation;
    private CancellationTokenSource? _submissionCancellation;
    private CancellationTokenSource? _uploadPreparationCancellation;
    private Task? _uploadPreparationTask;
    private Task? _activeSubmissionTask;
    private int _accountGeneration;
    private int _commentsPage;
    private int _commentsTotalPages;
    private bool _isAccountBusy;
    private bool _isLikeBusy;
    private bool _isCommentBusy;
    private bool _isSubmitting;
    private bool _isPreparingUpload;
    private bool _isUploadVisible;
    private bool _returnToDetailsAfterUpload;
    private string? _uploadPackagePath;
    private string? _pendingTemporaryUploadPath;
    private bool _uploadPackageIsTemporary;
    private SkinPackage? _uploadPackage;
    private SkinMarketRow? _returnToDetailsRow;

    private void InitializeCommunity(
        IMarketAccountAuthProvider? accountAuthProvider,
        bool useDefaultAccountAuth,
        string? accountProfileDirectory,
        Func<IReadOnlyList<SkinCatalogEntry>>? readLocalSkinCatalog,
        Action<string, string>? exportLocalSkinPackage)
    {
        _accountAuthProvider = accountAuthProvider ?? (useDefaultAccountAuth
            ? new WebView2MarketAccountAuthProvider(AccountCookieBridge, accountProfileDirectory)
            : null);
        _accountLifetimeCancellation = new CancellationTokenSource();
        _readLocalSkinCatalog = readLocalSkinCatalog;
        _exportLocalSkinPackage = exportLocalSkinPackage;
        CommentsList.ItemsSource = _comments;
        UpdateCommunityControls();
    }

    private async Task InitializeAccountSessionAsync()
    {
        var provider = _accountAuthProvider;
        if (provider is null || _isClosed)
        {
            return;
        }

        if (_isAccountBusy)
        {
            return;
        }

        _isAccountBusy = true;
        var oldUserId = _client.Account?.UserId;
        var cancellationToken = _accountLifetimeCancellation?.Token ?? CancellationToken.None;
        SetAccountStatus("正在恢复账号状态…");
        UpdateCommunityControls();
        try
        {
            await provider.InitializeAsync(cancellationToken);
            var cookieHeader = await provider.ReadCookieHeaderAsync(cancellationToken);
            await _client.RefreshAccountAsync(cookieHeader, cancellationToken);
            if (_isClosed || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            OnAccountChanged(oldUserId);
            SetAccountStatus(_client.Account is { } account
                ? $"已登录：{account.DisplayName}"
                : "未登录，可浏览市场；点赞、评论与投稿需要账号。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_isClosed)
            {
                SetAccountStatus($"无法验证市场账号：{exception.Message}", isError: true);
            }
        }
        finally
        {
            _isAccountBusy = false;
            UpdateCommunityControls();
        }
    }

    private async Task OpenAccountAsync(Uri startUri)
    {
        if (_isAccountBusy || _isClosed)
        {
            return;
        }

        if (_accountAuthProvider is null)
        {
            SetAccountStatus("当前窗口没有启用账号登录。", isError: true);
            return;
        }

        _isAccountBusy = true;
        var oldUserId = _client.Account?.UserId;
        var cancellationToken = _accountLifetimeCancellation?.Token ?? CancellationToken.None;
        SetAccountStatus("正在打开本站账号页面…");
        UpdateCommunityControls();
        try
        {
            await _accountAuthProvider.OpenAsync(this, startUri, cancellationToken);
            var cookieHeader = await _accountAuthProvider.ReadCookieHeaderAsync(cancellationToken);
            await _client.RefreshAccountAsync(cookieHeader, cancellationToken);
            if (_isClosed || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            OnAccountChanged(oldUserId);
            SetAccountStatus(_client.Account is { } account
                ? $"已登录：{account.DisplayName}"
                : "尚未验证为登录状态；完成网站注册验证后可再次登录。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_isClosed)
            {
                SetAccountStatus($"账号验证失败：{exception.Message}", isError: true);
            }
        }
        finally
        {
            _isAccountBusy = false;
            UpdateCommunityControls();
        }
    }

    private void OnAccountChanged(int? previousUserId)
    {
        var currentUserId = _client.Account?.UserId;
        if (previousUserId == currentUserId)
        {
            if (IsDetailsVisible && _selectedRow is not null)
            {
                _ = LoadCommentsAsync(_selectedRow, 1);
            }

            return;
        }

        _accountGeneration++;
        _commentsCancellation?.Cancel();
        _communityMutationCancellation?.Cancel();
        if (_client.Account is null)
        {
            foreach (var row in _rows)
            {
                row.SetLikeState(false, row.LikeCount);
            }

            _comments.Clear();
            _commentsPage = 0;
            _commentsTotalPages = 0;
        }

        if (IsDetailsVisible)
        {
            if (_client.Account is not null)
            {
                _ = RefreshPersonalizedLikesAsync(_accountGeneration);
            }

            if (_selectedRow is not null)
            {
                _ = LoadCommentsAsync(_selectedRow, 1);
            }
        }
        else
        {
            _ = RefreshAsync();
        }

        UpdateCommunityControls();
    }

    private async Task RefreshPersonalizedLikesAsync(int accountGeneration)
    {
        var expectedUserId = _client.Account?.UserId ?? 0;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _accountLifetimeCancellation?.Token ?? CancellationToken.None);
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
            if (_isClosed || cancellation.IsCancellationRequested || accountGeneration != _accountGeneration)
            {
                return;
            }

            var itemsById = result.Items.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var row in _rows)
            {
                if (itemsById.TryGetValue(row.Id, out var item))
                {
                    row.SetLikeState(item.LikedByMe, item.LikeCount);
                }
            }

            UpdateCommunityControls();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (HandleReauthenticationFailure(exception, expectedUserId, accountGeneration, "无法同步点赞状态"))
            {
                return;
            }

            if (!_isClosed && accountGeneration == _accountGeneration)
            {
                SetAccountStatus($"账号已登录，但无法同步点赞状态：{exception.Message}", isError: true);
            }
        }
    }

    private void BeginDetailsCommunityLoad(SkinMarketRow row)
    {
        _comments.Clear();
        _commentsPage = 0;
        _commentsTotalPages = 0;
        _ = LoadCommentsAsync(row, 1);
    }

    private void LeaveDetailsCommunityView()
    {
        _commentsCancellation?.Cancel();
        _communityMutationCancellation?.Cancel();
        _comments.Clear();
        _commentsPage = 0;
        _commentsTotalPages = 0;
        SetCommunityStatus(string.Empty);
    }

    private async Task LoadCommentsAsync(SkinMarketRow row, int page)
    {
        if (_isClosed || !ReferenceEquals(_selectedRow, row) || !IsDetailsVisible)
        {
            return;
        }

        _commentsCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _commentsCancellation = cancellation;
        var accountGeneration = _accountGeneration;
        var expectedUserId = _client.Account?.UserId ?? 0;
        _commentsPage = page;
        _comments.Clear();
        CommentsEmptyText.Text = page == 1 ? "正在读取评论…" : "正在读取下一页…";
        SetCommunityStatus(CommentsEmptyText.Text);
        UpdateCommunityControls();
        try
        {
            var result = await _client.GetCommentsAsync(row.Id, page, cancellation.Token);
            if (cancellation.IsCancellationRequested || _isClosed ||
                !ReferenceEquals(_selectedRow, row) || !IsDetailsVisible || accountGeneration != _accountGeneration)
            {
                return;
            }

            foreach (var comment in result.Items)
            {
                _comments.Add(MarketCommentRow.From(comment));
            }

            _commentsPage = result.Pagination.Page;
            _commentsTotalPages = result.Pagination.TotalPages;
            row.SetCommentCount(result.Pagination.Total);
            CommentsEmptyText.Text = _comments.Count == 0 ? "还没有评论。" : string.Empty;
            CommentsEmptyText.Visibility = _comments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CommentsPaginationText.Text = $"第 {_commentsPage}/{Math.Max(1, _commentsTotalPages)} 页 · 共 {result.Pagination.Total} 条";
            SetCommunityStatus(string.Empty);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!cancellation.IsCancellationRequested && !_isClosed &&
                ReferenceEquals(_selectedRow, row) && accountGeneration == _accountGeneration)
            {
                if (HandleReauthenticationFailure(exception, expectedUserId, accountGeneration, "评论读取"))
                {
                    CommentsEmptyText.Text = "市场登录状态已过期，请重新登录后继续。";
                    CommentsEmptyText.Visibility = Visibility.Visible;
                    return;
                }

                CommentsEmptyText.Text = "暂时无法读取评论。";
                CommentsEmptyText.Visibility = Visibility.Visible;
                SetCommunityStatus($"评论读取失败：{exception.Message}", isError: true);
            }
        }
        finally
        {
            if (ReferenceEquals(_commentsCancellation, cancellation))
            {
                _commentsCancellation = null;
            }

            cancellation.Dispose();

            UpdateCommunityControls();
        }
    }

    private void UpdateCommunityDetails(SkinMarketRow row)
    {
        if (!ReferenceEquals(_selectedRow, row))
        {
            return;
        }

        DetailsLikeButton.Content = _client.Account is null
            ? $"登录后点赞 · {row.LikeCount}"
            : row.LikedByMe ? $"取消点赞 · {row.LikeCount}" : $"点赞 · {row.LikeCount}";
        if (_comments.Count == 0 && _commentsCancellation is null && string.IsNullOrWhiteSpace(DetailsCommunityStatus.Text))
        {
            CommentsEmptyText.Visibility = Visibility.Visible;
        }
    }

    private void UpdateCommunityControls()
    {
        if (LoginButton is null || _isClosed)
        {
            return;
        }

        var account = _client.Account;
        var authenticated = account is not null;
        var authAvailable = _accountAuthProvider is not null;
        LoginButton.Visibility = authenticated ? Visibility.Collapsed : Visibility.Visible;
        RegisterButton.Visibility = authenticated ? Visibility.Collapsed : Visibility.Visible;
        LogoutButton.Visibility = authenticated ? Visibility.Visible : Visibility.Collapsed;
        AccountDisplayText.Text = account?.DisplayName ?? string.Empty;
        AccountDisplayText.Visibility = authenticated ? Visibility.Visible : Visibility.Collapsed;
        LoginButton.IsEnabled = authAvailable && !_isAccountBusy;
        RegisterButton.IsEnabled = authAvailable && !_isAccountBusy;
        LogoutButton.IsEnabled = !_isAccountBusy && !_isSubmitting && !_isPreparingUpload;
        OpenUploadButton.IsEnabled = !_isAccountBusy && !_isDownloading && !_isPreparingUpload &&
            !_isSubmitting && !_isLikeBusy && !_isCommentBusy;

        var canSubmit = account?.CanSubmit == true;
        UploadAccessPrompt.Visibility = _isUploadVisible && !canSubmit ? Visibility.Visible : Visibility.Collapsed;
        UploadForm.Visibility = _isUploadVisible && canSubmit ? Visibility.Visible : Visibility.Collapsed;
        if (_isUploadVisible && account is null)
        {
            UploadAccessMessage.Text = "登录后可以选择本地皮肤或 ZIP 包提交。投稿会由网站审核，审核前不会公开展示。";
            UploadLoginButton.Visibility = Visibility.Visible;
            UploadPermissionSummary.Text = "需要登录并具备投稿权限。";
        }
        else if (_isUploadVisible && account is { CanSubmit: false })
        {
            UploadAccessMessage.Text = "当前账号没有投稿权限，暂时不能提交皮肤。";
            UploadLoginButton.Visibility = Visibility.Collapsed;
            UploadPermissionSummary.Text = "当前账号没有投稿权限。";
        }
        else
        {
            UploadPermissionSummary.Text = _isUploadVisible ? "投稿内容会经过网站审核。" : string.Empty;
        }

        SubmitUploadButton.IsEnabled = canSubmit && !_isSubmitting && !_isPreparingUpload && _uploadPackagePath is not null &&
            !string.IsNullOrWhiteSpace(UploadDescriptionBox.Text) &&
            Version.TryParse(UploadMinimumVersionBox.Text.Trim(), out _);
        CancelSubmissionButton.Visibility = _isSubmitting ? Visibility.Visible : Visibility.Collapsed;
        CancelSubmissionButton.IsEnabled = _isSubmitting;
        UploadDescriptionBox.IsEnabled = canSubmit && !_isSubmitting && !_isPreparingUpload;
        UploadAuthorBox.IsEnabled = canSubmit && !_isSubmitting && !_isPreparingUpload;
        UploadLicenseBox.IsEnabled = canSubmit && !_isSubmitting && !_isPreparingUpload;
        UploadMinimumVersionBox.IsEnabled = canSubmit && !_isSubmitting && !_isPreparingUpload;
        UploadTagsBox.IsEnabled = canSubmit && !_isSubmitting && !_isPreparingUpload;
        LocalSkinBox.IsEnabled = canSubmit && !_isSubmitting && !_isPreparingUpload;
        ExportLocalSkinButton.IsEnabled = canSubmit && !_isSubmitting && !_isPreparingUpload && LocalSkinBox.Items.Count > 0;
        PickUploadZipButton.IsEnabled = canSubmit && !_isSubmitting && !_isPreparingUpload;
        DetailsLikeButton.IsEnabled = IsDetailsVisible && !_isLikeBusy && !_isCommentBusy;
        CommentInput.IsEnabled = authenticated && IsDetailsVisible && !_isCommentBusy;
        SendCommentButton.Visibility = authenticated ? Visibility.Visible : Visibility.Collapsed;
        LoginToCommentButton.Visibility = authenticated ? Visibility.Collapsed : Visibility.Visible;
        LoginToCommentButton.IsEnabled = authAvailable && !_isAccountBusy;
        SendCommentButton.IsEnabled = authenticated && IsDetailsVisible && !_isCommentBusy && !_isLikeBusy &&
            !string.IsNullOrWhiteSpace(CommentInput.Text);
        PreviousCommentsButton.IsEnabled = IsDetailsVisible && !_isCommentBusy && _commentsPage > 1;
        NextCommentsButton.IsEnabled = IsDetailsVisible && !_isCommentBusy &&
            _commentsTotalPages > 0 && _commentsPage < _commentsTotalPages;

        if (_selectedRow is not null)
        {
            UpdateCommunityDetails(_selectedRow);
        }
    }

    private void SetAccountStatus(string text, bool isError = false)
    {
        if (_isClosed)
        {
            return;
        }

        MarketAccountStatusText.Text = text;
        MarketAccountStatusText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        MarketAccountStatusText.SetResourceReference(TextBlock.ForegroundProperty,
            isError ? "ErrorText" : "SecondaryText");
    }

    private void SetCommunityStatus(string text, bool isError = false)
    {
        if (_isClosed)
        {
            return;
        }

        DetailsCommunityStatus.Text = text;
        DetailsCommunityStatus.SetResourceReference(TextBlock.ForegroundProperty,
            isError ? "ErrorText" : "SecondaryText");
    }

    private void SetUploadStatus(string text, bool isError = false)
    {
        if (_isClosed)
        {
            return;
        }

        UploadStatusText.Text = text;
        UploadStatusText.SetResourceReference(TextBlock.ForegroundProperty,
            isError ? "ErrorText" : "SecondaryText");
    }

    private void CancelCommunityOperations()
    {
        _accountLifetimeCancellation?.Cancel();
        _commentsCancellation?.Cancel();
        _communityMutationCancellation?.Cancel();
        _submissionCancellation?.Cancel();
        _uploadPreparationCancellation?.Cancel();
    }

    private async Task DisposeCommunityAsync()
    {
        CancelCommunityOperations();
        if (_uploadPreparationTask is not null)
        {
            try
            {
                await _uploadPreparationTask;
            }
            catch (Exception)
            {
            }
        }

        if (_activeSubmissionTask is not null)
        {
            try
            {
                await _activeSubmissionTask;
            }
            catch (Exception)
            {
            }
        }

        CleanupUploadPackage(updateUi: false);
        if (_pendingTemporaryUploadPath is { } pendingPath)
        {
            TryDeleteUploadFile(pendingPath);
            _pendingTemporaryUploadPath = null;
        }
        if (_accountAuthProvider is not null)
        {
            try
            {
                await _accountAuthProvider.DisposeAsync();
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"市场账号桥接关闭失败：{exception}");
            }
        }

        _accountLifetimeCancellation?.Dispose();
        _commentsCancellation?.Dispose();
        _communityMutationCancellation?.Dispose();
        _submissionCancellation?.Dispose();
    }

    private void LoginButton_Click(object sender, RoutedEventArgs e) => _ = OpenAccountAsync(_client.LoginUri);
    private void RegisterButton_Click(object sender, RoutedEventArgs e) => _ = OpenAccountAsync(_client.RegistrationUri);
    private void LoginToCommentButton_Click(object sender, RoutedEventArgs e) => _ = OpenAccountAsync(_client.LoginUri);
    private void UploadLoginButton_Click(object sender, RoutedEventArgs e) => _ = OpenAccountAsync(_client.LoginUri);

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isAccountBusy || _isSubmitting || _isPreparingUpload)
        {
            return;
        }

        _isAccountBusy = true;
        UpdateCommunityControls();
        try
        {
            _accountGeneration++;
            _commentsCancellation?.Cancel();
            _communityMutationCancellation?.Cancel();
            if (_accountAuthProvider is not null)
            {
                await _accountAuthProvider.ClearCookiesAsync(_accountLifetimeCancellation?.Token ?? CancellationToken.None);
            }

            _client.ClearAccount();
            foreach (var row in _rows)
            {
                row.SetLikeState(false, row.LikeCount);
            }

            _comments.Clear();
            _commentsPage = 0;
            _commentsTotalPages = 0;
            if (IsDetailsVisible && _selectedRow is not null)
            {
                _ = LoadCommentsAsync(_selectedRow, 1);
            }

            SetAccountStatus("已退出市场账号。", isError: false);
        }
        catch (Exception exception)
        {
            SetAccountStatus($"退出账号失败：{exception.Message}", isError: true);
        }
        finally
        {
            _isAccountBusy = false;
            UpdateCommunityControls();
        }
    }

    private void DetailsLikeButton_Click(object sender, RoutedEventArgs e) => _ = ToggleLikeAsync();

    private async Task ToggleLikeAsync()
    {
        if (_isLikeBusy || _selectedRow is not { } row)
        {
            return;
        }

        if (_client.Account is null)
        {
            await OpenAccountAsync(_client.LoginUri);
            return;
        }

        _isLikeBusy = true;
        var userId = _client.Account.UserId;
        var accountGeneration = _accountGeneration;
        var desiredLiked = !row.LikedByMe;
        var cancellation = new CancellationTokenSource();
        _communityMutationCancellation?.Cancel();
        _communityMutationCancellation = cancellation;
        UpdateCommunityControls();
        SetCommunityStatus(desiredLiked ? "正在点赞…" : "正在取消点赞…");
        try
        {
            var result = await _client.SetLikeAsync(row.Id, desiredLiked, cancellation.Token);
            if (cancellation.IsCancellationRequested || _isClosed || !ReferenceEquals(_selectedRow, row) ||
                _client.Account?.UserId != userId || accountGeneration != _accountGeneration)
            {
                return;
            }

            row.SetLikeState(result.Liked, result.LikeCount);
            SetCommunityStatus(result.Liked ? "已点赞。" : "已取消点赞。");
        }
        catch (SkinMarketReauthenticationException exception)
        {
            if (!cancellation.IsCancellationRequested && !_isClosed &&
                ReferenceEquals(_selectedRow, row) && IsDetailsVisible && accountGeneration == _accountGeneration)
            {
                HandleReauthenticationFailure(exception, userId, accountGeneration, "点赞");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_isClosed && ReferenceEquals(_selectedRow, row) && IsDetailsVisible && accountGeneration == _accountGeneration)
            {
                SetCommunityStatus("点赞请求已取消；请查看当前状态后再操作。");
            }
        }
        catch (Exception exception)
        {
            if (!cancellation.IsCancellationRequested && !_isClosed &&
                ReferenceEquals(_selectedRow, row) && accountGeneration == _accountGeneration)
            {
                SetCommunityStatus($"点赞没有完成：{exception.Message}", isError: true);
            }
        }
        finally
        {
            if (ReferenceEquals(_communityMutationCancellation, cancellation))
            {
                _communityMutationCancellation = null;
            }

            cancellation.Dispose();
            _isLikeBusy = false;
            UpdateCommunityControls();
        }
    }

    private void SendCommentButton_Click(object sender, RoutedEventArgs e) => _ = SendCommentAsync();

    private async Task SendCommentAsync()
    {
        if (_isCommentBusy || _selectedRow is not { } row)
        {
            return;
        }

        if (_client.Account is null)
        {
            await OpenAccountAsync(_client.LoginUri);
            return;
        }

        var content = CommentInput.Text.Trim();
        if (content.Length == 0)
        {
            return;
        }

        _isCommentBusy = true;
        var userId = _client.Account.UserId;
        var accountGeneration = _accountGeneration;
        var cancellation = new CancellationTokenSource();
        _communityMutationCancellation?.Cancel();
        _communityMutationCancellation = cancellation;
        UpdateCommunityControls();
        SetCommunityStatus("正在提交评论…");
        try
        {
            var result = await _client.CreateCommentAsync(row.Id, content, cancellation.Token);
            if (cancellation.IsCancellationRequested || _isClosed || !ReferenceEquals(_selectedRow, row) ||
                _client.Account?.UserId != userId || accountGeneration != _accountGeneration)
            {
                return;
            }

            CommentInput.Clear();
            SetCommunityStatus(string.IsNullOrWhiteSpace(result.Message)
                ? result.Pending ? "评论已提交，等待审核。" : "评论已发布。"
                : result.Message);
            await LoadCommentsAsync(row, 1);
            if (_isClosed || cancellation.IsCancellationRequested || !ReferenceEquals(_selectedRow, row) ||
                !IsDetailsVisible || accountGeneration != _accountGeneration || _client.Account?.UserId != userId)
            {
                return;
            }

            if (result.Pending && string.IsNullOrWhiteSpace(DetailsCommunityStatus.Text))
            {
                SetCommunityStatus("评论已提交，等待审核。");
            }
        }
        catch (SkinMarketReauthenticationException exception)
        {
            if (!cancellation.IsCancellationRequested && !_isClosed &&
                ReferenceEquals(_selectedRow, row) && IsDetailsVisible && accountGeneration == _accountGeneration)
            {
                if (HandleReauthenticationFailure(exception, userId, accountGeneration, "评论"))
                {
                    SetCommunityStatus("市场登录状态已过期，请重新登录后继续。", isError: true);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_isClosed && ReferenceEquals(_selectedRow, row) && IsDetailsVisible && accountGeneration == _accountGeneration)
            {
                SetCommunityStatus("评论请求已取消；结果可能尚未确认，请勿立即重复提交。");
            }
        }
        catch (Exception exception)
        {
            if (!cancellation.IsCancellationRequested && !_isClosed &&
                ReferenceEquals(_selectedRow, row) && accountGeneration == _accountGeneration)
            {
                SetCommunityStatus($"评论没有完成，输入内容已保留：{exception.Message}", isError: true);
            }
        }
        finally
        {
            if (ReferenceEquals(_communityMutationCancellation, cancellation))
            {
                _communityMutationCancellation = null;
            }

            cancellation.Dispose();
            _isCommentBusy = false;
            UpdateCommunityControls();
        }
    }

    private void PreviousCommentsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRow is { } row && _commentsPage > 1)
        {
            _ = LoadCommentsAsync(row, _commentsPage - 1);
        }
    }

    private void NextCommentsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRow is { } row && _commentsPage < _commentsTotalPages)
        {
            _ = LoadCommentsAsync(row, _commentsPage + 1);
        }
    }

    private void OpenUploadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSubmitting || _isPreparingUpload)
        {
            return;
        }

        _returnToDetailsAfterUpload = IsDetailsVisible;
        _returnToDetailsRow = _selectedRow;
        if (_returnToDetailsAfterUpload)
        {
            _commentsCancellation?.Cancel();
            DetailsPane.Visibility = Visibility.Collapsed;
        }
        else
        {
            CatalogPane.Visibility = Visibility.Collapsed;
        }

        _isUploadVisible = true;
        UploadPane.Visibility = Visibility.Visible;
        LoadLocalSkins();
        UpdateCommunityControls();
    }

    private void BackFromUploadButton_Click(object sender, RoutedEventArgs e) => ReturnFromUpload();

    private void ReturnFromUpload()
    {
        if (_isSubmitting || _isPreparingUpload)
        {
            return;
        }

        CleanupUploadPackage();
        _isUploadVisible = false;
        UploadPane.Visibility = Visibility.Collapsed;
        if (_returnToDetailsAfterUpload && _returnToDetailsRow is { } row && _rows.Contains(row))
        {
            _selectedRow = row;
            DetailsPane.Visibility = Visibility.Visible;
            CatalogPane.Visibility = Visibility.Collapsed;
            BeginDetailsCommunityLoad(row);
        }
        else
        {
            DetailsPane.Visibility = Visibility.Collapsed;
            CatalogPane.Visibility = Visibility.Visible;
        }

        _returnToDetailsRow = null;
        _returnToDetailsAfterUpload = false;
        UpdateCommunityControls();
    }

    private void LoadLocalSkins()
    {
        var previousId = (LocalSkinBox.SelectedItem as MarketLocalSkinOption)?.Entry.Id;
        IReadOnlyList<SkinCatalogEntry> catalog;
        try
        {
            catalog = _readLocalSkinCatalog?.Invoke() ?? Array.Empty<SkinCatalogEntry>();
        }
        catch (Exception exception)
        {
            catalog = Array.Empty<SkinCatalogEntry>();
            SetUploadStatus($"无法读取本地皮肤库：{exception.Message}", isError: true);
        }

        var options = catalog.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(entry => new MarketLocalSkinOption(entry)).ToArray();
        LocalSkinBox.ItemsSource = options;
        LocalSkinBox.SelectedItem = options.FirstOrDefault(option =>
            option.Entry.Id.Equals(previousId, StringComparison.OrdinalIgnoreCase));
        UpdateCommunityControls();
    }

    private async void ExportLocalSkinButton_Click(object sender, RoutedEventArgs e)
    {
        if (LocalSkinBox.SelectedItem is not MarketLocalSkinOption option || _exportLocalSkinPackage is null)
        {
            SetUploadStatus("请先选择本地皮肤。", isError: true);
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "SkinManagerMarketSubmissions");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"{Guid.NewGuid():N}.zip");
        _pendingTemporaryUploadPath = temporaryPath;
        var preparation = PrepareUploadPackageAsync(
            temporaryPath,
            isTemporary: true,
            () => _exportLocalSkinPackage(option.Entry.Id, temporaryPath));
        _uploadPreparationTask = preparation;
        await preparation;
        if (ReferenceEquals(_uploadPreparationTask, preparation))
        {
            _uploadPreparationTask = null;
        }
    }

    private async void PickUploadZipButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择皮肤 ZIP 包",
            Filter = "皮肤 ZIP 包 (*.zip)|*.zip",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            var preparation = PrepareUploadPackageAsync(dialog.FileName, isTemporary: false);
            _uploadPreparationTask = preparation;
            await preparation;
            if (ReferenceEquals(_uploadPreparationTask, preparation))
            {
                _uploadPreparationTask = null;
            }
        }
    }

    private async Task PrepareUploadPackageAsync(string path, bool isTemporary, Action? beforeRead = null)
    {
        if (_isPreparingUpload || _isSubmitting || _isClosed)
        {
            if (isTemporary)
            {
                TryDeleteUploadFile(path);
                if (string.Equals(_pendingTemporaryUploadPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _pendingTemporaryUploadPath = null;
                }
            }

            return;
        }

        _isPreparingUpload = true;
        var cancellation = new CancellationTokenSource();
        _uploadPreparationCancellation = cancellation;
        SetUploadStatus("正在读取皮肤包…");
        UpdateCommunityControls();
        try
        {
            var package = await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                beforeRead?.Invoke();
                cancellation.Token.ThrowIfCancellationRequested();
                var fileInfo = new FileInfo(path);
                if (!fileInfo.Exists || fileInfo.Length is <= 0 or > SkinMarketClient.MaxDownloadBytes)
                {
                    throw new InvalidDataException("皮肤包为空或超过 64 MB 上传上限。");
                }

                var packageBytes = File.ReadAllBytes(path);
                cancellation.Token.ThrowIfCancellationRequested();
                return SkinPackageReader.ReadZipBytes(packageBytes);
            }, cancellation.Token);
            if (_isClosed || cancellation.IsCancellationRequested)
            {
                return;
            }

            var fullPath = Path.GetFullPath(path);
            var previousPath = _uploadPackagePath;
            var deletePrevious = _uploadPackageIsTemporary && previousPath is not null &&
                !previousPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase);
            _uploadPackagePath = fullPath;
            _uploadPackageIsTemporary = isTemporary;
            _uploadPackage = package;
            if (deletePrevious)
            {
                TryDeleteUploadFile(previousPath!);
            }

            if (isTemporary && string.Equals(_pendingTemporaryUploadPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _pendingTemporaryUploadPath = null;
            }

            UploadPackageSummary.Text = $"{package.Name} · {package.Id} · v{package.Version} · skin v{package.Definition.FormatVersion}";
            UploadAuthorBox.Text = package.Definition.Author ?? string.Empty;
            SetUploadStatus("包已读取；ID、名称和版本来自包清单。", isError: false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_isClosed)
            {
                SetUploadStatus("已取消读取皮肤包。");
            }
        }
        catch (Exception exception)
        {
            if (!_isClosed)
            {
                SetUploadStatus($"无法读取皮肤包：{exception.Message}", isError: true);
            }
        }
        finally
        {
            if (isTemporary && string.Equals(_pendingTemporaryUploadPath, path, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteUploadFile(path);
                _pendingTemporaryUploadPath = null;
            }

            if (ReferenceEquals(_uploadPreparationCancellation, cancellation))
            {
                _uploadPreparationCancellation = null;
            }

            cancellation.Dispose();
            _isPreparingUpload = false;
            if (!_isClosed)
            {
                UpdateCommunityControls();
            }
        }
    }

    private async void SubmitUploadButton_Click(object sender, RoutedEventArgs e)
    {
        var submission = SubmitSkinAsync();
        _activeSubmissionTask = submission;
        try
        {
            await submission;
        }
        finally
        {
            if (ReferenceEquals(_activeSubmissionTask, submission))
            {
                _activeSubmissionTask = null;
            }
        }
    }

    private async Task SubmitSkinAsync()
    {
        if (_isSubmitting || _client.Account is not { CanSubmit: true } account)
        {
            UpdateCommunityControls();
            return;
        }

        if (_uploadPackagePath is null || _uploadPackage is null)
        {
            SetUploadStatus("请选择本地皮肤或 ZIP 包。", isError: true);
            return;
        }

        var minimumVersion = UploadMinimumVersionBox.Text.Trim();
        if (!Version.TryParse(minimumVersion, out _))
        {
            SetUploadStatus("最低管理器版本格式无效，例如 1.1.3。", isError: true);
            return;
        }

        var draft = new SkinMarketSubmissionDraft(
            _uploadPackagePath,
            UploadDescriptionBox.Text.Trim(),
            UploadAuthorBox.Text.Trim(),
            UploadLicenseBox.Text.Trim(),
            minimumVersion,
            UploadTagsBox.Text.Split([',', '，', '、', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        _isSubmitting = true;
        var userId = account.UserId;
        var accountGeneration = _accountGeneration;
        var cancellation = new CancellationTokenSource();
        _submissionCancellation?.Cancel();
        _submissionCancellation = cancellation;
        UpdateCommunityControls();
        SetUploadStatus("正在提交审核…");
        try
        {
            var result = await _client.SubmitSkinAsync(draft, cancellation.Token);
            if (cancellation.IsCancellationRequested || _isClosed ||
                _client.Account?.UserId != userId || accountGeneration != _accountGeneration)
            {
                return;
            }

            SetUploadStatus(result.Message, isError: false);
            CleanupUploadPackage();
        }
        catch (SkinMarketReauthenticationException exception)
        {
            if (!cancellation.IsCancellationRequested && !_isClosed && accountGeneration == _accountGeneration &&
                HandleReauthenticationFailure(exception, userId, accountGeneration, "投稿"))
            {
                SetUploadStatus("市场登录状态已过期，请重新登录后继续。投稿表单与所选皮肤包仍保留。", isError: true);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_isClosed)
            {
                SetUploadStatus("投稿请求已取消；结果可能尚未确认，请勿立即重复提交。");
            }
        }
        catch (Exception exception)
        {
            if (!cancellation.IsCancellationRequested && !_isClosed &&
                _client.Account?.UserId == userId && accountGeneration == _accountGeneration)
            {
                SetUploadStatus($"投稿没有完成，表单与包仍保留：{exception.Message}", isError: true);
            }
        }
        finally
        {
            if (ReferenceEquals(_submissionCancellation, cancellation))
            {
                _submissionCancellation = null;
            }

            cancellation.Dispose();
            _isSubmitting = false;
            if (!_isClosed)
            {
                UpdateCommunityControls();
            }
        }
    }

    private void CancelSubmissionButton_Click(object sender, RoutedEventArgs e) => _submissionCancellation?.Cancel();

    private void UploadSubmissionField_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized && SubmitUploadButton is not null && UploadDescriptionBox is not null &&
            UploadMinimumVersionBox is not null && LoginButton is not null)
        {
            UpdateCommunityControls();
        }
    }

    private void CommunityInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized && SendCommentButton is not null && CommentInput is not null && LoginButton is not null)
        {
            UpdateCommunityControls();
        }
    }

    private void CleanupUploadPackage(bool updateUi = true)
    {
        var path = _uploadPackagePath;
        var shouldDelete = _uploadPackageIsTemporary;
        _uploadPackagePath = null;
        _uploadPackage = null;
        _uploadPackageIsTemporary = false;
        if (shouldDelete && path is not null)
        {
            TryDeleteUploadFile(path);
        }

        if (updateUi && !_isClosed && UploadPackageSummary is not null)
        {
            UploadPackageSummary.Text = "请选择本地皮肤或 ZIP 包。";
        }

        if (updateUi && !_isClosed)
        {
            UpdateCommunityControls();
        }
    }

    private static void TryDeleteUploadFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException exception)
        {
            Debug.WriteLine($"市场投稿临时包清理失败：{exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            Debug.WriteLine($"市场投稿临时包清理失败：{exception.Message}");
        }
    }

    private bool HandleReauthenticationFailure(
        Exception exception,
        int expectedUserId,
        int expectedAccountGeneration,
        string operation)
    {
        if (exception is not SkinMarketReauthenticationException || _isClosed || expectedUserId <= 0 ||
            expectedAccountGeneration != _accountGeneration || _client.Account is not null)
        {
            return false;
        }

        OnAccountChanged(expectedUserId);
        var message = $"{operation}请求发现登录状态已过期，请重新登录后继续。";
        SetAccountStatus(message, isError: true);
        if (IsDetailsVisible)
        {
            SetCommunityStatus(message, isError: true);
        }

        if (_isUploadVisible)
        {
            SetUploadStatus($"{message} 投稿表单与所选皮肤包仍保留。", isError: true);
        }

        UpdateCommunityControls();
        if (_isUploadVisible)
        {
            UploadAccessMessage.Text = $"{message} 投稿表单与所选皮肤包已保留。";
            UploadLoginButton.Visibility = Visibility.Visible;
        }

        return true;
    }
}
