using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WBall.Recording;

/// <summary>V3.6 固定 FFmpeg 子进程；只接受 BGRA 帧并产出 H.264 MP4。</summary>
internal sealed class FfmpegEncoder : IDisposable
{
    private const int ErrorTailLimit = 32 * 1024;
    private readonly Process _process;
    private readonly Task _stderrDrain;
    private readonly StringBuilder _stderrTail = new();
    private readonly object _errorSync = new();
    private bool _completed;

    private FfmpegEncoder(Process process)
    {
        _process = process;
        _stderrDrain = DrainErrorsAsync(process.StandardError);
    }

    public static string ResolveBundledPath() =>
        Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");

    public static FfmpegEncoder Open(string executable, string outputPath, int fps, int width, int height)
    {
        if (!File.Exists(executable))
            throw new FileNotFoundException("随应用发布的 FFmpeg 不存在", executable);
        var start = CreateStartInfo(executable);
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "warning",
            "-f", "rawvideo", "-pixel_format", "bgra",
            "-video_size", $"{width}x{height}", "-framerate", fps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i", "pipe:0", "-an", "-c:v", "libx264", "-preset", "veryfast",
            "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-y", outputPath,
        })
            start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg 启动失败");
        return new FfmpegEncoder(process);
    }

    public void WriteFrame(byte[] bgra)
    {
        if (_completed)
            throw new InvalidOperationException("FFmpeg 输入已经关闭");
        _process.StandardInput.BaseStream.Write(bgra, 0, bgra.Length);
    }

    public void CompleteAndValidate(string outputPath)
    {
        if (_completed)
            return;
        _process.StandardInput.Close();
        if (!_process.WaitForExit((int)TimeSpan.FromMinutes(10).TotalMilliseconds))
        {
            Kill();
            throw new TimeoutException("FFmpeg 封装超时");
        }
        _stderrDrain.GetAwaiter().GetResult();
        _completed = true;
        if (_process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg 编码失败(exit={_process.ExitCode}): {ErrorTail()}");
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
            throw new InvalidDataException("FFmpeg 未生成有效 MP4 文件");
        Probe(executable: _process.StartInfo.FileName, outputPath);
    }

    public static (string Version, string Sha256) Identify(string executable)
    {
        if (!File.Exists(executable))
            throw new FileNotFoundException("随应用发布的 FFmpeg 不存在", executable);
        var start = CreateStartInfo(executable);
        start.RedirectStandardOutput = true;
        start.ArgumentList.Add("-version");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg 版本探测启动失败");
        var firstLine = process.StandardOutput.ReadLine() ?? "unknown";
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg 版本探测失败: {error}");
        using var stream = File.OpenRead(executable);
        return (firstLine.Trim(), Convert.ToHexString(SHA256.HashData(stream)));
    }

    public void Dispose()
    {
        if (!_completed)
            Kill();
        try { _stderrDrain.GetAwaiter().GetResult(); }
        catch { }
        _process.Dispose();
    }

    private static void Probe(string executable, string outputPath)
    {
        var start = CreateStartInfo(executable);
        foreach (var argument in new[]
        {
            "-v", "error", "-i", outputPath, "-map", "0:v:0", "-frames:v", "1", "-f", "null", "-",
        })
            start.ArgumentList.Add(argument);
        using var probe = Process.Start(start) ?? throw new InvalidOperationException("MP4 容器探测启动失败");
        var error = probe.StandardError.ReadToEnd();
        if (!probe.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds))
        {
            try { probe.Kill(entireProcessTree: true); }
            catch { }
            throw new TimeoutException("MP4 容器探测超时");
        }
        if (probe.ExitCode != 0)
            throw new InvalidDataException($"MP4 容器不可读取: {error}");
    }

    private static ProcessStartInfo CreateStartInfo(string executable) => new()
    {
        FileName = executable,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardError = true,
    };

    private async Task DrainErrorsAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            lock (_errorSync)
            {
                _stderrTail.AppendLine(line);
                if (_stderrTail.Length > ErrorTailLimit)
                    _stderrTail.Remove(0, _stderrTail.Length - ErrorTailLimit);
            }
        }
    }

    private string ErrorTail()
    {
        lock (_errorSync)
            return _stderrTail.ToString().Trim();
    }

    private void Kill()
    {
        try { _process.StandardInput.Close(); }
        catch { }
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch { }
        try { _process.WaitForExit(5_000); }
        catch { }
    }
}
