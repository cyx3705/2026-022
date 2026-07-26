using System.Globalization;
using System.IO;
using System.Text.Json;

namespace WBall.Model;

/// <summary>
/// 小球倍率公式(v1.6.2:参数支持小数;size 取整;weight 一位小数)。
/// </summary>
public sealed class PublicDefaults
{
    public const long MaxMultiplier = 1_000_000_000_000L; // 1T 显示上限相关
    public const int MinSize = 2;
    public const int MaxSize = 72;
    public const double MinWeight = 0.1;
    public const double MaxWeight = 500;

    public double SizeBase { get; set; } = 6;
    public double SizeScale { get; set; } = 6;
    public double WeightBase { get; set; } = 1;
    public double WeightScale { get; set; } = 1;
    public long InitialMultiplier { get; set; } = 1;

    public int SizeFromMultiplier(long multiplier)
    {
        var m = Math.Max(1, multiplier);
        var raw = SizeBase + SizeScale * Math.Sqrt(m);
        return RoundSize(raw);
    }

    public double WeightFromMultiplier(long multiplier)
    {
        var m = Math.Max(1, multiplier);
        var raw = WeightBase + WeightScale * Math.Sqrt(m);
        return RoundWeight(raw);
    }

    public void ApplyToBall(Ball ball)
    {
        ball.Size = SizeFromMultiplier(ball.Multiplier);
        ball.Weight = WeightFromMultiplier(ball.Multiplier);
    }

    public static int RoundSize(double raw)
        => Math.Clamp((int)Math.Round(raw, MidpointRounding.AwayFromZero), MinSize, MaxSize);

    /// <summary>重量量化为 1 位小数,避免任意高精度浮点。</summary>
    public static double RoundWeight(double raw)
    {
        var w = Math.Round(raw, 1, MidpointRounding.AwayFromZero);
        return Math.Clamp(w, MinWeight, MaxWeight);
    }

    public static string FormatMultiplier(long n)
    {
        n = Math.Max(0, n);
        if (n >= 1_000_000_000_000L)
            return (n / 1_000_000_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "T";
        if (n >= 1_000_000_000L)
            return (n / 1_000_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "G";
        if (n >= 1_000_000L)
            return (n / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        if (n >= 1_000L)
            return (n / 1_000d).ToString("0.##", CultureInfo.InvariantCulture) + "K";
        return n.ToString(CultureInfo.InvariantCulture);
    }

    public static long ClampMultiplier(long m)
        => Math.Clamp(m, 1, MaxMultiplier);
}

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
