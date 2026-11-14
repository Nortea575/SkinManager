using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using SkinManager.Market;

namespace SkinManager.Desktop;

public partial class SupportAuthorWindow : Window
{
    private static readonly Uri SponsorPageUri = new("https://blog.ancimen.top/?page_id=170");
    private static readonly Uri GitHubRepositoryUri = new("https://github.com/Nortea575/SkinManager");

    internal Action<Uri> ExternalUriLauncher { get; set; } = OpenExternalUri;
    internal event EventHandler? FeedbackRequested;

    public SupportAuthorWindow()
    {
        InitializeComponent();
        Icon = App.ReadApplicationIcon();
        WindowMotion.Attach(this, animateWindow: true, animateClose: true, animateButtonPress: true);
    }

    private void SupportAuthorLinkButton_Click(object sender, RoutedEventArgs e)
    {
        TryOpenUri(MarketLanguageUri.ForMarketPage(SponsorPageUri),
            AppLocalization.Get("无法打开赞助页，请检查系统浏览器设置。"), AppLocalization.Get("支持作者"));
    }

    private void GitHubLinkButton_Click(object sender, RoutedEventArgs e)
    {
        TryOpenUri(GitHubRepositoryUri, AppLocalization.Get("无法打开 GitHub 仓库，请检查系统浏览器设置。"), "GitHub");
    }

    private void FeedbackButton_Click(object sender, RoutedEventArgs e) =>
        FeedbackRequested?.Invoke(this, EventArgs.Empty);

    private void TryOpenUri(Uri uri, string errorMessage, string title)
    {
        try
        {
            ExternalUriLauncher(uri);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, errorMessage, title,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void OpenExternalUri(Uri uri) =>
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
}
