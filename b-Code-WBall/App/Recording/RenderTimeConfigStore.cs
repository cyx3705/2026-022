using System.IO;
using System.Text.Json;
using AppShell.Core.Logging;

namespace WBall.Recording;

public sealed class RenderTimeConfig
{
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 30;
    public int QueueCapacity { get; set; } = 4;
    public bool PreferMp4 { get; set; } = true;
    public bool KeepPng { get; set; } = true;
    public bool PreviewAutoSlow { get; set; } = true;
    public bool RenderAutoSlow { get; set; } = true;
    public int SlowStartBalls { get; set; } = 2_000;
    public int SlowFullBalls { get; set; } = 10_000;
    public double MinSimulationScale { get; set; } = 0.25;
    public double ManualSimulationScale { get; set; } = 1;
    public double ScaleQuantization { get; set; } = 0.05;
    public int HysteresisBalls { get; set; } = 200;
    public int MaxOutputSeconds { get; set; } = 600;
}

public sealed class RenderTimeConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly IShellLog _log;

    public RenderTimeConfigStore(string dataRoot, IShellLog log)
    {
        _path = System.IO.Path.Combine(dataRoot, "render_time.json");
        _log = log;
        Reload();
    }

    public RenderTimeConfig Current { get; private set; } = new();
    public string Path => _path;

    public void Save()
    {
        Validate(Current);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(Current, JsonOptions));
    }

    public void Reload()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        try
        {
            Current = File.Exists(_path)
                ? JsonSerializer.Deserialize<RenderTimeConfig>(File.ReadAllText(_path), JsonOptions) ?? new()
                : new RenderTimeConfig();
            Validate(Current);
            if (!File.Exists(_path))
                Save();
        }
        catch (Exception ex)
        {
            Current = new RenderTimeConfig();
            _log.Warn("render", $"出片时间配置无效,使用出厂默认: {ex.Message}");
        }
    }

    public static RenderTimeConfig Clone(RenderTimeConfig source) => new()
    {
        Width = source.Width,
        Height = source.Height,
        Fps = source.Fps,
        QueueCapacity = source.QueueCapacity,
        PreferMp4 = source.PreferMp4,
        KeepPng = source.KeepPng,
        PreviewAutoSlow = source.PreviewAutoSlow,
        RenderAutoSlow = source.RenderAutoSlow,
        SlowStartBalls = source.SlowStartBalls,
        SlowFullBalls = source.SlowFullBalls,
        MinSimulationScale = source.MinSimulationScale,
        ManualSimulationScale = source.ManualSimulationScale,
        ScaleQuantization = source.ScaleQuantization,
        HysteresisBalls = source.HysteresisBalls,
        MaxOutputSeconds = source.MaxOutputSeconds,
    };

    public static void Validate(RenderTimeConfig config)
    {
        config.Width = Math.Clamp(config.Width, 320, 7680);
        config.Height = Math.Clamp(config.Height, 240, 4320);
        config.Fps = Math.Clamp(config.Fps, 1, 120);
        config.QueueCapacity = Math.Clamp(config.QueueCapacity, 1, 8);
        config.SlowStartBalls = Math.Clamp(config.SlowStartBalls, 100, 100_000);
        config.SlowFullBalls = Math.Clamp(config.SlowFullBalls, config.SlowStartBalls + 1, 500_000);
        config.MinSimulationScale = Math.Clamp(config.MinSimulationScale, 0.05, 1);
        config.ManualSimulationScale = Math.Clamp(config.ManualSimulationScale, 0.05, 4);
        config.ScaleQuantization = Math.Clamp(config.ScaleQuantization, 0.01, 0.25);
        config.HysteresisBalls = Math.Clamp(config.HysteresisBalls, 0, 10_000);
        config.MaxOutputSeconds = Math.Clamp(config.MaxOutputSeconds, 1, 86_400);
    }
}
