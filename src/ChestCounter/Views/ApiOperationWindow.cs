using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace ChestCounter;

internal sealed class ApiOperationWindow : Window
{
    private readonly ApiSyncService _service;
    private readonly bool _testConnection;
    private readonly ObservableCollection<string> _logLines = new();
    private readonly List<ApiOperationLogEntry> _entries = new();
    private readonly TextBlock _operationText = new();
    private readonly TextBlock _urlText = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _startedText = new();
    private readonly TextBlock _statusText = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _progressText = new();
    private readonly Avalonia.Controls.ProgressBar _progressBar = new() { Minimum = 0, Maximum = 100, Height = 12 };
    private readonly ListBox _logList = new();
    private readonly TextBox _details = new()
    {
        AcceptsReturn = true,
        IsReadOnly = true,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
    };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ApiOperationProgress? _progress;

    public ApiOperationWindow(ApiSyncService service, bool testConnection)
    {
        _service = service;
        _testConnection = testConnection;
        Title = testConnection ? L.T("api_operation.test_title") : L.T("api_operation.sync_title");
        Width = 850;
        Height = 760;
        MinWidth = 640;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(18), Spacing = 10 };
        root.Children.Add(new TextBlock
        {
            Text = L.T("api_operation.title"),
            FontSize = 19,
            FontWeight = Avalonia.Media.FontWeight.Bold,
        });
        root.Children.Add(_operationText);
        root.Children.Add(_urlText);
        root.Children.Add(_startedText);
        root.Children.Add(_statusText);
        root.Children.Add(_progressBar);
        root.Children.Add(_progressText);
        root.Children.Add(new TextBlock
        {
            Text = L.T("api_operation.log"),
            FontWeight = Avalonia.Media.FontWeight.Bold,
            Margin = new Thickness(0, 6, 0, 0),
        });

        _logList.ItemsSource = _logLines;
        _logList.Height = 210;
        _logList.SelectionChanged += OnLogSelectionChanged;
        root.Children.Add(_logList);

        root.Children.Add(new TextBlock
        {
            Text = L.T("api_operation.details"),
            FontWeight = Avalonia.Media.FontWeight.Bold,
        });
        root.Children.Add(new ScrollViewer
        {
            Height = 230,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _details,
        });
        root.Children.Add(new TextBlock
        {
            Text = L.T("api_operation.close_hint"),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#AEB8C7")),
            FontSize = 11,
        });
        Content = root;

        _service.ProgressChanged += OnProgressChanged;
        _service.DiagnosticReported += OnDiagnosticReported;
        Closed += OnClosed;
        _timer.Tick += OnTimerTick;
        _timer.Start();

        if (_service.Progress is { } current)
        {
            ApplyProgress(current);
        }
        _operationText.Text = testConnection ? L.T("api_operation.test_title") : L.T("api_operation.sync_title");
        _details.Text = L.T("api_operation.select_log");
    }

    public async Task<string> RunAsync()
    {
        try
        {
            if (_testConnection)
            {
                return await _service.TestConnectionAsync();
            }

            var summary = await _service.SyncNowAsync();
            return summary.Message;
        }
        catch (Exception ex)
        {
            ErrorLog.Write(_testConnection ? "Test API connection" : "Manual API synchronization", ex);
            var safeMessage = ex.Message;
            ReportUnexpectedFailure(safeMessage);
            return safeMessage;
        }
    }

    private void OnProgressChanged(ApiOperationProgress progress) =>
        Dispatcher.UIThread.Post(() => ApplyProgress(progress));

    private void OnDiagnosticReported(ApiOperationLogEntry entry) =>
        Dispatcher.UIThread.Post(() => AddDiagnostic(entry));

    private void ApplyProgress(ApiOperationProgress progress)
    {
        _progress = progress;
        _operationText.Text = progress.Operation;
        _urlText.Text = L.F("api_operation.url", progress.BaseUrl);
        _startedText.Text = L.F("api_operation.started", progress.StartedAtUtc.ToLocalTime().ToString("g", L.Culture));
        _statusText.Text = $"{L.T("api_operation.stage")}: {progress.Stage}\n{progress.Status}";

        if (progress.Total is null && progress.Stage == "Operation failed")
        {
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = 0;
            _progressText.Text = L.T("api_operation.not_started");
        }
        else if (progress.Total is { } total)
        {
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = total == 0 ? 100 : Math.Min(100, progress.Processed * 100d / total);
            _progressText.Text = L.F(
                "api_operation.counts",
                total,
                progress.Processed,
                progress.Succeeded,
                progress.Failed,
                progress.Pending);
        }
        else
        {
            _progressBar.IsIndeterminate = true;
            _progressText.Text = L.T("api_operation.waiting");
        }

        if (progress.Stage is "Operation complete" or "Operation failed")
        {
            _timer.Stop();
        }
        else if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
        OnTimerTick(null, EventArgs.Empty);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_progress is { } progress)
        {
            var elapsed = DateTimeOffset.UtcNow - progress.StartedAtUtc;
            _startedText.Text = L.F(
                "api_operation.elapsed",
                progress.StartedAtUtc.ToLocalTime().ToString("g", L.Culture),
                elapsed.ToString(@"hh\:mm\:ss"));
        }
    }

    private void AddDiagnostic(ApiOperationLogEntry entry)
    {
        if (!IsVisible)
        {
            return;
        }

        _entries.Add(entry);
        var marker = entry.Severity switch
        {
            ApiDiagnosticSeverity.Success => "[OK]",
            ApiDiagnosticSeverity.Warning => "[!]",
            ApiDiagnosticSeverity.Error => "[ERROR]",
            ApiDiagnosticSeverity.Progress => "[...]",
            _ => "[INFO]",
        };
        var replay = entry.ReplayFileName is { Length: > 0 } name ? $" {name}" : "";
        var http = entry.HttpStatusCode is { } code ? $" HTTP {code}" : "";
        _logLines.Add($"[{entry.TimestampUtc.ToLocalTime():HH:mm:ss}] {marker}{replay}{http} {entry.Description}");
        _logList.SelectedIndex = _logLines.Count - 1;
    }

    private void OnLogSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_logList.SelectedIndex < 0 || _logList.SelectedIndex >= _entries.Count)
        {
            return;
        }

        var entry = _entries[_logList.SelectedIndex];
        var details = new List<string>
        {
            $"{entry.TimestampUtc.ToLocalTime():g} · {entry.Severity}",
            entry.Description,
        };
        if (entry.Method != null || entry.Endpoint != null)
        {
            details.Add($"{entry.Method ?? ""} {entry.Endpoint ?? ""}".Trim());
        }
        if (entry.ReplayFileName != null)
        {
            details.Add($"{L.T("api_operation.replay")}: {entry.ReplayFileName}");
        }
        if (entry.ReplaySha256 != null)
        {
            details.Add($"SHA-256: {entry.ReplaySha256}");
        }
        if (entry.RequestBody != null)
        {
            details.Add(L.T("api_operation.request_body"));
            details.Add(FormatJson(entry.RequestBody));
        }
        if (entry.HttpStatusCode is { } status)
        {
            details.Add($"HTTP: {status}");
        }
        if (entry.ResponseSummary != null)
        {
            details.Add(L.T("api_operation.response"));
            details.Add(FormatJson(entry.ResponseSummary));
        }
        if (entry.Duration is { } duration)
        {
            details.Add(L.F("api_operation.duration", duration.TotalMilliseconds.ToString("F0", L.Culture)));
        }
        if (entry.IsRetryable is { } retryable)
        {
            details.Add(L.F("api_operation.retryable", retryable ? L.T("common.yes") : L.T("common.no")));
        }
        if (entry.ErrorDetails != null)
        {
            details.Add(L.T("api_operation.error_details"));
            details.Add(entry.ErrorDetails);
        }
        _details.Text = string.Join(Environment.NewLine, details);
    }

    private static string FormatJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private void ReportUnexpectedFailure(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible)
            {
                return;
            }

            _statusText.Text = message;
            _progressBar.IsIndeterminate = false;
            _details.Text = message;
            _logLines.Add($"[{DateTime.Now:HH:mm:ss}] [ERROR] {message}");
        });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _service.ProgressChanged -= OnProgressChanged;
        _service.DiagnosticReported -= OnDiagnosticReported;
        _logList.SelectionChanged -= OnLogSelectionChanged;
    }
}
