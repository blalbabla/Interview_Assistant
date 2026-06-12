using System.IO;
using System.Windows;

namespace InterviewAssistant;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        InjectCudaPath();
        base.OnStartup(e);
    }

    // ggml-cuda-whisper.dll depends on cublas64_13.dll which lives in the CUDA toolkit
    // bin directory — that directory is often absent from PATH even when the toolkit is
    // installed. We inject it here, before Whisper.net touches any native library.
    private static void InjectCudaPath()
    {
        const string cudaRoot = @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA";
        if (!Directory.Exists(cudaRoot)) return;

        // Pick the highest installed CUDA version (v13.x preferred over v12.x etc.)
        var cudaBin = Directory.GetDirectories(cudaRoot)
            .OrderByDescending(d => d)
            .SelectMany(d => new[]
            {
                Path.Combine(d, "bin", "x64"), // CUDA 13+ layout
                Path.Combine(d, "bin"),         // older layout
            })
            .FirstOrDefault(Directory.Exists);

        if (cudaBin is null) return;

        var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!current.Contains(cudaBin, StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PATH", cudaBin + ";" + current);
    }
}
