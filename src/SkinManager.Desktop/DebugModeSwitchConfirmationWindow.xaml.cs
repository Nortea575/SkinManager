using System.Windows;

namespace SkinManager.Desktop;

internal partial class DebugModeSwitchConfirmationWindow : Window
{
    internal DebugModeSwitchConfirmationWindow()
    {
        InitializeComponent();
        Icon = App.ReadApplicationIcon();
        WindowMotion.Attach(this, animateWindow: true, animateClose: true, animateButtonPress: true);
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
