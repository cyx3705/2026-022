using WBall.Game;
using WBall.Model;

namespace WBall.Sim;

/// <summary>自研最小 2D 物理:重力矢量合成、球-方块、球-球、生成销毁闭环。</summary>
public static class PhysicsEngine
{
    public static void Step(SceneWorld world, double dt, Action<string>? warn = null)
    {
        if (dt <= 0 || dt > 0.1)
            dt = 1.0 / 60.0;

        var index = PhysicsWorldIndex.For(world);

        // 积分 + 碰撞。静态对象按场景结构缓存，保持 Objects 原始顺序。
        var ballCount = world.Balls.Count;
        for (var bi = 0; bi < ballCount; bi++)
        {
            var ball = world.Balls[bi];
            var (gx, gy) = SampleGravity(world, index.QueryArrows(ball.X, ball.Y), ball.X, ball.Y);
            ball.Vx += gx * dt;
            ball.Vy += gy * dt;
            ball.X += ball.Vx * dt;
            ball.Y += ball.Vy * dt;

            ResolveWorldBounds(world, ball);
            foreach (var block in index.QueryBlocks(ball.X, ball.Y, ball.Size))
                ResolveBallBlock(ball, block);
            foreach (var solid in world.Solids)
                ResolveBallSolid(ball, solid);

            if (world.TrailEnabled)
                ball.PushTrail(ball.X, ball.Y, world.TrailLength);

            if (ball.TeleportFlashT > 0)
                ball.TeleportFlashT = Math.Max(0, ball.TeleportFlashT - dt);
        }

        if (world.BallCollisionEnabled)
            ResolveBallBall(world);

        // 销毁器命中:Xn 倍率门=穿透(v2.7);RUN/结算槽/其他=同实例传送(不 new / 不换 id)
        Func<string, bool>? isSettlement = world.Settlements == null
            ? null
            : world.Settlements.IsKnown;
        for (var i = world.Balls.Count - 1; i >= 0; i--)
        {
            var ball = world.Balls[i];
            SceneObject? sink = null;
            foreach (var candidate in index.QueryDespawner(ball.X, ball.Y))
            {
                if (!Contains(candidate, ball.X, ball.Y))
                    continue;
                sink = candidate;
                break;
            }
            if (sink == null)
                continue;

            var fn = DespawnFunctions.Parse(sink.Name, isSettlement);
            if (fn.Kind == DespawnFunctions.Kind.Multiply)
                PassThroughGate(world, ball, sink, fn, warn);
            else
                TeleportBall(world, ball, sink, warn);
        }
    }

    /// <summary>v2.7 GT-01:Xn 倍率门穿透 — 乘倍率后沿门体局部 +Y 推出下沿继续下落。</summary>
    private static void PassThroughGate(
        SceneWorld world,
        Ball ball,
        SceneObject gate,
        DespawnFunctions.ParseResult fn,
        Action<string>? warn)
    {
        var changed = DespawnFunctions.Apply(fn, world, ball, world.Defaults, warn);
        gate.WorldToLocal(ball.X, ball.Y, out var lx, out _);
        gate.LocalToWorld(lx, gate.H / 2 + ball.Size + 0.5, out var wx, out var wy);
        ball.X = wx;
        ball.Y = wy;
        if (ball.Vy < 40)
            ball.Vy = 40;
        ball.TeleportFlashT = world.TeleportFlashSeconds * 0.5;
        if (changed)
            world.NotifyChanged(markDirty: false, visual: true, project: true);
    }

    public static (double gx, double gy) SampleGravity(SceneWorld world, double x, double y)
        => SampleGravity(world, PhysicsWorldIndex.For(world).QueryArrows(x, y), x, y);

    private static (double gx, double gy) SampleGravity(
        SceneWorld world,
        List<SceneObject> arrows,
        double x,
        double y)
    {
        double gx = 0;
        double gy = world.GravityG * SceneWorld.GUnit; // 向下 +Y

        foreach (var arrow in arrows)
        {
            var cx = arrow.X + arrow.W / 2;
            var cy = arrow.Y + arrow.H / 2;
            var dx = x - cx;
            var dy = y - cy;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            var r = Math.Max(1, arrow.InfluenceRadius);
            if (dist > r)
                continue;

            var len = Math.Sqrt(arrow.DirX * arrow.DirX + arrow.DirY * arrow.DirY);
            if (len < 1e-6)
                continue;

            var falloff = 1.0 - dist / r; // 线性衰减
            var mag = arrow.StrengthG * SceneWorld.GUnit * falloff;
            gx += arrow.DirX / len * mag;
            gy += arrow.DirY / len * mag;
        }

        return (gx, gy);
    }

    private static void ResolveWorldBounds(SceneWorld world, Ball ball)
    {
        // 场景内区四壁 = 界外实体碰撞(E0);球不得穿出 [r, W-r] × [r, H-r]
        var r = ball.Size;
        var maxX = Math.Max(r, world.WorldWidth - r);
        var maxY = Math.Max(r, world.WorldHeight - r);
        var restitution = Math.Clamp(world.WallRestitution, 0, 1);

        if (ball.X < r)
        {
            ball.X = r;
            if (ball.Vx < 0)
                ball.Vx = -ball.Vx * restitution;
        }
        else if (ball.X > maxX)
        {
            ball.X = maxX;
            if (ball.Vx > 0)
                ball.Vx = -ball.Vx * restitution;
        }

        if (ball.Y < r)
        {
            ball.Y = r;
            if (ball.Vy < 0)
                ball.Vy = -ball.Vy * restitution;
        }
        else if (ball.Y > maxY)
        {
            ball.Y = maxY;
            if (ball.Vy > 0)
                ball.Vy = -ball.Vy * restitution;
        }
    }

    private static void ResolveBallBlock(Ball ball, SceneObject block)
    {
        var r = ball.Size;
        block.WorldToLocal(ball.X, ball.Y, out var lx, out var ly);
        var hx = block.W / 2;
        var hy = block.H / 2;
        var nearestX = Math.Clamp(lx, -hx, hx);
        var nearestY = Math.Clamp(ly, -hy, hy);
        var dx = lx - nearestX;
        var dy = ly - nearestY;
        var distSq = dx * dx + dy * dy;
        if (distSq >= r * r)
            return;

        double nxLocal, nyLocal, penetration;
        if (distSq < 1e-8)
        {
            var left = lx + hx;
            var right = hx - lx;
            var top = ly + hy;
            var bottom = hy - ly;
            var m = Math.Min(Math.Min(left, right), Math.Min(top, bottom));
            if (m == left)
            {
                nxLocal = -1;
                nyLocal = 0;
                penetration = r + left;
            }
            else if (m == right)
            {
                nxLocal = 1;
                nyLocal = 0;
                penetration = r + right;
            }
            else if (m == top)
            {
                nxLocal = 0;
                nyLocal = -1;
                penetration = r + top;
            }
            else
            {
                nxLocal = 0;
                nyLocal = 1;
                penetration = r + bottom;
            }
        }
        else
        {
            var dist = Math.Sqrt(distSq);
            nxLocal = dx / dist;
            nyLocal = dy / dist;
            penetration = r - dist;
        }

        // 法线转回世界
        var rad = block.Rotation * Math.PI / 180.0;
        var c = Math.Cos(rad);
        var s = Math.Sin(rad);
        var nx = nxLocal * c - nyLocal * s;
        var ny = nxLocal * s + nyLocal * c;
        ball.X += nx * penetration;
        ball.Y += ny * penetration;
        var vn = ball.Vx * nx + ball.Vy * ny;
        if (vn < 0)
        {
            ball.Vx -= 1.4 * vn * nx;
            ball.Vy -= 1.4 * vn * ny;
        }
    }

    /// <summary>
    /// 圆 vs 异形实体(v1.5.1 MS-05/PH-01~03):
    /// 按原多边形轮廓求最近边界点;圆心在内部(三角网格判定)时推向最近边;
    /// AABB 仅作粗筛,不作碰撞体。
    /// </summary>
    private static void ResolveBallSolid(Ball ball, MeshSolid solid)
    {
        var pts = solid.Points;
        var n = pts.Count;
        if (n < 3)
            return;

        var r = ball.Size;
        solid.GetAabb(out var minX, out var minY, out var maxX, out var maxY);
        if (ball.X < minX - r || ball.X > maxX + r || ball.Y < minY - r || ball.Y > maxY + r)
            return;

        // 最近边界点 + 该边外法线(法线取命中边;朝向由多边形绕向决定)
        var orientation = SignedAreaOf(pts) >= 0 ? 1.0 : -1.0;
        var bestDistSq = double.PositiveInfinity;
        double bestQx = 0, bestQy = 0, bestNx = 0, bestNy = -1;
        for (var i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % n];
            var ex = b.X - a.X;
            var ey = b.Y - a.Y;
            var lenSq = ex * ex + ey * ey;
            double qx, qy;
            if (lenSq < 1e-12)
            {
                qx = a.X;
                qy = a.Y;
            }
            else
            {
                var t = Math.Clamp(((ball.X - a.X) * ex + (ball.Y - a.Y) * ey) / lenSq, 0, 1);
                qx = a.X + t * ex;
                qy = a.Y + t * ey;
            }

            var dx = ball.X - qx;
            var dy = ball.Y - qy;
            var distSq = dx * dx + dy * dy;
            if (distSq >= bestDistSq)
                continue;

            bestDistSq = distSq;
            bestQx = qx;
            bestQy = qy;
            var len = Math.Sqrt(lenSq);
            if (len > 1e-9)
            {
                bestNx = orientation * (ey / len);
                bestNy = orientation * (-ex / len);
            }
        }

        var inside = solid.ContainsPoint(ball.X, ball.Y);
        var dist = Math.Sqrt(bestDistSq);
        if (!inside && dist >= r)
            return;

        double nx, ny, penetration;
        if (inside)
        {
            // 圆心在内部:推向最近边(PH-02)
            if (dist > 1e-9)
            {
                nx = (bestQx - ball.X) / dist;
                ny = (bestQy - ball.Y) / dist;
            }
            else
            {
                nx = bestNx;
                ny = bestNy;
            }

            penetration = dist + r;
        }
        else
        {
            if (dist > 1e-9)
            {
                nx = (ball.X - bestQx) / dist;
                ny = (ball.Y - bestQy) / dist;
            }
            else
            {
                nx = bestNx;
                ny = bestNy;
            }

            penetration = r - dist;
        }

        ball.X += nx * penetration;
        ball.Y += ny * penetration;
        var vn = ball.Vx * nx + ball.Vy * ny;
        if (vn < 0)
        {
            ball.Vx -= 1.4 * vn * nx;
            ball.Vy -= 1.4 * vn * ny;
        }
    }

    private static double SignedAreaOf(IReadOnlyList<WirePoint> pts)
    {
        double a = 0;
        for (var i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            var q = pts[(i + 1) % pts.Count];
            a += p.X * q.Y - q.X * p.Y;
        }

        return a * 0.5;
    }

    private static void ResolveBallBall(SceneWorld world)
    {
        var balls = world.Balls;
        if (balls.Count >= 512)
        {
            ResolveBallBallIndexed(world, balls);
            return;
        }
        for (var i = 0; i < balls.Count; i++)
        {
            for (var j = i + 1; j < balls.Count; j++)
                ResolveBallPair(world, balls[i], balls[j]);
        }
    }

    private static void ResolveBallBallIndexed(SceneWorld world, List<Ball> balls)
    {
        var grid = BallCollisionGrid.For(world);
        grid.Build(balls);
        for (var i = 0; i < balls.Count; i++)
        {
            var a = balls[i];
            foreach (var j in grid.Query(i, a))
                ResolveBallPair(world, a, balls[j]);
        }
    }

    private static void ResolveBallPair(SceneWorld world, Ball a, Ball b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var distSq = dx * dx + dy * dy;
        var minDist = a.Size + b.Size;
        if (distSq >= minDist * minDist || distSq < 1e-12)
            return;

        var dist = Math.Sqrt(distSq);
        var nx = dx / dist;
        var ny = dy / dist;
        var overlap = minDist - dist;
        var ma = Math.Max(0.01, a.Weight);
        var mb = Math.Max(0.01, b.Weight);
        var inv = 1.0 / (ma + mb);
        a.X -= nx * overlap * mb * inv;
        a.Y -= ny * overlap * mb * inv;
        b.X += nx * overlap * ma * inv;
        b.Y += ny * overlap * ma * inv;

        var rvx = b.Vx - a.Vx;
        var rvy = b.Vy - a.Vy;
        var velAlong = rvx * nx + rvy * ny;
        if (velAlong > 0)
            return;

        var e = Math.Clamp(world.BallRestitution, 0, 1);
        var jImpulse = -(1 + e) * velAlong / (1 / ma + 1 / mb);
        var ix = jImpulse * nx;
        var iy = jImpulse * ny;
        a.Vx -= ix / ma;
        a.Vy -= iy / ma;
        b.Vx += ix / mb;
        b.Vy += iy / mb;
    }

    private static bool Contains(SceneObject box, double x, double y)
    {
        box.WorldToLocal(x, y, out var lx, out var ly);
        return Math.Abs(lx) <= box.W / 2 && Math.Abs(ly) <= box.H / 2;
    }

    public static void TeleportBall(SceneWorld world, Ball ball, SceneObject sink, Action<string>? warn)
    {
        var spawners = PhysicsWorldIndex.For(world).Spawners;
        var colorBefore = ball.Color;
        var weightBefore = ball.Weight;
        var sizeBefore = ball.Size;
        var multBefore = ball.Multiplier;

        // name 为函数权威:Xn/RUN/攻击结算跳过销毁器 patch;未知/空名仍可走 sink patch
        Func<string, bool>? isSettlement = world.Settlements == null
            ? null
            : world.Settlements.IsKnown;
        var fn = DespawnFunctions.Parse(sink.Name, isSettlement);
        var changed = DespawnFunctions.Apply(fn, world, ball, world.Defaults, warn);
        if (fn.Kind is not (DespawnFunctions.Kind.Multiply
            or DespawnFunctions.Kind.Run
            or DespawnFunctions.Kind.Settlement))
            SceneWorld.ApplyPatch(ball, SceneWorld.ParsePatch(sink.PatchJson));

        if (spawners.Length == 0)
        {
            world.Balls.Remove(ball);
            warn?.Invoke($"小球 {ball.Id} 已销毁,但场景中无生成器,已真正移除");
            world.NotifyChanged(markDirty: false, visual: true, project: true);
            return;
        }

        var fromX = ball.X;
        var fromY = ball.Y;
        var spawner = spawners[world.Rng.Next(spawners.Length)];
        SceneWorld.ApplyPatch(ball, SceneWorld.ParsePatch(spawner.PatchJson));
        // v2.10 DR-01:360° 抛球机 — 确定性随机角度与初速抛出,喷泉式布球
        ball.X = spawner.X + spawner.W / 2;
        ball.Y = spawner.Y + spawner.H / 2;
        var throwAngle = world.Rng.NextDouble() * Math.PI * 2;
        var throwSpeed = 150 + world.Rng.NextDouble() * 170;
        ball.Vx = Math.Cos(throwAngle) * throwSpeed;
        ball.Vy = Math.Sin(throwAngle) * throwSpeed;

        // 传送残影
        ball.TeleportFromX = fromX;
        ball.TeleportFromY = fromY;
        ball.TeleportToX = ball.X;
        ball.TeleportToY = ball.Y;
        ball.TeleportFlashT = world.TeleportFlashSeconds;
        if (world.TrailEnabled)
            ball.PushTrail(ball.X, ball.Y, world.TrailLength);

        // 性质变化:UI 立即刷新;投影由 ProjectionDirty 在非播放时节流写入
        var propsChanged = changed
                           || !string.Equals(colorBefore, ball.Color, StringComparison.Ordinal)
                           || Math.Abs(weightBefore - ball.Weight) > 1e-9
                           || Math.Abs(sizeBefore - ball.Size) > 1e-9
                           || multBefore != ball.Multiplier;
        if (propsChanged)
            world.NotifyChanged(markDirty: false, visual: true, project: true);
    }
}
