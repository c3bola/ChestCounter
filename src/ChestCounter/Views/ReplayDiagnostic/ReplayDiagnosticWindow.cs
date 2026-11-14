using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Unreal.Core.Models.Enums;

namespace ChestCounter;

internal sealed class ReplayDiagnosticWindow : Window
{
    private static readonly ParseMode[] ParsingModes =
    [
        ParseMode.EventsOnly,
        ParseMode.Minimal,
        ParseMode.Normal,
        ParseMode.Full,
    ];

    private readonly RuntimeContext _runtime;
    private readonly TextBox _path = new() { IsReadOnly = true };
    private readonly TextBlock _fileDetails = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBox _rawOutput = CreateOutputBox();
    private readonly TextBox _transcodedOutput = CreateOutputBox();
    private readonly TextBox _preSendOutput = CreateOutputBox();
    private readonly TextBox _responseOutput = CreateOutputBox();
    private readonly TextBox _logsOutput = CreateOutputBox();
    private readonly TextBox _parsingOutput = CreateOutputBox();
    private readonly TextBlock _parsingStatus = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _parsingSummary = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly ComboBox _parsingResultSelector = new() { MinWidth = 180, IsEnabled = false };
    private readonly Button _loadButton = new() { Content = "Carregar" };
    private readonly Button _readButton = new() { Content = "Ler arquivo", IsEnabled = false };
    private readonly Button _transcodeButton = new() { Content = "Transcodificar arquivo", IsEnabled = false };
    private readonly Button _preSendButton = new() { Content = "Pré-envio", IsEnabled = false };
    private readonly Button _sendButton = new() { Content = "Enviar", IsEnabled = false };
    private readonly Button _copyJsonButton = new() { Content = "Copiar JSON de processamento", IsEnabled = false };
    private readonly Button _copyParsingJsonButton = new() { Content = L.T("replay.parsing.copy"), IsEnabled = false };
    private readonly Button _saveParsingJsonButton = new() { Content = L.T("replay.parsing.save"), IsEnabled = false };
    private readonly Dictionary<ParseMode, Button> _parsingModeButtons = new();
    private readonly Dictionary<ParseMode, ReplayDiagnosticParsingResult> _parsingResults = new();

    private FileInfo? _selectedFile;
    private MatchRecord? _transcodedMatch;
    private ReplayDiagnosticPreparedData? _preparedData;
    private bool _readCompleted;
    private bool _transcodeCompleted;
    private bool _preSendCompleted;
    private bool _busy;

    public ReplayDiagnosticWindow(RuntimeContext runtime)
    {
        _runtime = runtime;
        Title = "Laboratório de Replay — Diagnóstico";
        Width = 1080;
        Height = 820;
        MinWidth = 760;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            RowSpacing = 10,
        };
        root.Children.Add(new TextBlock
        {
            Text = Title,
            FontSize = 20,
            FontWeight = Avalonia.Media.FontWeight.Bold,
        });

        var filePanel = new StackPanel { Spacing = 8 };
        Grid.SetRow(filePanel, 1);
        var pathRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
        };
        pathRow.Children.Add(_path);
        _loadButton.Click += OnLoadClicked;
        Grid.SetColumn(_loadButton, 1);
        pathRow.Children.Add(_loadButton);
        filePanel.Children.Add(pathRow);
        filePanel.Children.Add(_fileDetails);

        var operations = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        foreach (var button in new[] { _readButton, _transcodeButton, _preSendButton, _sendButton, _copyJsonButton })
        {
            button.Margin = new Thickness(0, 0, 8, 0);
            operations.Children.Add(button);
        }
        _readButton.Click += OnReadClicked;
        _transcodeButton.Click += OnTranscodeClicked;
        _preSendButton.Click += OnPreSendClicked;
        _sendButton.Click += OnSendClicked;
        _copyJsonButton.Click += OnCopyJsonClicked;
        filePanel.Children.Add(operations);
        root.Children.Add(filePanel);

        var tabs = new TabControl();
        Grid.SetRow(tabs, 2);
        tabs.Items.Add(CreateTab("Conteúdo bruto", _rawOutput));
        tabs.Items.Add(CreateTab("Dados transcodificados", _transcodedOutput));
        tabs.Items.Add(CreateTab("JSON de pré-envio", _preSendOutput));
        tabs.Items.Add(CreateTab("Resposta da API", _responseOutput));
        tabs.Items.Add(CreateTab("Logs", _logsOutput));
        tabs.Items.Add(CreateParsingTab());
        root.Children.Add(tabs);

        Grid.SetRow(_status, 3);
        root.Children.Add(_status);
        Content = root;
        SetStatus("Etapa atual: selecione um arquivo .replay.");
    }

    private async void OnLoadClicked(object? sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        SetStatus("Etapa atual: selecionando e validando o arquivo.");
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Selecionar arquivo .replay",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Replay Fortnite") { Patterns = ["*.replay"] },
                ],
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                SetStatus("Etapa atual: selecione um arquivo .replay.");
                return;
            }

            var file = await Task.Run(() => ReplayDiagnosticFileService.Load(path));
            _selectedFile = file;
            _path.Text = file.FullName;
            _fileDetails.Text =
                $"Arquivo carregado: {file.Name} · {file.Length:N0} bytes · " +
                $"Modificado: {file.LastWriteTimeUtc.ToLocalTime():g}";

            _readCompleted = false;
            _transcodeCompleted = false;
            _preSendCompleted = false;
            _transcodedMatch = null;
            _preparedData = null;
            _parsingResults.Clear();
            _rawOutput.Text = "";
            _transcodedOutput.Text = "";
            _preSendOutput.Text = "";
            _responseOutput.Text = "";
            _logsOutput.Text = "";
            _parsingOutput.Text = "";
            _parsingSummary.Text = "";
            _parsingStatus.Text = L.T("replay.parsing.select_mode");
            _parsingResultSelector.ItemsSource = null;
            _parsingResultSelector.SelectedItem = null;
            AppendLog($"[INFO] Arquivo selecionado: {file.FullName}");
            SetStatus($"Etapa atual: arquivo carregado ({file.Length:N0} bytes).");
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Load replay in diagnostic laboratory", ex);
            AppendLog($"[ERROR] Falha ao carregar o arquivo.{Environment.NewLine}{ex}");
            SetStatus("Falha ao carregar o arquivo. Consulte a aba Logs.");
        }
        finally
        {
            SetBusy(false);
            UpdateButtons();
        }
    }

    private async void OnReadClicked(object? sender, RoutedEventArgs e) =>
        await RunStageAsync(
            "leitura",
            _rawOutput,
            async () =>
            {
                var result = await Task.Run(() => ReplayDiagnosticFileService.Read(RequireSelectedFile()));
                return result.Details + Environment.NewLine + "Visualização hexadecimal:" + Environment.NewLine + result.HexPreview;
            },
            () => _readCompleted = true);

    private async void OnTranscodeClicked(object? sender, RoutedEventArgs e) =>
        await RunStageAsync(
            "transcodificação",
            _transcodedOutput,
            async () =>
            {
                var match = await ReplayDiagnosticTranscoder.TranscodeAsync(RequireSelectedFile());
                var json = JsonSerializer.Serialize(match, new JsonSerializerOptions { WriteIndented = true });
                _transcodedMatch = match;
                return json;
            },
            () => _transcodeCompleted = true);

    private async void OnPreSendClicked(object? sender, RoutedEventArgs e) =>
        await RunStageAsync(
            "pré-envio",
            _preSendOutput,
            () =>
            {
                var match = _transcodedMatch
                    ?? throw new InvalidOperationException("Transcodifique o replay antes de preparar o pré-envio.");
                var prepared = ReplayDiagnosticPreSendService.Prepare(match, RequireSelectedFile());
                _preparedData = prepared;
                return Task.FromResult(
                    prepared.DisplayJson
                    + Environment.NewLine
                    + Environment.NewLine
                    + "Campos obrigatórios do cadastro: fileSha256, fileName."
                    + Environment.NewLine
                    + "Campos opcionais do cadastro: fileSizeBytes, fileCreatedAt."
                    + Environment.NewLine
                    + "Campos obrigatórios do processamento: idempotencyKey, analyzerVersion, match, matchPlayers, containers, rewards."
                    + Environment.NewLine
                    + "Campos opcionais aninhados com valor null são omitidos, conforme as opções JSON do sincronizador.");
            },
            () => _preSendCompleted = true);

    private async void OnSendClicked(object? sender, RoutedEventArgs e)
    {
        if (_busy || !_preSendCompleted || _preparedData == null || _selectedFile == null || _transcodedMatch == null)
        {
            return;
        }

        ApiIntegrationConfig config;
        try
        {
            config = _runtime.ApiSync.Configuration;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Read API configuration for replay diagnostic", ex);
            AppendLog($"[ERROR] Não foi possível ler a configuração da API.{Environment.NewLine}{ex}");
            SetStatus("Falha ao ler a configuração da API.");
            return;
        }

        var confirmation = new InfoDialogWindow(
            "Confirmar envio para a API real",
            "Os dados deste replay serão transmitidos para a API configurada. O envio é manual e poderá criar registros reais.",
            $"Arquivo: {_selectedFile.FullName}{Environment.NewLine}API: {config.BaseUrl}",
            "Enviar",
            "Cancelar");
        if (!await confirmation.ShowDialog<bool>(this))
        {
            AppendLog("[INFO] Envio cancelado pelo usuário.");
            return;
        }

        await RunStageAsync(
            "envio",
            _responseOutput,
            async () =>
            {
                var sender = new ReplayDiagnosticApiSender();
                var result = await sender.SendAsync(
                    _runtime,
                    _preparedData.Snapshot,
                    _transcodedMatch,
                    _selectedFile,
                    OnApiDiagnostic,
                    OnApiProgress);
                return FormatApiResult(result, config.BaseUrl);
            },
            null);
    }

    private async void OnCopyJsonClicked(object? sender, RoutedEventArgs e)
    {
        if (_preparedData == null)
        {
            return;
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
            {
                throw new InvalidOperationException("A área de transferência não está disponível.");
            }

            await clipboard.SetTextAsync(_preparedData.RequestJson);
            AppendLog("[OK] JSON de pré-envio copiado.");
            SetStatus("JSON de pré-envio copiado.");
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Copy replay diagnostic JSON", ex);
            AppendLog($"[ERROR] Falha ao copiar o JSON.{Environment.NewLine}{ex}");
            SetStatus("Falha ao copiar o JSON.");
        }
    }

    private async void OnParsingModeClicked(ParseMode mode)
    {
        if (_busy || _selectedFile == null)
        {
            return;
        }

        var file = _selectedFile;
        SetBusy(true);
        _parsingStatus.Text = L.F("replay.parsing.transcoding", file.Name, mode);
        SetStatus(L.F("replay.parsing.stage_running", mode));
        AppendLog(L.F("replay.parsing.log_start", mode, file.Name));
        try
        {
            var result = await ReplayDiagnosticParsingTranscoder.TranscodeAsync(file, mode);
            _parsingResults[mode] = result;
            _parsingResultSelector.ItemsSource = ParsingModes.Where(_parsingResults.ContainsKey).ToArray();
            _parsingResultSelector.SelectedItem = mode;
            ShowParsingResult(result);
            _parsingStatus.Text = L.F("replay.parsing.completed", mode);
            AppendLog(L.F("replay.parsing.log_completed", mode, result.JsonSizeBytes));
            SetStatus(L.F("replay.parsing.stage_completed", mode));
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Replay diagnostic parsing mode {mode}", ex);
            _parsingStatus.Text = L.F("replay.parsing.failed_preserved", mode);
            var stage = ex is ReplayDiagnosticParsingException parsingException
                ? ParsingStageName(parsingException.Stage)
                : L.T("replay.parsing.stage_display");
            AppendLog(L.F("replay.parsing.log_failed", mode) + " " + L.F("replay.parsing.log_stage", stage) + Environment.NewLine + ex);
            SetStatus(L.F("replay.parsing.failed_status", mode));
        }
        finally
        {
            SetBusy(false);
            UpdateButtons();
        }
    }

    private async void OnCopyParsingJsonClicked(object? sender, RoutedEventArgs e)
    {
        if (GetSelectedParsingResult() is not { } result)
        {
            return;
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
            {
                throw new InvalidOperationException(L.T("replay.parsing.clipboard_unavailable"));
            }

            await clipboard.SetTextAsync(result.Json);
            _parsingStatus.Text = L.F("replay.parsing.copied", result.Mode);
            AppendLog("[OK] " + _parsingStatus.Text);
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Copy replay parsing JSON for {result.Mode}", ex);
            _parsingStatus.Text = L.F("replay.parsing.copy_failed", result.Mode);
            AppendLog("[ERROR] " + _parsingStatus.Text + Environment.NewLine + ex);
        }
    }

    private async void OnSaveParsingJsonClicked(object? sender, RoutedEventArgs e)
    {
        if (GetSelectedParsingResult() is not { } result)
        {
            return;
        }

        try
        {
            var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L.F("replay.parsing.save_title", result.Mode),
                SuggestedFileName = GetParsingFileName(result.Mode),
                DefaultExtension = "json",
                FileTypeChoices =
                [
                    new FilePickerFileType("JSON") { Patterns = ["*.json"] },
                ],
            });
            if (destination == null)
            {
                return;
            }

            await using var stream = await destination.OpenWriteAsync();
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await writer.WriteAsync(result.Json);
            _parsingStatus.Text = L.F("replay.parsing.saved", result.Mode, destination.Name);
            AppendLog("[OK] " + _parsingStatus.Text);
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Save replay parsing JSON for {result.Mode}", ex);
            _parsingStatus.Text = L.F("replay.parsing.save_failed", result.Mode);
            AppendLog("[ERROR] " + _parsingStatus.Text + Environment.NewLine + ex);
        }
    }

    private void OnParsingResultSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (GetSelectedParsingResult() is { } result)
        {
            ShowParsingResult(result);
        }
    }

    private void ShowParsingResult(ReplayDiagnosticParsingResult result)
    {
        _parsingOutput.Text = result.Json;
        _parsingSummary.Text = L.F(
            "replay.parsing.summary",
            result.Mode,
            result.FileName,
            result.ExecutedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", L.Culture),
            result.Duration.TotalMilliseconds,
            result.JsonSizeBytes,
            result.EventCount);
    }

    private ReplayDiagnosticParsingResult? GetSelectedParsingResult() =>
        _parsingResultSelector.SelectedItem is ParseMode mode && _parsingResults.TryGetValue(mode, out var result)
            ? result
            : null;

    private static string ParsingStageName(ReplayDiagnosticParsingStage stage) =>
        stage switch
        {
            ReplayDiagnosticParsingStage.FileAccess => L.T("replay.parsing.stage_file"),
            ReplayDiagnosticParsingStage.ParserInitialization => L.T("replay.parsing.stage_init"),
            ReplayDiagnosticParsingStage.ReadDecodeParseAndBuild => L.T("replay.parsing.stage_read"),
            ReplayDiagnosticParsingStage.JsonSerialization => L.T("replay.parsing.stage_json"),
            ReplayDiagnosticParsingStage.ResultSummary => L.T("replay.parsing.stage_summary"),
            _ => stage.ToString(),
        };

    private static string GetParsingFileName(ParseMode mode) =>
        mode switch
        {
            ParseMode.EventsOnly => "replay-events-only.json",
            ParseMode.Minimal => "replay-minimal.json",
            ParseMode.Normal => "replay-normal.json",
            ParseMode.Full => "replay-full.json",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, L.T("replay.parsing.invalid_mode")),
        };

    private TabItem CreateParsingTab()
    {
        var panel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*"),
            RowSpacing = 8,
        };
        panel.Children.Add(new TextBlock
        {
            Text = L.T("replay.parsing.title"),
            FontSize = 18,
            FontWeight = Avalonia.Media.FontWeight.Bold,
        });

        var modeButtons = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        foreach (var mode in ParsingModes)
        {
            var button = new Button
            {
                Content = L.F("replay.parsing.pending", mode),
                IsEnabled = false,
                Margin = new Thickness(0, 0, 8, 0),
            };
            button.Click += (_, _) => OnParsingModeClicked(mode);
            _parsingModeButtons.Add(mode, button);
            modeButtons.Children.Add(button);
        }
        Grid.SetRow(modeButtons, 1);
        panel.Children.Add(modeButtons);

        var resultActions = new WrapPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        resultActions.Children.Add(new TextBlock { Text = L.T("replay.parsing.result"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        _parsingResultSelector.SelectionChanged += OnParsingResultSelectionChanged;
        _parsingResultSelector.Margin = new Thickness(8, 0);
        resultActions.Children.Add(_parsingResultSelector);
        _copyParsingJsonButton.Margin = new Thickness(0, 0, 8, 0);
        _copyParsingJsonButton.Click += OnCopyParsingJsonClicked;
        resultActions.Children.Add(_copyParsingJsonButton);
        _saveParsingJsonButton.Click += OnSaveParsingJsonClicked;
        resultActions.Children.Add(_saveParsingJsonButton);
        Grid.SetRow(resultActions, 2);
        panel.Children.Add(resultActions);

        _parsingStatus.Text = L.T("replay.parsing.select_mode");
        Grid.SetRow(_parsingStatus, 3);
        panel.Children.Add(_parsingStatus);
        Grid.SetRow(_parsingSummary, 4);
        panel.Children.Add(_parsingSummary);
        Grid.SetRow(_parsingOutput, 5);
        panel.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _parsingOutput,
        });

        return new TabItem { Header = L.T("replay.parsing.title"), Content = panel };
    }

    private async Task RunStageAsync(
        string stage,
        TextBox output,
        Func<Task<string>> operation,
        Action? onSuccess)
    {
        if (_busy || _selectedFile == null)
        {
            return;
        }

        InvalidateAfter(stage);
        SetBusy(true);
        SetStatus($"Etapa atual: {stage} em andamento.");
        AppendLog($"[INFO] Iniciando {stage}.");
        try
        {
            var result = await operation();
            output.Text = result;
            onSuccess?.Invoke();
            AppendLog($"[OK] Etapa concluída: {stage}.");
            SetStatus($"Etapa atual: {stage} concluída.");
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Replay diagnostic {stage}", ex);
            output.Text = AppendResult(output.Text, $"[ERROR] Falha durante {stage}.{Environment.NewLine}{ex}");
            AppendLog($"[ERROR] Falha durante {stage}.{Environment.NewLine}{ex}");
            SetStatus($"Falha durante {stage}. Consulte a área de resultado e a aba Logs.");
        }
        finally
        {
            SetBusy(false);
            UpdateButtons();
        }
    }

    private void InvalidateAfter(string stage)
    {
        if (stage == "leitura")
        {
            _readCompleted = false;
            _transcodeCompleted = false;
            _preSendCompleted = false;
            _transcodedMatch = null;
            _preparedData = null;
        }
        else if (stage == "transcodificação")
        {
            _transcodeCompleted = false;
            _preSendCompleted = false;
            _transcodedMatch = null;
            _preparedData = null;
        }
        else if (stage == "pré-envio")
        {
            _preSendCompleted = false;
            _preparedData = null;
        }
    }

    private void OnApiDiagnostic(ApiOperationLogEntry entry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible)
            {
                return;
            }

            var marker = entry.Severity switch
            {
                ApiDiagnosticSeverity.Success => "[OK]",
                ApiDiagnosticSeverity.Warning => "[WARN]",
                ApiDiagnosticSeverity.Error => "[ERROR]",
                ApiDiagnosticSeverity.Progress => "[...]",
                _ => "[INFO]",
            };
            var statusCode = entry.HttpStatusCode is { } code ? $" HTTP {code}" : "";
            AppendLog($"[{entry.TimestampUtc.ToLocalTime():HH:mm:ss}] {marker}{statusCode} {entry.Description}");
            if (entry.ErrorDetails != null)
            {
                AppendLog(entry.ErrorDetails);
            }
        });
    }

    private void OnApiProgress(ApiOperationProgress progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible)
            {
                SetStatus($"Etapa atual: envio — {progress.Stage}. {progress.Status}");
            }
        });
    }

    private static string FormatApiResult(ReplayDiagnosticSendResult result, string baseUrl)
    {
        var text = new StringBuilder()
            .AppendLine(result.Summary.Message)
            .AppendLine($"Sincronizados: {result.Summary.Synchronized}")
            .AppendLine($"Falhas: {result.Summary.Failed}")
            .AppendLine($"Pendentes temporários: {result.Summary.Pending}")
            .AppendLine("A fila de diagnóstico é removida após a operação; não haverá novas tentativas automáticas.")
            .AppendLine();

        foreach (var entry in result.Diagnostics.Where(entry =>
                     entry.Method != null
                     || entry.HttpStatusCode != null
                     || entry.ResponseSummary != null
                     || entry.ErrorDetails != null))
        {
            text.AppendLine($"{entry.TimestampUtc.ToLocalTime():g} · {entry.Method ?? ""} {FormatUrl(baseUrl, entry.Endpoint)}".Trim());
            if (entry.HttpStatusCode is { } status)
            {
                text.AppendLine($"HTTP: {status}");
            }
            if (entry.Duration is { } duration)
            {
                text.AppendLine($"Duração: {duration.TotalMilliseconds:F0} ms");
            }
            if (entry.ResponseSummary != null)
            {
                text.AppendLine("Corpo da resposta:");
                text.AppendLine(entry.ResponseSummary);
            }
            if (entry.ErrorDetails != null)
            {
                text.AppendLine("Detalhes do erro:");
                text.AppendLine(entry.ErrorDetails);
            }
            text.AppendLine();
        }

        foreach (var response in result.HttpResponses)
        {
            text.AppendLine($"{response.Method} {response.Url} · HTTP {response.StatusCode}");
            foreach (var header in response.Headers)
            {
                text.AppendLine($"{header.Key}: {header.Value}");
            }
            text.AppendLine();
        }

        foreach (var exception in result.TransportExceptions)
        {
            text.AppendLine("Exceção HTTP completa:");
            text.AppendLine(exception);
            text.AppendLine();
        }
        text.AppendLine(result.PayloadComparison);
        if (result.CleanupWarning != null)
        {
            text.AppendLine("Aviso ao remover a fila temporária:");
            text.AppendLine(result.CleanupWarning);
        }

        return text.ToString();
    }

    private static string FormatUrl(string baseUrl, string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            return endpoint ?? "";
        }

        return Uri.TryCreate(baseUri, endpoint, out var uri) ? uri.ToString() : endpoint;
    }

    private FileInfo RequireSelectedFile() =>
        _selectedFile ?? throw new InvalidOperationException("Carregue um arquivo .replay antes de continuar.");

    private static TabItem CreateTab(string header, TextBox output) =>
        new()
        {
            Header = header,
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = output,
            },
        };

    private static TextBox CreateOutputBox() =>
        new()
        {
            AcceptsReturn = true,
            IsReadOnly = true,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            MinHeight = 400,
        };

    private static string AppendResult(string existing, string message) =>
        string.IsNullOrEmpty(existing) ? message : existing + Environment.NewLine + Environment.NewLine + message;

    private void AppendLog(string message) =>
        _logsOutput.Text = string.IsNullOrEmpty(_logsOutput.Text)
            ? message
            : _logsOutput.Text + Environment.NewLine + message;

    private void SetStatus(string message) => _status.Text = message;

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _loadButton.IsEnabled = !_busy;
        _readButton.IsEnabled = !_busy && _selectedFile != null;
        _transcodeButton.IsEnabled = !_busy && _readCompleted;
        _preSendButton.IsEnabled = !_busy && _transcodeCompleted;
        _sendButton.IsEnabled = !_busy && _preSendCompleted;
        _copyJsonButton.IsEnabled = !_busy && _preSendCompleted;
        foreach (var (mode, button) in _parsingModeButtons)
        {
            button.IsEnabled = !_busy && _selectedFile != null;
            button.Content = _parsingResults.ContainsKey(mode)
                ? L.F("replay.parsing.executed", mode)
                : L.F("replay.parsing.pending", mode);
        }
        _parsingResultSelector.IsEnabled = !_busy && _parsingResults.Count > 0;
        var hasSelectedResult = GetSelectedParsingResult() != null;
        _copyParsingJsonButton.IsEnabled = !_busy && hasSelectedResult;
        _saveParsingJsonButton.IsEnabled = !_busy && hasSelectedResult;
    }
}
