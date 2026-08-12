using System.Globalization;

namespace WBall.Model;

/// <summary>
/// 场景文件 ↔ SceneWorld。
/// 权威路径(验收 12 / v1.2.1): <b>仅</b> 文件 → SceneWorld；禁止 SQLite hydrate。
/// </summary>
public static partial class SceneStore
{
    public const int CurrentFormat = 5;

    public static SceneSnapshot Capture(SceneWorld world)
    {
        return new SceneSnapshot
        {
            Format = CurrentFormat,
            App = "WBall",
            GravityG = world.GravityG,
            BallCollision = world.BallCollisionEnabled,
            Seed = world.Seed,
            WorldWidth = world.WorldWidth,
            WorldHeight = world.WorldHeight,
            Objects = world.Objects.Select(o => new SceneObjectDto
            {
                Id = o.Id,
                Type = o.Type.ToString().ToLowerInvariant(),
                X = o.X,
                Y = o.Y,
                W = o.W,
                H = o.H,
                DirX = o.DirX,
                DirY = o.DirY,
                InfluenceRadius = o.InfluenceRadius,
                StrengthG = o.StrengthG,
                Rotation = o.Rotation,
                PatchJson = o.PatchJson,
                Name = o.Name,
            }).ToList(),
            Wireframes = world.Wireframes.Select(w => new WireframeDto
            {
                Id = w.Id,
                Closed = w.Closed,
                Points = w.Points.Select(p => new WirePointDto { X = p.X, Y = p.Y }).ToList(),
            }).ToList(),
            Solids = world.Solids.Select(s => new SolidDto
            {
                Id = s.Id,
                Color = s.Color,
                Points = s.Points.Select(p => new WirePointDto { X = p.X, Y = p.Y }).ToList(),
            }).ToList(),
            Balls = world.Balls.Select(b => new BallDto
            {
                Id = b.Id,
                X = b.X,
                Y = b.Y,
                Color = b.Color,
                Weight = b.Weight,
                Size = b.Size,
                Multiplier = b.Multiplier,
                Props = b.Props.Count > 0 ? new Dictionary<string, string>(b.Props) : null,
            }).ToList(),
        };
    }

    public static void Apply(SceneWorld world, SceneSnapshot snap)
    {
        ValidateSnapshot(snap);

        world.IsPlaying = false;
        world.Objects.Clear();
        world.Balls.Clear();
        world.Wireframes.Clear();
        world.Solids.Clear();
        world.Sketch.Clear();
        world.SelectedId = null;
        world.SelectedWireId = null;
        world.SelectedSolidId = null;
        world.SelectedBallId = null;

        world.GravityG = snap.GravityG;
        world.BallCollisionEnabled = snap.BallCollision;
        world.Seed = snap.Seed;

        var ww = snap.WorldWidth > 0 ? snap.WorldWidth : SceneWorld.DefaultWorldWidth;
        var wh = snap.WorldHeight > 0 ? snap.WorldHeight : SceneWorld.DefaultWorldHeight;
        // format=1 可能未写宽高(默认 800/600);钳制到合法范围
        ww = Math.Clamp(ww, SceneWorld.MinWorldSize, SceneWorld.MaxWorldSize);
        wh = Math.Clamp(wh, SceneWorld.MinWorldSize, SceneWorld.MaxWorldSize);
        world.SetWorldSize(ww, wh, markDirty: false);

        var maxObj = 0;
        var maxBall = 0;
        var maxWire = 0;
        var maxSolid = 0;
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dto in snap.Objects)
        {
            var type = ParseType(dto.Type);
            var id = string.IsNullOrWhiteSpace(dto.Id) ? $"obj{maxObj + 1}" : dto.Id.Trim();
            if (!seenIds.Add(id))
                throw new InvalidOperationException($"场景对象 id 重复: {id}");

            world.Objects.Add(new SceneObject
            {
                Id = id,
                Type = type,
                X = dto.X,
                Y = dto.Y,
                W = Math.Max(8, dto.W),
                H = Math.Max(8, dto.H),
                DirX = dto.DirX,
                DirY = dto.DirY,
                InfluenceRadius = dto.InfluenceRadius > 0 ? dto.InfluenceRadius : 160,
                StrengthG = dto.StrengthG,
                Rotation = dto.Rotation,
                PatchJson = dto.PatchJson,
                Name = snap.Format >= 5 ? dto.Name : null,
            });
            if (world.Objects[^1].Type == SceneObjectType.Arrow && Math.Abs(dto.Rotation) > 1e-6)
                world.Objects[^1].SyncArrowDirFromRotation();
            maxObj = Math.Max(maxObj, ParseTrailingInt(id, "obj"));
        }

        foreach (var dto in snap.Wireframes ?? [])
        {
            var id = string.IsNullOrWhiteSpace(dto.Id) ? $"wire{maxWire + 1}" : dto.Id.Trim();
            if (!seenIds.Add(id))
                throw new InvalidOperationException($"线框 id 重复: {id}");
            world.Wireframes.Add(new Wireframe
            {
                Id = id,
                Closed = true,
                Points = (dto.Points ?? [])
                    .Select(p => new WirePoint(p.X, p.Y))
                    .ToList(),
            });
            maxWire = Math.Max(maxWire, ParseTrailingInt(id, "wire"));
        }

        foreach (var dto in snap.Solids ?? [])
        {
            var id = string.IsNullOrWhiteSpace(dto.Id) ? $"solid{maxSolid + 1}" : dto.Id.Trim();
            if (!seenIds.Add(id))
                throw new InvalidOperationException($"异形实体 id 重复: {id}");

            // 多边形为权威;三角网格加载时重算(V51Q5);自交文件视为损坏
            var pts = (dto.Points ?? []).Select(p => (p.X, p.Y)).ToList();
            if (pts.Count < 3 || Geometry.PolygonMath.IsSelfIntersecting(pts))
                throw new InvalidOperationException($"异形实体 {id} 顶点不足或自交");
            var tris = Geometry.PolygonMath.TriangulateIndices(pts);
            if (tris.Count == 0)
                throw new InvalidOperationException($"异形实体 {id} 三角化失败(顶点退化)");

            world.Solids.Add(new MeshSolid
            {
                Id = id,
                Color = string.IsNullOrWhiteSpace(dto.Color) ? MeshSolid.DefaultColor : dto.Color,
                Points = pts.Select(p => new WirePoint(p.Item1, p.Item2)).ToList(),
                Triangles = tris,
            });
            maxSolid = Math.Max(maxSolid, ParseTrailingInt(id, "solid"));
        }

        foreach (var dto in snap.Balls)
        {
            var id = string.IsNullOrWhiteSpace(dto.Id) ? $"ball{maxBall + 1}" : dto.Id.Trim();
            if (!seenIds.Add(id))
                throw new InvalidOperationException($"场景实体 id 与对象冲突或重复: {id}");

            var ball = new Ball
            {
                Id = id,
                X = dto.X,
                Y = dto.Y,
                Vx = 0,
                Vy = 0,
                Color = string.IsNullOrWhiteSpace(dto.Color) ? "#3B82F6" : dto.Color,
                Weight = PublicDefaults.RoundWeight(dto.Weight),
                Size = PublicDefaults.RoundSize(dto.Size),
                Multiplier = snap.Format >= 5
                    ? PublicDefaults.ClampMultiplier(dto.Multiplier <= 0 ? 1 : dto.Multiplier)
                    : 1,
            };
            if (dto.Props != null)
            {
                foreach (var (k, v) in dto.Props)
                    ball.Props[k] = v;
            }

            world.Balls.Add(ball);
            maxBall = Math.Max(maxBall, ParseTrailingInt(id, "ball"));
        }

        world.SetIdCounters(maxObj + 1, maxBall + 1, maxWire + 1, maxSolid + 1);
        world.NotifyChanged(markDirty: false);
    }

    public static void NewScene(SceneWorld world)
    {
        world.IsPlaying = false;
        world.Objects.Clear();
        world.Balls.Clear();
        world.Wireframes.Clear();
        world.Solids.Clear();
        world.Factions.Clear();
        world.Sketch.Clear();
        world.SelectedId = null;
        world.SelectedWireId = null;
        world.SelectedSolidId = null;
        world.SelectedBallId = null;
        world.GravityG = 10;
        world.BallCollisionEnabled = false;
        world.Seed = 42;
        world.SetWorldSize(SceneWorld.DefaultWorldWidth, SceneWorld.DefaultWorldHeight, markDirty: false);
        world.SetIdCounters(1, 1, 1);
        world.Tool = EditorTool.Block;
        world.LastScenePath = null;
        world.SceneDirty = false;
        world.NotifyChanged(markDirty: false);
    }

    /// <summary>
    /// 核对 SceneWorld 是否与文件内容一致(验收 12:文件→运行时权威路径已正确应用)。
    /// </summary>
    public static IReadOnlyList<string> DiffAgainstFile(SceneWorld world, string fullPath)
    {
        var fileSnap = ReadFile(fullPath);
        var live = Capture(world);
        return Diff(fileSnap, live);
    }

    public static IReadOnlyList<string> Diff(SceneSnapshot expected, SceneSnapshot actual)
    {
        var diffs = new List<string>();

        // format 为版本标签:内容一致即可;不因 1↔2 误报失败(E1 旧档兼容)
        if (!Near(expected.GravityG, actual.GravityG))
            diffs.Add($"gravityG: 文件={expected.GravityG} 运行时={actual.GravityG}");
        if (expected.BallCollision != actual.BallCollision)
            diffs.Add($"ballCollision: 文件={expected.BallCollision} 运行时={actual.BallCollision}");
        if (expected.Seed != actual.Seed)
            diffs.Add($"seed: 文件={expected.Seed} 运行时={actual.Seed}");

        var expW = expected.WorldWidth > 0 ? expected.WorldWidth : SceneWorld.DefaultWorldWidth;
        var expH = expected.WorldHeight > 0 ? expected.WorldHeight : SceneWorld.DefaultWorldHeight;
        if (!Near(expW, actual.WorldWidth) || !Near(expH, actual.WorldHeight))
            diffs.Add($"worldSize: 文件={expW}x{expH} 运行时={actual.WorldWidth}x{actual.WorldHeight}");
        if (expected.Objects.Count != actual.Objects.Count)
            diffs.Add($"objects.count: 文件={expected.Objects.Count} 运行时={actual.Objects.Count}");
        if (expected.Balls.Count != actual.Balls.Count)
            diffs.Add($"balls.count: 文件={expected.Balls.Count} 运行时={actual.Balls.Count}");

        var expWires = expected.Wireframes ?? [];
        var actWires = actual.Wireframes ?? [];
        if (expWires.Count != actWires.Count)
            diffs.Add($"wireframes.count: 文件={expWires.Count} 运行时={actWires.Count}");

        var expSolids = expected.Solids ?? [];
        var actSolids = actual.Solids ?? [];
        if (expSolids.Count != actSolids.Count)
            diffs.Add($"solids.count: 文件={expSolids.Count} 运行时={actSolids.Count}");

        var expObj = expected.Objects.ToDictionary(o => o.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var a in actual.Objects)
        {
            if (!expObj.TryGetValue(a.Id, out var e))
            {
                diffs.Add($"多余对象: {a.Id}");
                continue;
            }

            if (!string.Equals(e.Type, a.Type, StringComparison.OrdinalIgnoreCase))
                diffs.Add($"{a.Id}.type: {e.Type}≠{a.Type}");
            if (!Near(e.X, a.X) || !Near(e.Y, a.Y) || !Near(e.W, a.W) || !Near(e.H, a.H))
                diffs.Add($"{a.Id}.geom: 文件=({e.X},{e.Y},{e.W},{e.H}) 运行时=({a.X},{a.Y},{a.W},{a.H})");
            if (!Near(e.DirX, a.DirX) || !Near(e.DirY, a.DirY))
                diffs.Add($"{a.Id}.dir: 文件=({e.DirX},{e.DirY}) 运行时=({a.DirX},{a.DirY})");
            if (!Near(e.InfluenceRadius, a.InfluenceRadius) || !Near(e.StrengthG, a.StrengthG))
                diffs.Add($"{a.Id}.field: radius/strength 不一致");
            if (!Near(e.Rotation, a.Rotation))
                diffs.Add($"{a.Id}.rotation: 文件={e.Rotation} 运行时={a.Rotation}");
            if (!string.Equals(e.PatchJson ?? "", a.PatchJson ?? "", StringComparison.Ordinal))
                diffs.Add($"{a.Id}.patch 不一致");
            if (!string.Equals(e.Name ?? "", a.Name ?? "", StringComparison.Ordinal))
                diffs.Add($"{a.Id}.name: 文件={e.Name ?? "(null)"} 运行时={a.Name ?? "(null)"}");
        }

        foreach (var id in expObj.Keys)
        {
            if (!actual.Objects.Any(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase)))
                diffs.Add($"缺失对象: {id}");
        }

        var expBall = expected.Balls.ToDictionary(b => b.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var a in actual.Balls)
        {
            if (!expBall.TryGetValue(a.Id, out var e))
            {
                diffs.Add($"多余小球: {a.Id}");
                continue;
            }

            if (!Near(e.X, a.X) || !Near(e.Y, a.Y))
                diffs.Add($"{a.Id}.pos: 文件=({e.X},{e.Y}) 运行时=({a.X},{a.Y})");
            if (!string.Equals(e.Color, a.Color, StringComparison.OrdinalIgnoreCase))
                diffs.Add($"{a.Id}.color: {e.Color}≠{a.Color}");
            if (!Near(e.Weight, a.Weight) || !Near(e.Size, a.Size))
                diffs.Add($"{a.Id}.weight/size 不一致");
            if (e.Multiplier != a.Multiplier)
                diffs.Add($"{a.Id}.multiplier: 文件={e.Multiplier} 运行时={a.Multiplier}");
            if (!PropsEqual(e.Props, a.Props))
                diffs.Add($"{a.Id}.props 不一致");
        }

        foreach (var id in expBall.Keys)
        {
            if (!actual.Balls.Any(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase)))
                diffs.Add($"缺失小球: {id}");
        }

        var expWireMap = expWires.ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var a in actWires)
        {
            if (!expWireMap.TryGetValue(a.Id, out var e))
            {
                diffs.Add($"多余线框: {a.Id}");
                continue;
            }

            if ((e.Points?.Count ?? 0) != (a.Points?.Count ?? 0))
                diffs.Add($"{a.Id}.points.count 不一致");
        }

        foreach (var id in expWireMap.Keys)
        {
            if (!actWires.Any(w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase)))
                diffs.Add($"缺失线框: {id}");
        }

        var expSolidMap = expSolids.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var a in actSolids)
        {
            if (!expSolidMap.TryGetValue(a.Id, out var e))
            {
                diffs.Add($"多余异形: {a.Id}");
                continue;
            }

            if ((e.Points?.Count ?? 0) != (a.Points?.Count ?? 0))
                diffs.Add($"{a.Id}.points.count 不一致");
        }

        foreach (var id in expSolidMap.Keys)
        {
            if (!actSolids.Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)))
                diffs.Add($"缺失异形: {id}");
        }

        return diffs;
    }

    private static bool PropsEqual(Dictionary<string, string>? a, Dictionary<string, string>? b)
    {
        var aa = new Dictionary<string, string>(a ?? new(), StringComparer.OrdinalIgnoreCase);
        var bb = new Dictionary<string, string>(b ?? new(), StringComparer.OrdinalIgnoreCase);
        if (aa.Count != bb.Count)
            return false;
        foreach (var (k, v) in aa)
        {
            if (!bb.TryGetValue(k, out var bv) || !string.Equals(v, bv, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

    private static SceneObjectType ParseType(string s) => s.Trim().ToLowerInvariant() switch
    {
        "block" => SceneObjectType.Block,
        "arrow" => SceneObjectType.Arrow,
        "spawner" => SceneObjectType.Spawner,
        "despawner" => SceneObjectType.Despawner,
        _ => throw new InvalidOperationException($"未知对象类型: {s}"),
    };

    private static int ParseTrailingInt(string? id, string prefix)
    {
        if (string.IsNullOrEmpty(id))
            return 0;
        if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return 0;
        return int.TryParse(id[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : 0;
    }
}
