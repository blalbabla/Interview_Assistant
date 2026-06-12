using System.IO;
using NAudio.Wave;

namespace InterviewAssistant.Services;

public class AudioCaptureService : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private WaveFileWriter? _writer;
    private string? _currentFilePath;
    private bool _disposed;

    public bool IsRecording { get; private set; }

    public event EventHandler? RecordingStarted;
    public event EventHandler<string>? RecordingStopped;

    public void StartRecording()
    {
        if (IsRecording) return;

        _currentFilePath = Path.Combine(Path.GetTempPath(), $"interview_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

        _capture = new WasapiLoopbackCapture();
        _writer = new WaveFileWriter(_currentFilePath, _capture.WaveFormat);

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnCaptureStopped;

        _capture.StartRecording();
        IsRecording = true;
        RecordingStarted?.Invoke(this, EventArgs.Empty);
    }

    public void StopRecording()
    {
        if (!IsRecording || _capture is null) return;
        _capture.StopRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _writer?.Write(e.Buffer, 0, e.BytesRecorded);
    }

    private void OnCaptureStopped(object? sender, StoppedEventArgs e)
    {
        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;

        _capture?.Dispose();
        _capture = null;

        IsRecording = false;

        if (_currentFilePath is not null)
            RecordingStopped?.Invoke(this, _currentFilePath);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsRecording) StopRecording();
        _writer?.Dispose();
        _capture?.Dispose();
    }
}
