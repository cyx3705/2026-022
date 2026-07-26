using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppShell.Core.Logging;
using WBall.Model;

namespace WBall.Battle;

public sealed class ScenarioSnapshot
{
    public string Name { get; set; } = "untitled";
    public int Seed { get; set; } = 42;
    public string? EconomyScenePath { get; set; }
    public List<TurretDefinition> Turrets { get; set; } = [];
    public ArenaLayoutConfig Arena { get; set; } = new();
    public List<WeaponDefinition> Weapons { get; set; } = [];
    public string AppVersion { get; set; } = "2.0.0";
}

/// <summary>对战剧本存取(workspace/scenarios)。</summary>
public sealed class ScenarioStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _directory;
    private readonly string _scenesDirectory;
    private readonly IShellLog _log;

    public ScenarioStore(string workspaceRoot, IShellLog log)
    {
        _directory = Path.Combine(workspaceRoot, "scenarios");
        _scenesDirectory = Path.Combine(workspaceRoot, "scenes");
        _log = log;
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(_scenesDirectory);
        EnsureExamples();
    }

    public string DirectoryPath => _directory;
    public string ScenesDirectory => _scenesDirectory;
    public string PlinkoScenePath => Path.Combine(_scenesDirectory, PlinkoDemoSeeder.SceneFileName);

    public IReadOnlyList<string> List() =>
        Directory.EnumerateFiles(_directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(x => x!)
            .ToList();

    public ScenarioSnapshot Load(string name)
    {
        var path = ResolvePath(name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"剧本不存在: {name}", path);
        var snap = JsonSerializer.Deserialize<ScenarioSnapshot>(File.ReadAllText(path), JsonOptions)
                   ?? throw new InvalidDataException("剧本为空");
        if (snap.Turrets.Count < 2)
            throw new InvalidDataException("剧本至少需要 2 座炮台");
        return snap;
    }

    public string Save(ScenarioSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Name))
            throw new InvalidOperationException("剧本 name 不能为空");
        var safe = Sanitize(snapshot.Name);
        snapshot.Name = safe;
        var path = ResolvePath(safe);
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOptions));
        _log.Info("scenario", $"已保存剧本 {path}");
        return path;
    }

    public void Apply(
        ScenarioSnapshot snapshot,
        BattleConfigStore battleConfig,
        WeaponCatalog weapons,
        SceneWorld? economyWorld = null)
    {
        battleConfig.Replace(snapshot.Turrets, snapshot.Arena);
        File.WriteAllText(weapons.Path, JsonSerializer.Serialize(snapshot.Weapons, JsonOptions));
        weapons.Reload();
        if (economyWorld != null)
            TryLoadEconomyScene(snapshot, economyWorld);
    }

    public void TryLoadEconomyScene(ScenarioSnapshot snapshot, SceneWorld economyWorld)
    {
        var relative = snapshot.EconomyScenePath;
        if (string.IsNullOrWhiteSpace(relative))
            return;
        var full = Path.IsPathRooted(relative)
            ? relative
            : Path.Combine(_scenesDirectory, relative);
        if (!File.Exists(full))
        {
            _log.Warn("scenario", $"剧本场景不存在: {full}");
            return;
        }
        SceneStore.Load(economyWorld, full);
        _log.Info("scenario", $"已加载经济场景 {full}");
    }

    public ScenarioSnapshot Capture(
        string name,
        int seed,
        BattleConfigStore battleConfig,
        WeaponCatalog weapons,
        string? economyScenePath)
    {
        return new ScenarioSnapshot
        {
            Name = name,
            Seed = seed,
            EconomyScenePath = economyScenePath,
            Turrets = battleConfig.Turrets.Select(CloneTurret).ToList(),
            Arena = CloneArena(battleConfig.Arena),
            Weapons = weapons.Weapons.Select(CloneWeapon).ToList(),
        };
    }

    private void EnsureExamples()
    {
        PlinkoDemoSeeder.EnsureScene(_scenesDirectory, _log);
        var plinkoRelative = PlinkoDemoSeeder.SceneFileName;

        // 始终刷新示例剧本,保证绑上 plinko 场景
        Save(new ScenarioSnapshot
        {
            Name = "demo2",
            Seed = 7,
            EconomyScenePath = plinkoRelative,
            Turrets =
            [
                new() { Id = "blue", Name = "Azure", Color = "#3B82F6", Quadrant = 2, InitialBalls = 4 },
                new() { Id = "red", Name = "Crimson", Color = "#EF4444", Quadrant = 1, InitialBalls = 4 },
            ],
            Arena = new ArenaLayoutConfig
            {
                Name = "quad4",
                Targeting = "spin",
                GravityG = 0,
                Width = 960,
                Height = 900,
                TurretRadius = 26,
            },
            Weapons = DefaultWeapons(),
        });

        Save(new ScenarioSnapshot
        {
            Name = "demo4",
            Seed = 42,
            EconomyScenePath = plinkoRelative,
            Turrets =
            [
                new() { Id = "green", Name = "Caleb", Color = "#22C55E", Quadrant = 2, InitialBalls = 4, MaxHp = 20_000_000, InitialShield = 2_000_000 },
                new() { Id = "cyan", Name = "Xiaolin", Color = "#06B6D4", Quadrant = 1, InitialBalls = 4, MaxHp = 20_000_000, InitialShield = 2_000_000 },
                new() { Id = "orange", Name = "Diu", Color = "#F97316", Quadrant = 3, InitialBalls = 4, MaxHp = 20_000_000, InitialShield = 2_000_000 },
                new() { Id = "magenta", Name = "Wemmbu", Color = "#EC4899", Quadrant = 4, InitialBalls = 4, MaxHp = 20_000_000, InitialShield = 2_000_000 },
            ],
            Arena = new ArenaLayoutConfig
            {
                Name = "quad4",
                Targeting = "spin",
                GravityG = 0,
                Width = 960,
                Height = 900,
                TurretRadius = 26,
            },
            Weapons = DefaultWeapons(),
        });
    }

    private string ResolvePath(string name) =>
        Path.Combine(_directory, Sanitize(name) + ".json");

    private static string Sanitize(string name)
    {
        var trimmed = name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            trimmed = trimmed.Replace(c, '_');
        return string.IsNullOrWhiteSpace(trimmed) ? "untitled" : trimmed;
    }

    private static TurretDefinition CloneTurret(TurretDefinition x) => new()
    {
        Id = x.Id,
        Name = x.Name,
        Color = x.Color,
        Quadrant = x.Quadrant,
        InitialBalls = x.InitialBalls,
        InitialMultiplier = x.InitialMultiplier,
        MaxHp = x.MaxHp,
        MaxShield = x.MaxShield,
        InitialShield = x.InitialShield,
        ProjectileSize = x.ProjectileSize,
        ProjectileCount = x.ProjectileCount,
        FireIntervalSec = x.FireIntervalSec,
        BarrelRpm = x.BarrelRpm,
    };

    private static ArenaLayoutConfig CloneArena(ArenaLayoutConfig x) => new()
    {
        Name = x.Name,
        Width = x.Width,
        Height = x.Height,
        GravityG = x.GravityG,
        BallCollision = x.BallCollision,
        Targeting = x.Targeting,
        TurretRadius = x.TurretRadius,
        MaxProjectiles = x.MaxProjectiles,
        ProjectileLifetimeSec = x.ProjectileLifetimeSec,
        Mode = x.Mode,
        CellSize = x.CellSize,
    };

    private static WeaponDefinition CloneWeapon(WeaponDefinition x) => new()
    {
        Name = x.Name,
        Aliases = x.Aliases.ToList(),
        Kind = x.Kind,
        Enabled = x.Enabled,
        DamageCoefficient = x.DamageCoefficient,
        Speed = x.Speed,
        SpreadDegrees = x.SpreadDegrees,
        BaseCount = x.BaseCount,
        EconomyScale = x.EconomyScale,
        UnlockAtSeconds = x.UnlockAtSeconds,
        Color = x.Color,
    };

    private static List<WeaponDefinition> DefaultWeapons() =>
    [
        new() { Name = "RUN", Aliases = ["通用积分"], Kind = WeaponKind.Score, Color = "#F8FAFC" },
        new() { Name = "大球", Aliases = ["BigBall"], Kind = WeaponKind.Size, DamageCoefficient = 2.4, Speed = 240, EconomyScale = 0.8, Color = "#F59E0B" },
        new() { Name = "小球", Aliases = ["SmallBall"], Kind = WeaponKind.Count, DamageCoefficient = 0.5, BaseCount = 3, EconomyScale = 0.25, Color = "#38BDF8" },
        new() { Name = "护盾", Aliases = ["Shield"], Kind = WeaponKind.Shield, EconomyScale = 1, Color = "#22D3EE" },
        new() { Name = "散弹", Aliases = ["Shotgun"], Kind = WeaponKind.Burst, DamageCoefficient = 0.7, BaseCount = 8, SpreadDegrees = 32, UnlockAtSeconds = 1200, Color = "#FB7185" },
        new() { Name = "直射", Aliases = ["Direct"], Kind = WeaponKind.Burst, DamageCoefficient = 1.8, SpreadDegrees = 0.5, Color = "#A78BFA" },
        new() { Name = "齐射", Aliases = ["Volley"], Kind = WeaponKind.Burst, DamageCoefficient = 1.1, BaseCount = 5, SpreadDegrees = 10, Color = "#34D399" },
        new() { Name = "中子星", Aliases = ["Neutron"], Kind = WeaponKind.Gravity, DamageCoefficient = 4, Speed = 180, Color = "#C084FC" },
        new() { Name = "散血球", Aliases = ["BloodSplit"], Kind = WeaponKind.Split, DamageCoefficient = 1.2, BaseCount = 4, Color = "#EF4444" },
    ];
}
