using System.IO;
using System.Text.Json;
using AppShell.Core.Logging;

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

    public bool ShieldBreakthrough { get; set; } = true;
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

/// <summary>battle_balance.json 的加载、校验与保存；无头试跑可使用内存实例。</summary>
public sealed class BalanceConfigStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static readonly IReadOnlyDictionary<string, (double Min, double Max)> Ranges =
        new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase)
        {
            ["shellIntervalAmmoFactor"] = (0, 5),
            ["shellIntervalFloorSec"] = (0.02, 5),
            ["smallRateBase"] = (0, 200),
            ["smallRatePerAmmo"] = (0, 5),
            ["smallRateMax"] = (1, 5000),
            ["smallRateFrozenFactor"] = (1, 10),
            ["smallRateFrozenMax"] = (1, 900),
            ["smallSpreadDeg"] = (0, 90),
            ["smallSpreadFrozenDeg"] = (0, 90),
            ["volleyRingCount"] = (4, 120),
            ["volleyPendingMax"] = (1, 64),
            ["freezeSecondsPerValue"] = (0, 2),
            ["freezeMaxSeconds"] = (0, 60),
            ["ammoQueueGuard"] = (512, 100_000_000),
            ["smallPackThreshold"] = (0, 1_000_000_000),
            ["smallPackRatio"] = (2, 10),
            ["smallPackMax"] = (2, 4096),
            ["haloReachFactor"] = (1, 4),
            ["grindRatePerSecond"] = (0.1, 50),
            ["friendlyAbsorbSmallRate"] = (0, 10),
            ["friendlyShellTransferRate"] = (0, 10),
            ["friendlyAssistReachFactor"] = (1, 3),
            ["friendlyAssistMaxValue"] = (2, 1_000_000),
            ["shieldSlotGainPerValue"] = (0, 1_000_000),
            ["shieldRegenPerSecond"] = (0, 1_000_000),
            ["emberSpeedMin"] = (10, 3000),
            ["emberSpeedMax"] = (10, 3000),
            ["intensityExponent"] = (0.1, 1),
            ["sizeGainBase"] = (2, 60),
            ["burstDamageGain"] = (0, 1),
            ["burstSpreadGain"] = (0, 1),
            ["pierceDamageGain"] = (0, 1),
            ["gravitySizeGain"] = (0, 2),
            ["gravityDamageGain"] = (0, 2),
            ["scoreDamageGain"] = (0, 1),
            ["wallRestitution"] = (0, 1),
            ["ballRestitution"] = (0, 1),
            ["countdownSeconds"] = (0, 30),
            ["settleSeconds"] = (0, 30),
            ["hardTimeLimitSeconds"] = (0, 7200),
        };

    private readonly string? _path;
    private readonly IShellLog _log;

    public BalanceConfigStore(string dataRoot, IShellLog log)
    {
        _path = System.IO.Path.Combine(dataRoot, "battle_balance.json");
        _log = log;
        Reload();
    }

    private BalanceConfigStore(BalanceConfig config, IShellLog log)
    {
        _log = log;
        Current = Clone(config);
        Validate(Current);
    }

    public BalanceConfig Current { get; private set; } = new();
    public string? Path => _path;

    public static BalanceConfigStore CreateMemory(BalanceConfig config, IShellLog log) => new(config, log);

    public void Replace(BalanceConfig config)
    {
        Validate(config);
        Current = Clone(config);
        Save();
    }

    public void ResetDefaults()
    {
        Current = new BalanceConfig();
        Save();
    }

    public void Reload()
    {
        if (_path == null)
            return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        if (!File.Exists(_path))
            File.WriteAllText(_path, JsonSerializer.Serialize(new BalanceConfig(), JsonOptions));
        try
        {
            var json = File.ReadAllText(_path);
            var config = JsonSerializer.Deserialize<BalanceConfig>(json, JsonOptions)
                         ?? new BalanceConfig();
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("friendlyAssistEnabled", out _)
                && !document.RootElement.TryGetProperty("FriendlyAssistEnabled", out _))
                config.FriendlyAssistEnabled = config.MergeSameOwnerSmall;
            Validate(config);
            Current = config;
            Save();
            _log.Info("balance", $"已加载战斗平衡配置 {_path}");
        }
        catch (Exception ex)
        {
            Current = new BalanceConfig();
            _log.Error("balance", $"战斗平衡配置无效,使用出厂默认: {ex.Message}");
        }
    }

    public void Save()
    {
        if (_path != null)
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, JsonOptions));
    }

    public static double ClampField(string field, double value) =>
        Ranges.TryGetValue(field, out var range) ? Math.Clamp(value, range.Min, range.Max) : value;

    public static void Validate(BalanceConfig config)
    {
        foreach (var property in typeof(BalanceConfig).GetProperties())
        {
            if (!Ranges.TryGetValue(property.Name, out var range))
                continue;
            var value = Convert.ToDouble(property.GetValue(config), System.Globalization.CultureInfo.InvariantCulture);
            if (!double.IsFinite(value) || value < range.Min || value > range.Max)
                throw new InvalidDataException(
                    $"{property.Name} 须在 {range.Min:0.###}~{range.Max:0.###}(当前 {value:0.###})");
        }
        if (config.EmberSpeedMin > config.EmberSpeedMax)
            throw new InvalidDataException("emberSpeedMin 不得大于 emberSpeedMax");
    }

    public static BalanceConfig Clone(BalanceConfig source) => new()
    {
        ShellIntervalAmmoFactor = source.ShellIntervalAmmoFactor,
        ShellIntervalFloorSec = source.ShellIntervalFloorSec,
        SmallRateBase = source.SmallRateBase,
        SmallRatePerAmmo = source.SmallRatePerAmmo,
        SmallRateMax = source.SmallRateMax,
        SmallRateFrozenFactor = source.SmallRateFrozenFactor,
        SmallRateFrozenMax = source.SmallRateFrozenMax,
        SmallSpreadDeg = source.SmallSpreadDeg,
        SmallSpreadFrozenDeg = source.SmallSpreadFrozenDeg,
        VolleyRingCount = source.VolleyRingCount,
        VolleyPendingMax = source.VolleyPendingMax,
        FreezeSecondsPerValue = source.FreezeSecondsPerValue,
        FreezeMaxSeconds = source.FreezeMaxSeconds,
        AmmoQueueGuard = source.AmmoQueueGuard,
        SmallPackThreshold = source.SmallPackThreshold,
        SmallPackRatio = source.SmallPackRatio,
        SmallPackMax = source.SmallPackMax,
        SmallPackSpeedFollowsSmall = source.SmallPackSpeedFollowsSmall,
        HaloReachFactor = source.HaloReachFactor,
        GrindRatePerSecond = source.GrindRatePerSecond,
        MergeSameOwnerSmall = source.MergeSameOwnerSmall,
        FriendlyAssistEnabled = source.FriendlyAssistEnabled,
        FriendlyAssistVisualEnabled = source.FriendlyAssistVisualEnabled,
        FriendlyAbsorbSmallRate = source.FriendlyAbsorbSmallRate,
        FriendlyShellTransferRate = source.FriendlyShellTransferRate,
        FriendlyAssistReachFactor = source.FriendlyAssistReachFactor,
        FriendlyAssistMaxValue = source.FriendlyAssistMaxValue,
        ShieldBreakthrough = source.ShieldBreakthrough,
        ContactKillEnabled = source.ContactKillEnabled,
        SelfShieldRefundEnabled = source.SelfShieldRefundEnabled,
        SuddenDeathShieldBlock = source.SuddenDeathShieldBlock,
        ShieldSlotGainPerValue = source.ShieldSlotGainPerValue,
        ShieldRegenPerSecond = source.ShieldRegenPerSecond,
        EmberSpeedMin = source.EmberSpeedMin,
        EmberSpeedMax = source.EmberSpeedMax,
        EmberFromAmmo = source.EmberFromAmmo,
        EmberDrainEconomy = source.EmberDrainEconomy,
        IntensityExponent = source.IntensityExponent,
        SizeGainBase = source.SizeGainBase,
        BurstDamageGain = source.BurstDamageGain,
        BurstSpreadGain = source.BurstSpreadGain,
        PierceDamageGain = source.PierceDamageGain,
        GravitySizeGain = source.GravitySizeGain,
        GravityDamageGain = source.GravityDamageGain,
        ScoreDamageGain = source.ScoreDamageGain,
        WallRestitution = source.WallRestitution,
        BallRestitution = source.BallRestitution,
        CountdownSeconds = source.CountdownSeconds,
        SettleSeconds = source.SettleSeconds,
        HardTimeLimitSeconds = source.HardTimeLimitSeconds,
    };
}
