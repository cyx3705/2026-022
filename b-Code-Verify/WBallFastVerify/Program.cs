using System.Diagnostics;
using System.Text.Json;
using WBall.Editing;
using WBall.Game;
using WBall.Model;
using WBall.Recording;

var watch = Stopwatch.StartNew();
var failures = new List<string>();
var passed = 0;
var root = Path.Combine(Path.GetTempPath(), $"wball_fast_verify_v35_{Guid.NewGuid():N}");
Directory.CreateDirectory(root);

void Check(string name, bool condition, string? detail = null)
{
    if (condition)
    {
        passed++;
        Console.WriteLine($"PASS {name}{(detail == null ? "" : $": {detail}")}");
        return;
    }

    failures.Add(name);
    Console.WriteLine($"FAIL {name}{(detail == null ? "" : $": {detail}")}");
}

try
{
    var coreReferences = typeof(SceneWorld).Assembly.GetReferencedAssemblies().Select(x => x.Name ?? "").ToArray();
    var applicationReferences = typeof(BallEditorService).Assembly.GetReferencedAssemblies().Select(x => x.Name ?? "").ToArray();
    string[] forbidden = ["PresentationCore", "PresentationFramework", "WindowsBase", "AppShell.Core", "AppShell.Services", "AppShell.Shell"];
    Check("fast assemblies exclude desktop dependencies",
        !coreReferences.Concat(applicationReferences).Any(x => forbidden.Contains(x, StringComparer.OrdinalIgnoreCase)));

    var world = new SceneWorld();
    var ball = new Ball { Id = "ball1", Color = "#FFFFFF", Multiplier = 1, Size = 12, Weight = 2 };
    world.Balls.Add(ball);
    var ballEditor = new BallEditorService(world);
    var ballResult = ballEditor.Apply(new BallEditRequest("ball1", "#ff0000", 25));
    Check("ball edit applies normalized values",
        ballResult.Success && ball.Color == "#FF0000" && ball.Multiplier == 25
        && ball.Size == world.Defaults.SizeFromMultiplier(25)
        && Math.Abs(ball.Weight - world.Defaults.WeightFromMultiplier(25)) < 1e-9);

    var ballSnapshot = (ball.Color, ball.Multiplier, ball.Size, ball.Weight);
    var badBall = ballEditor.Apply(new BallEditRequest("ball1", "broken", 50));
    Check("ball edit rejects transactionally", !badBall.Success
        && ballSnapshot == (ball.Color, ball.Multiplier, ball.Size, ball.Weight));

    var formulaEditor = new FormulaEditorService(world, root);
    var formulaResult = formulaEditor.Apply(new FormulaEditRequest(SizeBase: 8, WeightScale: 2, RecalculateAll: true));
    Check("formula edit persists and recalculates", formulaResult.Success
        && File.Exists(Path.Combine(root, "ball_formula.json"))
        && ball.Size == world.Defaults.SizeFromMultiplier(ball.Multiplier)
        && Math.Abs(ball.Weight - world.Defaults.WeightFromMultiplier(ball.Multiplier)) < 1e-9);

    var defaultsSnapshot = JsonSerializer.Serialize(world.Defaults);
    var badFormula = formulaEditor.Apply(new FormulaEditRequest(SizeBase: double.NaN));
    Check("formula edit rejects transactionally", !badFormula.Success
        && defaultsSnapshot == JsonSerializer.Serialize(world.Defaults));

    world.Factions.Add(new Faction { Id = "blue", Name = "蓝方", Color = "#3B82F6" });
    var factionEditor = new FactionEditorService(world);
    var factionResult = factionEditor.Apply(new FactionEditRequest(
        "blue", Name: "蓝队", Color: "22c55e", InitialBalls: 8, InitialMultiplier: 4, Score: 12));
    var faction = world.FindFaction("blue")!;
    Check("faction edit applies all fields", factionResult.Success
        && faction.Name == "蓝队" && faction.Color == "#22C55E"
        && faction.InitialBalls == 8 && faction.InitialMultiplier == 4 && faction.Score == 12);

    var factionSnapshot = (faction.Name, faction.Color, faction.InitialBalls, faction.InitialMultiplier, faction.Score);
    var badFaction = factionEditor.Apply(new FactionEditRequest("blue", Name: " ", Score: 99));
    Check("faction edit rejects transactionally", !badFaction.Success
        && factionSnapshot == (faction.Name, faction.Color, faction.InitialBalls, faction.InitialMultiplier, faction.Score));

    foreach (var fps in new[] { 24, 25, 30, 50, 60 })
    {
        var clock = new TimelineClock(new RenderTimeConfig { PreviewAutoSlow = false }, autoSlow: false);
        var steps = Enumerable.Range(0, fps * 10).Sum(_ => clock.AdvanceOutputFrame(fps, 0));
        Check($"timeline exact {fps}fps", steps == 600, $"steps={steps}");
    }
}
finally
{
    watch.Stop();
    try { Directory.Delete(root, recursive: true); } catch { }
}

var summary = new
{
    Version = "3.5.1",
    Suite = "fast",
    ElapsedMilliseconds = Math.Round(watch.Elapsed.TotalMilliseconds, 3),
    Passed = passed,
    Failed = failures.Count,
    Failures = failures,
};
Console.WriteLine("FAST_SUMMARY " + JsonSerializer.Serialize(summary));
Console.WriteLine(failures.Count == 0 ? "FAST VERIFY PASS" : $"FAST VERIFY FAIL ({failures.Count})");
return failures.Count == 0 ? 0 : 1;
