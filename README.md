# Interview Assistant — Setup & Dependencies

Приложение помогает на технических собеседованиях: захватывает системный звук, транскрибирует вопросы через Whisper локально, отправляет в локальный LLM и стримит ответ. Окно скрыто от захвата экрана.

---

## Системные требования

### Обязательно

| Компонент | Версия | Где взять |
|-----------|--------|-----------|
| Windows | 10/11 x64 | — |
| .NET Runtime | 8.0 | https://dotnet.microsoft.com/download/dotnet/8.0 |
| Whisper модель | ggml-medium или другая | см. ниже |

### Для GPU-ускорения (рекомендуется)

| Компонент | Версия | Где взять |
|-----------|--------|-----------|
| NVIDIA драйвер | 610+ (для RTX 5000) / 560+ (RTX 30/40xx) | https://www.nvidia.com/drivers |
| CUDA Toolkit | 13.x | https://developer.nvidia.com/cuda-downloads |

> **Почему нужен CUDA Toolkit, а не только драйвер:**  
> `ggml-cuda-whisper.dll` (бэкенд Whisper.net) зависит от `cublas64_13.dll`,  
> которая входит в CUDA Toolkit, но не в драйвер NVIDIA.  
> Приложение при старте автоматически добавляет `CUDA\v13.x\bin\x64` в PATH процесса,  
> но папка должна существовать.
>
> Без CUDA Toolkit транскрипция работает на CPU (~30-40 с для 20-30 с аудио вместо 2-5 с).

### LLM сервер (один из двух)

| Вариант | Порт | Где взять |
|---------|------|-----------|
| Ollama | 11434 | https://ollama.com |
| LM Studio | 1234 | https://lmstudio.ai |

---

## Скачать Whisper-модель

Модели хранятся локально в формате GGML. Путь прописан в коде:

```
ViewModels/MainViewModel.cs → const string WhisperModelPath = @"D:\ggml-medium.bin";
```

Изменить путь можно там же. Скачать модели:

```
# через Hugging Face CLI или напрямую:
https://huggingface.co/ggerganov/whisper.cpp/tree/main
```

Рекомендуемые варианты:

| Модель | Размер | Точность | Скорость (GPU) |
|--------|--------|----------|----------------|
| ggml-base.bin | 142 MB | низкая | ~0.5 с |
| ggml-small.bin | 466 MB | средняя | ~1 с |
| ggml-medium.bin | 1.5 GB | высокая | ~2-5 с |
| ggml-large-v3.bin | 3.1 GB | максимальная | ~8-15 с |

---

## NuGet-зависимости

```xml
<PackageReference Include="NAudio"                   Version="2.3.0" />
<PackageReference Include="NHotkey.Wpf"              Version="4.0.0" />
<PackageReference Include="OpenAI"                   Version="2.11.0" />
<PackageReference Include="Whisper.net"              Version="1.9.1" />
<PackageReference Include="Whisper.net.Runtime"      Version="1.9.1" />
<PackageReference Include="Whisper.net.Runtime.Cuda" Version="1.9.1" />
```

> `OllamaSharp 5.4.25` присутствует в csproj, но не используется в коде — можно удалить.

---

## Конфигурация в коде

Все настройки, которые нужно менять вручную в коде:

| Файл | Строка | Описание |
|------|--------|----------|
| `ViewModels/MainViewModel.cs` | `WhisperModelPath` | Путь к GGML-модели |
| `Services/SpeechToTextService.cs` | `.WithLanguage("auto")` | Язык Whisper: `"ru"`, `"en"`, `"ua"`, `"auto"` |
| `Services/LocalLLMService.cs` | `SystemPrompt` | Системный промпт для LLM |
| `Services/LocalLLMService.cs` | `Endpoints` | URL Ollama/LM Studio (по умолчанию localhost) |

---

## Горячие клавиши

| Клавиша | Действие |
|---------|----------|
| `Ctrl+Space` | Начать / остановить запись |

---

## Архитектура

```
MainWindow.xaml / .cs
  └── MainViewModel (INotifyPropertyChanged, IAsyncDisposable)
        ├── AudioCaptureService   — WASAPI Loopback (NAudio), запись системного звука
        ├── SpeechToTextService   — Whisper.net, транскрипция WAV → текст
        └── LocalLLMService       — OpenAI SDK → Ollama / LM Studio (OpenAI-совместимый API)
```

**Поток данных:**
1. `Ctrl+Space` → `AudioCaptureService.StartRecording()` → пишет PCM в temp WAV
2. `Ctrl+Space` → `StopRecording()` → `RecordingStopped(wavPath)`
3. `SpeechToTextService.TranscribeAsync(wavPath)` — ресэмплинг до 16 кГц (WdlResamplingSampleProvider), Whisper GGML
4. `LocalLLMService.GetStreamingResponseAsync(question)` — стриминг токенов через OpenAI SDK
5. `SetWindowDisplayAffinity(WDA_EXCLUDE_FROM_CAPTURE)` — окно невидимо для скриншотов/захвата экрана

---

## Диагностика

**Транскрипция медленная (~30 с вместо 2-5 с)**
- Убедитесь что CUDA Toolkit 13.x установлен
- В приложении проверьте бейдж в панели вопроса: `CUDA` (зелёный) или `CPU` (жёлтый)
- Бейдж определяет наличие `nvcuda.dll` в System32 — это только признак наличия видеокарты.  
  Фактическое использование GPU зависит от установки CUDA Toolkit

**LLM не отвечает**
- Ollama: `ollama list` — убедитесь что модель загружена, `ollama serve` запущен
- LM Studio: в интерфейсе должна быть запущена модель (кнопка Start Server)

**Приложение видно в захвате экрана**
- Включите "Скрывать от захвата экрана" в настройках
- Работает только на Windows 10 2004+ (build 19041)
