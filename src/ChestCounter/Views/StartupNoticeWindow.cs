using Avalonia.Controls;
using Avalonia.Layout;

namespace ChestCounter;

internal sealed class StartupNoticeWindow : Window
{
    public StartupNoticeWindow(string message)
    {
        var close = new Button
        {
            Content = L.T("settings.close"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        close.Click += (_, _) => Close();
        Title = "ChestCounter";
        Width = 430;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#20252B"));
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(22),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                close,
            },
        };
    }
}
