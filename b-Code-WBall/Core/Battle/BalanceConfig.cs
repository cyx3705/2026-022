using System.Text.Json.Serialization;

namespace WBall.Battle;

/// <summary>v3.2 战斗平衡参数。规模与初始量仍归 ArenaLayoutConfig。</summary>
public sealed class BalanceConfig
{
    public double ShellIntervalAmmoFactor { get; set; } = 0.25;
    public double ShellIntervalFloorSec { get; set; } = 0.08;
    public double SmallRateBase { get; set; } = 6;
    public double SmallRatePerAmmo { get; set; } = 0.15;
    public double SmallRateMax { get; set; } = 90;
    public double SmallRateFrozenFactor { get; set; } = 2;
    public double SmallRateFrozenMax { get; set; } = 150;
    public double SmallSpreadDeg { get; set; } = 8;
    public double SmallSpreadFrozenDeg { get; set; } = 1.5;
    public int VolleyRingCount { get; set; } = 24;
    public int VolleyPendingMax { get; set; } = 8;
    public double FreezeSecondsPerValue { get; set; } = 0.0625;
    public double FreezeMaxSeconds { get; set; } = 12;
    public int AmmoQueueGuard { get; set; } = 1_000_000;
    public long SmallPackThreshold { get; set; } = 40_000;
    public int SmallPackRatio { get; set; } = 2;
    public int SmallPackMax { get; set; } = 64;
    public bool SmallPackSpeedFollowsSmall { get; set; } = true;
    public double HaloReachFactor { get; set; } = 1.6;
    public double GrindRatePerSecond { get; set; } = 2;
    public bool MergeSameOwnerSmall { get; set; } = true;
    public bool FriendlyAssistEnabled { get; set; } = true;
    public bool FriendlyAssistVisualEnabled { get; set; } = true;
    public double FriendlyAbsorbSmallRate { get; set; } = 0.25;
    public double FriendlyShellTransferRate { get; set; } = 0.10;
    public double FriendlyAssistReachFactor { get; set; } = 1.20;
    public int FriendlyAssistMaxValue { get; set; } = 100_000;
    [JsonIgnore]
    public bool ShieldBreakthrough { get; set; }
    public bool ContactKillEnabled { get; set; } = true;
    public bool SelfShieldRefundEnabled { get; set; } = true;
    public bool SuddenDeathShieldBlock { get; set; } = true;
    public double ShieldSlotGainPerValue { get; set; } = 1;
    public double ShieldRegenPerSecond { get; set; }
    public double EmberSpeedMin { get; set; } = 150;
    public double EmberSpeedMax { get; set; } = 400;
    public bool EmberFromAmmo { get; set; } = true;
    public bool EmberDrainEconomy { get; set; } = true;
    public double IntensityExponent { get; set; } = 0.5;
    public double SizeGainBase { get; set; } = 8;
    public double BurstDamageGain { get; set; } = 0.02;
    public double BurstSpreadGain { get; set; } = 0.05;
    public double PierceDamageGain { get; set; } = 0.08;
    public double GravitySizeGain { get; set; } = 0.15;
    public double GravityDamageGain { get; set; } = 0.05;
    public double ScoreDamageGain { get; set; } = 0.01;
    public double WallRestitution { get; set; } = 0.55;
    public double BallRestitution { get; set; } = 0.85;
    public double CountdownSeconds { get; set; } = 1;
    public double SettleSeconds { get; set; } = 2;
    public double HardTimeLimitSeconds { get; set; }
}
