using AppShell.Core.Logging;
using WBall.Game;
using WBall.Model;
using WBall.Sim;

namespace WBall.Battle;

public sealed record BattleEvent(double Time, string Kind, string Message, string? FactionId = null);

public sealed record FriendlyAssistSnapshot(
    int SmallShots,
    int Shells,
    int Embers,
    int Others,
    int SmallTransferred,
    int ShellTransferred,
    int Reclaimed);

public sealed record AssistTransferVisual(
    double FromX,
    double FromY,
    double ToX,
    double ToY,
    string Color,
    int Amount,
    double RemainingSeconds);

public sealed record ProjectileValueLedger(
    long FriendlyMoved,
    long FriendlyPromotedSmallReclaimed,
    long EnemyGround,
    long TerritorySpent,
    long ShieldSpent);

public sealed record FactionCombatValue(
    string FactionId,
    string FactionName,
    bool TurretAlive,
    long EconomyBalls,
    long SmallAmmo,
    long QueuedAmmo,
    long Projectiles,
    long PendingAbsorption)
{
    public long Total => SaturatingSum(EconomyBalls, SmallAmmo, QueuedAmmo, Projectiles, PendingAbsorption);

    private static long SaturatingSum(params long[] values)
    {
        long total = 0;
        foreach (var value in values)
        {
            if (value <= 0)
                continue;
            if (long.MaxValue - total < value)
                return long.MaxValue;
            total += value;
        }
        return total;
    }
}

/// <summary>命中标记(纯渲染消费:伤害飘字);同炮台短窗口内合并累计。</summary>
public sealed class HitMarker
{
    public required string TurretId { get; init; }
    public double Time { get; set; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Damage { get; set; }
}

/// <summary>右世界炮台、投射物、命中与存亡的运行时权威。</summary>
public sealed class BattleRuntime
{
    private readonly SceneWorld _economyWorld;
    private readonly SceneWorld _battleWorld;
    private readonly BattleConfigStore _config;
    private readonly BalanceConfigStore _balance;
    private readonly WeaponCatalog _weapons;
    private readonly IShellLog _log;
    private readonly Dictionary<string, double> _fireCooldown = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<BattleEvent> _recentEvents = [];
    private readonly List<HitMarker> _hitMarkers = [];
    private readonly List<AssistTransferVisual> _assistVisuals = [];
    private readonly List<Faction> _turrets = [];
    private readonly Dictionary<(int Col, int Row), List<Ball>> _duelBuckets = [];
    private readonly List<List<Ball>> _duelBucketPool = [];
    private readonly Dictionary<Ball, AssistAssignment> _assistAssignments = [];
    private readonly Dictionary<(Ball Receiver, bool Small), AssistGroup> _assistGroups = [];
    private readonly List<AssistGroup> _assistGroupPool = [];
    private readonly List<AssistGroup> _orderedAssistGroups = [];
    private readonly List<DuelPair> _enemyPairs = [];
    private readonly HashSet<Ball> _deadBalls = [];
    private readonly List<Faction> _activeTurrets = [];
    private readonly Dictionary<string, double> _eliminationTimes = new(StringComparer.OrdinalIgnoreCase);
    private Func<WeaponDefinition, bool>? _isUnlocked;
    private string[] _territoryIds = [];
    private int[] _territory = [];
    private int[] _territoryOwned = [];
    private double _cellSize = 20;
    private int _assistWindowSecond;
    private int _assistSmallTransferred;
    private int _assistShellTransferred;
    private int _assistReclaimed;
    private long _friendlyMovedTotal;
    private long _friendlyPromotedSmallReclaimedTotal;
    private long _enemyGroundTotal;
    private long _territorySpentTotal;
    private long _shieldSpentTotal;

    public BattleRuntime(
        SceneWorld economyWorld,
        SceneWorld battleWorld,
        BattleConfigStore config,
        WeaponCatalog weapons,
        IShellLog log,
        BalanceConfigStore? balance = null)
    {
        _economyWorld = economyWorld;
        _battleWorld = battleWorld;
        _config = config;
        _balance = balance ?? BalanceConfigStore.CreateMemory(new BalanceConfig(), log);
        _weapons = weapons;
        _log = log;
        Reset(economyWorld.Seed);
    }

    public event Action<BattleEvent>? EventRaised;

    public IReadOnlyList<Faction> Turrets
    {
        get
        {
            RefreshTurretCache();
            return _turrets;
        }
    }

    public double ElapsedSeconds { get; private set; }
    public int Seed { get; private set; }
    public bool AutomaticFire { get; set; }
    public string? WinnerId { get; private set; }
    public int ProjectileCount
    {
        get
        {
            var count = 0;
            foreach (var ball in _battleWorld.Balls)
                if (ball.Projectile != null)
                    count++;
            return count;
        }
    }
    public IReadOnlyList<BattleEvent> RecentEvents => _recentEvents;
    public IReadOnlyList<HitMarker> HitMarkers => _hitMarkers;
    public IReadOnlyList<AssistTransferVisual> AssistVisuals => _assistVisuals;
    public ProjectileValueLedger ValueLedger => new(
        _friendlyMovedTotal, _friendlyPromotedSmallReclaimedTotal,
        _enemyGroundTotal, _territorySpentTotal, _shieldSpentTotal);
    public IReadOnlyDictionary<string, double> EliminationTimes => _eliminationTimes;

    public FriendlyAssistSnapshot FriendlyAssistStatus()
    {
        var smallShots = 0;
        var shells = 0;
        var embers = 0;
        var others = 0;
        foreach (var ball in _battleWorld.Balls)
        {
            if (ball.Projectile == null)
                continue;
            switch (RoleOf(ball.Projectile))
            {
                case ProjectileRole.SmallShot:
                    smallShots++;
                    break;
                case ProjectileRole.Shell:
                    shells++;
                    break;
                case ProjectileRole.Ember:
                    embers++;
                    break;
                default:
                    others++;
                    break;
            }
        }
        return new FriendlyAssistSnapshot(
            smallShots,
            shells,
            embers,
            others,
            _assistSmallTransferred,
            _assistShellTransferred,
            _assistReclaimed);
    }

    /// <summary>V3.6 权威可战价值账本；只统计真实积分载体，不用 Score/Points 代替。</summary>
    public IReadOnlyList<FactionCombatValue> RemainingCombatValues()
    {
        var turrets = Turrets;
        var economy = new long[turrets.Count];
        var projectiles = new long[turrets.Count];
        var pending = new long[turrets.Count];
        var byColor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < turrets.Count; index++)
        {
            byColor[FactionBoard.NormalizeColor(turrets[index].Color)] = index;
            byId[turrets[index].Id] = index;
        }

        foreach (var ball in _economyWorld.Balls)
        {
            if (byColor.TryGetValue(FactionBoard.NormalizeColor(ball.Color), out var index))
                economy[index] = SaturatingAdd(economy[index], Math.Max(1, ball.Multiplier));
        }
        foreach (var ball in _battleWorld.Balls)
        {
            if (ball.Projectile is not { } projectile
                || !byId.TryGetValue(projectile.OwnerFactionId, out var index))
                continue;
            projectiles[index] = SaturatingAdd(projectiles[index], Math.Max(0, projectile.CapturesLeft));
            pending[index] = SaturatingAdd(pending[index], Math.Max(0, projectile.FriendlyPendingSmallValue));
        }

        var result = new FactionCombatValue[turrets.Count];
        for (var index = 0; index < turrets.Count; index++)
        {
            var turret = turrets[index];
            result[index] = new FactionCombatValue(
                turret.Id,
                turret.Name,
                turret.Alive,
                economy[index],
                Math.Max(0, turret.SmallAmmo),
                Math.Max(0, turret.QueuedAmmoValue),
                projectiles[index],
                pending[index]);
        }
        return result;
    }

    /// <summary>v2.9 领地战:mode=direct 时回退直击旧语义。</summary>
    public bool TerritoryMode =>
        !string.Equals(_config.Arena.Mode?.Trim(), "direct", StringComparison.OrdinalIgnoreCase);

    public int TerritoryCols { get; private set; }
    public int TerritoryRows { get; private set; }
    public double TerritoryCellSize => _cellSize;
    public int TerritoryVersion { get; private set; }

    /// <summary>领地归属(index=_territoryIds 下标,-1=中立);渲染只读。</summary>
    public int[] TerritoryOwners => _territory;

    public IReadOnlyList<string> TerritoryFactionIds => _territoryIds;

    /// <summary>v2.12.4 TK-07:决胜时刻已到 — 护盾只降不升。</summary>
    public bool SuddenDeath =>
        TerritoryMode && ElapsedSeconds >= Math.Max(0, _config.Arena.SuddenDeathAtSeconds);

    /// <summary>v3.1:护罩环半径倍率 — 判定与渲染共读(单一真相)。</summary>
    public double ShieldRingScale => _config.Arena.ShieldRingScale;

    /// <summary>v3.1:护盾计价(一点弹体积分 = 多少护盾)。</summary>
    public double ShieldCostPerValue => _config.Arena.ShieldCostPerValue;

    public double ShieldValueOf(Faction turret) =>
        _config.Arena.ShieldCostPerValue <= 0
            ? 0
            : Math.Max(0, turret.Shield / _config.Arena.ShieldCostPerValue);

    /// <summary>v3.1 Q4:弹体积分数字的字号/暗淡参数(渲染读)。</summary>
    public (double Factor, double Min, double Max, double OutsideOpacity) ShellLabelStyle => (
        _config.Arena.ShellLabelFontFactor,
        _config.Arena.ShellLabelFontMin,
        _config.Arena.ShellLabelFontMax,
        _config.Arena.ShellLabelOutsideOpacity);

    /// <summary>v3.1:弹体尺寸映射 — 出膛/研磨/余烬三处共用 ArenaFormulas 同一公式。</summary>
    public double ShellSizeFor(double value) =>
        ArenaFormulas.ShellSize(_config.Arena, _cellSize, value);

    /// <summary>v3.1:弹体质量映射(动量 = 质量 × 速度)。</summary>
    public double ShellWeightFor(double value) =>
        ArenaFormulas.ShellWeight(_config.Arena, value);

    /// <summary>v3.1:大球出膛速度映射;jitter01 ∈ [0,1) 由确定性随机源给出。</summary>
    public double ShellSpeedFor(double weaponSpeed, double value, double jitter01) =>
        ArenaFormulas.ShellSpeed(_config.Arena, weaponSpeed, value, jitter01);

    /// <summary>v3.1:当前配置的派生值(设置窗与 arena.config 共用)。</summary>
    public ArenaMetrics Metrics(WeaponCatalog? weapons = null) =>
        ArenaMetrics.Compute(_config.Arena, _config.Turrets, weapons ?? _weapons);

    /// <summary>v2.12.3 NB-02:总弹药 = 小球池 + 大球队列数值和。</summary>
    public long AmmoTotalOf(Faction turret) =>
        SaturatingAdd(turret.SmallAmmo, turret.QueuedAmmoValue);

    /// <summary>v3.2:当前小球池对应的升格弹积分。</summary>
    public int SmallPackValue(long ammo)
    {
        var config = _balance.Current;
        if (config.SmallPackThreshold <= 0 || ammo < config.SmallPackThreshold)
            return 1;
        var ratio = Math.Max(2, config.SmallPackRatio);
        var level = 1 + (int)Math.Floor(Math.Log(
            Math.Max(1, ammo / (double)config.SmallPackThreshold), ratio));
        var value = Math.Pow(ratio, Math.Max(1, level));
        return (int)Math.Clamp(value, 2, config.SmallPackMax);
    }

    public int TerritoryChecksum()
    {
        var hash = 2166136261u;
        foreach (var owner in _territory)
            hash = (hash ^ (uint)(owner + 2)) * 16777619u;
        return unchecked((int)hash);
    }

    public void SetUnlockPredicate(Func<WeaponDefinition, bool>? predicate) => _isUnlocked = predicate;

    public void ReloadConfiguration()
    {
        _config.Reload();
        Reset(Seed);
    }

    public void Reset(int seed, bool preserveFactionSetup = false)
    {
        var factionSetup = preserveFactionSetup
            ? _economyWorld.Factions.ToDictionary(
                x => x.Id,
                x => new FactionSetup(x.Name, x.Color, x.InitialBalls, x.InitialMultiplier),
                StringComparer.OrdinalIgnoreCase)
            : null;
        Seed = seed;
        ElapsedSeconds = 0;
        WinnerId = null;
        _eliminationTimes.Clear();
        AutomaticFire = false;
        _fireCooldown.Clear();
        _recentEvents.Clear();
        _hitMarkers.Clear();
        _assistWindowSecond = 0;
        _assistSmallTransferred = 0;
        _assistShellTransferred = 0;
        _assistReclaimed = 0;
        _assistVisuals.Clear();
        _friendlyMovedTotal = 0;
        _friendlyPromotedSmallReclaimedTotal = 0;
        _enemyGroundTotal = 0;
        _territorySpentTotal = 0;
        _shieldSpentTotal = 0;
        _battleWorld.ResetSimulation();
        _battleWorld.SetWorldSize(_config.Arena.Width, _config.Arena.Height, markDirty: false);
        _battleWorld.GravityG = _config.Arena.GravityG;
        _battleWorld.BallCollisionEnabled = _config.Arena.BallCollision;
        _battleWorld.WallRestitution = _balance.Current.WallRestitution;
        _battleWorld.BallRestitution = _balance.Current.BallRestitution;
        _battleWorld.Seed = seed;

        _economyWorld.Factions.Clear();
        _turrets.Clear();
        foreach (var definition in _config.Turrets)
        {
            var turret = CreateTurret(definition);
            if (factionSetup?.TryGetValue(turret.Id, out var setup) == true)
            {
                turret.Name = setup.Name;
                turret.Color = setup.Color;
                turret.InitialBalls = setup.InitialBalls;
                turret.InitialMultiplier = setup.InitialMultiplier;
            }
            PlaceTurret(turret);
            _economyWorld.Factions.Add(turret);
            _turrets.Add(turret);
            _fireCooldown[turret.Id] = 0;
            // v2.10 AM-04:开局预载,避免弹药未产出前冷场;v3.1:发数/数值/武器可配
            if (TerritoryMode)
            {
                var preloadValue = Math.Max(1, _config.Arena.InitialShellValue);
                var preloadWeapon = string.IsNullOrWhiteSpace(_config.Arena.InitialShellWeapon)
                    ? "直射"
                    : _config.Arena.InitialShellWeapon.Trim();
                for (var i = 0; i < Math.Max(0, _config.Arena.InitialShellCount); i++)
                    turret.EnqueueAmmo(new AmmoShell(preloadValue, preloadWeapon));
            }
        }
        _economyWorld.Seed = seed;
        InitTerritory();
        _economyWorld.NotifyChanged(markDirty: false);
        _battleWorld.NotifyChanged(markDirty: false);
        Raise("reset", $"战场已按种子 {seed} 重置");
    }

    private sealed record FactionSetup(
        string Name,
        string Color,
        int InitialBalls,
        long InitialMultiplier);

    /// <summary>v2.9 TE-01:按象限均分领地;领地模式下 HP=拥有格数。</summary>
    private void InitTerritory()
    {
        var turrets = Turrets;
        _cellSize = ArenaFormulas.CellSize(_config.Arena);
        TerritoryCols = Math.Max(1, (int)Math.Ceiling(_battleWorld.WorldWidth / _cellSize));
        TerritoryRows = Math.Max(1, (int)Math.Ceiling(_battleWorld.WorldHeight / _cellSize));
        _territory = new int[TerritoryCols * TerritoryRows];
        _territoryIds = turrets.Select(x => x.Id).ToArray();
        _territoryOwned = new int[_territoryIds.Length];

        var cx = _battleWorld.WorldWidth / 2;
        var cy = _battleWorld.WorldHeight / 2;
        for (var row = 0; row < TerritoryRows; row++)
        {
            for (var col = 0; col < TerritoryCols; col++)
            {
                var x = (col + 0.5) * _cellSize;
                var y = (row + 0.5) * _cellSize;
                var quadrant = x >= cx ? (y < cy ? 1 : 4) : (y < cy ? 2 : 3);
                var owner = -1;
                for (var i = 0; i < turrets.Count; i++)
                {
                    if (turrets[i].Quadrant == quadrant)
                    {
                        owner = i;
                        break;
                    }
                }
                _territory[row * TerritoryCols + col] = owner;
                if (owner >= 0)
                    _territoryOwned[owner]++;
            }
        }

        if (TerritoryMode)
        {
            for (var i = 0; i < turrets.Count; i++)
            {
                turrets[i].MaxHp = Math.Max(1, _territoryOwned[i]);
                turrets[i].Hp = _territoryOwned[i];
            }
        }
        TerritoryVersion++;
    }

    private int FactionIndexOf(string factionId)
    {
        for (var i = 0; i < _territoryIds.Length; i++)
        {
            if (_territoryIds[i].Equals(factionId, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    public void Step(double dt)
    {
        if (dt <= 0)
            return;
        dt = Math.Min(dt, 0.1);
        for (var i = _assistVisuals.Count - 1; i >= 0; i--)
        {
            var remaining = _assistVisuals[i].RemainingSeconds - dt;
            if (remaining <= 0)
                _assistVisuals.RemoveAt(i);
            else
                _assistVisuals[i] = _assistVisuals[i] with { RemainingSeconds = remaining };
        }
        ElapsedSeconds += dt;
        var assistSecond = (int)Math.Floor(ElapsedSeconds);
        if (assistSecond != _assistWindowSecond)
        {
            _assistWindowSecond = assistSecond;
            _assistSmallTransferred = 0;
            _assistShellTransferred = 0;
            _assistReclaimed = 0;
        }

        var turrets = Turrets;
        foreach (var turret in turrets)
        {
            if (!turret.Alive)
                continue;
            // v2.11 SA-05:直射模式炮管定格
            if (turret.BarrelFreezeRemaining > 0)
            {
                turret.BarrelFreezeRemaining = Math.Max(0, turret.BarrelFreezeRemaining - dt);
                continue;
            }
            turret.BarrelAngleDeg = (turret.BarrelAngleDeg + turret.BarrelRpm * 6.0 * dt) % 360;
        }

        if (AutomaticFire)
        {
            foreach (var turret in turrets)
            {
                if (!turret.Alive)
                    continue;
                _fireCooldown[turret.Id] -= dt;
                if (_fireCooldown[turret.Id] <= 0)
                {
                    Fire(turret.Id);
                    // v2.12.3 NB-03:大球出膛间隔随队列长度缩短(弹药越多打得越快)
                    var balance = _balance.Current;
                    var interval = TerritoryMode
                        ? Math.Max(
                            balance.ShellIntervalFloorSec,
                            turret.Firepower.FireIntervalSec
                            / (1 + turret.Ammo.Count * balance.ShellIntervalAmmoFactor))
                        : Math.Max(0.05, turret.Firepower.FireIntervalSec);
                    _fireCooldown[turret.Id] = interval;
                }

                // v2.12 CF-01:小球按速率连续流,不再一拍一簇
                if (TerritoryMode)
                    StepSmallFire(turret, dt);
            }
        }

        PhysicsEngine.Step(_battleWorld, dt, message => _log.Warn("arena", message));
        if (TerritoryMode)
            ResolveBallDuels(dt);
        ResolveProjectiles(dt);
        _battleWorld.NotifyChanged(markDirty: false, visual: true, project: false);
        RegenerateShields(dt);
        CheckWinner();
    }

    public int Fire(string turretId, string? weaponName = null)
    {
        var turret = FindRequired(turretId);
        if (!turret.Alive)
            return 0;

        var mode = _config.Arena.Targeting?.Trim().ToLowerInvariant() ?? "spin";
        Faction? target = null;
        double direction;
        if (mode == "spin")
        {
            if (!Turrets.Any(x => x.Alive && !x.Id.Equals(turret.Id, StringComparison.OrdinalIgnoreCase)))
                return 0;
            direction = turret.BarrelAngleDeg * Math.PI / 180;
        }
        else
        {
            target = SelectTarget(turret);
            if (target == null)
                return 0;
            direction = Math.Atan2(target.TurretY - turret.TurretY, target.TurretX - turret.TurretX);
        }

        if (TerritoryMode)
            return FireShell(turret, direction);

        var weapon = ResolveFireWeapon(turret, weaponName);
        var intensity = turret.Firepower.Intensities.GetValueOrDefault(weapon.Name);
        var intensityBoost = 1 + Math.Sqrt(Math.Max(0, intensity)) * 0.05 * weapon.EconomyScale;

        var count = Math.Clamp(
            turret.Firepower.ProjectileCount + Math.Max(0, weapon.BaseCount - 1),
            1,
            200);
        if (weapon.Kind is WeaponKind.Burst or WeaponKind.Split)
            count = Math.Clamp(count + weapon.BaseCount, 1, 200);

        var size = Math.Clamp(turret.Firepower.ProjectileSize, 2, 60);
        if (weapon.Kind == WeaponKind.Size)
            size = Math.Clamp(size * 1.15, 2, 60);

        var spread = Math.Clamp(
            weapon.SpreadDegrees + turret.Firepower.SpreadBonus + (count == 1 ? 0 : Math.Sqrt(count)),
            0,
            90);
        if (weapon.Kind == WeaponKind.Pierce)
            spread = Math.Min(spread, 2);

        var speed = Math.Clamp(weapon.Speed * (weapon.Kind == WeaponKind.Gravity ? 0.7 : 1), 80, 1200);
        var damage = Math.Max(
            1,
            size * 50_000 * weapon.DamageCoefficient * turret.Firepower.DamageMultiplier * intensityBoost);

        for (var index = 0; index < count; index++)
        {
            var offset = count == 1
                ? 0
                : (index / (double)(count - 1) - 0.5) * spread * Math.PI / 180;
            offset += (_battleWorld.Rng.NextDouble() - 0.5) * 0.01;
            var angle = direction + offset;
            var startDistance = turret.TurretRadius + size + 3;
            _battleWorld.Balls.Add(new Ball
            {
                Id = _battleWorld.NextBallId(),
                X = turret.TurretX + Math.Cos(angle) * startDistance,
                Y = turret.TurretY + Math.Sin(angle) * startDistance,
                Vx = Math.Cos(angle) * speed,
                Vy = Math.Sin(angle) * speed,
                Color = turret.Color,
                Size = size,
                Weight = Math.Max(1, size * size / 16),
                Projectile = new ProjectileState
                {
                    OwnerFactionId = turret.Id,
                    WeaponName = weapon.Name,
                    Damage = damage,
                    Role = ProjectileRole.Other,
                },
            });
        }

        EnforceProjectileLimit();
        Raise(
            "fire",
            target == null
                ? $"{turret.Name} 用 {weapon.Name} 旋转开火 x{count}"
                : $"{turret.Name} 用 {weapon.Name} 向 {target.Name} 开火 x{count}",
            turret.Id);
        return count;
    }

    /// <summary>v2.10 AM-02/03:领地模式开火 — 出队一发,大小与占领预算由结算数值决定(一数值一格)。</summary>
    private int FireShell(Faction turret, double direction)
    {
        if (turret.Ammo.Count == 0)
            return 0;

        if (!turret.TryDequeueAmmo(out var shell))
            return 0;
        if (!_weapons.TryResolve(shell.WeaponName, out var weapon) || !weapon.Enabled)
            weapon = ResolveFireWeapon(turret, null);

        var value = Math.Max(1, shell.Value);
        var size = ShellSizeFor(value);
        // v2.11 MO-02:发射动量适度随机(默认 ±25%),重弹略慢;v3.1:映射参数可配
        var speed = ShellSpeedFor(weapon.Speed, value, _battleWorld.Rng.NextDouble());
        var angle = direction + (_battleWorld.Rng.NextDouble() - 0.5) * weapon.SpreadDegrees * Math.PI / 180;
        var startDistance = turret.TurretRadius + size + 3;
        _battleWorld.Balls.Add(new Ball
        {
            Id = _battleWorld.NextBallId(),
            X = turret.TurretX + Math.Cos(angle) * startDistance,
            Y = turret.TurretY + Math.Sin(angle) * startDistance,
            Vx = Math.Cos(angle) * speed,
            Vy = Math.Sin(angle) * speed,
            Color = turret.Color,
            Size = size,
            // v2.11 MO-01:质量=数值,动量碰撞下大弹推小弹
            Weight = ShellWeightFor(value),
            Projectile = new ProjectileState
            {
                OwnerFactionId = turret.Id,
                WeaponName = weapon.Name,
                Damage = value,
                CapturesLeft = (int)Math.Clamp(value, 1, 100_000),
                Role = ProjectileRole.Shell,
            },
        });
        EnforceProjectileLimit();
        return 1;
    }

    /// <summary>v2.12 ST-01~04:小球状态机 — 齐射瞬发环射,直射定格密射,默认连射。</summary>
    private void StepSmallFire(Faction turret, double dt)
    {
        if (!turret.Alive)
            return;

        if (turret.VolleyPending > 0)
        {
            turret.VolleyPending--;
            FireVolleyRing(turret);
        }

        if (turret.SmallAmmo <= 0)
        {
            turret.SmallFireCarry = 0;
            return;
        }

        // v2.12.3 NB-03:小球射速随池值加猛,弹药囤积可被打空
        var frozen = turret.BarrelFreezeRemaining > 0;
        var config = _balance.Current;
        var rate = Math.Min(config.SmallRateMax, config.SmallRateBase + turret.SmallAmmo * config.SmallRatePerAmmo);
        if (frozen)
            rate = Math.Min(config.SmallRateFrozenMax, rate * config.SmallRateFrozenFactor);
        turret.SmallFireCarry += rate * dt;
        while (turret.SmallFireCarry >= 1 && turret.SmallAmmo > 0)
        {
            turret.SmallFireCarry -= 1;
            var spreadDeg = frozen ? config.SmallSpreadFrozenDeg : config.SmallSpreadDeg;
            var angle = turret.BarrelAngleDeg * Math.PI / 180
                + (_battleWorld.Rng.NextDouble() - 0.5) * spreadDeg * Math.PI / 180;
            var pack = Math.Min(SmallPackValue(turret.SmallAmmo), (int)Math.Min(int.MaxValue, turret.SmallAmmo));
            SpawnSmallBall(turret, angle, pack);
            turret.SmallAmmo -= pack;
        }
        EnforceProjectileLimit();
    }

    /// <summary>v2.12 ST-03:齐射瞬发 — 360° 均匀环射一圈后回到连射态。</summary>
    private void FireVolleyRing(Faction turret)
    {
        var count = (int)Math.Min(_balance.Current.VolleyRingCount, turret.SmallAmmo);
        if (count <= 0)
            return;
        var phase = _battleWorld.Rng.NextDouble() * Math.PI * 2;
        for (var index = 0; index < count; index++)
            SpawnSmallBall(turret, phase + index * Math.PI * 2 / count, 1);
        turret.SmallAmmo -= count;
        EnforceProjectileLimit();
    }

    private void SpawnSmallBall(Faction turret, double angle, int value)
    {
        var packed = Math.Max(1, value);
        var size = packed > 1
            ? ShellSizeFor(packed)
            : ArenaFormulas.SmallBallSize(_config.Arena, _cellSize);
        var speed = packed > 1 && !_balance.Current.SmallPackSpeedFollowsSmall
            ? ShellSpeedFor(_config.Arena.SmallBallSpeed, packed, 0.5)
            : ArenaFormulas.SmallBallSpeed(_config.Arena);
        var startDistance = turret.TurretRadius + size + 3;
        _battleWorld.Balls.Add(new Ball
        {
            Id = _battleWorld.NextBallId(),
            X = turret.TurretX + Math.Cos(angle) * startDistance,
            Y = turret.TurretY + Math.Sin(angle) * startDistance,
            Vx = Math.Cos(angle) * speed,
            Vy = Math.Sin(angle) * speed,
            Color = turret.Color,
            Size = size,
            Weight = packed > 1 ? ShellWeightFor(packed) : 1,
            Projectile = new ProjectileState
            {
                OwnerFactionId = turret.Id,
                WeaponName = "小球",
                Damage = packed,
                CapturesLeft = packed,
                Role = ProjectileRole.SmallShot,
                IsPromotedSmall = packed > 1,
            },
        });
    }

    public void Hit(string turretId, double damage)
    {
        var turret = FindRequired(turretId);
        if (!turret.Alive || damage <= 0)
            return;

        var absorbed = Math.Min(turret.Shield, damage);
        turret.Shield -= absorbed;
        var hpDamage = damage - absorbed;
        turret.Hp = Math.Max(0, turret.Hp - hpDamage);

        var merged = _hitMarkers.LastOrDefault(m =>
            m.TurretId.Equals(turret.Id, StringComparison.OrdinalIgnoreCase)
            && ElapsedSeconds - m.Time < 0.15);
        if (merged != null)
        {
            merged.Damage += damage;
            merged.Time = ElapsedSeconds;
        }
        else
        {
            _hitMarkers.Add(new HitMarker
            {
                TurretId = turret.Id,
                Time = ElapsedSeconds,
                X = turret.TurretX,
                Y = turret.TurretY,
                Damage = damage,
            });
            if (_hitMarkers.Count > 64)
                _hitMarkers.RemoveRange(0, _hitMarkers.Count - 64);
        }
        Raise("hit", $"{turret.Name} 受击 {damage:0};护盾吸收 {absorbed:0}", turret.Id);
        if (turret.Hp <= 0)
            Kill(turretId);
        _economyWorld.NotifyChanged(markDirty: false);
    }

    public void Kill(string turretId)
    {
        var turret = FindRequired(turretId);
        if (!turret.Alive)
            return;
        turret.Alive = false;
        turret.Points = 0;
        turret.Score = 0;
        if (TerritoryMode)
        {
            // v2.12.4 TK-03/04:余烬爆发 — 经济球+弹药库化为大球继续作战
            DeathBurst(turret);
            Raise("kill", $"{turret.Name} 炮台被摧毁,余烬升空", turret.Id);
        }
        else
        {
            turret.Hp = 0;
            _battleWorld.Balls.RemoveAll(x =>
                x.Projectile?.OwnerFactionId.Equals(turret.Id, StringComparison.OrdinalIgnoreCase) == true);
            Raise("kill", $"{turret.Name} 已被消灭", turret.Id);
        }
        _economyWorld.NotifyChanged(markDirty: false);
        CheckWinner();
    }

    /// <summary>v2.12.4 TK-03:左侧同色经济球(带倍率)与弹药库存货统统化作大球,360° 射出。</summary>
    private void DeathBurst(Faction turret)
    {
        var config = _balance.Current;
        var payloads = new List<(long Value, string Weapon)>();
        while (turret.TryDequeueAmmo(out var shell))
        {
            if (config.EmberFromAmmo)
                payloads.Add((shell.Value, shell.WeaponName));
        }
        if (turret.SmallAmmo > 0)
        {
            if (config.EmberFromAmmo)
                payloads.Add((turret.SmallAmmo, "小球"));
            turret.SmallAmmo = 0;
        }
        if (config.EmberDrainEconomy)
        {
            var color = FactionBoard.NormalizeColor(turret.Color);
            for (var index = _economyWorld.Balls.Count - 1; index >= 0; index--)
            {
                var economyBall = _economyWorld.Balls[index];
                if (!FactionBoard.NormalizeColor(economyBall.Color)
                        .Equals(color, StringComparison.OrdinalIgnoreCase))
                    continue;
                payloads.Add((Math.Max(1, economyBall.Multiplier), "大球"));
                _economyWorld.Balls.RemoveAt(index);
            }
        }

        foreach (var (value, weapon) in payloads)
        {
            var angle = _battleWorld.Rng.NextDouble() * Math.PI * 2;
            var speed = config.EmberSpeedMin
                        + _battleWorld.Rng.NextDouble() * (config.EmberSpeedMax - config.EmberSpeedMin);
            var size = ShellSizeFor(value);
            _battleWorld.Balls.Add(new Ball
            {
                Id = _battleWorld.NextBallId(),
                X = turret.TurretX + Math.Cos(angle) * (turret.TurretRadius + size + 2),
                Y = turret.TurretY + Math.Sin(angle) * (turret.TurretRadius + size + 2),
                Vx = Math.Cos(angle) * speed,
                Vy = Math.Sin(angle) * speed,
                Color = turret.Color,
                Size = size,
                Weight = ShellWeightFor(value),
                Projectile = new ProjectileState
                {
                    OwnerFactionId = turret.Id,
                    WeaponName = weapon,
                    Damage = value,
                    CapturesLeft = (int)Math.Clamp(value, 1, 100_000),
                    Role = ProjectileRole.Ember,
                },
            });
        }
        EnforceProjectileLimit();
    }

    public Faction SetTurret(
        string id,
        double? hp = null,
        double? shield = null,
        double? size = null,
        int? count = null,
        double? interval = null,
        int? quadrant = null,
        string? color = null,
        double? rpm = null)
    {
        var turret = FindRequired(id);
        if (hp is not null)
        {
            turret.MaxHp = Math.Max(1, hp.Value);
            turret.Hp = turret.MaxHp;
            turret.Alive = true;
        }
        if (shield is not null)
        {
            turret.Shield = Math.Max(0, shield.Value);
            turret.MaxShield = Math.Max(turret.MaxShield, turret.Shield);
        }
        if (size is not null) turret.Firepower.ProjectileSize = Math.Clamp(size.Value, 2, 60);
        if (count is not null) turret.Firepower.ProjectileCount = Math.Clamp(count.Value, 1, 200);
        if (interval is not null) turret.Firepower.FireIntervalSec = Math.Clamp(interval.Value, 0.05, 60);
        if (quadrant is not null)
        {
            turret.Quadrant = Math.Clamp(quadrant.Value, 1, 4);
            PlaceTurret(turret);
        }
        if (!string.IsNullOrWhiteSpace(color)) turret.Color = FactionBoard.NormalizeColor(color);
        if (rpm is not null) turret.BarrelRpm = Math.Clamp(rpm.Value, 0.5, 60);
        _economyWorld.NotifyChanged(markDirty: false);
        return turret;
    }

    /// <summary>
    /// v3.1 turret.setall:把配置定义里的数值刷到运行时炮台(不重置战场)。
    /// 领地模式下 Hp/MaxHp = 占格数由领地系统维护,此处不动,只有 direct 模式才刷生命。
    /// </summary>
    public void SyncTurretNumbersFromConfig()
    {
        foreach (var definition in _config.Turrets)
        {
            var turret = Turrets.FirstOrDefault(x =>
                x.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase));
            if (turret == null)
                continue;

            turret.MaxShield = Math.Max(definition.MaxShield, definition.InitialShield);
            turret.Shield = Math.Max(0, definition.InitialShield);
            turret.Firepower.ProjectileSize = Math.Clamp(definition.ProjectileSize, 2, 60);
            turret.Firepower.ProjectileCount = Math.Clamp(definition.ProjectileCount, 1, 200);
            turret.Firepower.FireIntervalSec = Math.Clamp(definition.FireIntervalSec, 0.05, 60);
            turret.BarrelRpm = Math.Clamp(definition.BarrelRpm, 0.5, 60);
            if (!TerritoryMode)
            {
                turret.MaxHp = Math.Max(1, definition.MaxHp);
                turret.Hp = turret.MaxHp;
            }
        }
        _economyWorld.NotifyChanged(markDirty: false);
    }

    public Faction FindRequired(string id) =>
        Turrets.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"炮台不存在: {id}");

    public string FormatCombatLog(int limit = 40)
    {
        limit = Math.Clamp(limit, 1, 500);
        return string.Join(
            Environment.NewLine,
            _recentEvents.TakeLast(limit).Select(x =>
                $"[{x.Time:0.000}] {x.Kind} {x.Message}"));
    }

    private WeaponDefinition ResolveFireWeapon(Faction turret, string? preferredName)
    {
        if (!string.IsNullOrWhiteSpace(preferredName)
            && _weapons.TryResolve(preferredName, out var preferred)
            && preferred.Enabled
            && (_isUnlocked?.Invoke(preferred) ?? true))
        {
            return preferred;
        }

        WeaponDefinition? best = null;
        var bestScore = double.MinValue;
        foreach (var weapon in _weapons.Weapons)
        {
            if (!weapon.Enabled)
                continue;
            if (_isUnlocked != null && !_isUnlocked(weapon))
                continue;
            if (weapon.Kind is WeaponKind.Size or WeaponKind.Count or WeaponKind.Shield)
                continue;
            var intensity = turret.Firepower.Intensities.GetValueOrDefault(weapon.Name);
            var score = intensity * 10 + weapon.DamageCoefficient;
            if (score > bestScore)
            {
                bestScore = score;
                best = weapon;
            }
        }

        if (best != null)
            return best;

        if (_weapons.TryResolve("直射", out var direct))
            return direct;
        if (_weapons.TryResolve("Direct", out direct))
            return direct;
        return _weapons.Weapons.First(x => x.Enabled);
    }

    private Faction CreateTurret(TurretDefinition definition) => new()
    {
        Id = definition.Id,
        Name = definition.Name,
        Color = FactionBoard.NormalizeColor(definition.Color),
        Quadrant = Math.Clamp(definition.Quadrant, 1, 4),
        InitialBalls = Math.Max(0, definition.InitialBalls),
        InitialMultiplier = Math.Max(1, definition.InitialMultiplier),
        Hp = definition.MaxHp,
        MaxHp = definition.MaxHp,
        Shield = Math.Max(0, definition.InitialShield),
        MaxShield = Math.Max(definition.MaxShield, definition.InitialShield),
        Alive = true,
        BarrelRpm = Math.Clamp(definition.BarrelRpm, 0.5, 60),
        Firepower = new FirepowerState
        {
            ProjectileSize = definition.ProjectileSize,
            ProjectileCount = definition.ProjectileCount,
            FireIntervalSec = definition.FireIntervalSec,
        },
    };

    private void PlaceTurret(Faction turret)
    {
        // 更靠四角(参考图风格);v3.1:离角比例可配
        var marginX = _battleWorld.WorldWidth * _config.Arena.TurretMarginXRatio;
        var marginY = _battleWorld.WorldHeight * _config.Arena.TurretMarginYRatio;
        (turret.TurretX, turret.TurretY) = turret.Quadrant switch
        {
            1 => (_battleWorld.WorldWidth - marginX, marginY),
            2 => (marginX, marginY),
            3 => (marginX, _battleWorld.WorldHeight - marginY),
            _ => (_battleWorld.WorldWidth - marginX, _battleWorld.WorldHeight - marginY),
        };
        turret.TurretRadius = _config.Arena.TurretRadius;
        var initialAngle = Math.Atan2(
            _battleWorld.WorldHeight / 2 - turret.TurretY,
            _battleWorld.WorldWidth / 2 - turret.TurretX) * 180 / Math.PI;
        turret.BarrelAngleDeg = (initialAngle + 360) % 360;
    }

    private Faction? SelectTarget(Faction source)
    {
        var enemies = Turrets
            .Where(x => x.Alive && !x.Id.Equals(source.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (enemies.Count == 0)
            return null;

        var mode = _config.Arena.Targeting?.Trim().ToLowerInvariant() ?? "highesthp";
        return mode switch
        {
            "nearest" or "closest" => enemies
                .OrderBy(x => DistanceSquared(source, x))
                .ThenBy(x => x.Id, StringComparer.Ordinal)
                .First(),
            "rotate" or "roundrobin" => enemies
                .OrderBy(x => x.Id, StringComparer.Ordinal)
                .ElementAt((int)(ElapsedSeconds / Math.Max(0.05, source.Firepower.FireIntervalSec)) % enemies.Count),
            "lowesthp" => enemies
                .OrderBy(x => x.Hp + x.Shield)
                .ThenBy(x => DistanceSquared(source, x))
                .ThenBy(x => x.Id, StringComparer.Ordinal)
                .First(),
            _ => enemies
                .OrderByDescending(x => x.Hp + x.Shield)
                .ThenBy(x => DistanceSquared(source, x))
                .ThenBy(x => x.Id, StringComparer.Ordinal)
                .First(),
        };
    }

    private void ResolveProjectiles(double dt)
    {
        for (var index = _battleWorld.Balls.Count - 1; index >= 0; index--)
        {
            // Hit→Kill 会批量移除阵亡方在飞弹,索引须重新夹紧
            if (index >= _battleWorld.Balls.Count)
            {
                index = _battleWorld.Balls.Count;
                continue;
            }
            var ball = _battleWorld.Balls[index];
            var projectile = ball.Projectile;
            if (projectile == null)
                continue;
            projectile.AgeSeconds += dt;
            // v2.12.1 BD-01:弹丸不再超时消失,只因啃尽/护罩抵消/敌弹对消而亡(direct 模式保留寿命)
            if (!TerritoryMode && projectile.AgeSeconds >= _config.Arena.ProjectileLifetimeSec)
            {
                _battleWorld.Balls.RemoveAt(index);
                continue;
            }

            if (TerritoryMode)
            {
                // v2.12 SH:先过护罩判定(拦截/抵消/反弹)
                if (TryShieldIntercept(ball, projectile))
                    continue;

                // v2.12.4 TK-01:护罩失守后,任意敌球触碰炮台本体即摧毁
                var victim = _balance.Current.ContactKillEnabled
                    ? FindContactVictim(ball, projectile)
                    : null;
                if (victim != null)
                    Kill(victim.Id);

                if (TryCaptureTerritory(ball, projectile))
                    _battleWorld.Balls.Remove(ball);
                continue;
            }

            var target = FindContactVictim(ball, projectile);
            if (target == null)
                continue;

            _battleWorld.Balls.RemoveAt(index);
            Hit(target.Id, projectile.Damage);
        }
    }

    /// <summary>v2.12.2 HD-01~06:光晕研磨对消 — 异色光晕相触逐帧同步等量抵消(速率随较大球);
    /// 自家小球碰自家大球融入。范围=1.6×半径和,保证触发。</summary>
    private void ResolveBallDuels(double dt)
    {
        var balls = _battleWorld.Balls;
        var config = _balance.Current;
        var absorptionEnabled = config.FriendlyAssistEnabled && !_battleWorld.BallCollisionEnabled;
        if (absorptionEnabled)
        {
            AccrueFriendlyAssistBudgets(balls, dt, config);
            FlushPendingFriendlySmallValues(balls);
        }
        if (balls.Count < 2)
            return;

        // 步长随光晕与当前最大弹体推导，调大光晕后仍只需查相邻桶且不会漏检。
        var maxRadius = Math.Max(
            ArenaFormulas.SmallBallSize(_config.Arena, _cellSize),
            _cellSize * _config.Arena.ShellSizeMaxCells);
        if (balls.Count >= 512 && config.FriendlyAssistEnabled)
        {
            // 高容量场景按本帧真实最大半径定桶；使用配置理论上限会把 1 万个小球
            // 挤进少数巨桶，候选数接近 O(n²)。小场景保留旧桶宽以冻结历史哈希。
            maxRadius = 1;
            foreach (var ball in balls)
                if (ball.Projectile != null)
                    maxRadius = Math.Max(maxRadius, ball.Size);
        }
        var bucketReach = Math.Max(config.HaloReachFactor, config.FriendlyAssistReachFactor);
        var step = Math.Max(1, bucketReach * 2 * maxRadius);
        var bucketPoolIndex = 0;
        foreach (var bucket in _duelBucketPool)
            bucket.Clear();
        _duelBuckets.Clear();
        foreach (var ball in balls)
        {
            if (ball.Projectile == null)
                continue;
            EnsureProjectileRole(ball.Projectile);
            var key = ((int)(ball.X / step), (int)(ball.Y / step));
            if (!_duelBuckets.TryGetValue(key, out var list))
            {
                if (bucketPoolIndex >= _duelBucketPool.Count)
                    _duelBucketPool.Add(new List<Ball>());
                list = _duelBucketPool[bucketPoolIndex++];
                _duelBuckets[key] = list;
            }
            list.Add(ball);
        }

        _deadBalls.Clear();
        HashSet<Ball>? dead = _deadBalls;
        if (absorptionEnabled)
        {
            dead = ResolveFriendlyAssists(balls, _duelBuckets, step, dt, config, out var enemyPairs);
            foreach (var pair in enemyPairs)
                ResolveEnemyDuel(pair.Left, pair.Right, dt, config, ref dead);
        }
        else
        {
            // 关闭新机制时保持 v3.2 的遍历与结算顺序，确保旧回放哈希不变。
            foreach (var ball in balls)
            {
                var mine = ball.Projectile;
                if (mine == null || dead?.Contains(ball) == true)
                    continue;
                var col = (int)(ball.X / step);
                var row = (int)(ball.Y / step);
                for (var dx = -1; dx <= 1 && mine.CapturesLeft > 0; dx++)
                {
                    for (var dy = -1; dy <= 1 && mine.CapturesLeft > 0; dy++)
                    {
                        if (!_duelBuckets.TryGetValue((col + dx, row + dy), out var list))
                            continue;
                        foreach (var other in list)
                        {
                            if (ReferenceEquals(other, ball)
                                || dead?.Contains(other) == true
                                || string.CompareOrdinal(ball.Id, other.Id) >= 0)
                                continue;
                            var theirs = other.Projectile;
                            if (theirs == null)
                                continue;

                            // HD-01:光晕(1.6×半径)相触即算接触
                            var reach = (ball.Size + other.Size) * config.HaloReachFactor;
                            if (DistanceSquared(ball.X, ball.Y, other.X, other.Y) > reach * reach)
                                continue;

                            var sameOwner = theirs.OwnerFactionId.Equals(
                                mine.OwnerFactionId, StringComparison.OrdinalIgnoreCase);
                            if (sameOwner)
                                continue;

                            ResolveEnemyDuel(ball, other, dt, config, ref dead);
                            if (dead?.Contains(ball) == true)
                                break;
                        }
                    }
                }
            }
        }

        if (dead is { Count: > 0 })
        {
            for (var i = balls.Count - 1; i >= 0; i--)
                if (dead.Contains(balls[i]))
                    balls.RemoveAt(i);
        }
    }

    private HashSet<Ball>? ResolveFriendlyAssists(
        List<Ball> balls,
        Dictionary<(int Col, int Row), List<Ball>> buckets,
        double step,
        double dt,
        BalanceConfig config,
        out List<DuelPair> enemyPairs)
    {
        _enemyPairs.Clear();
        enemyPairs = _enemyPairs;
        if (!config.FriendlyAssistEnabled)
            return null;

        _assistAssignments.Clear();
        var assignments = _assistAssignments;
        foreach (var ball in balls)
        {
            var mine = ball.Projectile;
            if (mine == null || mine.CapturesLeft <= 0)
                continue;
            var col = (int)(ball.X / step);
            var row = (int)(ball.Y / step);
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (!buckets.TryGetValue((col + dx, row + dy), out var nearby))
                        continue;
                    foreach (var other in nearby)
                    {
                        if (ReferenceEquals(ball, other) || string.CompareOrdinal(ball.Id, other.Id) >= 0)
                            continue;
                        var theirs = other.Projectile;
                        if (theirs == null || theirs.CapturesLeft <= 0)
                            continue;
                        var distance = DistanceSquared(ball.X, ball.Y, other.X, other.Y);
                        var sameOwner = mine.OwnerFactionId.Equals(
                            theirs.OwnerFactionId, StringComparison.OrdinalIgnoreCase);
                        if (!sameOwner)
                        {
                            var enemyReach = (ball.Size + other.Size) * config.HaloReachFactor;
                            if (distance <= enemyReach * enemyReach)
                                enemyPairs.Add(new DuelPair(ball, other));
                            continue;
                        }
                        var reach = (ball.Size + other.Size) * config.FriendlyAssistReachFactor;
                        if (!WasWithinReachDuringStep(ball, other, reach, dt))
                            continue;

                        var roleA = RoleOf(mine);
                        var roleB = RoleOf(theirs);
                        Ball donor;
                        Ball receiver;
                        var small = false;
                        if (roleA == ProjectileRole.SmallShot && roleB == ProjectileRole.Shell)
                        {
                            donor = ball;
                            receiver = other;
                            small = true;
                        }
                        else if (roleB == ProjectileRole.SmallShot && roleA == ProjectileRole.Shell)
                        {
                            donor = other;
                            receiver = ball;
                            small = true;
                        }
                        else if (roleA == ProjectileRole.Shell && roleB == ProjectileRole.Shell)
                        {
                            var compare = CompareAssistRank(ball, other);
                            receiver = compare >= 0 ? ball : other;
                            donor = ReferenceEquals(receiver, ball) ? other : ball;
                        }
                        else
                        {
                            continue;
                        }

                        var candidate = new AssistAssignment(donor, receiver, small, distance);
                        if (!assignments.TryGetValue(donor, out var existing)
                            || IsBetterReceiver(candidate, existing))
                        {
                            assignments[donor] = candidate;
                        }
                    }
                }
        }

        if (assignments.Count == 0)
            return _deadBalls;

        HashSet<Ball>? dead = _deadBalls;
        PrepareAssistGroups(assignments);
        foreach (var group in _orderedAssistGroups)
        {
            var receiver = group.Receiver;
            var target = receiver.Projectile!;
            if (dead?.Contains(receiver) == true || target.CapturesLeft <= 0)
                continue;
            var small = group.Small;
            group.Assignments.Sort(CompareDonorAssignments);
            if (small)
            {
                double smallVisualFromX = receiver.X;
                double smallVisualFromY = receiver.Y;
                var absorbed = 0;
                foreach (var assignment in group.Assignments)
                {
                    var donor = assignment.Donor;
                    var source = donor.Projectile!;
                    if (dead?.Contains(donor) == true || source.CapturesLeft <= 0)
                        continue;

                    var amount = Math.Min(source.CapturesLeft, int.MaxValue - target.CapturesLeft);
                    if (amount <= 0)
                        continue;
                    target.CapturesLeft += amount;
                    source.CapturesLeft -= amount;
                    absorbed += amount;
                    _friendlyMovedTotal += amount;
                    _assistSmallTransferred += amount;
                    if (source.CapturesLeft == 0)
                    {
                        dead ??= [];
                        dead.Add(donor);
                        _assistReclaimed++;
                        if (source.IsPromotedSmall)
                            _friendlyPromotedSmallReclaimedTotal++;
                    }
                    smallVisualFromX = donor.X;
                    smallVisualFromY = donor.Y;
                }

                if (absorbed > 0)
                {
                    SyncShellSize(receiver);
                    if (config.FriendlyAssistVisualEnabled)
                        AddAssistVisual(receiver, smallVisualFromX, smallVisualFromY, absorbed);
                }
                continue;
            }

            var carry = target.FriendlyShellCarry;
            var rate = config.FriendlyShellTransferRate;
            // First halo contact transfers immediately, then the receiver repays one point
            // of debt at the configured rate before it can transfer again.
            var budget = rate > 0 && carry >= -1e-12 ? 1 : 0;
            var transferred = 0;
            double visualFromX = receiver.X;
            double visualFromY = receiver.Y;
            foreach (var assignment in group.Assignments)
            {
                if (budget <= 0 || target.CapturesLeft >= config.FriendlyAssistMaxValue)
                    break;
                var donor = assignment.Donor;
                var source = donor.Projectile!;
                if (dead?.Contains(donor) == true || source.CapturesLeft <= 0)
                    continue;
                var amount = Math.Min(
                    budget,
                    Math.Min(source.CapturesLeft, config.FriendlyAssistMaxValue - target.CapturesLeft));
                if (amount <= 0)
                    continue;
                source.CapturesLeft -= amount;
                target.CapturesLeft += amount;
                budget -= amount;
                transferred += amount;
                _friendlyMovedTotal += amount;
                visualFromX = donor.X;
                visualFromY = donor.Y;
                if (source.CapturesLeft <= 0)
                {
                    target.FriendlyPendingSmallValue += source.FriendlyPendingSmallValue;
                    source.FriendlyPendingSmallValue = 0;
                    dead ??= [];
                    dead.Add(donor);
                    _assistReclaimed++;
                    if (source.IsPromotedSmall)
                        _friendlyPromotedSmallReclaimedTotal++;
                }
                else if (!small)
                {
                    SyncShellSize(donor);
                }
            }

            carry = Math.Clamp(carry - transferred, -1, 1);
            target.FriendlyShellCarry = carry;
            _assistShellTransferred += transferred;
            if (transferred > 0)
            {
                SyncShellSize(receiver);
                if (config.FriendlyAssistVisualEnabled)
                    AddAssistVisual(receiver, visualFromX, visualFromY, transferred);
            }
        }
        return dead;
    }

    private void FlushPendingFriendlySmallValues(IEnumerable<Ball> balls)
    {
        foreach (var ball in balls)
        {
            var target = ball.Projectile;
            if (target == null
                || RoleOf(target) != ProjectileRole.Shell
                || target.FriendlyPendingSmallValue <= 0)
            {
                continue;
            }

            var transferred = (int)Math.Min(
                target.FriendlyPendingSmallValue,
                int.MaxValue - (long)target.CapturesLeft);
            if (transferred <= 0)
                continue;
            target.FriendlyPendingSmallValue -= transferred;
            target.CapturesLeft += transferred;
            _friendlyMovedTotal += transferred;
            _assistSmallTransferred += transferred;
            SyncShellSize(ball);
        }
    }

    private void AddAssistVisual(Ball receiver, double fromX, double fromY, int amount)
    {
        var existingVisual = _assistVisuals.FindIndex(x =>
            Math.Abs(x.ToX - receiver.X) < 1e-6
            && Math.Abs(x.ToY - receiver.Y) < 1e-6
            && x.Color.Equals(receiver.Color, StringComparison.OrdinalIgnoreCase));
        var visual = new AssistTransferVisual(
            fromX, fromY, receiver.X, receiver.Y,
            receiver.Color, amount, 0.65);
        if (existingVisual >= 0)
        {
            var existing = _assistVisuals[existingVisual];
            _assistVisuals[existingVisual] = visual with { Amount = existing.Amount + amount };
        }
        else
        {
            _assistVisuals.Add(visual);
        }
    }

    private static bool WasWithinReachDuringStep(Ball left, Ball right, double reach, double dt)
    {
        var dx = right.X - left.X;
        var dy = right.Y - left.Y;
        var reachSquared = reach * reach;
        if (dx * dx + dy * dy <= reachSquared)
            return true;

        var relativeVx = right.Vx - left.Vx;
        var relativeVy = right.Vy - left.Vy;
        var speedSquared = relativeVx * relativeVx + relativeVy * relativeVy;
        if (speedSquared < 1e-12)
            return false;

        var startX = dx - relativeVx * dt;
        var startY = dy - relativeVy * dt;
        var closestTime = Math.Clamp(
            -(startX * relativeVx + startY * relativeVy) / speedSquared,
            0,
            dt);
        var closestX = startX + relativeVx * closestTime;
        var closestY = startY + relativeVy * closestTime;
        return closestX * closestX + closestY * closestY <= reachSquared;
    }

    private static void AccrueFriendlyAssistBudgets(
        IEnumerable<Ball> balls,
        double dt,
        BalanceConfig config)
    {
        foreach (var ball in balls)
        {
            var projectile = ball.Projectile;
            if (projectile == null || projectile.CapturesLeft <= 0 || RoleOf(projectile) != ProjectileRole.Shell)
                continue;
            projectile.FriendlyShellCarry = AccrueBudget(
                projectile.FriendlyShellCarry, config.FriendlyShellTransferRate, dt);
        }
    }

    private static double AccrueBudget(double current, double rate, double dt) =>
        rate <= 0 ? 0 : Math.Min(1, Math.Max(-1, current) + rate * dt);

    private void ResolveEnemyDuel(
        Ball ball,
        Ball other,
        double dt,
        BalanceConfig config,
        ref HashSet<Ball>? dead)
    {
        if (dead?.Contains(ball) == true || dead?.Contains(other) == true)
            return;
        var mine = ball.Projectile;
        var theirs = other.Projectile;
        if (mine == null || theirs == null || mine.CapturesLeft <= 0 || theirs.CapturesLeft <= 0)
            return;
        if (mine.OwnerFactionId.Equals(theirs.OwnerFactionId, StringComparison.OrdinalIgnoreCase))
            return;

        var v1 = Math.Max(1, mine.CapturesLeft);
        var v2 = Math.Max(1, theirs.CapturesLeft);
        var drain = (int)Math.Min(
            Math.Min(v1, v2),
            Math.Max(1, Math.Round(Math.Max(v1, v2) * config.GrindRatePerSecond * dt)));
        mine.CapturesLeft = v1 - drain;
        theirs.CapturesLeft = v2 - drain;
        _enemyGroundTotal += drain * 2L;
        SyncTransferredProjectileSize(ball);
        SyncTransferredProjectileSize(other);
        dead ??= [];
        if (theirs.CapturesLeft <= 0)
            dead.Add(other);
        if (mine.CapturesLeft <= 0)
            dead.Add(ball);
    }

    private int CompareAssistRank(Ball left, Ball right)
    {
        var value = left.Projectile!.CapturesLeft.CompareTo(right.Projectile!.CapturesLeft);
        if (value != 0)
            return value;
        var random = StableAssistScore(left.Id).CompareTo(StableAssistScore(right.Id));
        return random != 0 ? random : string.CompareOrdinal(left.Id, right.Id);
    }

    private bool IsBetterReceiver(AssistAssignment candidate, AssistAssignment existing)
    {
        var rank = CompareAssistRank(candidate.Receiver, existing.Receiver);
        if (rank != 0)
            return rank > 0;
        var distance = candidate.DistanceSquared.CompareTo(existing.DistanceSquared);
        return distance != 0
            ? distance < 0
            : string.CompareOrdinal(candidate.Receiver.Id, existing.Receiver.Id) < 0;
    }

    private uint StableAssistScore(string ballId)
    {
        var hash = 2166136261u ^ unchecked((uint)Seed);
        foreach (var c in ballId)
            hash = (hash ^ c) * 16777619u;
        return hash;
    }

    private static void EnsureProjectileRole(ProjectileState projectile)
    {
        if (projectile.Role != ProjectileRole.Unknown)
            return;
        projectile.Role = projectile.WeaponName.Trim() switch
        {
            "小球" or "SmallBall" or "齐射" or "Volley" or "直射" or "Direct" => ProjectileRole.SmallShot,
            "大球" or "BigBall" => ProjectileRole.Shell,
            _ => ProjectileRole.Other,
        };
    }

    private static ProjectileRole RoleOf(ProjectileState projectile)
    {
        EnsureProjectileRole(projectile);
        return projectile.Role;
    }

    private readonly record struct AssistAssignment(Ball Donor, Ball Receiver, bool Small, double DistanceSquared);
    private readonly record struct DuelPair(Ball Left, Ball Right);

    private sealed class AssistGroup
    {
        public Ball Receiver { get; private set; } = null!;
        public bool Small { get; private set; }
        public int EncounterOrder { get; private set; }
        public List<AssistAssignment> Assignments { get; } = [];

        public void Reset(Ball receiver, bool small, int encounterOrder)
        {
            Receiver = receiver;
            Small = small;
            EncounterOrder = encounterOrder;
            Assignments.Clear();
        }
    }

    private void PrepareAssistGroups(Dictionary<Ball, AssistAssignment> assignments)
    {
        _assistGroups.Clear();
        _orderedAssistGroups.Clear();
        var poolIndex = 0;
        foreach (var assignment in assignments.Values)
        {
            var key = (assignment.Receiver, assignment.Small);
            if (!_assistGroups.TryGetValue(key, out var group))
            {
                if (poolIndex >= _assistGroupPool.Count)
                    _assistGroupPool.Add(new AssistGroup());
                group = _assistGroupPool[poolIndex++];
                group.Reset(assignment.Receiver, assignment.Small, _orderedAssistGroups.Count);
                _assistGroups[key] = group;
                _orderedAssistGroups.Add(group);
            }
            group.Assignments.Add(assignment);
        }
        _orderedAssistGroups.Sort(CompareAssistGroups);
    }

    private int CompareAssistGroups(AssistGroup left, AssistGroup right)
    {
        var value = right.Receiver.Projectile!.CapturesLeft.CompareTo(
            left.Receiver.Projectile!.CapturesLeft);
        if (value != 0)
            return value;
        value = StableAssistScore(right.Receiver.Id).CompareTo(StableAssistScore(left.Receiver.Id));
        if (value != 0)
            return value;
        value = string.CompareOrdinal(left.Receiver.Id, right.Receiver.Id);
        return value != 0 ? value : left.EncounterOrder.CompareTo(right.EncounterOrder);
    }

    private static int CompareDonorAssignments(AssistAssignment left, AssistAssignment right)
    {
        var value = right.Donor.Projectile!.CapturesLeft.CompareTo(left.Donor.Projectile!.CapturesLeft);
        if (value != 0)
            return value;
        value = left.DistanceSquared.CompareTo(right.DistanceSquared);
        return value != 0 ? value : string.CompareOrdinal(left.Donor.Id, right.Donor.Id);
    }

    private void RefreshTurretCache()
    {
        var factions = _economyWorld.Factions;
        var expected = 0;
        var matches = true;
        for (var i = 0; i < factions.Count; i++)
        {
            var faction = factions[i];
            if (faction.Id.Equals(FactionBoard.UnassignedId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (expected >= _turrets.Count || !ReferenceEquals(_turrets[expected], faction))
                matches = false;
            expected++;
        }
        if (matches && expected == _turrets.Count)
            return;

        _turrets.Clear();
        foreach (var faction in factions)
        {
            if (!faction.Id.Equals(FactionBoard.UnassignedId, StringComparison.OrdinalIgnoreCase))
                _turrets.Add(faction);
        }
    }

    private void SyncTransferredProjectileSize(Ball ball)
    {
        if (ball.Projectile is { } projectile
            && RoleOf(projectile) is ProjectileRole.Shell or ProjectileRole.Ember)
        {
            SyncShellSize(ball);
        }
    }

    /// <summary>v2.12.2 HD-03:弹体尺寸/质量随数值同步(磨小/融入即时可见)。</summary>
    private void SyncShellSize(Ball ball)
    {
        var value = Math.Max(1, ball.Projectile?.CapturesLeft ?? 1);
        var size = ShellSizeFor(value);
        var weight = ShellWeightFor(value);
        if (Math.Abs(ball.Size - size) > 1e-9)
            ball.Size = size;
        if (Math.Abs(ball.Weight - weight) > 1e-9)
            ball.Weight = weight;
    }

    /// <summary>v2.12 SH-01~04:实体护罩 — 小球湮灭磨盾,大球按比例抵消并反弹。返回 true 表示本弹已处理完毕(消失或反弹)。</summary>
    private bool TryShieldIntercept(Ball ball, ProjectileState projectile)
    {
        var costPerValue = _config.Arena.ShieldCostPerValue; // v3.1:护盾计价可配
        if (costPerValue <= 0)
            return false;
        RefreshTurretCache();
        foreach (var turret in _turrets)
        {
            if (!turret.Alive || turret.Shield <= 0)
                continue;

            // v3.1:与外圈护盾环视觉共读同一字段,杜绝两处漂移
            var shieldRadius = turret.TurretRadius * _config.Arena.ShieldRingScale;
            var reach = shieldRadius + ball.Size;
            var distSq = DistanceSquared(ball.X, ball.Y, turret.TurretX, turret.TurretY);
            if (distSq > reach * reach)
                continue;

            var remaining = Math.Max(1, projectile.CapturesLeft);
            var roleAwareSmall = _balance.Current.FriendlyAssistEnabled
                                 && RoleOf(projectile) == ProjectileRole.SmallShot;
            if (turret.Id.Equals(projectile.OwnerFactionId, StringComparison.OrdinalIgnoreCase))
            {
                // v2.12.2 HD-05:自家小球**飞回**碰自家护罩 → 转化为护盾;
                // 出膛外飞的放行(否则出膛点即在护罩内,小球攻势会被整体吞掉)
                // v2.12.4 TK-07:决胜时刻后转化关闭
                if ((roleAwareSmall || remaining <= 1)
                    && _balance.Current.SelfShieldRefundEnabled
                    && (!SuddenDeath || !_balance.Current.SuddenDeathShieldBlock))
                {
                    var towardTurret = ball.Vx * (turret.TurretX - ball.X)
                        + ball.Vy * (turret.TurretY - ball.Y);
                    if (towardTurret > 0)
                    {
                        turret.Shield = ArenaFormulas.AddShield(
                            turret.Shield,
                            costPerValue * (roleAwareSmall ? remaining : 1));
                        _shieldSpentTotal += roleAwareSmall ? remaining : 1;
                        _battleWorld.Balls.Remove(ball);
                        return true;
                    }
                }
                continue;
            }
            if (!_balance.Current.FriendlyAssistEnabled && remaining <= 1)
            {
                // Frozen v3.1/v3.2 rollback behavior.
                turret.Shield = Math.Max(0, turret.Shield - costPerValue);
                _shieldSpentTotal++;
                _battleWorld.Balls.Remove(ball);
                return true;
            }

            // Shield and projectile values cancel in the same displayed unit. Promoted small
            // shots preserve any value left after the shield is exhausted, just like shells.
            long capacity;
            int cancel;
            if (_balance.Current.FriendlyAssistEnabled)
            {
                capacity = (long)Math.Floor(turret.Shield / costPerValue + 1e-12);
                if (capacity <= 0)
                    continue;
                cancel = (int)Math.Min((long)remaining, capacity);
            }
            else
            {
                capacity = (long)(turret.Shield / costPerValue);
                cancel = (int)Math.Clamp(Math.Min((long)remaining, capacity), 1, int.MaxValue);
            }
            projectile.CapturesLeft = remaining - cancel;
            _shieldSpentTotal += cancel;
            turret.Shield = Math.Max(0, turret.Shield - cancel * costPerValue);
            if (projectile.CapturesLeft <= 0)
            {
                _battleWorld.Balls.Remove(ball);
                return true;
            }
            SyncShellSize(ball);

            // 只供旧版本确定性回退；持久化配置和 UI 均不能重新开启。
            if (turret.Shield <= 0 && _balance.Current.ShieldBreakthrough)
                return false;

            // v3.5.1:即使本次命中刚好破盾，大球也必须反弹，避免同一发直入炮台触杀。
            var dist = Math.Sqrt(Math.Max(1e-6, distSq));
            var nx = (ball.X - turret.TurretX) / dist;
            var ny = (ball.Y - turret.TurretY) / dist;
            var dot = ball.Vx * nx + ball.Vy * ny;
            if (dot < 0)
            {
                ball.Vx -= 2 * dot * nx;
                ball.Vy -= 2 * dot * ny;
            }
            var pushTo = shieldRadius + ball.Size + 1;
            ball.X = turret.TurretX + nx * pushTo;
            ball.Y = turret.TurretY + ny * pushTo;
            return true;
        }
        return false;
    }

    /// <summary>v2.9 TE-02/03/04 + v2.10:球体覆盖范围内逐格占领(大弹整片啃)。返回 true 表示预算耗尽应回收。</summary>
    private bool TryCaptureTerritory(Ball ball, ProjectileState projectile)
    {
        var attacker = FactionIndexOf(projectile.OwnerFactionId);
        if (attacker < 0)
            return false;

        if (projectile.CapturesLeft <= 0)
        {
            // direct 模式遗留弹进入领地模式时的兜底初始化
            projectile.CapturesLeft = Math.Max(
                1,
                (int)Math.Round(Math.Sqrt(Math.Max(1, projectile.Damage)) / 250));
        }

        var turrets = Turrets;
        var radius = Math.Max(ball.Size, _cellSize * 0.5);
        var minCol = Math.Max(0, (int)((ball.X - radius) / _cellSize));
        var maxCol = Math.Min(TerritoryCols - 1, (int)((ball.X + radius) / _cellSize));
        var minRow = Math.Max(0, (int)((ball.Y - radius) / _cellSize));
        var maxRow = Math.Min(TerritoryRows - 1, (int)((ball.Y + radius) / _cellSize));
        var captured = false;

        for (var row = minRow; row <= maxRow && projectile.CapturesLeft > 0; row++)
        {
            for (var col = minCol; col <= maxCol && projectile.CapturesLeft > 0; col++)
            {
                var cellX = (col + 0.5) * _cellSize;
                var cellY = (row + 0.5) * _cellSize;
                if (DistanceSquared(ball.X, ball.Y, cellX, cellY) > radius * radius)
                    continue;

                var cell = row * TerritoryCols + col;
                var owner = _territory[cell];
                if (owner == attacker)
                    continue;

                _territory[cell] = attacker;
                _territoryOwned[attacker]++;
                captured = true;
                if (attacker < turrets.Count)
                    turrets[attacker].Hp = _territoryOwned[attacker];

                if (owner >= 0 && owner < turrets.Count)
                {
                    // v2.12.4 TK-02:本垒格陷落死法退役,触杀取代
                    var defender = turrets[owner];
                    _territoryOwned[owner] = Math.Max(0, _territoryOwned[owner] - 1);
                    defender.Hp = _territoryOwned[owner];
                }
                projectile.CapturesLeft--;
                _territorySpentTotal++;
            }
        }

        if (captured)
        {
            TerritoryVersion++;
            if (projectile.CapturesLeft > 0
                && (!_balance.Current.FriendlyAssistEnabled
                    || RoleOf(projectile) is ProjectileRole.Shell or ProjectileRole.Ember))
                SyncShellSize(ball);
        }
        return projectile.CapturesLeft <= 0;
    }

    private void RegenerateShields(double dt)
    {
        var config = _balance.Current;
        if (SuddenDeath && config.SuddenDeathShieldBlock)
            return;
        if (config.ShieldRegenPerSecond <= 0)
            return;
        foreach (var turret in Turrets)
        {
            if (!turret.Alive)
                continue;
            turret.Shield = ArenaFormulas.AddShield(
                turret.Shield,
                config.ShieldRegenPerSecond * dt);
        }
    }

    private void EnforceProjectileLimit()
    {
        var overflow = ProjectileCount - _config.Arena.MaxProjectiles;
        if (overflow <= 0)
            return;
        _battleWorld.Balls.RemoveRange(0, Math.Min(overflow, _battleWorld.Balls.Count));
        _log.Warn("arena", $"投射物超过上限 {_config.Arena.MaxProjectiles},已回收 {overflow}");
    }

    private void CheckWinner()
    {
        if (WinnerId != null)
            return;

        var hardLimit = _balance.Current.HardTimeLimitSeconds;
        if (hardLimit > 0 && ElapsedSeconds + 1e-9 >= hardLimit)
        {
            var ranked = Turrets
                .Select((turret, index) => new
                {
                    Turret = turret,
                    Remaining = TerritoryMode && index < _territoryOwned.Length
                        ? _territoryOwned[index]
                        : turret.Hp,
                })
                .OrderByDescending(x => x.Remaining)
                .ToList();
            WinnerId = ranked.Count == 0 || ranked.Count > 1
                && Math.Abs(ranked[0].Remaining - ranked[1].Remaining) < 1e-9
                    ? "draw"
                    : ranked[0].Turret.Id;
            AutomaticFire = false;
            Raise("ended", WinnerId == "draw"
                ? $"硬性时限 {hardLimit:0.###}s 到达,剩余领地相同,平局"
                : $"硬性时限 {hardLimit:0.###}s 到达,按剩余领地判胜: {WinnerId}", WinnerId);
            return;
        }

        // V3.6:两个以上炮台存活时不扫描球池；进入决胜阶段后才核算完整可战价值。
        var turrets = Turrets;
        var aliveCount = 0;
        foreach (var turret in turrets)
            if (turret.Alive)
                aliveCount++;
        var eliminatedCount = turrets.Count - aliveCount;
        if (aliveCount > 1 && _eliminationTimes.Count >= eliminatedCount)
            return;

        _activeTurrets.Clear();
        var activeList = _activeTurrets;
        var values = RemainingCombatValues();
        for (var index = 0; index < turrets.Count; index++)
        {
            if (!turrets[index].Alive && values[index].Total == 0)
                _eliminationTimes.TryAdd(turrets[index].Id, ElapsedSeconds);
            if (turrets[index].Alive || values[index].Total > 0)
                activeList.Add(turrets[index]);
        }

        if (aliveCount > 1)
            return;

        if (activeList.Count > 1)
            return;
        WinnerId = activeList.Count == 1 ? activeList[0].Id : "draw";
        AutomaticFire = false;
        Raise("ended", activeList.Count == 1 ? $"胜者: {activeList[0].Name}" : "全灭,平局", WinnerId);
    }

    private void Raise(string kind, string message, string? factionId = null)
    {
        var battleEvent = new BattleEvent(ElapsedSeconds, kind, message, factionId);
        _recentEvents.Add(battleEvent);
        if (_recentEvents.Count > 500)
            _recentEvents.RemoveRange(0, _recentEvents.Count - 500);
        EventRaised?.Invoke(battleEvent);
        _log.Info("battle", $"[{ElapsedSeconds:0.000}] {message}");
    }

    private Faction? FindContactVictim(Ball ball, ProjectileState projectile)
    {
        RefreshTurretCache();
        foreach (var turret in _turrets)
        {
            if (!turret.Alive
                || turret.Id.Equals(projectile.OwnerFactionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var reach = ball.Size + turret.TurretRadius;
            if (DistanceSquared(ball.X, ball.Y, turret.TurretX, turret.TurretY) <= reach * reach)
                return turret;
        }
        return null;
    }

    private static double DistanceSquared(Faction a, Faction b) =>
        DistanceSquared(a.TurretX, a.TurretY, b.TurretX, b.TurretY);

    private static double DistanceSquared(double ax, double ay, double bx, double by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return dx * dx + dy * dy;
    }

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
