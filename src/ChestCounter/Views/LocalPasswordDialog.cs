using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace ChestCounter;

internal sealed class LocalPasswordDialog : Window
{
    private const string LocalPassword = "fnbr26";
    private readonly TextBox _password = new() { PasswordChar = '*', PlaceholderText = L.T("settings.api_password") };
    private readonly TextBlock _error = new() { Foreground = new SolidColorBrush(Color.Parse("#FF6363")) };

    private LocalPasswordDialog()
    {
        Title = L.T("settings.api_password");
        Width = 420;
        Height = 190;
        MinWidth = 360;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var confirm = new Button { Content = L.T("settings.api_apply"), MinWidth = 100 };
        confirm.Click += OnConfirm;
        var cancel = new Button { Content = L.T("settings.cancel"), MinWidth = 100 };
        cancel.Click += (_, _) => Close(false);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, confirm },
        };
        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = L.T("settings.api_password"), FontWeight = FontWeight.Bold },
                _password,
                _error,
                buttons,
            },
        };
    }

    public static bool IsAuthorized(string? password) =>
        string.Equals(password, LocalPassword, StringComparison.Ordinal);

    public static Task<bool> RequestAsync(Window owner) =>
        new LocalPasswordDialog().ShowDialog<bool>(owner);

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (!IsAuthorized(_password.Text))
        {
            _password.Text = "";
            _error.Text = L.T("settings.api_password_invalid");
            return;
        }

        _password.Text = "";
        Close(true);
    }
}
