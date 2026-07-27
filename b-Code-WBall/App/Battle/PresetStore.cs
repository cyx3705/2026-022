using System.IO;
using System.Text.Json;
using AppShell.Core.Logging;

namespace WBall.Battle;

public sealed class BattlePreset
{
    public string Name { get; set; } = "untitled";
    public ArenaLayoutConfig Arena { get; set; } = new();
    public BalanceConfig Balance { get; set; } = new();
}

/// <summary>v3.2 数值预设：只携带 arena 与 balance，不包含场景、炮台或武器库。</summary>
public sealed class PresetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _directory;
    private readonly IShellLog _log;

    public PresetStore(string dataRoot, IShellLog log)
    {
        _directory = Path.Combine(dataRoot, "presets");
        _log = log;
        Directory.CreateDirectory(_directory);
        SeedBuiltInsOnce();
    }

    public string DirectoryPath => _directory;

    public IReadOnlyList<string> List() => Directory.EnumerateFiles(_directory, "*.json")
        .Select(Path.GetFileNameWithoutExtension)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
        .Select(x => x!)
        .ToList();

    public BattlePreset Load(string name)
    {
        var path = ResolvePath(name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"预设不存在: {name}", path);
        var preset = JsonSerializer.Deserialize<BattlePreset>(File.ReadAllText(path), JsonOptions)
                     ?? throw new InvalidDataException("预设为空");
        BalanceConfigStore.Validate(preset.Balance);
        preset.Name = Sanitize(preset.Name);
        return Clone(preset);
    }

    public string Save(string name, ArenaLayoutConfig arena, BalanceConfig balance)
    {
        var safe = Sanitize(name);
        var preset = new BattlePreset
        {
            Name = safe,
            Arena = CloneArena(arena),
            Balance = BalanceConfigStore.Clone(balance),
        };
        var path = ResolvePath(safe);
        File.WriteAllText(path, JsonSerializer.Serialize(preset, JsonOptions));
        _log.Info("preset", $"已保存数值预设 {path}");
        return path;
    }

    public void Delete(string name)
    {
        var path = ResolvePath(name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"预设不存在: {name}", path);
        File.Delete(path);
        _log.Info("preset", $"已删除数值预设 {path}");
    }

    private void SeedBuiltInsOnce()
    {
        var marker = Path.Combine(_directory, ".seeded-v3.2-calibrated");
        if (File.Exists(marker))
            return;

        Save("standard", new ArenaLayoutConfig(), new BalanceConfig());

        var rushArena = new ArenaLayoutConfig { SuddenDeathAtSeconds = 120 };
        var rush = new BalanceConfig
        {
            SmallRateBase = 12,
            SmallRatePerAmmo = 0.25,
            SmallRateMax = 180,
            ShellIntervalAmmoFactor = 0.4,
            GrindRatePerSecond = 4,
            HardTimeLimitSeconds = 120,
        };
        Save("rush", rushArena, rush);

        var marathonArena = new ArenaLayoutConfig { SuddenDeathAtSeconds = 480 };
        var marathon = new BalanceConfig
        {
            SmallRateBase = 3,
            SmallRatePerAmmo = 0.08,
            SmallRateMax = 45,
            ShellIntervalAmmoFactor = 0.12,
            GrindRatePerSecond = 1,
        };
        Save("marathon", marathonArena, marathon);
        File.WriteAllText(marker, "v3.2 calibrated presets");
    }

    private string ResolvePath(string name) => Path.Combine(_directory, Sanitize(name) + ".json");

    private static string Sanitize(string name)
    {
        var trimmed = name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            trimmed = trimmed.Replace(c, '_');
        if (trimmed is "" or "." or "..")
            throw new InvalidOperationException("预设名不能为空或路径标记");
        return trimmed;
    }

    public static BattlePreset Clone(BattlePreset source) => new()
    {
        Name = source.Name,
        Arena = CloneArena(source.Arena),
        Balance = BalanceConfigStore.Clone(source.Balance),
    };

    public static ArenaLayoutConfig CloneArena(ArenaLayoutConfig source) =>
        JsonSerializer.Deserialize<ArenaLayoutConfig>(
            JsonSerializer.Serialize(source, JsonOptions), JsonOptions) ?? new ArenaLayoutConfig();
}
