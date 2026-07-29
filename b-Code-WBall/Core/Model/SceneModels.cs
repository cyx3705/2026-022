namespace WBall.Model;

public enum SceneObjectType
{
    Block,
    Arrow,
    Spawner,
    Despawner,
}

public enum EditorTool
{
    Select,
    Block,
    Arrow,
    Spawner,
    Despawner,
    Wire,
}

/// <summary>落球区场景对象(权威在 SceneWorld)。</summary>
public sealed class SceneObject
{
    public required string Id { get; set; }
    public required SceneObjectType Type { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 40;
    public double H { get; set; } = 40;

    /// <summary>顺时针旋转角(度);0=轴对齐。v1.4 全对象可转。</summary>
    public double Rotation { get; set; }

    /// <summary>箭头方向(未归一化也可;采样时归一化)。默认向下(+Y)。</summary>
    public double DirX { get; set; }
    public double DirY { get; set; } = 1;

    /// <summary>箭头影响半径(逻辑像素)。</summary>
    public double InfluenceRadius { get; set; } = 160;

    /// <summary>箭头场强度倍数(相对 1g)。</summary>
    public double StrengthG { get; set; } = 10;

    /// <summary>生成器/销毁器属性补丁 JSON。</summary>
    public string? PatchJson { get; set; }

    /// <summary>对象显示名;销毁器上即函数 name(v1.6)。</summary>
    public string? Name { get; set; }

    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;

    /// <summary>旋转后轴对齐包围盒(用于越界裁剪等)。</summary>
    public void GetAabb(out double minX, out double minY, out double maxX, out double maxY)
    {
        if (Math.Abs(Rotation) < 1e-6)
        {
            minX = X;
            minY = Y;
            maxX = X + W;
            maxY = Y + H;
            return;
        }

        var rad = Rotation * Math.PI / 180.0;
        var c = Math.Cos(rad);
        var s = Math.Sin(rad);
        var hx = W / 2;
        var hy = H / 2;
        var cx = CenterX;
        var cy = CenterY;
        minX = double.PositiveInfinity;
        minY = double.PositiveInfinity;
        maxX = double.NegativeInfinity;
        maxY = double.NegativeInfinity;
        foreach (var (lx, ly) in new[] { (-hx, -hy), (hx, -hy), (hx, hy), (-hx, hy) })
        {
            var wx = cx + lx * c - ly * s;
            var wy = cy + lx * s + ly * c;
            minX = Math.Min(minX, wx);
            minY = Math.Min(minY, wy);
            maxX = Math.Max(maxX, wx);
            maxY = Math.Max(maxY, wy);
        }
    }

    /// <summary>世界点 → 物体局部坐标(原点在中心,轴随 Rotation)。</summary>
    public void WorldToLocal(double wx, double wy, out double lx, out double ly)
    {
        var dx = wx - CenterX;
        var dy = wy - CenterY;
        var rad = -Rotation * Math.PI / 180.0;
        var c = Math.Cos(rad);
        var s = Math.Sin(rad);
        lx = dx * c - dy * s;
        ly = dx * s + dy * c;
    }

    public void LocalToWorld(double lx, double ly, out double wx, out double wy)
    {
        var rad = Rotation * Math.PI / 180.0;
        var c = Math.Cos(rad);
        var s = Math.Sin(rad);
        wx = CenterX + lx * c - ly * s;
        wy = CenterY + lx * s + ly * c;
    }

    /// <summary>同步箭头重力方向与 Rotation(0°=向下)。</summary>
    public void SyncArrowDirFromRotation()
    {
        if (Type != SceneObjectType.Arrow)
            return;
        var rad = Rotation * Math.PI / 180.0;
        DirX = Math.Sin(rad);
        DirY = Math.Cos(rad);
    }

    /// <summary>由 Dir 反推 Rotation(度)。</summary>
    public void SyncRotationFromArrowDir()
    {
        if (Type != SceneObjectType.Arrow)
            return;
        Rotation = Math.Atan2(DirX, DirY) * 180.0 / Math.PI;
    }

    /// <summary>点是否落在旋转后的矩形内(局部 OBB)。</summary>
    public bool ContainsWorldPoint(double wx, double wy)
    {
        WorldToLocal(wx, wy, out var lx, out var ly);
        return Math.Abs(lx) <= W / 2 && Math.Abs(ly) <= H / 2;
    }
}

/// <summary>运行时小球;位置/速度仅存内存。</summary>
public sealed class Ball
{
    public required string Id { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Vx { get; set; }
    public double Vy { get; set; }
    public string Color { get; set; } = "#3B82F6";
    public double Weight { get; set; } = 1;
    public double Size { get; set; } = 12; // 半径

    /// <summary>倍率/标号(v1.6);整型,显示可用 K/M/G/T。</summary>
    public long Multiplier { get; set; } = 1;

    /// <summary>仅右世界使用的瞬态投射物元数据；不写入旧场景格式。</summary>
    public ProjectileState? Projectile { get; set; }

    public Dictionary<string, string> Props { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>运动尾迹(环形,世界坐标);不入库。</summary>
    public Queue<(double X, double Y)> Trail { get; } = new();

    /// <summary>传送残影:从销毁点到生成点,剩余寿命(秒)。</summary>
    public double TeleportFlashT { get; set; }
    public double TeleportFromX { get; set; }
    public double TeleportFromY { get; set; }
    public double TeleportToX { get; set; }
    public double TeleportToY { get; set; }

    public void PushTrail(double x, double y, int maxLen)
    {
        Trail.Enqueue((x, y));
        while (Trail.Count > maxLen)
            Trail.Dequeue();
    }

    public void ClearTrail()
    {
        Trail.Clear();
        TeleportFlashT = 0;
    }
}

public enum ProjectileRole
{
    Unknown,
    Other,
    SmallShot,
    Shell,
    Ember,
}

public sealed class ProjectileState
{
    public required string OwnerFactionId { get; init; }
    public required string WeaponName { get; init; }
    public double Damage { get; init; }
    public double AgeSeconds { get; set; }

    /// <summary>领地模式:剩余可占领格数(0=未初始化,由运行时按伤害推导)。</summary>
    public int CapturesLeft { get; set; }

    /// <summary>弹体身份与积分正交；升格小球即使 CapturesLeft&gt;1 仍是 SmallShot。</summary>
    public ProjectileRole Role { get; set; }

    /// <summary>由小球池打包产生；用于累计回收审计，不参与身份判断。</summary>
    public bool IsPromotedSmall { get; set; }

    /// <summary>友军小球/大球积分传递的小数预算；无目标时不积累整点。</summary>
    public double FriendlySmallCarry { get; set; }
    public double FriendlyShellCarry { get; set; }
}

/// <summary>属性补丁(生成器/销毁器)。</summary>
public sealed class PropertyPatch
{
    public Dictionary<string, string>? Set { get; set; }
    public Dictionary<string, double>? Add { get; set; }
    public List<string>? Unset { get; set; }
}
