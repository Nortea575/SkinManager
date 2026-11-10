using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace SkinManager.Desktop;

public partial class SupportAuthorWindow : Window
{
    private static readonly Uri SponsorPageUri = new("https://blog.ancimen.top/?page_id=170");

    internal Action<Uri> ExternalUriLauncher { get; set; } = OpenExternalUri;

    public SupportAuthorWindow()
    {
        InitializeComponent();
        Icon = App.ReadApplicationIcon();
        WindowMotion.Attach(this, animateWindow: true, animateClose: true, animateButtonPress: true);
    }

    private void SupportAuthorLinkButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ExternalUriLauncher(SponsorPageUri);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, "无法打开赞助页，请检查系统浏览器设置。", "支持作者",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private static void OpenExternalUri(Uri uri) =>
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
}
