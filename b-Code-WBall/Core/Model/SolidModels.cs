using WBall.Geometry;

namespace WBall.Model;

/// <summary>
/// 异形实体(v1.5.1 V51Q1):实体化产物,用户感知为一块障碍。
/// 权威几何为闭合简单多边形;三角网格为内部表示(填充绘制 + 碰撞),存盘可省略、加载重算。
/// </summary>
public sealed class MeshSolid
{
    /// <summary>与手动方块默认色一致(v1.5.2 CL-01)。</summary>
    public const string DefaultColor = "#64748B";

    public required string Id { get; set; }

    /// <summary>填充色(HEX);默认与 block 同色。</summary>
    public string Color { get; set; } = DefaultColor;

    /// <summary>闭合多边形顶点(世界坐标,已合并近点)。</summary>
    public List<WirePoint> Points { get; set; } = new();

    /// <summary>三角形顶点索引(指向 Points);共边拼接表示同一实体。</summary>
    public List<(int A, int B, int C)> Triangles { get; set; } = new();

    /// <summary>轴对齐包围盒(仅用于越界裁剪与碰撞粗筛,禁止当碰撞体,PH-03)。</summary>
    public void GetAabb(out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = double.PositiveInfinity;
        minY = double.PositiveInfinity;
        maxX = double.NegativeInfinity;
        maxY = double.NegativeInfinity;
        foreach (var p in Points)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        if (Points.Count == 0)
        {
            minX = minY = maxX = maxY = 0;
        }
    }

    /// <summary>点是否在异形内部(任一三角形内)。</summary>
    public bool ContainsPoint(double x, double y)
    {
        foreach (var (a, b, c) in Triangles)
        {
            if (PolygonMath.PointInTriangle(
                    (x, y),
                    (Points[a].X, Points[a].Y),
                    (Points[b].X, Points[b].Y),
                    (Points[c].X, Points[c].Y)))
                return true;
        }

        return false;
    }

    /// <summary>整块平移(ED-01);三角索引不变。</summary>
    public void MoveBy(double dx, double dy)
    {
        foreach (var p in Points)
        {
            p.X += dx;
            p.Y += dy;
        }
    }
}
