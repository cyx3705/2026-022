namespace WBall.Model;

/// <summary>CAD 式临时草图(V5Q3):绘制中不进 wireframes 表、默认不落盘。</summary>
public sealed class WireSketch
{
    public List<WirePoint> Points { get; } = new();
    public double CloseRadius { get; set; } = 12;

    public bool IsEmpty => Points.Count == 0;

    public void Clear() => Points.Clear();
}

/// <summary>已闭合并提交的线框(可进快照与投影表)。</summary>
public sealed class Wireframe
{
    public required string Id { get; set; }
    public List<WirePoint> Points { get; set; } = new();

    /// <summary>提交时即为闭合;保留字段便于扩展。</summary>
    public bool Closed { get; set; } = true;
}

public sealed class WirePoint
{
    public double X { get; set; }
    public double Y { get; set; }

    public WirePoint()
    {
    }

    public WirePoint(double x, double y)
    {
        X = x;
        Y = y;
    }
}
