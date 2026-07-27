using System.Text.Json;
using System.IO;
using AppShell.Core.Logging;

namespace WBall.Battle;

public sealed class TurretDefinition
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Color { get; set; }
    public int Quadrant { get; set; }
    public int InitialBalls { get; set; } = 3;
    public long InitialMultiplier { get; set; } = 1;
    public double MaxHp { get; set; } = 20_000_000;
    public double MaxShield { get; set; } = 5_000_000;
    public double InitialShield { get; set; } = 500_000;
    public double ProjectileSize { get; set; } = 8;
    public int ProjectileCount { get; set; } = 1;
    public double FireIntervalSec { get; set; } = 1.2;
    public double BarrelRpm { get; set; } = 6;
}

public sealed class ArenaLayoutConfig
{
    public string Name { get; set; } = "quad4";
    public double Width { get; set; } = 960;
    public double Height { get; set; } = 900;
    public double GravityG { get; set; }
    public bool BallCollision { get; set; } = true;
    public string Targeting { get; set; } = "spin";
    public double TurretRadius { get; set; } = 26;
    public int MaxProjectiles { get; set; } = 2000;
    public double ProjectileLifetimeSec { get; set; } = 12;

    /// <summary>v2.9:battle 玩法 — territory(领地战,默认) / direct(直击扣血,旧语义)。</summary>
    public string Mode { get; set; } = "territory";

    /// <summary>v2.9:领地格边长(px);v2.10 细化为 10。</summary>
    public double CellSize { get; set; } = 10;

    /// <summary>v2.12.4 TK-07:决胜时刻(秒) — 此后护盾停止一切补给只降不升,保证必出胜者。</summary>
    public double SuddenDeathAtSeconds { get; set; } = 240;

    // ── v3.1:以下字段默认值一律等于 v3.0 硬编码值,不改设置即零行为漂移 ──

    /// <summary>v3.1:炮台离左右边距 ÷ 战场宽。</summary>
    public double TurretMarginXRatio { get; set; } = 0.12;

    /// <summary>v3.1:炮台离上下边距 ÷ 战场高。</summary>
    public double TurretMarginYRatio { get; set; } = 0.14;

    /// <summary>v3.1:护罩环半径 ÷ 炮塔半径。判定与渲染共读此字段(单一真相)。</summary>
    public double ShieldRingScale { get; set; } = 1.55;

    /// <summary>v3.1:护盾计价 — 一点弹体积分磨掉多少护盾(也是自家小球回充量)。</summary>
    public double ShieldCostPerValue { get; set; } = 50_000;

    /// <summary>v3.1:弹体速度总缩放(arena.scale 等比缩放时同乘;武器基速仍归武器库)。</summary>
    public double ProjectileSpeedScale { get; set; } = 1;

    // 大球(领地模式弹体)尺寸映射:size = clamp(cell*factor*value^exp, cell*min, cell*max)
    public double ShellSizeCellFactor { get; set; } = 0.5;
    public double ShellSizeValueExponent { get; set; } = 0.25;
    public double ShellSizeMinCells { get; set; } = 0.5;
    public double ShellSizeMaxCells { get; set; } = 5;

    // 大球动量映射:speed = clamp(基速*jitter/value^exp, min, max) * ProjectileSpeedScale;weight = value*scale
    public double ShellSpeedJitter { get; set; } = 0.25;
    public double ShellSpeedValueExponent { get; set; } = 0.12;
    public double ShellSpeedMin { get; set; } = 60;
    public double ShellSpeedMax { get; set; } = 700;
    public double ShellWeightScale { get; set; } = 1;

    /// <summary>v3.1:开局预载大球发数(0=不预载)。</summary>
    public int InitialShellCount { get; set; } = 12;

    /// <summary>v3.1:开局预载每发数值。</summary>
    public long InitialShellValue { get; set; } = 1;

    /// <summary>v3.1:开局预载武器名(决定基速)。</summary>
    public string InitialShellWeapon { get; set; } = "直射";

    /// <summary>v3.1:小球出膛速度(乘 ProjectileSpeedScale 后生效)。</summary>
    public double SmallBallSpeed { get; set; } = 380;

    /// <summary>v3.1:小球半径 ÷ 格边长。</summary>
    public double SmallBallSizeCellFactor { get; set; } = 0.5;

    // v3.1 Q4:弹体积分数字随规模缩放,但有最小字号;超出球体的部分暗淡,防止数字视觉上取代小球
    public double ShellLabelFontFactor { get; set; } = 0.8;
    public double ShellLabelFontMin { get; set; } = 8;
    public double ShellLabelFontMax { get; set; } = 22;
    public double ShellLabelOutsideOpacity { get; set; } = 0.28;
}

public sealed class BattleConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _turretsPath;
    private readonly string _arenaPath;
    private readonly IShellLog _log;
    private readonly bool _memoryOnly;

    public BattleConfigStore(string dataRoot, IShellLog log)
    {
        _turretsPath = Path.Combine(dataRoot, "turrets.json");
        _arenaPath = Path.Combine(dataRoot, "arena_layout.json");
        _log = log;
        Reload();
    }

    private BattleConfigStore(
        IReadOnlyList<TurretDefinition> turrets,
        ArenaLayoutConfig arena,
        IShellLog log)
    {
        _turretsPath = "";
        _arenaPath = "";
        _log = log;
        _memoryOnly = true;
        Turrets = JsonSerializer.Deserialize<List<TurretDefinition>>(
            JsonSerializer.Serialize(turrets, JsonOptions), JsonOptions) ?? [];
        Arena = JsonSerializer.Deserialize<ArenaLayoutConfig>(
            JsonSerializer.Serialize(arena, JsonOptions), JsonOptions) ?? new ArenaLayoutConfig();
        Validate(Turrets, Arena);
    }

    public IReadOnlyList<TurretDefinition> Turrets { get; private set; } = [];
    public ArenaLayoutConfig Arena { get; private set; } = new();
    public string TurretsPath => _turretsPath;
    public string ArenaPath => _arenaPath;

    public static BattleConfigStore CreateMemory(
        IReadOnlyList<TurretDefinition> turrets,
        ArenaLayoutConfig arena,
        IShellLog log) => new(turrets, arena, log);

    public void Replace(IReadOnlyList<TurretDefinition> turrets, ArenaLayoutConfig arena)
    {
        Validate(turrets, arena);
        Turrets = turrets.ToList();
        Arena = arena;
        Save();
    }

    /// <summary>v3.1 arena.default:arena 段恢复出厂默认(炮台段不动)。</summary>
    public void ResetArenaDefaults()
    {
        Arena = new ArenaLayoutConfig();
        Save();
    }

    /// <summary>v3.1 arena.default turrets=true:炮台数值回默认,保留 id/名称/颜色/象限。</summary>
    public void ResetTurretNumberDefaults()
    {
        var template = new TurretDefinition { Id = "_", Name = "_", Color = "#FFFFFF" };
        foreach (var turret in Turrets)
        {
            turret.InitialBalls = template.InitialBalls;
            turret.InitialMultiplier = template.InitialMultiplier;
            turret.MaxHp = template.MaxHp;
            turret.MaxShield = template.MaxShield;
            turret.InitialShield = template.InitialShield;
            turret.ProjectileSize = template.ProjectileSize;
            turret.ProjectileCount = template.ProjectileCount;
            turret.FireIntervalSec = template.FireIntervalSec;
            turret.BarrelRpm = template.BarrelRpm;
        }
        Save();
    }

    public void Reload()
    {
        if (_memoryOnly)
            return;
        EnsureSeedFiles();
        try
        {
            var turrets = JsonSerializer.Deserialize<List<TurretDefinition>>(
                File.ReadAllText(_turretsPath), JsonOptions) ?? [];
            var arena = JsonSerializer.Deserialize<ArenaLayoutConfig>(
                File.ReadAllText(_arenaPath), JsonOptions) ?? new ArenaLayoutConfig();
            Validate(turrets, arena);
            Turrets = turrets;
            Arena = arena;
            _log.Info("battle", $"已加载炮台 {turrets.Count} 座,阵型 {arena.Name}");
        }
        catch (Exception ex)
        {
            _log.Error("battle", $"炮台/阵型配置无效,使用内置四方模板: {ex.Message}");
            Turrets = Defaults();
            Arena = new ArenaLayoutConfig();
        }
    }

    public void Save()
    {
        if (_memoryOnly)
            return;
        File.WriteAllText(_turretsPath, JsonSerializer.Serialize(Turrets, JsonOptions));
        File.WriteAllText(_arenaPath, JsonSerializer.Serialize(Arena, JsonOptions));
    }

    private void EnsureSeedFiles()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_turretsPath)!);
        if (!File.Exists(_turretsPath))
            File.WriteAllText(_turretsPath, JsonSerializer.Serialize(Defaults(), JsonOptions));
        if (!File.Exists(_arenaPath))
            File.WriteAllText(_arenaPath, JsonSerializer.Serialize(new ArenaLayoutConfig(), JsonOptions));
    }

    /// <summary>v3.1:字段合法区间(命令层 clamp 与本处校验共用同一张表)。</summary>
    public static readonly IReadOnlyDictionary<string, (double Min, double Max)> Ranges =
        new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase)
        {
            ["width"] = (200, 4000),
            ["height"] = (200, 4000),
            ["turretRadius"] = (6, 200),
            ["turretMarginXRatio"] = (0.02, 0.45),
            ["turretMarginYRatio"] = (0.02, 0.45),
            ["shieldRingScale"] = (1.0, 4.0),
            ["shieldCostPerValue"] = (1, 1e9),
            ["projectileSpeedScale"] = (0.05, 20),
            ["cellSize"] = (5, 100),
            ["suddenDeathAtSeconds"] = (0, 3600),
            ["shellSizeCellFactor"] = (0.1, 5),
            ["shellSizeValueExponent"] = (0, 1),
            ["shellSizeMinCells"] = (0.1, 20),
            ["shellSizeMaxCells"] = (0.1, 20),
            ["shellSpeedJitter"] = (0, 0.9),
            ["shellSpeedValueExponent"] = (0, 1),
            ["shellSpeedMin"] = (10, 3000),
            ["shellSpeedMax"] = (10, 3000),
            ["shellWeightScale"] = (0.1, 100),
            ["initialShellCount"] = (0, 512),
            ["initialShellValue"] = (1, 100_000),
            ["smallBallSpeed"] = (20, 3000),
            ["smallBallSizeCellFactor"] = (0.1, 3),
            ["shellLabelFontFactor"] = (0.1, 3),
            ["shellLabelFontMin"] = (2, 40),
            ["shellLabelFontMax"] = (2, 96),
            ["shellLabelOutsideOpacity"] = (0, 1),
            ["gravityG"] = (-50, 50),
            ["maxProjectiles"] = (10, 20_000),
            ["projectileLifetimeSec"] = (0.5, 600),
        };

    public static double ClampField(string field, double value)
    {
        if (!Ranges.TryGetValue(field, out var range))
            return value;
        return Math.Clamp(value, range.Min, range.Max);
    }

    private static void Validate(IReadOnlyList<TurretDefinition> turrets, ArenaLayoutConfig arena)
    {
        if (turrets.Count < 2)
            throw new InvalidDataException("至少需要 2 座炮台");
        if (turrets.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != turrets.Count)
            throw new InvalidDataException("炮台 id 重复");
        if (arena.Width < 200 || arena.Height < 200)
            throw new InvalidDataException("战场尺寸不得小于 200");
        if (arena.MaxProjectiles is < 10 or > 20_000)
            throw new InvalidDataException("maxProjectiles 须在 10~20000");

        // v3.1 AC-03:新字段越界 = 加载失败,走既有"回退内置模板 + Error 日志"语义,不静默 clamp
        RequireRange("turretRadius", arena.TurretRadius);
        RequireRange("turretMarginXRatio", arena.TurretMarginXRatio);
        RequireRange("turretMarginYRatio", arena.TurretMarginYRatio);
        RequireRange("shieldRingScale", arena.ShieldRingScale);
        RequireRange("shieldCostPerValue", arena.ShieldCostPerValue);
        RequireRange("projectileSpeedScale", arena.ProjectileSpeedScale);
        RequireRange("cellSize", arena.CellSize);
        RequireRange("suddenDeathAtSeconds", arena.SuddenDeathAtSeconds);
        RequireRange("shellSizeCellFactor", arena.ShellSizeCellFactor);
        RequireRange("shellSizeValueExponent", arena.ShellSizeValueExponent);
        RequireRange("shellSizeMinCells", arena.ShellSizeMinCells);
        RequireRange("shellSizeMaxCells", arena.ShellSizeMaxCells);
        RequireRange("shellSpeedJitter", arena.ShellSpeedJitter);
        RequireRange("shellSpeedValueExponent", arena.ShellSpeedValueExponent);
        RequireRange("shellSpeedMin", arena.ShellSpeedMin);
        RequireRange("shellSpeedMax", arena.ShellSpeedMax);
        RequireRange("shellWeightScale", arena.ShellWeightScale);
        RequireRange("initialShellCount", arena.InitialShellCount);
        RequireRange("initialShellValue", arena.InitialShellValue);
        RequireRange("smallBallSpeed", arena.SmallBallSpeed);
        RequireRange("smallBallSizeCellFactor", arena.SmallBallSizeCellFactor);
        RequireRange("shellLabelFontFactor", arena.ShellLabelFontFactor);
        RequireRange("shellLabelFontMin", arena.ShellLabelFontMin);
        RequireRange("shellLabelFontMax", arena.ShellLabelFontMax);
        RequireRange("shellLabelOutsideOpacity", arena.ShellLabelOutsideOpacity);
        if (arena.ShellSizeMinCells > arena.ShellSizeMaxCells)
            throw new InvalidDataException("shellSizeMinCells 不得大于 shellSizeMaxCells");
        if (arena.ShellSpeedMin > arena.ShellSpeedMax)
            throw new InvalidDataException("shellSpeedMin 不得大于 shellSpeedMax");
        if (arena.ShellLabelFontMin > arena.ShellLabelFontMax)
            throw new InvalidDataException("shellLabelFontMin 不得大于 shellLabelFontMax");
        if (string.IsNullOrWhiteSpace(arena.InitialShellWeapon))
            throw new InvalidDataException("initialShellWeapon 不能为空");
    }

    private static void RequireRange(string field, double value)
    {
        var (min, max) = Ranges[field];
        if (double.IsNaN(value) || value < min || value > max)
            throw new InvalidDataException($"{field} 须在 {min:0.###}~{max:0.###}(当前 {value:0.###})");
    }

    private static List<TurretDefinition> Defaults() =>
    [
        new() { Id = "blue", Name = "蓝方", Color = "#3B82F6", Quadrant = 2 },
        new() { Id = "red", Name = "红方", Color = "#EF4444", Quadrant = 1 },
        new() { Id = "green", Name = "绿方", Color = "#22C55E", Quadrant = 3 },
        new() { Id = "yellow", Name = "黄方", Color = "#EAB308", Quadrant = 4 },
    ];
}
