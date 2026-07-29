namespace WBall.Geometry;

/// <summary>多边形几何:自交、面积、耳切三角化(v1.5.1 单异形实体基础)。</summary>
public static class PolygonMath
{
    public const double MinArea = 64.0;
    public const double MinEdge = 1.0;

    public static List<(double X, double Y)> MergeClosePoints(
        IReadOnlyList<(double X, double Y)> pts,
        double eps = 1.5)
    {
        if (pts.Count == 0)
            return [];
        var result = new List<(double X, double Y)> { pts[0] };
        for (var i = 1; i < pts.Count; i++)
        {
            var (x, y) = pts[i];
            var (lx, ly) = result[^1];
            if (Dist(x, y, lx, ly) >= eps)
                result.Add((x, y));
        }

        if (result.Count >= 2)
        {
            var (fx, fy) = result[0];
            var (lx, ly) = result[^1];
            if (Dist(fx, fy, lx, ly) < eps)
                result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    public static double SignedArea(IReadOnlyList<(double X, double Y)> pts)
    {
        if (pts.Count < 3)
            return 0;
        double a = 0;
        for (var i = 0; i < pts.Count; i++)
        {
            var (x1, y1) = pts[i];
            var (x2, y2) = pts[(i + 1) % pts.Count];
            a += x1 * y2 - x2 * y1;
        }

        return a * 0.5;
    }

    public static double AbsArea(IReadOnlyList<(double X, double Y)> pts)
        => Math.Abs(SignedArea(pts));

    /// <summary>非相邻边是否相交(闭合多边形)。</summary>
    public static bool IsSelfIntersecting(IReadOnlyList<(double X, double Y)> pts)
    {
        var n = pts.Count;
        if (n < 4)
            return false;

        for (var i = 0; i < n; i++)
        {
            var a1 = pts[i];
            var a2 = pts[(i + 1) % n];
            for (var j = i + 1; j < n; j++)
            {
                // 跳过相邻边与同一边
                if (Math.Abs(i - j) <= 1 || (i == 0 && j == n - 1) || (j == 0 && i == n - 1))
                    continue;
                // 共享顶点的边对已由上面相邻跳过;再跳过首尾共点
                if ((i + 1) % n == j || (j + 1) % n == i)
                    continue;

                var b1 = pts[j];
                var b2 = pts[(j + 1) % n];
                if (SegmentsIntersect(a1.X, a1.Y, a2.X, a2.Y, b1.X, b1.Y, b2.X, b2.Y))
                    return true;
            }
        }

        return false;
    }

    public static bool SegmentsIntersect(
        double ax, double ay, double bx, double by,
        double cx, double cy, double dx, double dy)
    {
        var d1 = Cross(bx - ax, by - ay, cx - ax, cy - ay);
        var d2 = Cross(bx - ax, by - ay, dx - ax, dy - ay);
        var d3 = Cross(dx - cx, dy - cy, ax - cx, ay - cy);
        var d4 = Cross(dx - cx, dy - cy, bx - cx, by - cy);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
            && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            return true;
        return false;
    }

    /// <summary>
    /// 耳切三角化;返回顶点索引三元组(指向传入点列)。
    /// v1.5.1:调用方须已 MergeClosePoints;失败(退化/自交)返回空列表,不产出半截结果。
    /// </summary>
    public static List<(int A, int B, int C)> TriangulateIndices(
        IReadOnlyList<(double X, double Y)> pts)
    {
        if (pts.Count < 3)
            return [];

        var idx = Enumerable.Range(0, pts.Count).ToList();
        // 保证遍历顺序为 CCW;索引仍指向原点列
        if (SignedArea(pts) < 0)
            idx.Reverse();

        var tris = new List<(int A, int B, int C)>();
        var guard = 0;
        while (idx.Count > 3 && guard++ < 10_000)
        {
            var earFound = false;
            for (var i = 0; i < idx.Count; i++)
            {
                var i0 = idx[(i + idx.Count - 1) % idx.Count];
                var i1 = idx[i];
                var i2 = idx[(i + 1) % idx.Count];
                var a = pts[i0];
                var b = pts[i1];
                var c = pts[i2];
                if (!IsConvexTip(a, b, c))
                    continue;
                if (AnyPointInTriangle(pts, idx, i0, i1, i2, a, b, c))
                    continue;

                tris.Add((i0, i1, i2));
                idx.RemoveAt(i);
                earFound = true;
                break;
            }

            if (!earFound)
                return []; // 退化/自交:原子失败
        }

        if (idx.Count == 3)
            tris.Add((idx[0], idx[1], idx[2]));

        return tris;
    }

    private static bool IsConvexTip(
        (double X, double Y) a,
        (double X, double Y) b,
        (double X, double Y) c)
        => Cross(b.X - a.X, b.Y - a.Y, c.X - b.X, c.Y - b.Y) > 1e-9;

    private static bool AnyPointInTriangle(
        IReadOnlyList<(double X, double Y)> pts,
        List<int> idx,
        int i0, int i1, int i2,
        (double X, double Y) a,
        (double X, double Y) b,
        (double X, double Y) c)
    {
        foreach (var id in idx)
        {
            if (id == i0 || id == i1 || id == i2)
                continue;
            if (PointInTriangle(pts[id], a, b, c))
                return true;
        }

        return false;
    }

    public static bool PointInTriangle(
        (double X, double Y) p,
        (double X, double Y) a,
        (double X, double Y) b,
        (double X, double Y) c)
    {
        var d1 = Cross(b.X - a.X, b.Y - a.Y, p.X - a.X, p.Y - a.Y);
        var d2 = Cross(c.X - b.X, c.Y - b.Y, p.X - b.X, p.Y - b.Y);
        var d3 = Cross(a.X - c.X, a.Y - c.Y, p.X - c.X, p.Y - c.Y);
        var hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        var hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);
    }

    private static double Cross(double ax, double ay, double bx, double by)
        => ax * by - ay * bx;

    private static double Dist(double x1, double y1, double x2, double y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
