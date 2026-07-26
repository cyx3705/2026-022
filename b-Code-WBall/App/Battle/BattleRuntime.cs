using AppShell.Core.Logging;
using WBall.Game;
using WBall.Model;
using WBall.Sim;

namespace WBall.Battle;

public sealed record BattleEvent(double Time, string Kind, string Message, string? FactionId = null);

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
    private readonly WeaponCatalog _weapons;
    private readonly IShellLog _log;
    private readonly Dictionary<string, double> _fireCooldown = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<BattleEvent> _recentEvents = [];
    private readonly List<HitMarker> _hitMarkers = [];
    private Func<WeaponDefinition, bool>? _isUnlocked;
    private string[] _territoryIds = [];
    private int[] _territory = [];
    private int[] _territoryOwned = [];
    private double _cellSize = 20;

    public BattleRuntime(
        SceneWorld economyWorld,
        SceneWorld battleWorld,
        BattleConfigStore config,
        WeaponCatalog weapons,
        IShellLog log)
    {
        _economyWorld = economyWorld;
        _battleWorld = battleWorld;
        _config = config;
        _weapons = weapons;
        _log = log;
        Reset(economyWorld.Seed);
    }

    public event Action<BattleEvent>? EventRaised;

    public IReadOnlyList<Faction> Turrets => _economyWorld.Factions
        .Where(x => !x.Id.Equals(FactionBoard.UnassignedId, StringComparison.OrdinalIgnoreCase))
        .ToList();

    public double ElapsedSeconds { get; private set; }
    public int Seed { get; private set; }
    public bool AutomaticFire { get; set; }
    public string? WinnerId { get; private set; }
    public int ProjectileCount => _battleWorld.Balls.Count(x => x.Projectile != null);
    public IReadOnlyList<BattleEvent> RecentEvents => _recentEvents;
    public IReadOnlyList<HitMarker> HitMarkers => _hitMarkers;

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

    /// <summary>v2.12.3 NB-02:总弹药 = 小球池 + 大球队列数值和。</summary>
    public long AmmoTotalOf(Faction turret) =>
        turret.SmallAmmo + turret.Ammo.Sum(shell => shell.Value);

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

    public void Reset(int seed)
    {
        Seed = seed;
        ElapsedSeconds = 0;
        WinnerId = null;
        AutomaticFire = false;
        _fireCooldown.Clear();
        _recentEvents.Clear();
        _hitMarkers.Clear();
        _battleWorld.ResetSimulation();
        _battleWorld.SetWorldSize(_config.Arena.Width, _config.Arena.Height, markDirty: false);
        _battleWorld.GravityG = _config.Arena.GravityG;
        _battleWorld.BallCollisionEnabled = _config.Arena.BallCollision;
        _battleWorld.Seed = seed;

        _economyWorld.Factions.Clear();
        foreach (var definition in _config.Turrets)
        {
            var turret = CreateTurret(definition);
            PlaceTurret(turret);
            _economyWorld.Factions.Add(turret);
            _fireCooldown[turret.Id] = 0;
            // v2.10 AM-04:开局预载,避免弹药未产出前冷场
            if (TerritoryMode)
            {
                for (var i = 0; i < 12; i++)
                    turret.Ammo.Enqueue(new AmmoShell(1, "直射"));
            }
        }
        _economyWorld.Seed = seed;
        InitTerritory();
        _economyWorld.NotifyChanged(markDirty: false);
        _battleWorld.NotifyChanged(markDirty: false);
        Raise("reset", $"战场已按种子 {seed} 重置");
    }

    /// <summary>v2.9 TE-01:按象限均分领地;领地模式下 HP=拥有格数。</summary>
    private void InitTerritory()
    {
        var turrets = Turrets;
        _cellSize = Math.Clamp(_config.Arena.CellSize, 5, 100);
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
        ElapsedSeconds += dt;

        foreach (var turret in Turrets.Where(x => x.Alive))
        {
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
            foreach (var turret in Turrets.Where(x => x.Alive))
            {
                _fireCooldown[turret.Id] -= dt;
                if (_fireCooldown[turret.Id] <= 0)
                {
                    Fire(turret.Id);
                    // v2.12.3 NB-03:大球出膛间隔随队列长度缩短(弹药越多打得越快)
                    var interval = TerritoryMode
                        ? Math.Max(0.08, turret.Firepower.FireIntervalSec / (1 + turret.Ammo.Count * 0.25))
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

        var shell = turret.Ammo.Dequeue();
        if (!_weapons.TryResolve(shell.WeaponName, out var weapon) || !weapon.Enabled)
            weapon = ResolveFireWeapon(turret, null);

        var value = Math.Max(1, shell.Value);
        var size = Math.Clamp(_cellSize * 0.5 * Math.Pow(value, 0.25), _cellSize * 0.5, _cellSize * 5);
        // v2.11 MO-02:发射动量适度随机(±25%),重弹略慢
        var jitter = 0.75 + _battleWorld.Rng.NextDouble() * 0.5;
        var speed = Math.Clamp(
            Math.Clamp(weapon.Speed, 80, 1200) * jitter / Math.Pow(value, 0.12),
            60,
            700);
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
            Weight = Math.Max(1, value),
            Projectile = new ProjectileState
            {
                OwnerFactionId = turret.Id,
                WeaponName = weapon.Name,
                Damage = value,
                CapturesLeft = (int)Math.Clamp(value, 1, 100_000),
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
        var rate = Math.Min(90, 6 + turret.SmallAmmo * 0.15);
        if (frozen)
            rate = Math.Min(150, rate * 2);
        turret.SmallFireCarry += rate * dt;
        while (turret.SmallFireCarry >= 1 && turret.SmallAmmo > 0)
        {
            turret.SmallFireCarry -= 1;
            var spreadDeg = frozen ? 1.5 : 8.0;
            var angle = turret.BarrelAngleDeg * Math.PI / 180
                + (_battleWorld.Rng.NextDouble() - 0.5) * spreadDeg * Math.PI / 180;
            SpawnSmallBall(turret, angle);
            turret.SmallAmmo--;
        }
        EnforceProjectileLimit();
    }

    /// <summary>v2.12 ST-03:齐射瞬发 — 360° 均匀环射一圈后回到连射态。</summary>
    private void FireVolleyRing(Faction turret)
    {
        var count = (int)Math.Min(24, turret.SmallAmmo);
        if (count <= 0)
            return;
        var phase = _battleWorld.Rng.NextDouble() * Math.PI * 2;
        for (var index = 0; index < count; index++)
            SpawnSmallBall(turret, phase + index * Math.PI * 2 / count);
        turret.SmallAmmo -= count;
        EnforceProjectileLimit();
    }

    private void SpawnSmallBall(Faction turret, double angle)
    {
        var size = _cellSize * 0.5;
        const double speed = 380;
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
            Weight = 1,
            Projectile = new ProjectileState
            {
                OwnerFactionId = turret.Id,
                WeaponName = "小球",
                Damage = 1,
                CapturesLeft = 1,
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
        var payloads = new List<(long Value, string Weapon)>();
        while (turret.Ammo.Count > 0)
        {
            var shell = turret.Ammo.Dequeue();
            payloads.Add((shell.Value, shell.WeaponName));
        }
        if (turret.SmallAmmo > 0)
        {
            payloads.Add((turret.SmallAmmo, "小球"));
            turret.SmallAmmo = 0;
        }
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

        foreach (var (value, weapon) in payloads)
        {
            var angle = _battleWorld.Rng.NextDouble() * Math.PI * 2;
            var speed = 150 + _battleWorld.Rng.NextDouble() * 250;
            var size = Math.Clamp(_cellSize * 0.5 * Math.Pow(value, 0.25), _cellSize * 0.5, _cellSize * 5);
            _battleWorld.Balls.Add(new Ball
            {
                Id = _battleWorld.NextBallId(),
                X = turret.TurretX + Math.Cos(angle) * (turret.TurretRadius + size + 2),
                Y = turret.TurretY + Math.Sin(angle) * (turret.TurretRadius + size + 2),
                Vx = Math.Cos(angle) * speed,
                Vy = Math.Sin(angle) * speed,
                Color = turret.Color,
                Size = size,
                Weight = Math.Max(1, value),
                Projectile = new ProjectileState
                {
                    OwnerFactionId = turret.Id,
                    WeaponName = weapon,
                    Damage = value,
                    CapturesLeft = (int)Math.Clamp(value, 1, 100_000),
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
            turret.MaxShield = Math.Max(0, shield.Value);
            turret.Shield = turret.MaxShield;
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
        Shield = Math.Clamp(definition.InitialShield, 0, definition.MaxShield),
        MaxShield = definition.MaxShield,
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
        // 更靠四角(参考图风格)
        var marginX = _battleWorld.WorldWidth * 0.12;
        var marginY = _battleWorld.WorldHeight * 0.14;
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
                var victim = Turrets.FirstOrDefault(t =>
                    t.Alive
                    && !t.Id.Equals(projectile.OwnerFactionId, StringComparison.OrdinalIgnoreCase)
                    && DistanceSquared(ball.X, ball.Y, t.TurretX, t.TurretY)
                        <= Math.Pow(ball.Size + t.TurretRadius, 2));
                if (victim != null)
                    Kill(victim.Id);

                if (TryCaptureTerritory(ball, projectile))
                    _battleWorld.Balls.Remove(ball);
                continue;
            }

            var target = Turrets.FirstOrDefault(t =>
                t.Alive
                && !t.Id.Equals(projectile.OwnerFactionId, StringComparison.OrdinalIgnoreCase)
                && DistanceSquared(ball.X, ball.Y, t.TurretX, t.TurretY)
                    <= Math.Pow(ball.Size + t.TurretRadius, 2));
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
        if (balls.Count < 2)
            return;

        // 步长须覆盖最大光晕和(1.6×(50+50)=160),保证相邻桶即可命中
        const double step = 160;
        var buckets = new Dictionary<(int Col, int Row), List<Ball>>();
        foreach (var ball in balls)
        {
            if (ball.Projectile == null)
                continue;
            var key = ((int)(ball.X / step), (int)(ball.Y / step));
            if (!buckets.TryGetValue(key, out var list))
                buckets[key] = list = [];
            list.Add(ball);
        }

        HashSet<Ball>? dead = null;
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
                    if (!buckets.TryGetValue((col + dx, row + dy), out var list))
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
                        var reach = (ball.Size + other.Size) * 1.6;
                        if (DistanceSquared(ball.X, ball.Y, other.X, other.Y) > reach * reach)
                            continue;

                        var sameOwner = theirs.OwnerFactionId.Equals(
                            mine.OwnerFactionId, StringComparison.OrdinalIgnoreCase);
                        var v1 = Math.Max(1, mine.CapturesLeft);
                        var v2 = Math.Max(1, theirs.CapturesLeft);

                        if (sameOwner)
                        {
                            // HD-06:自家小球融入自家大球(大球间不融合)
                            dead ??= [];
                            if (v1 == 1 && v2 > 1)
                            {
                                theirs.CapturesLeft = v2 + 1;
                                SyncShellSize(other);
                                dead.Add(ball);
                            }
                            else if (v2 == 1 && v1 > 1)
                            {
                                mine.CapturesLeft = v1 + 1;
                                SyncShellSize(ball);
                                dead.Add(other);
                            }
                            if (dead.Contains(ball))
                                break;
                            continue;
                        }

                        // HD-02:同步等量研磨 — 速率随较大球,单帧不超过较小方余值(1:1 守恒)
                        var drain = (int)Math.Min(
                            Math.Min(v1, v2),
                            Math.Max(1, Math.Round(Math.Max(v1, v2) * 2.0 * dt)));
                        mine.CapturesLeft = v1 - drain;
                        theirs.CapturesLeft = v2 - drain;
                        SyncShellSize(ball);
                        SyncShellSize(other);
                        dead ??= [];
                        if (theirs.CapturesLeft <= 0)
                            dead.Add(other);
                        if (mine.CapturesLeft <= 0)
                        {
                            dead.Add(ball);
                            break;
                        }
                    }
                }
            }
        }

        if (dead is { Count: > 0 })
            balls.RemoveAll(dead.Contains);
    }

    /// <summary>v2.12.2 HD-03:弹体尺寸/质量随数值同步(磨小/融入即时可见)。</summary>
    private void SyncShellSize(Ball ball)
    {
        var value = Math.Max(1, ball.Projectile?.CapturesLeft ?? 1);
        ball.Size = Math.Clamp(_cellSize * 0.5 * Math.Pow(value, 0.25), _cellSize * 0.5, _cellSize * 5);
        ball.Weight = Math.Max(1, value);
    }

    /// <summary>v2.12 SH-01~04:实体护罩 — 小球湮灭磨盾,大球按比例抵消并反弹。返回 true 表示本弹已处理完毕(消失或反弹)。</summary>
    private bool TryShieldIntercept(Ball ball, ProjectileState projectile)
    {
        const double costPerValue = 50_000;
        foreach (var turret in Turrets)
        {
            if (!turret.Alive || turret.Shield <= 0)
                continue;

            var shieldRadius = turret.TurretRadius * 1.55; // 与外圈护盾环视觉一致
            var reach = shieldRadius + ball.Size;
            var distSq = DistanceSquared(ball.X, ball.Y, turret.TurretX, turret.TurretY);
            if (distSq > reach * reach)
                continue;

            var remaining = Math.Max(1, projectile.CapturesLeft);
            if (turret.Id.Equals(projectile.OwnerFactionId, StringComparison.OrdinalIgnoreCase))
            {
                // v2.12.2 HD-05:自家小球**飞回**碰自家护罩 → 转化为护盾;
                // 出膛外飞的放行(否则出膛点即在护罩内,小球攻势会被整体吞掉)
                // v2.12.4 TK-07:决胜时刻后转化关闭
                if (remaining <= 1 && !SuddenDeath)
                {
                    var towardTurret = ball.Vx * (turret.TurretX - ball.X)
                        + ball.Vy * (turret.TurretY - ball.Y);
                    if (towardTurret > 0)
                    {
                        turret.Shield = Math.Min(turret.MaxShield, turret.Shield + costPerValue);
                        _battleWorld.Balls.Remove(ball);
                        return true;
                    }
                }
                continue;
            }
            if (remaining <= 1)
            {
                // 小球:湮灭并磨损护盾
                turret.Shield = Math.Max(0, turret.Shield - costPerValue);
                _battleWorld.Balls.Remove(ball);
                return true;
            }

            // 大球:按护盾余量比例抵消
            var capacity = (long)(turret.Shield / costPerValue);
            var cancel = (int)Math.Clamp(Math.Min((long)remaining, capacity), 1, int.MaxValue);
            projectile.CapturesLeft = remaining - cancel;
            turret.Shield = Math.Max(0, turret.Shield - cancel * costPerValue);
            if (projectile.CapturesLeft <= 0)
            {
                _battleWorld.Balls.Remove(ball);
                return true;
            }
            SyncShellSize(ball);

            // v2.12.5 BS-01:破盾直入 — 护盾被本弹打穿则不反弹,余值继续压向本体触杀
            if (turret.Shield <= 0)
                return false;

            // 护盾仍在:沿法线反弹并推出护罩
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
            }
        }

        if (captured)
        {
            TerritoryVersion++;
            if (projectile.CapturesLeft > 0)
                SyncShellSize(ball);
        }
        return projectile.CapturesLeft <= 0;
    }

    private void RegenerateShields(double dt)
    {
        if (SuddenDeath)
            return;
        foreach (var turret in Turrets.Where(x => x.Alive && x.Firepower.ShieldGain > 0))
            turret.Shield = Math.Min(turret.MaxShield, turret.Shield + turret.Firepower.ShieldGain * dt);
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

        // v2.12.4 TK-05:活跃 = 炮台在或余烬弹在;只剩一家活跃即胜者
        var turrets = Turrets;
        List<Faction> activeList = [];
        if (TerritoryMode)
        {
            foreach (var turret in turrets)
            {
                var hasShells = _battleWorld.Balls.Any(b =>
                    b.Projectile?.OwnerFactionId.Equals(turret.Id, StringComparison.OrdinalIgnoreCase) == true);
                if (turret.Alive || hasShells)
                    activeList.Add(turret);
            }
        }
        else
        {
            activeList = turrets.Where(x => x.Alive).ToList();
        }

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

    private static double DistanceSquared(Faction a, Faction b) =>
        DistanceSquared(a.TurretX, a.TurretY, b.TurretX, b.TurretY);

    private static double DistanceSquared(double ax, double ay, double bx, double by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return dx * dx + dy * dy;
    }
}
