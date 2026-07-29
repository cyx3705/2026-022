using System.Globalization;
using System.Text.Json;
using WBall.Game;
using WBall.Battle;

namespace WBall.Model;

/// <summary>缩小场景后的裁剪结果(V4Q1:越界对象删除,越界球回生成器)。</summary>
public readonly struct WorldCullResult
{
    public int RemovedObjects { get; init; }
    public int RespawnedBalls { get; init; }
    public int RemovedBalls { get; init; }

    public bool Any => RemovedObjects > 0 || RespawnedBalls > 0 || RemovedBalls > 0;

    public override string ToString()
    {
        if (!Any)
            return "无越界裁剪";
        return $"裁剪: 删除对象 {RemovedObjects}, 球回生成器 {RespawnedBalls}, 无生成器移除球 {RemovedBalls}";
    }
}

/// <summary>运行时场景权威(v1.3 §2.3)。</summary>
public sealed class SceneWorld
{
    public const double GUnit = 98.0; // 1g ≈ 98 px/s²
    public const double MinWorldSize = 200;
    public const double MaxWorldSize = 4000;
    public const double DefaultWorldWidth = 800;
    public const double DefaultWorldHeight = 600;

    private int _nextObjectId = 1;
    private int _nextBallId = 1;
    private int _nextWireId = 1;
    private int _nextSolidId = 1;
    private Random _rng = new(42);

    public List<SceneObject> Objects { get; } = new();
    public List<Ball> Balls { get; } = new();
    public List<Wireframe> Wireframes { get; } = new();

    /// <summary>异形实体(v1.5.1):独立于 Objects,碰撞与绘制走三角网格路径。</summary>
    public List<MeshSolid> Solids { get; } = new();

    /// <summary>表公共行 / 倍率公式参数(v1.6)。</summary>
    public PublicDefaults Defaults { get; set; } = new();

    /// <summary>阵营 / 裁判表(v1.6);非场景文件权威。</summary>
    public List<Faction> Factions { get; } = new();

    /// <summary>可选的攻击类型结算桥；未配置时保持 v1.6 行为。</summary>
    public ISettlementService? Settlements { get; set; }

    /// <summary>CAD 草图(临时绘制缓冲)。</summary>
    public WireSketch Sketch { get; } = new();

    public EditorTool Tool { get; set; } = EditorTool.Block;
    public string? SelectedId { get; set; }
    public string? SelectedWireId { get; set; }
    public string? SelectedSolidId { get; set; }
    public string? SelectedBallId { get; set; }

    public bool IsPlaying { get; set; }
    public bool BallCollisionEnabled { get; set; } = true;
    public double WallRestitution { get; set; } = 0.55;
    public double BallRestitution { get; set; } = 0.85;
    public bool TrailEnabled { get; set; } = false;
    public int TrailLength { get; set; } = 24;
    public double TeleportFlashSeconds { get; set; } = 0.35;

    /// <summary>全局重力倍数(默认 10g,方向向下)。</summary>
    public double GravityG { get; set; } = 10;

    public int Seed
    {
        get => _seed;
        set
        {
            _seed = value;
            _rng = new Random(_seed);
        }
    }

    private int _seed = 42;

    /// <summary>场景内区逻辑宽高(与视图缩放无关)。v1.4 E0。</summary>
    public double WorldWidth { get; private set; } = DefaultWorldWidth;
    public double WorldHeight { get; private set; } = DefaultWorldHeight;

    /// <summary>设置场景尺寸;缩小时空出越界对象并处理越界球(V4Q1)。</summary>
    public WorldCullResult SetWorldSize(double width, double height, bool markDirty = true)
    {
        if (width < MinWorldSize || height < MinWorldSize)
            throw new ArgumentOutOfRangeException(
                nameof(width),
                $"场景尺寸过小,最短边须 ≥ {MinWorldSize}");
        if (width > MaxWorldSize || height > MaxWorldSize)
            throw new ArgumentOutOfRangeException(
                nameof(width),
                $"场景尺寸过大,最长边须 ≤ {MaxWorldSize}");

        var shrunk = width < WorldWidth - 1e-6 || height < WorldHeight - 1e-6;
        WorldWidth = width;
        WorldHeight = height;

        var cull = shrunk ? CullOutsideInterior() : default;

        if (SelectedId != null && FindObject(SelectedId) == null)
            SelectedId = null;
        if (SelectedSolidId != null && FindSolid(SelectedSolidId) == null)
            SelectedSolidId = null;
        if (SelectedBallId != null && FindBall(SelectedBallId) == null)
            SelectedBallId = null;

        NotifyChanged(markDirty);
        return cull;
    }

    /// <summary>
    /// 内区外的对象删除;越界小球传送到随机生成器(同 id),无生成器则移除。
    /// </summary>
    public WorldCullResult CullOutsideInterior()
    {
        var removedObjects = 0;
        for (var i = Objects.Count - 1; i >= 0; i--)
        {
            if (ObjectFullyInside(Objects[i]))
                continue;
            Objects.RemoveAt(i);
            removedObjects++;
        }

        for (var i = Solids.Count - 1; i >= 0; i--)
        {
            if (SolidFullyInside(Solids[i]))
                continue;
            Solids.RemoveAt(i);
            removedObjects++;
        }

        var spawners = OfType(SceneObjectType.Spawner).ToList();
        var respawned = 0;
        var removedBalls = 0;
        for (var i = Balls.Count - 1; i >= 0; i--)
        {
            var ball = Balls[i];
            if (BallFullyInside(ball))
                continue;

            if (spawners.Count == 0)
            {
                Balls.RemoveAt(i);
                removedBalls++;
                continue;
            }

            var sp = spawners[_rng.Next(spawners.Count)];
            ball.X = sp.X + sp.W / 2;
            ball.Y = sp.Y + sp.H / 2;
            ball.Vx = 0;
            ball.Vy = 0;
            // 若生成器中心仍越界(极端),夹到内区
            ball.X = Math.Clamp(ball.X, ball.Size, Math.Max(ball.Size, WorldWidth - ball.Size));
            ball.Y = Math.Clamp(ball.Y, ball.Size, Math.Max(ball.Size, WorldHeight - ball.Size));
            respawned++;
        }

        return new WorldCullResult
        {
            RemovedObjects = removedObjects,
            RespawnedBalls = respawned,
            RemovedBalls = removedBalls,
        };
    }

    public bool ObjectFullyInside(SceneObject o)
    {
        o.GetAabb(out var minX, out var minY, out var maxX, out var maxY);
        return minX >= -1e-6 && minY >= -1e-6
               && maxX <= WorldWidth + 1e-6
               && maxY <= WorldHeight + 1e-6;
    }

    public bool SolidFullyInside(MeshSolid s)
    {
        s.GetAabb(out var minX, out var minY, out var maxX, out var maxY);
        return minX >= -1e-6 && minY >= -1e-6
               && maxX <= WorldWidth + 1e-6
               && maxY <= WorldHeight + 1e-6;
    }

    public bool BallFullyInside(Ball b)
        => b.X - b.Size >= -1e-6 && b.Y - b.Size >= -1e-6
           && b.X + b.Size <= WorldWidth + 1e-6
           && b.Y + b.Size <= WorldHeight + 1e-6;

    /// <summary>点是否在场景内区(含边界)。</summary>
    public bool ContainsPoint(double x, double y)
        => x >= 0 && y >= 0 && x <= WorldWidth && y <= WorldHeight;

    /// <summary>最近一次 save/load 的场景文件绝对路径(验收 12)。</summary>
    public string? LastScenePath { get; set; }

    /// <summary>相对 LastScenePath 是否有未保存改动(提示用)。</summary>
    public bool SceneDirty { get; set; }

    public event Action? Changed;

    /// <summary>投影表需要与内存对齐(可与 UI 刷新分离)。</summary>
    public event Action? ProjectionDirty;

    /// <param name="markDirty">是否标记场景未保存。</param>
    /// <param name="visual">是否刷新画布/面板(Changed)。</param>
    /// <param name="project">是否请求投影同步(ProjectionDirty)。</param>
    public void NotifyChanged(bool markDirty = true, bool visual = true, bool project = true)
    {
        if (markDirty)
            SceneDirty = true;
        if (visual)
            Changed?.Invoke();
        if (project)
            ProjectionDirty?.Invoke();
    }

    public string NextObjectId() => $"obj{_nextObjectId++}";
    public string NextBallId() => $"ball{_nextBallId++}";
    public string NextWireId() => $"wire{_nextWireId++}";
    public string NextSolidId() => $"solid{_nextSolidId++}";

    /// <summary>加载快照后同步 id 计数器,避免与已有 id 冲突。</summary>
    public void SetIdCounters(int nextObjectId, int nextBallId, int nextWireId = 1, int nextSolidId = 1)
    {
        _nextObjectId = Math.Max(1, nextObjectId);
        _nextBallId = Math.Max(1, nextBallId);
        _nextWireId = Math.Max(1, nextWireId);
        _nextSolidId = Math.Max(1, nextSolidId);
    }

    public SceneObject? FindObject(string id)
        => Objects.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));

    public Ball? FindBall(string id)
        => Balls.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));

    public Wireframe? FindWire(string id)
        => Wireframes.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));

    public MeshSolid? FindSolid(string id)
        => Solids.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    public Faction? FindFaction(string id)
        => Factions.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<SceneObject> OfType(SceneObjectType type)
        => Objects.Where(o => o.Type == type);

    public void ClearObjects()
    {
        Objects.Clear();
        Solids.Clear();
        SelectedId = null;
        SelectedSolidId = null;
        NotifyChanged();
    }

    public void ClearBalls()
    {
        Balls.Clear();
        SelectedBallId = null;
        NotifyChanged();
    }

    public void ClearWiresAndSketch()
    {
        Wireframes.Clear();
        Sketch.Clear();
        SelectedWireId = null;
        NotifyChanged();
    }

    public void ResetSimulation()
    {
        IsPlaying = false;
        ClearBallTrails();
        ClearBalls();
        _rng = new Random(_seed);
        NotifyChanged();
    }

    public void ClearBallTrails()
    {
        foreach (var b in Balls)
            b.ClearTrail();
    }

    public Random Rng => _rng;

    /// <summary>导演将左右世界绑定到同一个有种子随机源。</summary>
    public void UseRandom(Random random) => _rng = random ?? throw new ArgumentNullException(nameof(random));

    public static PropertyPatch? ParsePatch(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<PropertyPatch>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch
        {
            return null;
        }
    }

    public static void ApplyPatch(Ball ball, PropertyPatch? patch)
    {
        if (patch == null)
            return;

        if (patch.Unset != null)
        {
            foreach (var key in patch.Unset)
            {
                if (IsRequired(key))
                    continue;
                ball.Props.Remove(key);
            }
        }

        if (patch.Add != null)
        {
            foreach (var (key, delta) in patch.Add)
            {
                if (string.Equals(key, "weight", StringComparison.OrdinalIgnoreCase))
                    ball.Weight = PublicDefaults.RoundWeight(ball.Weight + delta);
                else if (string.Equals(key, "size", StringComparison.OrdinalIgnoreCase))
                    ball.Size = PublicDefaults.RoundSize(ball.Size + delta);
                else
                {
                    var cur = 0.0;
                    if (ball.Props.TryGetValue(key, out var s))
                        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out cur);
                    ball.Props[key] = (cur + delta).ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        if (patch.Set != null)
        {
            foreach (var (key, value) in patch.Set)
            {
                if (string.Equals(key, "color", StringComparison.OrdinalIgnoreCase))
                    ball.Color = value;
                else if (string.Equals(key, "weight", StringComparison.OrdinalIgnoreCase)
                         && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var w))
                    ball.Weight = PublicDefaults.RoundWeight(w);
                else if (string.Equals(key, "size", StringComparison.OrdinalIgnoreCase)
                         && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sz))
                    ball.Size = PublicDefaults.RoundSize(sz);
                else
                    ball.Props[key] = value;
            }
        }
    }

    private static bool IsRequired(string key)
        => key is "color" or "weight" or "size"
           || string.Equals(key, "color", StringComparison.OrdinalIgnoreCase)
           || string.Equals(key, "weight", StringComparison.OrdinalIgnoreCase)
           || string.Equals(key, "size", StringComparison.OrdinalIgnoreCase);

}
