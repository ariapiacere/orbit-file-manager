using OrbitFileManager.Services;
using System.Windows;

namespace OrbitFileManager.Views.Dialogs;

public partial class ConfirmActionDialog : Window
{
    public ConfirmActionDialog(
        string title,
        string message,
        string confirmLabel,
        bool isDanger,
        bool useDarkTitleBar,
        bool hideCancel = false)
    {
        InitializeComponent();
        Title = title;
        HeaderTitleText.Text = title;
        MessageBodyText.Text = message;
        ConfirmBtn.Content = confirmLabel;

        if (isDanger)
            ConfirmBtn.Style = (Style)FindResource("DangerButtonStyle");

        if (hideCancel)
            CancelBtn.Visibility = Visibility.Collapsed;

        SourceInitialized += (_, _) => ThemeService.ApplyWindowTitleBarTheme(this, useDarkTitleBar);
    }

    private void ConfirmBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
