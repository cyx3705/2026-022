using System.IO;
using System.Text.Json;
using AppShell.Core.Logging;

namespace WBall.Recording;

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
        SaveCore(Current);
    }

    public void Apply(RenderTimeConfig next)
    {
        Validate(next);
        var clone = Clone(next);
        SaveCore(clone);
        Current = clone;
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

    public static RenderTimeConfig Clone(RenderTimeConfig source) => source.Clone();

    public static void Validate(RenderTimeConfig config)
    {
        if (config.Width is < 320 or > 7680 || (config.Width & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(config.Width), "出片宽度须为 320..7680 内的偶数");
        if (config.Height is < 240 or > 4320 || (config.Height & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(config.Height), "出片高度须为 240..4320 内的偶数");
        if (config.Fps is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(config.Fps), "FPS 须为 1..120");
        if (config.QueueCapacity is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(config.QueueCapacity), "帧队列容量须为 1..8");
        if (config.SlowStartBalls is < 100 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(config.SlowStartBalls), "开始降速球数须为 100..100000");
        if (config.SlowFullBalls <= config.SlowStartBalls || config.SlowFullBalls > 500_000)
            throw new ArgumentOutOfRangeException(nameof(config.SlowFullBalls), "最低倍率球数须大于开始降速球数且不超过 500000");
        if (!double.IsFinite(config.MinSimulationScale) || config.MinSimulationScale is < 0.05 or > 1)
            throw new ArgumentOutOfRangeException(nameof(config.MinSimulationScale), "最低倍率须为 0.05..1");
        if (!double.IsFinite(config.ManualSimulationScale) || config.ManualSimulationScale is < 0.05 or > 4)
            throw new ArgumentOutOfRangeException(nameof(config.ManualSimulationScale), "手动倍率须为 0.05..4");
        if (!double.IsFinite(config.ScaleQuantization) || config.ScaleQuantization is < 0.01 or > 0.25)
            throw new ArgumentOutOfRangeException(nameof(config.ScaleQuantization), "倍率量化须为 0.01..0.25");
        if (config.HysteresisBalls is < 0 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(config.HysteresisBalls), "迟滞球数须为 0..10000");
    }

    private void SaveCore(RenderTimeConfig config)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
