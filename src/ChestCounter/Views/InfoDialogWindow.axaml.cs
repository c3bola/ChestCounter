using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ChestCounter;

public partial class InfoDialogWindow : Window
{
    public InfoDialogWindow()
        : this(
            L.T("startup.welcome_to_chestcounter"),
            L.T("startup.counts_your_fortnite_chests_from_your_replays"),
            "",
            L.T("startup.start"))
    {
    }

    public InfoDialogWindow(
        string title,
        string message,
        string details,
        string acceptText,
        string? cancelText = null)
    {
        DialogTitle = title;
        Message = message;
        Details = details;
        AcceptText = acceptText;
        CancelText = cancelText ?? "";
        HasDetails = !string.IsNullOrWhiteSpace(details);
        InitializeComponent();
        DataContext = this;
        if (cancelText == null)
        {
            CancelButton.IsVisible = false;
        }
    }

    public string DialogTitle { get; }
    public string Message { get; }
    public string Details { get; }
    public string AcceptText { get; }
    public string CancelText { get; }
    public bool HasDetails { get; }

    private void OnAcceptClicked(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(false);
}
