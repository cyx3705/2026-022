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

    public BattleConfigStore(string dataRoot, IShellLog log)
    {
        _turretsPath = Path.Combine(dataRoot, "turrets.json");
        _arenaPath = Path.Combine(dataRoot, "arena_layout.json");
        _log = log;
        Reload();
    }

    public IReadOnlyList<TurretDefinition> Turrets { get; private set; } = [];
    public ArenaLayoutConfig Arena { get; private set; } = new();
    public string TurretsPath => _turretsPath;
    public string ArenaPath => _arenaPath;

    public void Replace(IReadOnlyList<TurretDefinition> turrets, ArenaLayoutConfig arena)
    {
        Validate(turrets, arena);
        Turrets = turrets.ToList();
        Arena = arena;
        Save();
    }

    public void Reload()
    {
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
    }

    private static List<TurretDefinition> Defaults() =>
    [
        new() { Id = "blue", Name = "蓝方", Color = "#3B82F6", Quadrant = 2 },
        new() { Id = "red", Name = "红方", Color = "#EF4444", Quadrant = 1 },
        new() { Id = "green", Name = "绿方", Color = "#22C55E", Quadrant = 3 },
        new() { Id = "yellow", Name = "黄方", Color = "#EAB308", Quadrant = 4 },
    ];
}
