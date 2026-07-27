using WBall.Model;

namespace WBall.Game;

/// <summary>v2.10:弹药 — 左侧结算的一颗球兑换的一发炮弹(数值=占领格数)。</summary>
public readonly record struct AmmoShell(long Value, string WeaponName);

/// <summary>阵营 / 裁判表运行时(v1.6)。</summary>
public sealed class Faction
{
    /// <summary>v2.10:弹药队列(领地模式火力来源;上限由结算方控制)。</summary>
    public Queue<AmmoShell> Ammo { get; } = new();

    /// <summary>v3.2:队列积分增量值，避免 HUD 每帧遍历长队列。</summary>
    public long QueuedAmmoValue { get; private set; }

    /// <summary>v3.2:防 OOM 硬顶只告警一次。</summary>
    public bool AmmoGuardWarned { get; set; }

    public void EnqueueAmmo(AmmoShell shell)
    {
        Ammo.Enqueue(shell);
        QueuedAmmoValue = SaturatingAdd(QueuedAmmoValue, Math.Max(1, shell.Value));
    }

    public bool TryDequeueAmmo(out AmmoShell shell)
    {
        if (!Ammo.TryDequeue(out shell))
            return false;
        QueuedAmmoValue = Math.Max(0, QueuedAmmoValue - Math.Max(1, shell.Value));
        return true;
    }

    public void ClearAmmo()
    {
        Ammo.Clear();
        QueuedAmmoValue = 0;
        AmmoGuardWarned = false;
    }

    /// <summary>v2.11:小球弹药库 — 小球/齐射/直射三槽共池的数值池。</summary>
    public long SmallAmmo { get; set; }

    /// <summary>v2.11:小球出球模式(小球/齐射/直射),跟随最后一次落槽。</summary>
    public string SmallMode { get; set; } = "小球";

    /// <summary>v2.11:直射模式炮管定格剩余秒数。</summary>
    public double BarrelFreezeRemaining { get; set; }

    /// <summary>v2.12:待发的齐射环数(瞬发事件队列,封顶 8)。</summary>
    public int VolleyPending { get; set; }

    /// <summary>v2.12:连续发射的分数弹积累器(速率×dt)。</summary>
    public double SmallFireCarry { get; set; }

    public required string Id { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#3B82F6";
    public int InitialBalls { get; set; } = 3;
    public long InitialMultiplier { get; set; } = 1;
    public long Score { get; set; }
    public double Hp { get; set; } = 20_000_000;
    public double MaxHp { get; set; } = 20_000_000;
    public double Shield { get; set; }
    public double MaxShield { get; set; } = 5_000_000;
    public bool Alive { get; set; } = true;
    public int Quadrant { get; set; }
    public long Points { get; set; }
    public double TurretX { get; set; }
    public double TurretY { get; set; }
    public double TurretRadius { get; set; } = 38;
    public double BarrelAngleDeg { get; set; }
    public double BarrelRpm { get; set; } = 6;
    public FirepowerState Firepower { get; set; } = new();

    private static long SaturatingAdd(long left, long right)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }
}

public sealed class FirepowerState
{
    public double ProjectileSize { get; set; } = 8;
    public int ProjectileCount { get; set; } = 1;
    public double FireIntervalSec { get; set; } = 1.2;
    public double ShieldGain { get; set; } = 1;
    public double DamageMultiplier { get; set; } = 1;
    public double SpreadBonus { get; set; }
    public Dictionary<string, double> Intensities { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class FactionBoard
{
    public const string UnassignedId = "unassigned";

    public static Faction EnsureUnassigned(SceneWorld world)
    {
        var f = world.Factions.FirstOrDefault(x =>
            x.Id.Equals(UnassignedId, StringComparison.OrdinalIgnoreCase));
        if (f != null)
            return f;
        f = new Faction
        {
            Id = UnassignedId,
            Name = "未分配",
            Color = "#9CA3AF",
            InitialBalls = 0,
            InitialMultiplier = 1,
            Score = 0,
            Alive = false,
            Hp = 0,
            MaxHp = 0,
        };
        world.Factions.Add(f);
        return f;
    }

    public static Faction? FindByColor(SceneWorld world, string color)
    {
        var c = NormalizeColor(color);
        return world.Factions.FirstOrDefault(f =>
            NormalizeColor(f.Color).Equals(c, StringComparison.OrdinalIgnoreCase)
            && !f.Id.Equals(UnassignedId, StringComparison.OrdinalIgnoreCase));
    }

    public static void AddScore(SceneWorld world, string ballColor, long points, Action<string>? warn)
    {
        var faction = FindByColor(world, ballColor);
        if (faction == null)
        {
            faction = EnsureUnassigned(world);
            warn?.Invoke($"颜色 {ballColor} 无匹配阵营,积分计入未分配 (+{points})");
        }

        faction.Score = checked(faction.Score + Math.Max(0, points));
    }

    public static string NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
            return "#3B82F6";
        var c = color.Trim();
        if (!c.StartsWith('#'))
            c = "#" + c;
        return c.ToUpperInvariant();
    }
}
