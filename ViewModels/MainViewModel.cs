using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using InterviewAssistant.Commands;
using InterviewAssistant.Services;
using Microsoft.Win32;

namespace InterviewAssistant.ViewModels;

public class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private static readonly string[] WhisperLanguages = { "auto", "ru", "en", "ua" };

    private readonly AudioCaptureService _audioCapture;
    private SpeechToTextService _stt;
    private LocalLLMService _localLLM;
    private CancellationTokenSource? _llmCts;

    // STT recreation is deferred until the next transcription to avoid
    // disposing an instance that might be mid-use.
    private bool _sttRecreationPending;

    // ── Состояние ────────────────────────────────────────────────────────────
    private bool   _isRecording;
    private bool   _isProcessing;
    private bool   _isSttProcessing;
    private string _statusText = "Готов. Нажмите Ctrl+Space для начала записи.";

    // ── Контент ──────────────────────────────────────────────────────────────
    private string _recognizedQuestion = string.Empty;
    private string _localLLMResponse   = string.Empty;

    // ── Тайминги ─────────────────────────────────────────────────────────────
    private string _sttElapsedText  = string.Empty;
    private string _localLlmElapsed = string.Empty;

    // ── Настройки STT ────────────────────────────────────────────────────────
    private string _whisperModelPath      = @"D:\ggml-medium.bin";
    private int    _whisperLanguageIndex  = 0;

    // ── Настройки LLM ────────────────────────────────────────────────────────
    private LocalLlmProvider _selectedProvider = LocalLlmProvider.Ollama;
    private string           _localLlmModel    = "llama3:latest";
    private string           _llmBaseUrl;
    private string           _llmSystemPrompt  = LocalLLMService.DefaultSystemPrompt;

    // ── UI ───────────────────────────────────────────────────────────────────
    private bool _isScreenCaptureHidden = true;
    private bool _isSettingsVisible     = false;

    public MainViewModel()
    {
        _llmBaseUrl = LocalLLMService.DefaultEndpoints[_selectedProvider].ToString();

        _audioCapture = new AudioCaptureService();
        _stt          = new SpeechToTextService(_whisperModelPath, WhisperLanguages[_whisperLanguageIndex]);
        _localLLM     = new LocalLLMService(_selectedProvider, _localLlmModel, _llmBaseUrl, _llmSystemPrompt);

        _audioCapture.RecordingStarted += (_, _) => UI(() =>
        {
            IsRecording = true;
            _llmCts?.Cancel();
            StatusText = "Запись... Ctrl+Space для остановки.";
        });

        _audioCapture.RecordingStopped += async (_, wavPath) =>
        {
            UI(() =>
            {
                IsRecording     = false;
                IsProcessing    = true;
                IsSttProcessing = true;
                SttElapsedText  = string.Empty;
                LocalLlmElapsed = string.Empty;
                StatusText = "Распознавание речи...";
            });

            try
            {
                if (_sttRecreationPending) RecreateStt();

                var sttSw = Stopwatch.StartNew();
                var text  = await _stt.TranscribeAsync(wavPath);
                sttSw.Stop();

                UI(() => IsSttProcessing = false);

                if (string.IsNullOrWhiteSpace(text))
                {
                    UI(() =>
                    {
                        StatusText   = "Речь не распознана. Попробуйте ещё раз.";
                        IsProcessing = false;
                    });
                    return;
                }

                UI(() =>
                {
                    RecognizedQuestion = text;
                    SttElapsedText     = $"{sttSw.Elapsed.TotalSeconds:F1}с";
                    LocalLLMResponse   = string.Empty;
                    StatusText = "Генерация ответа...";
                });

                _llmCts = new CancellationTokenSource();
                await StreamLocalLLMAsync(text, _llmCts.Token);

                UI(() => StatusText = "Готово. Ctrl+Space — новая запись.");
            }
            catch (OperationCanceledException)
            {
                UI(() => StatusText = "Прервано. Ctrl+Space — новая запись.");
            }
            catch (Exception ex)
            {
                UI(() => StatusText = $"Ошибка: {ex.Message}");
            }
            finally
            {
                UI(() => { IsProcessing = false; IsSttProcessing = false; });
                try { File.Delete(wavPath); } catch { }
            }
        };

        ToggleRecordingCommand = new RelayCommand(
            execute:    ToggleRecording,
            canExecute: () => !IsProcessing);

        RetryLocalLLMCommand = new RelayCommand(
            execute:    () => _ = RetryLocalLLMAsync(),
            canExecute: () => !IsProcessing && !string.IsNullOrWhiteSpace(RecognizedQuestion));

        ToggleSettingsCommand = new RelayCommand(
            execute: () => IsSettingsVisible = !IsSettingsVisible);

        BrowseModelPathCommand = new RelayCommand(execute: () =>
        {
            var dlg = new OpenFileDialog
            {
                Title           = "Выбор модели Whisper",
                Filter          = "GGML модели (*.bin)|*.bin|Все файлы (*.*)|*.*",
                CheckFileExists = true,
            };
            var dir = Path.GetDirectoryName(_whisperModelPath);
            if (dir is not null && Directory.Exists(dir))
                dlg.InitialDirectory = dir;

            if (dlg.ShowDialog() == true)
                WhisperModelPath = dlg.FileName;
        });
    }

    // ── STT helpers ──────────────────────────────────────────────────────────

    private void RecreateStt()
    {
        var old = _stt;
        _stt = new SpeechToTextService(_whisperModelPath, WhisperLanguages[_whisperLanguageIndex]);
        _ = old.DisposeAsync().AsTask();
        _sttRecreationPending = false;
    }

    // ── LLM streaming ────────────────────────────────────────────────────────

    private async Task StreamLocalLLMAsync(string question, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await foreach (var token in _localLLM.GetStreamingResponseAsync(question, ct))
                UI(() => LocalLLMResponse += token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            UI(() => LocalLLMResponse += $"\n\n[Ошибка: {ex.Message}]");
        }
        finally
        {
            sw.Stop();
            UI(() => LocalLlmElapsed = $"{sw.Elapsed.TotalSeconds:F1}с");
        }
    }

    private async Task RetryLocalLLMAsync()
    {
        var question = RecognizedQuestion;
        if (string.IsNullOrWhiteSpace(question)) return;

        _llmCts?.Cancel();
        _llmCts = new CancellationTokenSource();

        UI(() =>
        {
            IsProcessing     = true;
            LocalLLMResponse = string.Empty;
            LocalLlmElapsed  = string.Empty;
            StatusText = "Генерация ответа...";
        });

        try
        {
            await StreamLocalLLMAsync(question, _llmCts.Token);
            UI(() => StatusText = "Готово. Ctrl+Space — новая запись.");
        }
        catch (OperationCanceledException)
        {
            UI(() => StatusText = "Прервано.");
        }
        catch (Exception ex)
        {
            UI(() => StatusText = $"Ошибка: {ex.Message}");
        }
        finally
        {
            UI(() => IsProcessing = false);
        }
    }

    private void RecreateLocalLLM()
    {
        if (!string.IsNullOrWhiteSpace(_localLlmModel))
            _localLLM = new LocalLLMService(_selectedProvider, _localLlmModel, _llmBaseUrl, _llmSystemPrompt);
    }

    // ── Состояние ────────────────────────────────────────────────────────────

    public bool IsRecording
    {
        get => _isRecording;
        private set => Set(ref _isRecording, value);
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        private set
        {
            Set(ref _isProcessing, value);
            (ToggleRecordingCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RetryLocalLLMCommand   as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsSttProcessing
    {
        get => _isSttProcessing;
        private set => Set(ref _isSttProcessing, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    // ── Контент ──────────────────────────────────────────────────────────────

    public string RecognizedQuestion
    {
        get => _recognizedQuestion;
        set
        {
            Set(ref _recognizedQuestion, value);
            (RetryLocalLLMCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string LocalLLMResponse
    {
        get => _localLLMResponse;
        set => Set(ref _localLLMResponse, value);
    }

    // ── Тайминги ─────────────────────────────────────────────────────────────

    public string SttElapsedText
    {
        get => _sttElapsedText;
        private set => Set(ref _sttElapsedText, value);
    }

    public string LocalLlmElapsed
    {
        get => _localLlmElapsed;
        private set => Set(ref _localLlmElapsed, value);
    }

    // ── Настройки STT ────────────────────────────────────────────────────────

    public string SttDeviceText => _stt.ComputeDevice;

    public string WhisperModelPath
    {
        get => _whisperModelPath;
        set
        {
            if (_whisperModelPath == value) return;
            _whisperModelPath     = value;
            _sttRecreationPending = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WhisperModelPath)));
        }
    }

    public int WhisperLanguageIndex
    {
        get => _whisperLanguageIndex;
        set
        {
            if (_whisperLanguageIndex == value) return;
            _whisperLanguageIndex = value;
            _sttRecreationPending = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WhisperLanguageIndex)));
        }
    }

    // ── Настройки LLM ────────────────────────────────────────────────────────

    public int SelectedProviderIndex
    {
        get => (int)_selectedProvider;
        set
        {
            if ((int)_selectedProvider == value) return;

            var oldDefault = LocalLLMService.DefaultEndpoints[_selectedProvider].ToString();
            _selectedProvider = (LocalLlmProvider)value;

            // Auto-update URL if user hasn't overridden it from the previous default
            if (string.Equals(_llmBaseUrl, oldDefault, StringComparison.OrdinalIgnoreCase))
            {
                _llmBaseUrl = LocalLLMService.DefaultEndpoints[_selectedProvider].ToString();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LlmBaseUrl)));
            }

            LocalLlmModelName = _selectedProvider == LocalLlmProvider.Ollama ? "llama3:latest" : "local-model";
            RecreateLocalLLM();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedProviderIndex)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalLlmPanelTitle)));
        }
    }

    public string LocalLlmModelName
    {
        get => _localLlmModel;
        set
        {
            if (_localLlmModel == value) return;
            _localLlmModel = value;
            RecreateLocalLLM();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalLlmModelName)));
        }
    }

    public string LlmBaseUrl
    {
        get => _llmBaseUrl;
        set
        {
            if (_llmBaseUrl == value) return;
            _llmBaseUrl = value;
            RecreateLocalLLM();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LlmBaseUrl)));
        }
    }

    public string LlmSystemPrompt
    {
        get => _llmSystemPrompt;
        set
        {
            if (_llmSystemPrompt == value) return;
            _llmSystemPrompt = value;
            RecreateLocalLLM();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LlmSystemPrompt)));
        }
    }

    public string LocalLlmPanelTitle => _selectedProvider == LocalLlmProvider.Ollama
        ? "LOCAL LLM (Ollama)"
        : "LOCAL LLM (LM Studio)";

    // ── Настройки приложения ─────────────────────────────────────────────────

    public bool IsScreenCaptureHidden
    {
        get => _isScreenCaptureHidden;
        set => Set(ref _isScreenCaptureHidden, value);
    }

    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        set
        {
            Set(ref _isSettingsVisible, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SettingsToggleText)));
        }
    }

    public string SettingsToggleText => _isSettingsVisible ? "▾ Настройки" : "▸ Настройки";

    // ── Команды ──────────────────────────────────────────────────────────────

    public ICommand ToggleRecordingCommand { get; }
    public ICommand RetryLocalLLMCommand   { get; }
    public ICommand ToggleSettingsCommand  { get; }
    public ICommand BrowseModelPathCommand { get; }

    public void ToggleRecording()
    {
        if (_audioCapture.IsRecording)
            _audioCapture.StopRecording();
        else if (!IsProcessing)
            _audioCapture.StartRecording();
    }

    // ── Инфраструктура ───────────────────────────────────────────────────────

    private static void UI(Action action) =>
        Application.Current.Dispatcher.Invoke(action);

    public async ValueTask DisposeAsync()
    {
        _llmCts?.Cancel();
        _audioCapture.Dispose();
        await _stt.DisposeAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
