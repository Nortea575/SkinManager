using System.Text;
using System.Windows;
using System.Windows.Controls;
using SkinManager.Core;
using SkinManager.Market;

namespace SkinManager.Desktop;

public partial class FeedbackWindow : Window
{
    private readonly MarketAccountSession _accountSession;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _isAccountBusy;
    private bool _isSending;
    private bool _sendSucceeded;
    private bool _isClosed;

    internal FeedbackWindow(MarketAccountSession accountSession)
    {
        _accountSession = accountSession ?? throw new ArgumentNullException(nameof(accountSession));
        InitializeComponent();
        Icon = App.ReadApplicationIcon();
        WindowMotion.Attach(this, animateWindow: true, animateClose: true, animateButtonPress: true);
        TitleCharacterCount.Text = AppLocalization.Format(
            "字符计数 · {0}/{1}",
            0,
            SkinMarketFeedbackRules.MaxTitleCharacters);
        MessageCharacterCount.Text = AppLocalization.Format(
            "字符计数 · {0}/{1}",
            0,
            SkinMarketFeedbackRules.MaxMessageCharacters);
        _accountSession.StateChanged += AccountSession_StateChanged;
        AppLocalization.LanguageChanged += FeedbackWindow_LanguageChanged;
        Loaded += FeedbackWindow_Loaded;
        UpdateControls();
    }

    internal TextBox TitleBoxForTests => TitleBox;
    internal TextBox MessageBoxForTests => MessageBox;
    internal Button LoginButtonForTests => LoginButton;
    internal Button SubmitButtonForTests => SubmitButton;
    internal Button CloseButtonForTests => CloseButton;
    internal TextBlock AccountStatusTextForTests => AccountStatusText;
    internal TextBlock OperationStatusTextForTests => OperationStatusText;
    internal bool IsSendingForTests => _isSending;

    private async void FeedbackWindow_Loaded(object sender, RoutedEventArgs e) =>
        await RefreshAccountAsync();

    private async Task RefreshAccountAsync()
    {
        if (_isClosed || _isAccountBusy)
        {
            return;
        }

        if (_accountSession.AuthProvider is null)
        {
            SetAccountStatus("尚未登录，登录后可提交反馈。");
            UpdateControls();
            return;
        }

        _isAccountBusy = true;
        SetOperationStatus("正在验证市场账号…");
        UpdateControls();
        try
        {
            await _accountSession.RefreshAsync(_lifetimeCancellation.Token);
            if (!_isClosed)
            {
                UpdateAccountStatus();
                ClearOperationStatus();
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_isClosed)
            {
                SetAccountStatus("无法验证市场账号：{0}", exception.Message, isError: true);
                ClearOperationStatus();
            }
        }
        finally
        {
            _isAccountBusy = false;
            UpdateControls();
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed || _isAccountBusy || _isSending || _accountSession.AuthProvider is null)
        {
            return;
        }

        _isAccountBusy = true;
        SetOperationStatus("正在打开本站账号页面…");
        UpdateControls();
        try
        {
            var account = await _accountSession.SignInAsync(
                this,
                _accountSession.Client.LoginUri,
                _lifetimeCancellation.Token);
            if (_isClosed)
            {
                return;
            }

            if (account is null)
            {
                SetAccountStatus("尚未登录，登录后可提交反馈。", isError: true);
                SetOperationStatus("账号登录失败：{0}", AppLocalization.Get("尚未验证为登录状态；完成网站注册验证后可再次登录。"), isError: true);
            }
            else
            {
                UpdateAccountStatus();
                ClearOperationStatus();
                TitleBox.Focus();
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_isClosed)
            {
                SetAccountStatus("账号登录失败：{0}", exception.Message, isError: true);
                SetOperationStatus(exception.Message, isError: true);
            }
        }
        finally
        {
            _isAccountBusy = false;
            UpdateControls();
        }
    }

    private async void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed || _isSending || _sendSucceeded)
        {
            return;
        }

        if (!ValidateFields() || _accountSession.Account is null)
        {
            if (_accountSession.Account is null)
            {
                SetOperationStatus("登录市场账号后即可发送反馈。", isError: true);
            }

            UpdateControls();
            return;
        }

        _isSending = true;
        SetOperationStatus("正在发送反馈…");
        UpdateControls();
        try
        {
            var result = await _accountSession.SubmitFeedbackAsync(
                TitleBox.Text,
                MessageBox.Text,
                UpdateCoordinator.ReadCurrentVersionTag(),
                _lifetimeCancellation.Token);
            if (_isClosed)
            {
                return;
            }

            _sendSucceeded = true;
            SetOperationStatus(string.IsNullOrWhiteSpace(result.Message)
                ? AppLocalization.Get("反馈发送成功。")
                : result.Message);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_isClosed)
            {
                SetOperationStatus(exception.Message, isError: true);
            }
        }
        finally
        {
            _isSending = false;
            UpdateControls();
        }
    }

    private bool ValidateFields()
    {
        var title = TitleBox.Text;
        var message = MessageBox.Text;
        if (string.IsNullOrWhiteSpace(title))
        {
            SetOperationStatus("反馈标题不能为空。", isError: true);
            TitleBox.Focus();
            return false;
        }

        if (SkinMarketFeedbackRules.CountUnicodeCharacters(title) > SkinMarketFeedbackRules.MaxTitleCharacters)
        {
            SetOperationStatus("{0}不能超过 {1} 个字符。",
                AppLocalization.Get("反馈标题"),
                SkinMarketFeedbackRules.MaxTitleCharacters,
                isError: true);
            TitleBox.Focus();
            return false;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            SetOperationStatus("反馈内容不能为空。", isError: true);
            MessageBox.Focus();
            return false;
        }

        if (SkinMarketFeedbackRules.CountUnicodeCharacters(message) > SkinMarketFeedbackRules.MaxMessageCharacters)
        {
            SetOperationStatus("{0}不能超过 {1} 个字符。",
                AppLocalization.Get("反馈内容"),
                SkinMarketFeedbackRules.MaxMessageCharacters,
                isError: true);
            MessageBox.Focus();
            return false;
        }

        return true;
    }

    private void TitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        LimitUnicodeCharacters(TitleBox, SkinMarketFeedbackRules.MaxTitleCharacters);
        TitleCharacterCount.Text = AppLocalization.Format(
            "字符计数 · {0}/{1}",
            SkinMarketFeedbackRules.CountUnicodeCharacters(TitleBox.Text),
            SkinMarketFeedbackRules.MaxTitleCharacters);
        UpdateControls();
    }

    private void MessageBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        LimitUnicodeCharacters(MessageBox, SkinMarketFeedbackRules.MaxMessageCharacters);
        MessageCharacterCount.Text = AppLocalization.Format(
            "字符计数 · {0}/{1}",
            SkinMarketFeedbackRules.CountUnicodeCharacters(MessageBox.Text),
            SkinMarketFeedbackRules.MaxMessageCharacters);
        UpdateControls();
    }

    private static void LimitUnicodeCharacters(TextBox textBox, int maximum)
    {
        if (SkinMarketFeedbackRules.CountUnicodeCharacters(textBox.Text) <= maximum)
        {
            return;
        }

        var builder = new StringBuilder();
        var count = 0;
        foreach (var rune in textBox.Text.EnumerateRunes())
        {
            if (count >= maximum)
            {
                break;
            }

            builder.Append(rune.ToString());
            count++;
        }

        textBox.Text = builder.ToString();
        textBox.CaretIndex = textBox.Text.Length;
    }

    private void AccountSession_StateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                _ = Dispatcher.BeginInvoke(new Action(() => AccountSession_StateChanged(sender, e)));
            }

            return;
        }

        if (_isClosed)
        {
            return;
        }

        UpdateAccountStatus();
        UpdateControls();
    }

    private void FeedbackWindow_LanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                _ = Dispatcher.BeginInvoke(new Action(() => FeedbackWindow_LanguageChanged(sender, e)));
            }

            return;
        }

        TitleCharacterCount.Text = AppLocalization.Format(
            "字符计数 · {0}/{1}",
            SkinMarketFeedbackRules.CountUnicodeCharacters(TitleBox.Text),
            SkinMarketFeedbackRules.MaxTitleCharacters);
        MessageCharacterCount.Text = AppLocalization.Format(
            "字符计数 · {0}/{1}",
            SkinMarketFeedbackRules.CountUnicodeCharacters(MessageBox.Text),
            SkinMarketFeedbackRules.MaxMessageCharacters);
        UpdateControls();
    }

    private void UpdateAccountStatus()
    {
        if (_accountSession.Account is { } account)
        {
            SetAccountStatus("已登录：{0}", account.DisplayName);
        }
        else
        {
            SetAccountStatus("尚未登录，登录后可提交反馈。");
        }
    }

    private void SetAccountStatus(string source, string? argument = null, bool isError = false)
    {
        if (argument is null)
        {
            LocalizedUiText.SetText(AccountStatusText, source);
        }
        else
        {
            LocalizedUiText.SetText(AccountStatusText, source, argument);
        }

        AccountStatusText.SetResourceReference(TextBlock.ForegroundProperty,
            isError ? "ErrorText" : "SecondaryText");
    }

    private void SetOperationStatus(string source, bool isError = false) =>
        SetOperationStatusCore(source, isError, Array.Empty<object?>());

    private void SetOperationStatus(string source, string argument, bool isError = false) =>
        SetOperationStatusCore(source, isError, [argument]);

    private void SetOperationStatus(string source, string firstArgument, int secondArgument, bool isError = false) =>
        SetOperationStatusCore(source, isError, [firstArgument, secondArgument]);

    private void SetOperationStatusCore(string source, bool isError, object?[] arguments)
    {
        LocalizedUiText.SetText(OperationStatusText, source, arguments);
        OperationStatusText.Visibility = string.IsNullOrWhiteSpace(LocalizedUiText.Format(source, arguments))
            ? Visibility.Collapsed
            : Visibility.Visible;
        OperationStatusText.SetResourceReference(TextBlock.ForegroundProperty,
            isError ? "ErrorText" : "SecondaryText");
    }

    private void ClearOperationStatus() => SetOperationStatus(string.Empty);

    private void UpdateControls()
    {
        if (TitleBox is null)
        {
            return;
        }

        var accountBusy = _isAccountBusy || _accountSession.IsBusy;
        var account = _accountSession.Account;
        LoginButton.Visibility = account is null ? Visibility.Visible : Visibility.Collapsed;
        LoginButton.IsEnabled = !accountBusy && !_isSending && _accountSession.AuthProvider is not null;
        TitleBox.IsEnabled = !_isSending;
        MessageBox.IsEnabled = !_isSending;
        TitleBox.IsReadOnly = _sendSucceeded;
        MessageBox.IsReadOnly = _sendSucceeded;
        SubmitButton.IsEnabled = !_isSending && !_sendSucceeded && !accountBusy && account is not null &&
            !string.IsNullOrWhiteSpace(TitleBox.Text) &&
            !string.IsNullOrWhiteSpace(MessageBox.Text);
        LocalizedUiText.SetContent(SubmitButton, _sendSucceeded ? "已发送" : "发送反馈");
        LocalizedUiText.SetContent(CloseButton, _sendSucceeded ? "关闭" : "取消");
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void FeedbackWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _isClosed = true;
        _lifetimeCancellation.Cancel();
    }

    private void FeedbackWindow_Closed(object? sender, EventArgs e)
    {
        _accountSession.StateChanged -= AccountSession_StateChanged;
        AppLocalization.LanguageChanged -= FeedbackWindow_LanguageChanged;
        _lifetimeCancellation.Dispose();
    }
}
