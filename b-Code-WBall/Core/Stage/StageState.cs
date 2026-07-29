namespace WBall.Stage;

public enum StageMode
{
    Edit,
    Play,
    Record,
}

public enum StageOrientation
{
    Horizontal,
    Vertical,
}

/// <summary>合成舞台的运行时配置权威。</summary>
public sealed class StageState
{
    private double _split = 0.4;
    private int _logicalWidth = 1280;
    private int _logicalHeight = 720;

    public event Action? Changed;

    public StageMode Mode { get; private set; } = StageMode.Edit;
    public StageOrientation Orientation { get; private set; } = StageOrientation.Horizontal;
    public bool CompositeVisible { get; private set; } = true;
    public bool HudVisible { get; private set; } = true;
    public string Background { get; private set; } = "#0A0C10";
    public double Split => _split;
    public int LogicalWidth => _logicalWidth;
    public int LogicalHeight => _logicalHeight;

    /// <summary>v2.11 AR-01/02:舞台内容区长宽比 = 逻辑分辨率宽高比(出片与预览一致)。</summary>
    public double AspectRatio => (double)_logicalWidth / Math.Max(1, _logicalHeight);

    public void SetMode(StageMode mode)
    {
        if (Mode == mode)
            return;
        Mode = mode;
        Changed?.Invoke();
    }

    public void SetCompositeVisible(bool visible)
    {
        if (CompositeVisible == visible)
            return;
        CompositeVisible = visible;
        Changed?.Invoke();
    }

    public void Configure(
        double? split = null,
        StageOrientation? orientation = null,
        bool? hudVisible = null,
        string? background = null,
        int? logicalWidth = null,
        int? logicalHeight = null)
    {
        if (split is not null)
            _split = Math.Clamp(split.Value, 0.2, 0.8);
        if (orientation is not null)
            Orientation = orientation.Value;
        if (hudVisible is not null)
            HudVisible = hudVisible.Value;
        if (!string.IsNullOrWhiteSpace(background))
            Background = NormalizeColor(background);
        if (logicalWidth is not null)
            _logicalWidth = Math.Clamp(logicalWidth.Value, 640, 7680);
        if (logicalHeight is not null)
            _logicalHeight = Math.Clamp(logicalHeight.Value, 360, 4320);
        Changed?.Invoke();
    }

    public override string ToString() =>
        $"mode={Mode.ToString().ToLowerInvariant()} composite={CompositeVisible.ToString().ToLowerInvariant()} " +
        $"orientation={Orientation.ToString().ToLowerInvariant()} split={Split:0.##} " +
        $"hud={HudVisible.ToString().ToLowerInvariant()} logical={LogicalWidth}x{LogicalHeight} bg={Background}";

    private static string NormalizeColor(string color)
    {
        var value = color.Trim();
        if (!value.StartsWith('#'))
            value = "#" + value;
        if (value.Length is not (7 or 9)
            || !value[1..].All(Uri.IsHexDigit))
        {
            throw new ArgumentException("背景色须为 #RRGGBB 或 #AARRGGBB", nameof(color));
        }
        return value.ToUpperInvariant();
    }
}
