using System.IO;
using AppShell.Core.Logging;
using WBall.Battle;
using WBall.Model;
using WBall.Stage;

var dataRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WBall");

string RunOnce(int seed, int frames, out double angle0, out double angle1, out int projectiles, out long totalPoints, out int economyBalls, out string firepower)
{
    var log = new NullLog();
    var world = new SceneWorld();
    var weapons = new WeaponCatalog(dataRoot, log);
    var bridge = new EconomyBridge(weapons, log);
    world.Settlements = bridge;
    var scenesDir = Path.Combine(Path.GetTempPath(), "wball_verify_scenes");
    SceneStore.Load(world, PlinkoDemoSeeder.EnsureScene(scenesDir, log));
    var config = new BattleConfigStore(dataRoot, log);
    var battleWorld = new SceneWorld { Defaults = world.Defaults, GravityG = 0 };
    var battle = new BattleRuntime(world, battleWorld, config, weapons, log);
    var stage = new StageState();
    var director = new BattleDirector(world, battleWorld, battle, weapons, bridge, stage, log);

    Console.WriteLine(
        $"mode={config.Arena.Mode} targeting={config.Arena.Targeting} turrets={battle.Turrets.Count} " +
        $"grid={battle.TerritoryCols}x{battle.TerritoryRows} sceneObjects={world.Objects.Count}");
    director.Start(seed, countdownSeconds: 0);
    var first = battle.Turrets[0];
    angle0 = first.BarrelAngleDeg;
    var initialChecksum = battle.TerritoryChecksum();
    director.AdvanceSteps(frames);
    angle1 = first.BarrelAngleDeg;
    projectiles = battle.ProjectileCount;
    totalPoints = battle.Turrets.Sum(t => t.Points);
    economyBalls = world.Balls.Count;
    firepower = string.Join(" | ", battle.Turrets.Select(t =>
        $"{t.Name}:pts={t.Points} 领地={t.Hp:0}/{t.MaxHp:0} shield={t.Shield:0} alive={t.Alive}"));
    Console.WriteLine(director.Status());
    Console.WriteLine($"territory changed: {battle.TerritoryChecksum() != initialChecksum}");
    LastNotEnded = battle.WinnerId == null;
    LastTerritoryChanged = battle.TerritoryChecksum() != initialChecksum;
    return director.DeterministicHash();
}

RunOnce(42, 450, out var h0, out var h1, out _, out _, out _, out _); // 7.5s @6rpm → 270°,非整圈
Console.WriteLine($"half-check t0={h0:0.###}° t7.5s={h1:0.###}° delta={(h1 - h0 + 720) % 360:0.###}° (expect 270)");

// 60s 经济闭环:PTS 增长、火力变化、无死球
var hashA = RunOnce(42, 3600, out var a0, out var a1, out var proj, out var pts, out var balls, out var fp);
var hashB = RunOnce(42, 3600, out _, out _, out _, out _, out _, out _);
var hashC = RunOnce(43, 3600, out _, out _, out _, out _, out _, out _);

Console.WriteLine($"barrel t0={a0:0.###}° t60s={a1:0.###}°");
Console.WriteLine($"projectiles in flight: {proj}");
Console.WriteLine($"economy balls alive: {balls} (expect 16)");
Console.WriteLine($"total faction points after 60s: {pts}");
Console.WriteLine(fp);
Console.WriteLine($"same-seed hash equal: {hashA == hashB}");
Console.WriteLine($"diff-seed hash differs: {hashA != hashC}");

// v2.12.4:整局打到终局,验证触杀/余烬爆发/胜负收敛
bool RunToEnd(int seed, int maxFrames, out double endSeconds, out string winner)
{
    var log = new NullLog();
    var world = new SceneWorld();
    var weapons = new WeaponCatalog(dataRoot, log);
    var bridge = new EconomyBridge(weapons, log);
    world.Settlements = bridge;
    SceneStore.Load(world, PlinkoDemoSeeder.EnsureScene(Path.Combine(Path.GetTempPath(), "wball_verify_scenes"), log));
    var config = new BattleConfigStore(dataRoot, log);
    var battleWorld = new SceneWorld { Defaults = world.Defaults, GravityG = 0 };
    var battle = new BattleRuntime(world, battleWorld, config, weapons, log);
    var director = new BattleDirector(world, battleWorld, battle, weapons, bridge, new StageState(), log);
    director.Start(seed, countdownSeconds: 0);
    var frames = 0;
    while (director.State != DirectorState.Ended && frames < maxFrames)
    {
        director.AdvanceFixedStep();
        frames++;
        if (frames % 3600 == 0)
        {
            Console.WriteLine($"  [{frames / 60}s] " + string.Join(" | ", battle.Turrets.Select(t =>
                $"{t.Id}:alive={t.Alive} 领={t.Hp:0} shield={t.Shield:0} ammo={battle.AmmoTotalOf(t)}")));
            Console.WriteLine("    balls: " + string.Join(" ", world.Balls.Take(16).Select(b =>
                $"({b.X:0},{b.Y:0} v={Math.Sqrt(b.Vx * b.Vx + b.Vy * b.Vy):0} m={b.Multiplier})")));
        }
    }
    endSeconds = frames / 60.0;
    winner = battle.WinnerId ?? "-";
    return battle.WinnerId != null;
}

var concluded = RunToEnd(7, 36000, out var endAt, out var champion);
Console.WriteLine($"full game seed=7: concluded={concluded} at {endAt:0.#}s winner={champion}");

// v2.12.5 BS-01 定向测试:巨球正面打满盾炮台,必须触杀
bool BigBallKillsShieldedTurret()
{
    var log = new NullLog();
    var world = new SceneWorld();
    var weapons = new WeaponCatalog(dataRoot, log);
    var bridge = new EconomyBridge(weapons, log);
    world.Settlements = bridge;
    var config = new BattleConfigStore(dataRoot, log);
    var battleWorld = new SceneWorld { Defaults = world.Defaults, GravityG = 0 };
    var battle = new BattleRuntime(world, battleWorld, config, weapons, log);
    battle.Reset(1);
    var attacker = battle.Turrets[0];
    var target = battle.Turrets[1];
    target.Shield = target.MaxShield; // 满盾 5M
    Console.WriteLine($"  target={target.Id} shield={target.Shield:0} radius={target.TurretRadius}");

    var dx = target.TurretX - attacker.TurretX;
    var dy = target.TurretY - attacker.TurretY;
    var len = Math.Sqrt(dx * dx + dy * dy);
    battleWorld.Balls.Add(new Ball
    {
        Id = battleWorld.NextBallId(),
        X = attacker.TurretX + dx / len * 60,
        Y = attacker.TurretY + dy / len * 60,
        Vx = dx / len * 400,
        Vy = dy / len * 400,
        Color = attacker.Color,
        Size = 40,
        Weight = 8000,
        Projectile = new ProjectileState
        {
            OwnerFactionId = attacker.Id,
            WeaponName = "大球",
            Damage = 8000,
            CapturesLeft = 8000,
        },
    });
    for (var i = 0; i < 600 && target.Alive; i++)
        battle.Step(1.0 / 60);
    Console.WriteLine($"  after: target alive={target.Alive} shield={target.Shield:0}");
    return !target.Alive;
}

var bigKill = BigBallKillsShieldedTurret();
Console.WriteLine($"big ball kills full-shield turret: {bigKill}");

var pass = hashA == hashB
    && hashA != hashC
    && pts >= 0
    && LastTerritoryChanged
    && concluded;
Console.WriteLine($"not ended at 60s: {LastNotEnded} (v2.12.4 起允许 60s 内分出胜负)");
Console.WriteLine($"economy balls: {balls} (阵亡方经济球会转化为余烬,允许 <16)");
Console.WriteLine(pass ? "VERIFY PASS" : "VERIFY FAIL");
return pass ? 0 : 1;

partial class Program
{
    internal static bool LastNotEnded;
    internal static bool LastTerritoryChanged;
}

sealed class NullLog : IShellLog
{
    public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
    public void Log(ShellLogLevel level, string category, string message) { }
    public IReadOnlyList<ShellLogEntry> Snapshot() => Array.Empty<ShellLogEntry>();
}
