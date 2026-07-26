using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppShell.Core.Logging;
using WBall.Model;

namespace WBall.Battle;

/// <summary>参考图风格左侧 Plinko 经济场景种子。</summary>
public static class PlinkoDemoSeeder
{
    public const string SceneFileName = "plinko_demo.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string EnsureScene(string scenesDirectory, IShellLog log)
    {
        Directory.CreateDirectory(scenesDirectory);
        var path = Path.Combine(scenesDirectory, SceneFileName);
        // 始终覆写,保证 demo 布局与版本一致
        File.WriteAllText(path, JsonSerializer.Serialize(BuildSnapshot(), JsonOptions));
        log.Info("demo", $"已写入经济场景 {path}");
        return path;
    }

    public static SceneSnapshot BuildSnapshot()
    {
        const double w = 720;
        const double h = 900;
        var objects = new List<SceneObjectDto>();
        var id = 1;

        // 顶部漏斗生成器
        objects.Add(Obj(id++, "spawner", w / 2 - 36, 24, 72, 48, name: null));

        // 顶部 x16 门
        objects.Add(Obj(id++, "despawner", w / 2 - 40, 100, 80, 36, name: "X16"));

        // 中部两排倍率门(v2.10 DR-02:提高大数值概率)
        double[] mults = [16, 8, 4, 2, 2, 2, 2, 4, 8, 16];
        var gateW = 52.0;
        var gap = 8.0;
        var total = mults.Length * gateW + (mults.Length - 1) * gap;
        var startX = (w - total) / 2;
        for (var row = 0; row < 2; row++)
        {
            var y = 220 + row * 70;
            for (var i = 0; i < mults.Length; i++)
            {
                var x = startX + i * (gateW + gap);
                objects.Add(Obj(id++, "despawner", x, y, gateW, 36, name: $"X{mults[i]}"));
            }
        }

        // v2.10 DR-02:两侧 X32 高倍边门(竖条),配合 360° 抛球机可链式过门
        objects.Add(Obj(id++, "despawner", 0, 330, 26, 200, name: "X32"));
        objects.Add(Obj(id++, "despawner", w - 26, 330, 26, 200, name: "X32"));

        // V 形引导钉(方块)
        for (var row = 0; row < 5; row++)
        {
            var count = 3 + row;
            var y = 400 + row * 42;
            var pegW = 18.0;
            var pegGap = 28.0;
            var pegTotal = count * pegW + (count - 1) * pegGap;
            var pegStart = (w - pegTotal) / 2;
            for (var i = 0; i < count; i++)
            {
                var x = pegStart + i * (pegW + pegGap);
                // v2.12.4:菱形钉(45°),防球搁置在钉顶饿死经济
                objects.Add(Obj(id++, "block", x, y, pegW, pegW, rotation: 45));
            }
        }

        // 底部结算槽:全宽无缝铺满(SC-01),槽底贴地(SC-02),球必落入某一槽
        // v2.11 LZ-01:取消激光球,五槽全宽
        // v2.12.4:槽高 70→120 — 高倍巨球半径可超 70,球心须能进槽判定区,否则堵死经济
        string[] slots = ["大球", "小球", "护盾", "直射", "齐射"];
        var slotW = w / slots.Length;
        var slotY = h - 120;
        for (var i = 0; i < slots.Length; i++)
            objects.Add(Obj(id++, "despawner", i * slotW, slotY, slotW, 120, name: slots[i]));

        return new SceneSnapshot
        {
            Format = SceneStore.CurrentFormat,
            App = "WBall",
            // v2.9 EC-01:落球减速
            GravityG = 4,
            BallCollision = true,
            Seed = 42,
            WorldWidth = w,
            WorldHeight = h,
            Objects = objects,
        };
    }

    private static SceneObjectDto Obj(
        int n,
        string type,
        double x,
        double y,
        double ww,
        double hh,
        string? name = null,
        double rotation = 0) => new()
    {
        Id = $"obj{n}",
        Type = type,
        X = x,
        Y = y,
        W = ww,
        H = hh,
        Name = name,
        Rotation = rotation,
    };
}
