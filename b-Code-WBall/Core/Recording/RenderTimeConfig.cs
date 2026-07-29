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

    public RenderTimeConfig Clone() => new()
    {
        Width = Width,
        Height = Height,
        Fps = Fps,
        QueueCapacity = QueueCapacity,
        PreferMp4 = PreferMp4,
        KeepPng = KeepPng,
        PreviewAutoSlow = PreviewAutoSlow,
        RenderAutoSlow = RenderAutoSlow,
        SlowStartBalls = SlowStartBalls,
        SlowFullBalls = SlowFullBalls,
        MinSimulationScale = MinSimulationScale,
        ManualSimulationScale = ManualSimulationScale,
        ScaleQuantization = ScaleQuantization,
        HysteresisBalls = HysteresisBalls,
        MaxOutputSeconds = MaxOutputSeconds,
    };
}
