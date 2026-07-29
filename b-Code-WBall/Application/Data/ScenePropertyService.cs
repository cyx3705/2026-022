using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using WBall.Model;

namespace WBall.Data;

/// <summary>
/// v3.4:小球自定义性质定义 + 场景文件目录。
///
/// 前身是 <c>PropertyProjection</c> —— 把 SceneWorld 镜像进 SQLite 供表窗口/db.* 读写。
/// 取消数据库后镜像已无接收方,那 872 行里真正还有人调的只剩两件事:
/// 1. 自定义性质名的登记与持久化(property-schema.json),供 ball.addprop/removeprop/setprop;
/// 2. scenes 目录的枚举与改名,供 scene.refresh/scene.rename 与资源窗口。
/// 其余(6 个表名常量、IsManaged、EnsureSchema、按表名的 Update/Insert/Delete 与 WHERE 解析)
/// 外部引用为 0,随本次一并删除 —— 留着只会让人以为还有个库在后面。
///
/// 权威仍是 SceneWorld 与 scenes/*.json;本类不持有运行时状态,不参与确定性。
/// </summary>
public sealed class ScenePropertyService
{
    /// <summary>内建性质:由 Ball 自身字段承载,不能被登记成自定义性质。</summary>
    private static readonly HashSet<string> BuiltInProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "alive", "color", "weight", "size", "multiplier",
    };

    /// <summary>运行时量:权威在物理引擎,禁止用自定义性质覆盖(v1.x 起的老规矩)。</summary>
    private static readonly HashSet<string> ReservedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "x", "y", "vx", "vy",
    };

    private readonly SceneWorld _world;
    private readonly object _gate = new();
    private readonly List<string> _customProperties = new();
    private readonly string? _dataRoot;
    private string? _scenesDirectory;

    public ScenePropertyService(SceneWorld world, string? dataRoot = null)
    {
        _world = world;
        _dataRoot = dataRoot;
        Reload();
    }

    public SceneWorld World => _world;

    /// <summary>已登记的自定义性质名(排除内建与保留名)。</summary>
    public IReadOnlyList<string> CustomProperties
    {
        get
        {
            lock (_gate)
                return _customProperties.ToList();
        }
    }

    /// <summary>从 property-schema.json 与现有小球身上重建登记表。</summary>
    public void Reload()
    {
        lock (_gate)
        {
            _customProperties.Clear();
            foreach (var name in LoadSchema()
                         .Concat(_world.Balls.SelectMany(ball => ball.Props.Keys))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!BuiltInProperties.Contains(name) && !ReservedProperties.Contains(name))
                    _customProperties.Add(name);
            }
        }
    }

    public string AddProperty(string name)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new InvalidOperationException("性质名须为字母/数字/下划线,且不以数字开头");
        if (BuiltInProperties.Contains(name))
            throw new InvalidOperationException($"内建性质请用 ball.set: {name}");
        if (ReservedProperties.Contains(name))
            throw new InvalidOperationException($"运行时量不可自定义: {name}");
        if (CustomProperties.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"性质已存在: {name}");

        lock (_gate)
            _customProperties.Add(name);
        SaveSchema();
        return name;
    }

    public void RemoveProperty(string name)
    {
        name = name.Trim();
        if (BuiltInProperties.Contains(name))
            throw new InvalidOperationException($"不能删除内建性质: {name}");
        if (!CustomProperties.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"性质不存在: {name}");

        lock (_gate)
            _customProperties.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
        foreach (var ball in _world.Balls)
            ball.Props.Remove(name);
        SaveSchema();
    }

    /// <summary>登记一个由场景文件带进来的性质名(加载场景后调用,保证 setprop 认得它)。</summary>
    public void EnsureProperty(string name)
    {
        if (BuiltInProperties.Contains(name) || ReservedProperties.Contains(name))
            return;
        lock (_gate)
        {
            if (_customProperties.Contains(name, StringComparer.OrdinalIgnoreCase))
                return;
            _customProperties.Add(name);
        }
        SaveSchema();
    }

    /// <summary>扫描 scenes 目录,返回场景文件数;同时记住目录供 <see cref="RenameSceneFile"/> 用。</summary>
    public int RefreshScenes(string scenesDirectory, string? currentScenePath = null)
    {
        _scenesDirectory = scenesDirectory;
        Directory.CreateDirectory(scenesDirectory);
        var count = Directory.EnumerateFiles(scenesDirectory, "*.json").Count();
        // 加载新场景可能带进新的自定义性质名,顺手登记
        Reload();
        _ = currentScenePath;
        return count;
    }

    public void RenameSceneFile(string oldFileName, string newFileName)
    {
        if (string.IsNullOrWhiteSpace(_scenesDirectory))
            throw new InvalidOperationException("scenes 目录未知;请先执行 scene.refresh");

        var oldName = WithJsonSuffix(oldFileName);
        var newName = WithJsonSuffix(newFileName);
        var src = Path.GetFullPath(Path.Combine(_scenesDirectory!, oldName));
        var dst = Path.GetFullPath(Path.Combine(_scenesDirectory!, newName));
        var root = Path.GetFullPath(_scenesDirectory!);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        if (!src.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !dst.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("场景路径越出 scenes 目录");
        if (!File.Exists(src))
            throw new InvalidOperationException($"场景不存在: {oldName}");
        if (File.Exists(dst))
            throw new InvalidOperationException($"目标已存在: {newName}");

        File.Move(src, dst);
        if (!string.IsNullOrWhiteSpace(_world.LastScenePath)
            && Path.GetFullPath(_world.LastScenePath).Equals(src, StringComparison.OrdinalIgnoreCase))
            _world.LastScenePath = dst;
    }

    private static string WithJsonSuffix(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + ".json";
    }

    private string SchemaPath => Path.Combine(_dataRoot ?? AppContext.BaseDirectory, "property-schema.json");

    private IReadOnlyList<string> LoadSchema()
    {
        try
        {
            return File.Exists(SchemaPath)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(SchemaPath)) ?? []
                : [];
        }
        catch
        {
            // schema 只是便利登记表,坏了就当空的重建,不该拦住启动
            return [];
        }
    }

    private void SaveSchema()
    {
        var path = SchemaPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        IReadOnlyList<string> snapshot;
        lock (_gate)
            snapshot = _customProperties.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }
}
