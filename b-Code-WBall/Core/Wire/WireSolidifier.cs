using WBall.Geometry;
using WBall.Model;

namespace WBall.Wire;

/// <summary>
/// 封闭线框 → 单个异形实体(v1.5.1 V51Q1):
/// merge → 校验(自交/点数/面积)→ 耳切三角化 → 1 个 MeshSolid。
/// 禁止对三角形做 OBB/AABB 拟合或产出多个 block(MS-01/MS-03)。
/// </summary>
public static class WireSolidifier
{
    /// <summary>单实体三角形软上限(PH-04),超限拒绝实体化。</summary>
    public const int MaxTriangles = 256;

    public static MeshSolid Solidify(Wireframe wire, Func<string> nextId)
    {
        var raw = wire.Points.Select(p => (p.X, p.Y)).ToList();
        var pts = PolygonMath.MergeClosePoints(raw);
        if (pts.Count < 3)
            throw new InvalidOperationException("线框点数不足(<3),无法实体化");

        if (PolygonMath.IsSelfIntersecting(pts))
            throw new InvalidOperationException("线框自交,禁止实体化");

        var area = PolygonMath.AbsArea(pts);
        if (area < PolygonMath.MinArea)
            throw new InvalidOperationException($"线框面积过小({area:0.#}<{PolygonMath.MinArea}),无法实体化");

        var tris = PolygonMath.TriangulateIndices(pts);
        if (tris.Count == 0)
            throw new InvalidOperationException("三角化失败(可能自交或退化),无法实体化");
        if (tris.Count > MaxTriangles)
            throw new InvalidOperationException($"三角形数量 {tris.Count} 超过上限 {MaxTriangles},请简化轮廓");

        return new MeshSolid
        {
            Id = nextId(),
            Color = MeshSolid.DefaultColor,
            Points = pts.Select(p => new WirePoint(p.X, p.Y)).ToList(),
            Triangles = tris,
        };
    }
}
