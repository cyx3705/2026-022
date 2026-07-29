using System.Text.Json;
using WBall.Model;

namespace WBall.Model;

/// <summary>公式配置持久化(ball_formula.json;兼容旧 table_public.json)。</summary>
public static class PublicDefaultsStore
{
    private const string NewFileName = "ball_formula.json";
    private const string LegacyFileName = "table_public.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static PublicDefaults Load(string dataRoot)
    {
        var newPath = Path.Combine(dataRoot, NewFileName);
        var legacyPath = Path.Combine(dataRoot, LegacyFileName);
        try
        {
            string? path = null;
            if (File.Exists(newPath))
                path = newPath;
            else if (File.Exists(legacyPath))
                path = legacyPath;

            if (path == null)
                return new PublicDefaults();

            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<PublicDefaults>(json, JsonOptions) ?? new PublicDefaults();

            if (path == legacyPath)
                Save(dataRoot, loaded);

            return loaded;
        }
        catch
        {
            return new PublicDefaults();
        }
    }

    public static void Save(string dataRoot, PublicDefaults defaults)
    {
        Directory.CreateDirectory(dataRoot);
        var path = Path.Combine(dataRoot, NewFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(defaults, JsonOptions));
    }
}
