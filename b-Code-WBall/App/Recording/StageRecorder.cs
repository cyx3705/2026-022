using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AppShell.Core.Logging;
using WBall.Battle;
using WBall.Stage;

namespace WBall.Recording;

public sealed class RecordConfig
{
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 30;
    public bool PreferMp4 { get; set; } = true;
    public bool KeepPng { get; set; } = true;
    /// <summary>为 true 时优先使用 StageView 实际尺寸(普通窗口大小)。</summary>
    public bool UseStageViewSize { get; set; } = true;
}

/// <summary>离线逐帧录制:PNG 兜底 + 可选 MF H.264 MP4。</summary>
public sealed class StageRecorder
{
    private readonly StageView _stageView;
    private readonly StageState _stage;
    private readonly BattleDirector _director;
    private readonly string _recordsRoot;
    private readonly IShellLog _log;

    public StageRecorder(
        StageView stageView,
        StageState stage,
        BattleDirector director,
        string workspaceRoot,
        IShellLog log)
    {
        _stageView = stageView;
        _stage = stage;
        _director = director;
        _recordsRoot = Path.Combine(workspaceRoot, "records");
        _log = log;
        Directory.CreateDirectory(_recordsRoot);
        Config = new RecordConfig();
    }

    public RecordConfig Config { get; }
    public bool IsRecording { get; private set; }
    public string? LastOutputDirectory { get; private set; }
    public string? LastMp4Path { get; private set; }
    public string StatusText { get; private set; } = "idle";

    public string Record(double seconds, int? seed = null, string? name = null)
    {
        if (IsRecording)
            throw new InvalidOperationException("已在录制中");
        if (seconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(seconds));

        IsRecording = true;
        var previousMode = _stage.Mode;
        try
        {
            var fps = Math.Clamp(Config.Fps, 1, 120);
            var (width, height) = ResolveCaptureSize();
            var frames = Math.Max(1, (int)Math.Ceiling(seconds * fps));
            var stepsPerFrame = Math.Max(1, (int)Math.Round(60.0 / fps));
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var runSeed = seed ?? _director.Seed;
            var folderName = $"{Sanitize(name ?? "battle")}_{runSeed}_{stamp}";
            var dir = Path.Combine(_recordsRoot, folderName);
            Directory.CreateDirectory(dir);
            LastOutputDirectory = dir;
            LastMp4Path = null;

            _director.Reset();
            _director.Start(runSeed, countdownSeconds: 0);
            _stage.SetMode(StageMode.Record);
            _stage.Configure(logicalWidth: width, logicalHeight: height);
            Layout(width, height);

            StatusText = $"recording frames={frames} {width}x{height}@{fps}";
            _log.Info("record", StatusText);

            var pngPaths = new List<string>(frames);
            var bgraFrames = Config.PreferMp4 ? new List<byte[]>(frames) : null;

            for (var i = 0; i < frames; i++)
            {
                for (var s = 0; s < stepsPerFrame; s++)
                    _director.AdvanceFixedStep();

                var path = Path.Combine(dir, $"frame_{i:D6}.png");
                var pixels = Capture(width, height, Config.KeepPng || !Config.PreferMp4 ? path : null);
                if (Config.KeepPng || !Config.PreferMp4)
                    pngPaths.Add(path);
                bgraFrames?.Add(pixels);

                if (i % 30 == 0)
                    StatusText = $"recording {i + 1}/{frames}";
            }

            if (Config.PreferMp4 && bgraFrames is { Count: > 0 })
            {
                var mp4 = Path.Combine(dir, folderName + ".mp4");
                try
                {
                    MediaFoundationEncoder.EncodeBgraFrames(bgraFrames, mp4, fps, width, height, _log);
                    LastMp4Path = mp4;
                    StatusText = $"done mp4={mp4}";
                    if (!Config.KeepPng)
                    {
                        foreach (var p in Directory.EnumerateFiles(dir, "frame_*.png"))
                            File.Delete(p);
                    }
                }
                catch (Exception ex)
                {
                    // 确保 PNG 存在作为兜底
                    if (pngPaths.Count == 0)
                    {
                        for (var i = 0; i < bgraFrames.Count; i++)
                        {
                            var path = Path.Combine(dir, $"frame_{i:D6}.png");
                            SavePng(bgraFrames[i], width, height, path);
                            pngPaths.Add(path);
                        }
                    }
                    _log.Warn("record", $"MP4 编码失败,保留 PNG: {ex.Message}");
                    StatusText = $"done png={dir} mp4_failed={ex.Message}";
                }
            }
            else
            {
                StatusText = $"done png={dir} frames={pngPaths.Count}";
            }

            return StatusText;
        }
        finally
        {
            IsRecording = false;
            _stage.SetMode(previousMode is StageMode.Record ? StageMode.Edit : previousMode);
        }
    }

    private byte[] Capture(int width, int height, string? pngPath)
    {
        Layout(width, height);
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(_stageView);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        rtb.CopyPixels(pixels, stride, 0);
        if (pngPath != null)
            SavePng(pixels, width, height, pngPath);
        return pixels;
    }

    private static void SavePng(byte[] bgra, int width, int height, string path)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, bgra, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private (int Width, int Height) ResolveCaptureSize()
    {
        // 计划定稿:优先 StageView 实际像素,宽钳制 640–1920;无效则回退 config/1280×720
        if (Config.UseStageViewSize)
        {
            var aw = _stageView.ActualWidth;
            var ah = _stageView.ActualHeight;
            if (aw >= 320 && ah >= 240)
            {
                return (
                    Math.Clamp((int)Math.Round(aw), 640, 1920),
                    Math.Clamp((int)Math.Round(ah), 360, 1080));
            }
        }

        return (
            Math.Clamp(Config.Width, 640, 1920),
            Math.Clamp(Config.Height, 360, 1080));
    }

    private void Layout(int width, int height)
    {
        _stageView.Measure(new Size(width, height));
        _stageView.Arrange(new Rect(0, 0, width, height));
        _stageView.UpdateLayout();
    }

    private static string Sanitize(string name)
    {
        var trimmed = name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            trimmed = trimmed.Replace(c, '_');
        return string.IsNullOrWhiteSpace(trimmed) ? "battle" : trimmed;
    }
}
