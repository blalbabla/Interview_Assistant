using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;

namespace InterviewAssistant.Services;

public class SpeechToTextService : IAsyncDisposable
{
    private readonly string _modelPath;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _processLock = new(1, 1);

    // Определяем при старте: nvcuda.dll в System32 = NVIDIA CUDA доступна
    public string ComputeDevice { get; } = DetectComputeDevice();

    private readonly string _language;

    private static string DetectComputeDevice()
    {
        var cudaDriver = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "nvcuda.dll");
        return File.Exists(cudaDriver) ? "CUDA" : "CPU";
    }

    public SpeechToTextService(string modelPath, string language = "auto")
    {
        _modelPath = modelPath;
        _language  = language;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_processor is not null) return;
        await _initLock.WaitAsync();
        try
        {
            if (_processor is not null) return;
            _factory = WhisperFactory.FromPath(_modelPath);
            _processor = _factory.CreateBuilder()
                .WithLanguage(_language)
                .WithGreedySamplingStrategy()
                .Build();
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<string> TranscribeAsync(string wavPath, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();

        // MediaFoundationResampler ломается в async-контексте (COM threading).
        // WdlResamplingSampleProvider — чисто управляемый, без COM.
        using var audioStream = ResampleTo16kHz(wavPath);

        // Нативный whisper.cpp должен работать на thread pool thread, без SynchronizationContext.
        // Semaphore гарантирует, что два вызова не попадут в нативный код одновременно.
        await _processLock.WaitAsync(ct);
        try
        {
            return await Task.Run(async () =>
            {
                var parts = new List<string>();
                await foreach (var segment in _processor!.ProcessAsync(audioStream, ct))
                {
                    var text = segment.Text.Trim();
                    if (!string.IsNullOrEmpty(text))
                        parts.Add(text);
                }
                return string.Join(" ", parts);
            }, ct);
        }
        finally
        {
            _processLock.Release();
        }
    }

    // Управляемый ресэмплинг без COM/MF: безопасно из любого потока
    private static MemoryStream ResampleTo16kHz(string wavPath)
    {
        using var reader = new AudioFileReader(wavPath);

        // 1. Ресэмплинг до 16 кГц
        ISampleProvider resampled = new WdlResamplingSampleProvider(reader, 16000);

        // 2. Стерео → моно (если нужно)
        if (resampled.WaveFormat.Channels > 1)
            resampled = new StereoToMonoSampleProvider(resampled);

        // 3. Float32 → PCM 16-bit
        var pcm16 = new SampleToWaveProvider16(resampled);

        var ms = new MemoryStream();
        WaveFileWriter.WriteWavFileToStream(ms, pcm16);
        ms.Position = 0;
        return ms;
    }

    public ValueTask DisposeAsync()
    {
        _processor?.Dispose();
        _factory?.Dispose();
        _initLock.Dispose();
        _processLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
