using System.Globalization;
using System.Reflection;

namespace WBall.Battle;

public enum BalanceFieldKind
{
    Double,
    Int,
    Long,
    Bool,
}

/// <summary>
/// v3.4 V34-05:一个平衡字段的全部元数据。
/// 加字段只改这里一行 + 该字段的行为映射,不再同步改"范围表 / Clone / 命令 switch / UI 列表"四处。
/// </summary>
public sealed class BalanceFieldDescriptor
{
    internal BalanceFieldDescriptor(
        string property,
        string json,
        string command,
        string parameter,
        string group,
        string label,
        string scope,
        BalanceFieldKind kind,
        double? min = null,
        double? max = null)
    {
        Property = property;
        Json = json;
        Command = command;
        Parameter = parameter;
        Group = group;
        Label = label;
        Scope = scope;
        Kind = kind;
        Min = min;
        Max = max;
        Info = typeof(BalanceConfig).GetProperty(property)
               ?? throw new InvalidOperationException($"BalanceConfig 缺少属性 {property}");
    }

    /// <summary>BalanceConfig 上的属性名(PascalCase)。</summary>
    public string Property { get; }

    /// <summary>battle_balance.json 里的键(camelCase),也是范围表与 clamp 的键。</summary>
    public string Json { get; }

    /// <summary>所属命令,如 balance.rate。</summary>
    public string Command { get; }

    /// <summary>命令参数名,如 smallBase。</summary>
    public string Parameter { get; }

    /// <summary>UI 分组标题。</summary>
    public string Group { get; }

    /// <summary>UI 显示名。</summary>
    public string Label { get; }

    /// <summary>生效时机 / 作用域说明(UI 提示与 balance.config 标注共用)。</summary>
    public string Scope { get; }

    public BalanceFieldKind Kind { get; }

    /// <summary>数值下限(bool 字段为 null)。</summary>
    public double? Min { get; }

    /// <summary>数值上限(bool 字段为 null)。</summary>
    public double? Max { get; }

    public bool IsBoolean => Kind == BalanceFieldKind.Bool;

    private PropertyInfo Info { get; }

    public double GetNumber(BalanceConfig config) =>
        Convert.ToDouble(Info.GetValue(config), CultureInfo.InvariantCulture);

    public bool GetBool(BalanceConfig config) => (bool)(Info.GetValue(config) ?? false);

    /// <summary>
    /// 夹取范围后写入(命令层与 UI 共用同一夹取语义)。
    /// 装箱类型按**属性的真实 CLR 类型**决定,而不是按声明的 Kind ——
    /// Kind 只影响命令参数解析类型;若两者不一致,由 <see cref="BalanceFields.AuditCoverage"/> 报出来,
    /// 而不是在这里抛 ArgumentException。
    /// </summary>
    public void SetNumber(BalanceConfig config, double value)
    {
        if (IsBoolean)
            throw new InvalidOperationException($"{Property} 是布尔字段,应走 SetBool");
        var clamped = Clamp(value);
        var type = Info.PropertyType;

        // 坑:不要写成 `object boxed = kind switch { Int => (int)x, Long => (long)x, _ => x }` ——
        // switch/条件表达式会把各分支统一成公共类型(int/long/double → double),
        // 于是 int 属性会收到装箱的 Double,SetValue 抛 ArgumentException。
        // 这里逐分支显式装箱成 object,装箱类型才真的是属性类型。
        object boxed;
        if (type == typeof(int))
            boxed = (int)Math.Round(clamped, MidpointRounding.AwayFromZero);
        else if (type == typeof(long))
            boxed = (long)Math.Round(clamped, MidpointRounding.AwayFromZero);
        else
            boxed = Convert.ChangeType(clamped, type, CultureInfo.InvariantCulture);

        Info.SetValue(config, boxed);
    }

    /// <summary>声明的 Kind 是否与属性真实类型一致(注册表自检用)。</summary>
    internal bool KindMatchesClrType() => Kind switch
    {
        BalanceFieldKind.Bool => Info.PropertyType == typeof(bool),
        BalanceFieldKind.Int => Info.PropertyType == typeof(int),
        BalanceFieldKind.Long => Info.PropertyType == typeof(long),
        _ => Info.PropertyType == typeof(double),
    };

    internal string ClrTypeName => Info.PropertyType.Name;

    public void SetBool(BalanceConfig config, bool value)
    {
        if (!IsBoolean)
            throw new InvalidOperationException($"{Property} 不是布尔字段");
        Info.SetValue(config, value);
    }

    public double Clamp(double value) =>
        Min is null || Max is null ? value : Math.Clamp(value, Min.Value, Max.Value);

    /// <summary>字段级复制:Clone 由此驱动,不再手写 50 行赋值(手写就会漏)。</summary>
    public void CopyTo(BalanceConfig source, BalanceConfig target) =>
        Info.SetValue(target, Info.GetValue(source));

    /// <summary>值是否越界(加载期校验用)。</summary>
    public bool IsOutOfRange(BalanceConfig config, out double value)
    {
        if (IsBoolean)
        {
            value = 0;
            return false;
        }
        value = GetNumber(config);
        return !double.IsFinite(value)
               || (Min is not null && value < Min.Value)
               || (Max is not null && value > Max.Value);
    }
}

/// <summary>
/// v3.4 V34-05:战斗平衡字段的**唯一真相**。范围校验、JSON 迁移、Clone、
/// balance.* 命令参数规格与读写、以及「战斗平衡」窗控件,全部从这张表派生。
///
/// 不在此表内的东西(有意保留):
/// - 每条命令的回显措辞 —— 各命令口径不同(ember 合并成 speed=min~max、pack 附当前池/包值、
///   duel 带兼容别名说明),属人面向文案,不做机器生成;
/// - 兼容别名(如 duel 的 merge 同时写两个属性)—— 单独声明在命令层,不混进字段表。
/// </summary>
public static class BalanceFields
{
    private const string TerritoryImmediate = "即时 / territory";

    public static IReadOnlyList<BalanceFieldDescriptor> All { get; } =
    [
        // ── 火力节奏(balance.rate)──────────────────────────────
        D("ShellIntervalAmmoFactor", "shellFactor", "火力节奏", "大球提速系数", 0, 5, "balance.rate"),
        D("ShellIntervalFloorSec", "shellFloor", "火力节奏", "大球间隔下限(s)", 0.02, 5, "balance.rate"),
        D("SmallRateBase", "smallBase", "火力节奏", "小球基础射速", 0, 200, "balance.rate"),
        D("SmallRatePerAmmo", "smallPerAmmo", "火力节奏", "每点弹药射速", 0, 5, "balance.rate"),
        D("SmallRateMax", "smallMax", "火力节奏", "小球射速上限", 1, 5000, "balance.rate"),
        D("SmallRateFrozenFactor", "frozenFactor", "火力节奏", "定格射速倍率", 1, 10, "balance.rate"),
        D("SmallRateFrozenMax", "frozenMax", "火力节奏", "定格射速上限", 1, 900, "balance.rate"),
        D("SmallSpreadDeg", "spread", "火力节奏", "小球散布(°)", 0, 90, "balance.rate"),
        D("SmallSpreadFrozenDeg", "frozenSpread", "火力节奏", "定格散布(°)", 0, 90, "balance.rate"),
        I("VolleyRingCount", "volley", "火力节奏", "齐射环发数", 4, 120, "balance.rate"),
        I("VolleyPendingMax", "pending", "火力节奏", "齐射待发上限", 1, 64, "balance.rate"),
        D("FreezeSecondsPerValue", "freezePerValue", "火力节奏", "每点定格秒数", 0, 2, "balance.rate"),
        D("FreezeMaxSeconds", "freezeMax", "火力节奏", "定格时长上限", 0, 60, "balance.rate"),
        I("AmmoQueueGuard", "ammoGuard", "火力节奏", "队列防 OOM 硬顶", 512, 100_000_000, "balance.rate"),

        // ── 小球升格(balance.pack)──────────────────────────────
        L("SmallPackThreshold", "threshold", "小球升格", "升格阈值", 0, 1_000_000_000, "balance.pack",
            "即时 / territory; 0=关闭"),
        I("SmallPackRatio", "ratio", "小球升格", "分档倍率", 2, 10, "balance.pack"),
        I("SmallPackMax", "max", "小球升格", "包值上限", 2, 4096, "balance.pack"),
        B("SmallPackSpeedFollowsSmall", "followSmall", "小球升格", "沿用小球速度", "balance.pack"),

        // ── 对消与融合(balance.duel)────────────────────────────
        D("HaloReachFactor", "halo", "对消与融合", "光晕范围系数", 1, 4, "balance.duel"),
        D("GrindRatePerSecond", "grind", "对消与融合", "研磨速率", 0.1, 50, "balance.duel"),
        // MergeSameOwnerSmall 没有独立命令参数:由 balance.duel 的 merge 兼容别名与
        // FriendlyAssistEnabled 一起写(命令层声明),但它仍是持久化字段,必须进表参与 Clone/迁移。
        B("MergeSameOwnerSmall", "", "对消与融合", "同色小球融入(兼容别名 merge)", "balance.duel"),

        // ── 同阵营助力与回收(balance.assist)────────────────────
        B("FriendlyAssistEnabled", "enabled", "同阵营助力与回收", "启用低速助力", "balance.assist"),
        B("FriendlyAssistVisualEnabled", "visual", "同阵营助力与回收", "显示助力连线", "balance.assist",
            "即时 / 纯视觉"),
        D("FriendlyAbsorbSmallRate", "smallRate", "同阵营助力与回收", "大球吸收小球(点/秒)", 0, 10, "balance.assist",
            "即时 / 低速机制"),
        D("FriendlyShellTransferRate", "shellRate", "同阵营助力与回收", "大球之间助力(点/秒)", 0, 10, "balance.assist",
            "即时 / 低速机制"),
        D("FriendlyAssistReachFactor", "reach", "同阵营助力与回收", "助力范围系数", 1, 3, "balance.assist"),
        I("FriendlyAssistMaxValue", "max", "同阵营助力与回收", "单球积分上限", 2, 1_000_000, "balance.assist"),

        // ── 护罩与触杀(balance.shield)──────────────────────────
        // 仅供旧哈希回退测试；不序列化、不暴露到命令或 UI。
        B("ShieldBreakthrough", "", "兼容", "旧版破盾直入", ""),
        B("ContactKillEnabled", "contact", "护罩与触杀", "炮台触杀", "balance.shield"),
        B("SelfShieldRefundEnabled", "refund", "护罩与触杀", "自家小球回充", "balance.shield"),
        B("SuddenDeathShieldBlock", "suddenBlock", "护罩与触杀", "决胜期封锁护盾", "balance.shield"),
        D("ShieldSlotGainPerValue", "slotGain", "护罩与触杀", "护盾槽每点增益", 0, 1_000_000, "balance.shield",
            "即时 / 两者; 建议对拍 50000"),
        D("ShieldRegenPerSecond", "regen", "护罩与触杀", "自然再生/秒", 0, 1_000_000, "balance.shield",
            "即时 / 两者; 默认 0"),

        // ── 余烬爆发(balance.ember)─────────────────────────────
        D("EmberSpeedMin", "speedMin", "余烬爆发", "余烬最低速度", 10, 3000, "balance.ember"),
        D("EmberSpeedMax", "speedMax", "余烬爆发", "余烬最高速度", 10, 3000, "balance.ember"),
        B("EmberFromAmmo", "ammo", "余烬爆发", "弹药转余烬", "balance.ember"),
        B("EmberDrainEconomy", "economy", "余烬爆发", "吸收经济球", "balance.ember"),

        // ── 经济到火力(balance.economy;direct 为主)─────────────
        D("IntensityExponent", "exponent", "经济到火力", "强度指数", 0.1, 1, "balance.economy", "即时 / direct 为主"),
        D("SizeGainBase", "sizeBase", "经济到火力", "尺寸基数", 2, 60, "balance.economy", "即时 / direct 为主"),
        D("BurstDamageGain", "burstDamage", "经济到火力", "爆发伤害增益", 0, 1, "balance.economy", "即时 / direct 为主"),
        D("BurstSpreadGain", "burstSpread", "经济到火力", "爆发散布增益", 0, 1, "balance.economy", "即时 / direct 为主"),
        D("PierceDamageGain", "pierce", "经济到火力", "穿透伤害增益", 0, 1, "balance.economy", "即时 / direct 为主"),
        D("GravitySizeGain", "gravitySize", "经济到火力", "重力尺寸增益", 0, 2, "balance.economy", "即时 / direct 为主"),
        D("GravityDamageGain", "gravityDamage", "经济到火力", "重力伤害增益", 0, 2, "balance.economy", "即时 / direct 为主"),
        D("ScoreDamageGain", "score", "经济到火力", "积分伤害增益", 0, 1, "balance.economy", "即时 / direct 为主"),

        // ── 战场物理(balance.physics;仅右世界)─────────────────
        D("WallRestitution", "wall", "战场物理", "墙面弹性", 0, 1, "balance.physics", "即时 / 仅右世界"),
        D("BallRestitution", "ball", "战场物理", "弹体碰撞弹性", 0, 1, "balance.physics", "即时 / 仅右世界"),

        // ── 收敛与胜负(balance.round)───────────────────────────
        D("CountdownSeconds", "countdown", "收敛与胜负", "开局倒计时(s)", 0, 30, "balance.round", "下局"),
        D("SettleSeconds", "settle", "收敛与胜负", "结算展示(s)", 0, 30, "balance.round", "即时"),
        D("HardTimeLimitSeconds", "limit", "收敛与胜负", "硬性时限(s)", 0, 7200, "balance.round", "即时; 0=关闭"),
    ];

    /// <summary>按 JSON 键索引(范围表与 clamp 用)。</summary>
    public static IReadOnlyDictionary<string, BalanceFieldDescriptor> ByJson { get; } =
        All.Where(x => x.Json.Length > 0)
            .ToDictionary(x => x.Json, x => x, StringComparer.OrdinalIgnoreCase);

    /// <summary>按属性名索引。</summary>
    public static IReadOnlyDictionary<string, BalanceFieldDescriptor> ByProperty { get; } =
        All.ToDictionary(x => x.Property, x => x, StringComparer.Ordinal);

    /// <summary>某条命令下、带命令参数的字段(声明顺序即回显与参数规格顺序)。</summary>
    public static IReadOnlyList<BalanceFieldDescriptor> ForCommand(string command) =>
        All.Where(x => x.Json.Length > 0
                       && x.Parameter.Length > 0
                       && x.Command.Equals(command, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>UI 分组(声明顺序)。</summary>
    public static IReadOnlyList<(string Group, string Command, IReadOnlyList<BalanceFieldDescriptor> Fields)> Groups { get; } =
        All.Where(x => x.Parameter.Length > 0)
            .GroupBy(x => (x.Group, x.Command))
            .Select(g => (g.Key.Group, g.Key.Command, (IReadOnlyList<BalanceFieldDescriptor>)g.ToList()))
            .ToList();

    /// <summary>范围表(兼容 BalanceConfigStore.Ranges 的旧形状)。</summary>
    public static IReadOnlyDictionary<string, (double Min, double Max)> Ranges { get; } =
        All.Where(x => x.Json.Length > 0 && x.Min is not null && x.Max is not null)
            .ToDictionary(x => x.Json, x => (x.Min!.Value, x.Max!.Value), StringComparer.OrdinalIgnoreCase);

    public static double ClampField(string json, double value) =>
        ByJson.TryGetValue(json, out var field) ? field.Clamp(value) : value;

    /// <summary>字段级逐项复制 —— 手写 Clone 漏字段的老问题由此绝根。</summary>
    public static BalanceConfig Clone(BalanceConfig source)
    {
        var target = new BalanceConfig();
        foreach (var field in All)
            field.CopyTo(source, target);
        return target;
    }

    /// <summary>
    /// 覆盖完整性自检:BalanceConfig 的每个可读写属性都必须在表内(反之亦然)。
    /// 由无头验证调用 —— 以后加字段忘记登记,验证直接红,而不是等到某条路径静默丢值。
    /// </summary>
    public static IReadOnlyList<string> AuditCoverage()
    {
        var problems = new List<string>();
        var declared = All.Select(x => x.Property).ToHashSet(StringComparer.Ordinal);
        foreach (var property in typeof(BalanceConfig).GetProperties())
        {
            if (!property.CanRead || !property.CanWrite)
                continue;
            if (!declared.Contains(property.Name))
                problems.Add($"BalanceConfig.{property.Name} 未登记进 BalanceFields");
        }

        foreach (var field in All)
        {
            if (!field.IsBoolean && (field.Min is null || field.Max is null))
                problems.Add($"{field.Property} 是数值字段但没有范围");
            if (field.IsBoolean && (field.Min is not null || field.Max is not null))
                problems.Add($"{field.Property} 是布尔字段但声明了范围");
            if (!field.KindMatchesClrType())
                problems.Add($"{field.Property} 声明为 {field.Kind} 但实际类型是 {field.ClrTypeName}");
        }

        var duplicateJson = All.Where(x => x.Json.Length > 0)
            .GroupBy(x => x.Json, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
        problems.AddRange(duplicateJson.Select(json => $"JSON 键重复: {json}"));

        var duplicateParam = All.Where(x => x.Parameter.Length > 0)
            .GroupBy(x => (x.Command, x.Parameter))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Command} 参数重复: {g.Key.Parameter}");
        problems.AddRange(duplicateParam);
        return problems;
    }

    private static BalanceFieldDescriptor D(
        string property, string parameter, string group, string label,
        double min, double max, string command, string scope = TerritoryImmediate) =>
        new(property, ToJson(property), command, parameter, group, label, scope,
            BalanceFieldKind.Double, min, max);

    private static BalanceFieldDescriptor I(
        string property, string parameter, string group, string label,
        double min, double max, string command, string scope = TerritoryImmediate) =>
        new(property, ToJson(property), command, parameter, group, label, scope,
            BalanceFieldKind.Int, min, max);

    private static BalanceFieldDescriptor L(
        string property, string parameter, string group, string label,
        double min, double max, string command, string scope = TerritoryImmediate) =>
        new(property, ToJson(property), command, parameter, group, label, scope,
            BalanceFieldKind.Long, min, max);

    private static BalanceFieldDescriptor B(
        string property, string parameter, string group, string label,
        string command, string scope = TerritoryImmediate) =>
        new(property, ToJson(property), command, parameter, group, label, scope,
            BalanceFieldKind.Bool);

    /// <summary>PascalCase → camelCase,与 JsonNamingPolicy.CamelCase 对齐(键不再手抄)。</summary>
    private static string ToJson(string property) =>
        char.ToLowerInvariant(property[0]) + property[1..];
}
