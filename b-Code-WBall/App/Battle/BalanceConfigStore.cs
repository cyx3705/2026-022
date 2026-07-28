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

    /// <summary>
    /// v3.4 V34-05:范围表不再在此手抄一份,改由 <see cref="BalanceFields"/> 派生。
    /// 保留这个成员名是为了不动既有调用方。
    /// </summary>
    public static IReadOnlyDictionary<string, (double Min, double Max)> Ranges => BalanceFields.Ranges;

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

    public static double ClampField(string field, double value) => BalanceFields.ClampField(field, value);

    /// <summary>
    /// v3.4 V34-05:逐字段校验由描述符驱动。
    /// 旧实现按 <c>property.Name</c> 去查 camelCase 键 —— 大小写靠字典的 OrdinalIgnoreCase 兜着,
    /// 一旦有字段漏登记就静默跳过校验;现在漏登记会被 BalanceFields.AuditCoverage 直接抓出来。
    /// </summary>
    public static void Validate(BalanceConfig config)
    {
        foreach (var field in BalanceFields.All)
        {
            if (field.IsOutOfRange(config, out var value))
                throw new InvalidDataException(
                    $"{field.Property} 须在 {field.Min:0.###}~{field.Max:0.###}(当前 {value:0.###})");
        }

        // 跨字段约束不属于单字段描述符,显式保留
        if (config.EmberSpeedMin > config.EmberSpeedMax)
            throw new InvalidDataException("emberSpeedMin 不得大于 emberSpeedMax");
    }

    /// <summary>
    /// v3.4 V34-05:Clone 由字段描述符逐项复制。
    /// 旧实现是 50 行手写赋值 —— 加字段忘了补一行,预设/剧本/试跑就会静默丢值(且不报错)。
    /// </summary>
    public static BalanceConfig Clone(BalanceConfig source) => BalanceFields.Clone(source);
}
