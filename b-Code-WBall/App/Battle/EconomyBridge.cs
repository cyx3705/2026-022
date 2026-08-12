using AppShell.Core.Logging;
using WBall.Game;
using WBall.Model;

namespace WBall.Battle;

/// <summary>把左世界结算事件映射到阵营经济与火力参数。</summary>
public sealed class EconomyBridge : ISettlementService
{
    private readonly WeaponCatalog _weapons;
    private readonly IShellLog _log;
    private readonly BalanceConfigStore _balance;
    private readonly BattleConfigStore? _battleConfig;

    public EconomyBridge(
        WeaponCatalog weapons,
        IShellLog log,
        BalanceConfigStore? balance = null,
        BattleConfigStore? battleConfig = null)
    {
        _weapons = weapons;
        _log = log;
        _balance = balance ?? BalanceConfigStore.CreateMemory(new BalanceConfig(), log);
        _battleConfig = battleConfig;
    }

    public Func<WeaponDefinition, bool>? Availability { get; set; }

    /// <summary>v2.12.4 TK-07:决胜期护盾经济封锁(护盾槽结算转喂小球弹药)。</summary>
    public Func<bool>? ShieldEconomyBlocked { get; set; }

    public bool IsKnown(string name) => _weapons.TryResolve(name, out _);

    public bool TrySettle(
        string name,
        SceneWorld economyWorld,
        Ball ball,
        long value,
        Action<string>? warn)
    {
        if (!_weapons.TryResolve(name, out var weapon))
            return false;

        if (!weapon.Enabled)
        {
            warn?.Invoke($"武器 {weapon.Name} 已停用,本次结算丢弃");
            return true;
        }

        if (Availability?.Invoke(weapon) == false)
        {
            warn?.Invoke($"武器 {weapon.Name} 尚未解封,本次结算丢弃");
            return true;
        }

        var faction = FactionBoard.FindByColor(economyWorld, ball.Color);
        if (faction is { Alive: false })
        {
            warn?.Invoke($"阵营 {faction.Name} 已死亡,{weapon.Name} 结算丢弃");
            return true;
        }

        FactionBoard.AddScore(economyWorld, ball.Color, value, warn);
        faction ??= FactionBoard.FindByColor(economyWorld, ball.Color);
        if (faction != null)
        {
            var config = _balance.Current;
            faction.Points = SaturatingAdd(faction.Points, value);
            var current = faction.Firepower.Intensities.GetValueOrDefault(weapon.Name);
            var total = Math.Max(0, current + value);
            faction.Firepower.Intensities[weapon.Name] = total;
            var scaled = weapon.EconomyScale * (Math.Abs(config.IntensityExponent - 0.5) < 1e-12
                ? Math.Sqrt(total)
                : Math.Pow(total, config.IntensityExponent));
            if (weapon.Kind == WeaponKind.Shield && ShieldEconomyBlocked?.Invoke() == true)
                faction.SmallAmmo = SaturatingAdd(faction.SmallAmmo, value); // 决胜期:护盾槽转喂弹药
            else
                ApplyKind(
                    faction,
                    weapon,
                    value,
                    scaled,
                    config,
                    config.FriendlyAssistEnabled
                        ? Math.Max(0, _battleConfig?.Arena.ShieldCostPerValue ?? 1)
                        : 1);

            // v2.11 SA-01/02:小球/齐射/直射共入小球弹药库,模式跟随最后落槽
            if (IsSmallPoolWeapon(weapon.Name))
            {
                faction.SmallAmmo = SaturatingAdd(faction.SmallAmmo, value);
                // v2.12 ST-01~03:连射为默认态,直射=限时定格,齐射=瞬发环射事件
                switch (CanonicalSmallMode(weapon.Name))
                {
                    case "直射":
                        faction.BarrelFreezeRemaining = Math.Min(
                            config.FreezeMaxSeconds,
                            faction.BarrelFreezeRemaining + Math.Clamp(
                                value * config.FreezeSecondsPerValue,
                                Math.Min(1, config.FreezeMaxSeconds),
                                config.FreezeMaxSeconds));
                        break;
                    case "齐射":
                        faction.VolleyPending = Math.Min(config.VolleyPendingMax, faction.VolleyPending + 1);
                        break;
                }
            }
            // v3.2:取消 512 玩法上限；仅保留防 OOM 硬顶，触顶明确告警且只告警一次。
            else if (weapon.Kind != WeaponKind.Shield)
            {
                if (faction.Ammo.Count < config.AmmoQueueGuard)
                {
                    faction.EnqueueAmmo(new AmmoShell(Math.Max(1, value), weapon.Name));
                    faction.AmmoGuardWarned = false;
                }
                else if (!faction.AmmoGuardWarned)
                {
                    faction.AmmoGuardWarned = true;
                    var message = $"阵营 {faction.Name} 弹药队列触及防 OOM 硬顶 {config.AmmoQueueGuard},停止入队";
                    warn?.Invoke(message);
                    _log.Warn("economy", message);
                }
            }
        }
        _log.Info("economy", $"{ball.Color} -> {weapon.Name} +{value}");
        return true;
    }

    private static void ApplyKind(
        Faction faction,
        WeaponDefinition weapon,
        long value,
        double scaled,
        BalanceConfig config,
        double shieldCostPerValue)
    {
        switch (weapon.Kind)
        {
            case WeaponKind.Size:
                faction.Firepower.ProjectileSize = Math.Clamp(config.SizeGainBase + scaled, 2, 60);
                break;
            case WeaponKind.Count:
                faction.Firepower.ProjectileCount = Math.Clamp(1 + (int)Math.Floor(scaled), 1, 200);
                break;
            case WeaponKind.Shield:
                var configuredGain = config.ShieldSlotGainPerValue;
                var rawGainPerPoint = configuredGain > 100
                    ? configuredGain
                    : configuredGain * shieldCostPerValue;
                faction.Shield = ArenaFormulas.AddShield(
                    faction.Shield,
                    value * weapon.EconomyScale * rawGainPerPoint);
                break;
            case WeaponKind.Burst:
                faction.Firepower.ProjectileCount = Math.Clamp(
                    faction.Firepower.ProjectileCount + Math.Max(1, weapon.BaseCount / 2),
                    1,
                    200);
                faction.Firepower.SpreadBonus = Math.Clamp(
                    faction.Firepower.SpreadBonus + weapon.SpreadDegrees * config.BurstSpreadGain,
                    0,
                    60);
                faction.Firepower.DamageMultiplier = Math.Clamp(
                    faction.Firepower.DamageMultiplier + scaled * config.BurstDamageGain,
                    0.2,
                    20);
                break;
            case WeaponKind.Pierce:
                faction.Firepower.DamageMultiplier = Math.Clamp(
                    faction.Firepower.DamageMultiplier + scaled * config.PierceDamageGain,
                    0.2,
                    20);
                faction.Firepower.SpreadBonus = Math.Max(0, faction.Firepower.SpreadBonus - 0.5);
                break;
            case WeaponKind.Split:
                faction.Firepower.ProjectileCount = Math.Clamp(
                    faction.Firepower.ProjectileCount + Math.Max(1, weapon.BaseCount),
                    1,
                    200);
                faction.Firepower.SpreadBonus = Math.Clamp(
                    faction.Firepower.SpreadBonus + 2,
                    0,
                    60);
                break;
            case WeaponKind.Gravity:
                faction.Firepower.ProjectileSize = Math.Clamp(
                    faction.Firepower.ProjectileSize + scaled * config.GravitySizeGain,
                    2,
                    60);
                faction.Firepower.DamageMultiplier = Math.Clamp(
                    faction.Firepower.DamageMultiplier + scaled * config.GravityDamageGain,
                    0.2,
                    20);
                break;
            case WeaponKind.Score:
            default:
                faction.Firepower.DamageMultiplier = Math.Clamp(
                    faction.Firepower.DamageMultiplier + scaled * config.ScoreDamageGain,
                    0.2,
                    20);
                break;
        }
    }

    /// <summary>v2.11:小球弹药库三槽(含英文别名规范化)。</summary>
    private static bool IsSmallPoolWeapon(string name) =>
        CanonicalSmallMode(name) != null;

    private static string? CanonicalSmallMode(string name) => name.Trim() switch
    {
        "小球" or "SmallBall" => "小球",
        "齐射" or "Volley" => "齐射",
        "直射" or "Direct" => "直射",
        _ => null,
    };

    private static long SaturatingAdd(long left, long right)
    {
        try
        {
            return checked(left + Math.Max(0, right));
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }
}
