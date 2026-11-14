using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Interactivity;

namespace ChestCounter;

internal sealed class ApiUrlEditWindow : Window
{
    private readonly TextBox _password = new() { PasswordChar = '*', PlaceholderText = L.T("settings.api_password") };
    private readonly TextBox _url = new() { PlaceholderText = L.T("settings.api_new_url") };
    private readonly TextBlock _error = new() { Foreground = new SolidColorBrush(Color.Parse("#FF6363")) };

    public ApiUrlEditWindow(string currentUrl)
    {
        Title = L.T("settings.api_url_edit_title");
        Width = 560;
        Height = 270;
        MinWidth = 480;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _url.Text = currentUrl;

        var save = new Button { Content = L.T("settings.api_apply"), MinWidth = 100 };
        save.Click += OnSave;
        var cancel = new Button { Content = L.T("settings.cancel"), MinWidth = 100 };
        cancel.Click += (_, _) => Close(false);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, save },
        };
        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = L.T("settings.api_password"), FontWeight = FontWeight.Bold },
                _password,
                new TextBlock { Text = L.T("settings.api_new_url"), FontWeight = FontWeight.Bold },
                _url,
                _error,
                buttons,
            },
        };
    }

    public string? SavedUrl { get; private set; }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (!LocalPasswordDialog.IsAuthorized(_password.Text))
        {
            _password.Text = "";
            _error.Text = L.T("settings.api_password_invalid");
            return;
        }
        if (!ApiIntegrationConfigStore.TryNormalizeBaseUrl(_url.Text, out var normalized))
        {
            _error.Text = L.T("settings.api_url_invalid");
            return;
        }

        SavedUrl = normalized;
        _password.Text = "";
        Close(true);
    }
}
