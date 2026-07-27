using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppShell.Core.Logging;

namespace WBall.Battle;

public enum WeaponKind
{
    Score,
    Size,
    Count,
    Shield,
    Burst,
    Pierce,
    Split,
    Gravity,
}

public sealed class WeaponDefinition
{
    public required string Name { get; set; }
    public List<string> Aliases { get; set; } = [];
    public WeaponKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
    public double DamageCoefficient { get; set; } = 1;
    public double Speed { get; set; } = 360;
    public double SpreadDegrees { get; set; } = 6;
    public int BaseCount { get; set; } = 1;
    public double EconomyScale { get; set; } = 1;
    public double UnlockAtSeconds { get; set; }
    public string Color { get; set; } = "#FFFFFF";
}

public sealed class WeaponCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly IShellLog _log;
    private Dictionary<string, WeaponDefinition> _byName = new(StringComparer.OrdinalIgnoreCase);

    public WeaponCatalog(string dataRoot, IShellLog log)
    {
        _path = System.IO.Path.Combine(dataRoot, "weapons.json");
        _log = log;
        Reload();
    }

    private WeaponCatalog(IReadOnlyList<WeaponDefinition> weapons, IShellLog log)
    {
        _path = "";
        _log = log;
        Weapons = CloneDefinitions(weapons);
        Validate(Weapons);
        RebuildIndex();
    }

    public IReadOnlyList<WeaponDefinition> Weapons { get; private set; } = [];

    public string Path => _path;

    public static WeaponCatalog CreateMemory(IReadOnlyList<WeaponDefinition> weapons, IShellLog log) =>
        new(weapons, log);

    public static IReadOnlyList<WeaponDefinition> CloneDefinitions(IReadOnlyList<WeaponDefinition> weapons) =>
        JsonSerializer.Deserialize<List<WeaponDefinition>>(
            JsonSerializer.Serialize(weapons, JsonOptions), JsonOptions) ?? [];

    public bool TryResolve(string? name, out WeaponDefinition weapon)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            weapon = null!;
            return false;
        }
        return _byName.TryGetValue(name.Trim(), out weapon!);
    }

    public void Reload()
    {
        if (!File.Exists(_path))
            Save(DefaultWeapons());

        try
        {
            var loaded = JsonSerializer.Deserialize<List<WeaponDefinition>>(
                File.ReadAllText(_path),
                JsonOptions) ?? [];
            Validate(loaded);
            Weapons = loaded;
            RebuildIndex();
            _log.Info("weapon", $"已加载武器库 {Weapons.Count} 项: {_path}");
        }
        catch (Exception ex)
        {
            _log.Error("weapon", $"武器库加载失败,使用内置模板: {ex.Message}");
            Weapons = DefaultWeapons();
            RebuildIndex();
        }
    }

    public WeaponDefinition Set(string name, string key, string value)
    {
        if (!TryResolve(name, out var weapon))
            throw new InvalidOperationException($"未知武器: {name}");

        switch (key.Trim().ToLowerInvariant())
        {
            case "enabled": weapon.Enabled = ParseBool(value); break;
            case "kind": weapon.Kind = Enum.Parse<WeaponKind>(value, true); break;
            case "damage": weapon.DamageCoefficient = ParseDouble(value, 0, 1_000_000); break;
            case "speed": weapon.Speed = ParseDouble(value, 1, 10_000); break;
            case "spread": weapon.SpreadDegrees = ParseDouble(value, 0, 180); break;
            case "count": weapon.BaseCount = Math.Clamp(ParseInt(value), 1, 10_000); break;
            case "scale": weapon.EconomyScale = ParseDouble(value, 0, 1_000_000); break;
            case "unlock": weapon.UnlockAtSeconds = ParseDouble(value, 0, 86_400); break;
            case "color": weapon.Color = value; break;
            default: throw new InvalidOperationException($"不可设置的武器字段: {key}");
        }

        Save(Weapons);
        RebuildIndex();
        return weapon;
    }

    private void Save(IEnumerable<WeaponDefinition> weapons)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(weapons, JsonOptions));
    }

    private void RebuildIndex()
    {
        var index = new Dictionary<string, WeaponDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var weapon in Weapons)
        {
            index[weapon.Name] = weapon;
            foreach (var alias in weapon.Aliases.Where(x => !string.IsNullOrWhiteSpace(x)))
                index[alias] = weapon;
        }
        _byName = index;
    }

    private static void Validate(IReadOnlyList<WeaponDefinition> weapons)
    {
        if (weapons.Count == 0)
            throw new InvalidDataException("武器库不能为空");
        if (weapons.Any(x => string.IsNullOrWhiteSpace(x.Name)))
            throw new InvalidDataException("武器 name 不能为空");
        var duplicate = weapons.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1);
        if (duplicate != null)
            throw new InvalidDataException($"武器 name 重复: {duplicate.Key}");
    }

    private static bool ParseBool(string value) => value.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "on" => true,
        "false" or "0" or "no" or "off" => false,
        _ => throw new FormatException("布尔值须为 true/false 或 on/off"),
    };

    private static int ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new FormatException($"不是整数: {value}");

    private static double ParseDouble(string value, double min, double max)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            throw new FormatException($"不是数字: {value}");
        return Math.Clamp(result, min, max);
    }

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
