using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WBall.Model;

public static partial class SceneStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static void Save(SceneWorld world, string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var snap = Capture(world);
        var json = JsonSerializer.Serialize(snap, JsonOptions);
        var tmp = fullPath + ".tmp";
        File.WriteAllText(tmp, json, Encoding.UTF8);
        File.Copy(tmp, fullPath, overwrite: true);
        File.Delete(tmp);

        world.LastScenePath = fullPath;
        world.SceneDirty = false;
    }

    public static SceneSnapshot ReadFile(string fullPath)
    {
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("场景文件不存在", fullPath);

        string json;
        try
        {
            json = File.ReadAllText(fullPath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法读取场景文件: {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("场景文件为空");

        SceneSnapshot? snap;
        try
        {
            snap = JsonSerializer.Deserialize<SceneSnapshot>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"场景 JSON 损坏或格式错误: {ex.Message}", ex);
        }

        if (snap == null)
            throw new InvalidOperationException("场景文件反序列化为空");

        ValidateSnapshot(snap);
        return snap;
    }

    public static void Load(SceneWorld world, string fullPath)
    {
        var snap = ReadFile(fullPath);
        Apply(world, snap);
        world.LastScenePath = fullPath;
        world.SceneDirty = false;
    }

    /// <summary>轻读场景头/计数,供 scenes 管理表刷新(不 hydrate 进 SceneWorld)。</summary>
    public static bool TryPeekMeta(
        string path,
        out double worldWidth,
        out double worldHeight,
        out int objectCount,
        out int ballCount)
    {
        worldWidth = SceneWorld.DefaultWorldWidth;
        worldHeight = SceneWorld.DefaultWorldHeight;
        objectCount = 0;
        ballCount = 0;
        try
        {
            var json = File.ReadAllText(path);
            var snap = JsonSerializer.Deserialize<SceneSnapshot>(json, JsonOptions);
            if (snap == null)
                return false;
            ValidateSnapshot(snap);
            worldWidth = snap.WorldWidth > 0 ? snap.WorldWidth : SceneWorld.DefaultWorldWidth;
            worldHeight = snap.WorldHeight > 0 ? snap.WorldHeight : SceneWorld.DefaultWorldHeight;
            objectCount = snap.Objects?.Count ?? 0;
            ballCount = snap.Balls?.Count ?? 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ValidateSnapshot(SceneSnapshot snap)
    {
        if (snap.Format <= 0)
            snap.Format = 1;
        if (snap.Format > CurrentFormat)
            throw new InvalidOperationException(
                $"场景 format={snap.Format} 高于当前支持的 {CurrentFormat},请升级 WBall");
        snap.Objects ??= [];
        snap.Balls ??= [];
        snap.Wireframes ??= [];
        snap.Solids ??= [];

        if (snap.Format < 2)
        {
            if (snap.WorldWidth <= 0)
                snap.WorldWidth = SceneWorld.DefaultWorldWidth;
            if (snap.WorldHeight <= 0)
                snap.WorldHeight = SceneWorld.DefaultWorldHeight;
        }

        if (snap.Format < 3)
            snap.Wireframes = [];
        if (snap.Format < 4)
            snap.Solids = [];
        if (snap.Format < 5)
        {
            foreach (var sceneObject in snap.Objects)
                sceneObject.Name = null;
            foreach (var ball in snap.Balls)
                ball.Multiplier = 1;
        }
    }
}
