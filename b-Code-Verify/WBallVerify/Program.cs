using System.IO;
using AppShell.Core.Commands;
using AppShell.Core.Logging;
using WBall.Battle;
using WBall.Commands;
using WBall.Game;
using WBall.Model;
using WBall.Stage;

var dataRoot = Path.Combine(Path.GetTempPath(), $"wball_verify_v32_{Environment.ProcessId}");
Directory.CreateDirectory(dataRoot);
var log = new NullLog();
var failures = new List<string>();

void Check(string name, bool passed, string? detail = null)
{
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}" + (detail == null ? "" : $": {detail}"));
    if (!passed)
        failures.Add(name + (detail == null ? "" : $": {detail}"));
}

string RunHash(BalanceConfig balance, int seed, int frames, out Harness harness)
{
    harness = new Harness(dataRoot, log, balance);
    harness.Director.Start(seed, countdownSeconds: 0);
    harness.Director.AdvanceSteps(frames);
    return harness.Director.DeterministicHash();
}

if (args.Contains("--calibrate", StringComparer.OrdinalIgnoreCase))
{
    var calibrationHarness = new Harness(dataRoot, log, new BalanceConfig());
    var calibrationPresets = new PresetStore(Path.Combine(dataRoot, "calibration"), log);
    var calibrationSimulator = new BalanceSimulator(
        calibrationHarness.EconomyWorld, calibrationHarness.BattleConfig, calibrationHarness.Weapons, log);
    var requestedProfiles = args.SkipWhile(x => !x.Equals("--calibrate", StringComparison.OrdinalIgnoreCase))
        .Skip(1)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var profileNames = new[] { "standard", "rush", "marathon" }
        .Where(name => requestedProfiles.Count == 0 || requestedProfiles.Contains(name));
    var profiles = profileNames.Select(calibrationPresets.Load).ToArray();
    var calibrationTasks = (
        from profile in profiles
        from seed in Enumerable.Range(42, 8)
        select Task.Run(() =>
        {
            var result = calibrationSimulator.Run(
                [seed], 180, profile.Arena, profile.Balance,
                TimeSpan.FromMinutes(10), null, CancellationToken.None);
            return (profile.Name, Result: result);
        })).ToArray();
    var calibrationRows = await Task.WhenAll(calibrationTasks);
    var calibrationResults = calibrationRows
        .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
        .Select(group => (
            Name: group.Key,
            Result: new BalanceSimulationResult
            {
                Rows = group.SelectMany(x => x.Result.Rows).OrderBy(x => x.Seed).ToArray(),
                Interrupted = group.Any(x => x.Result.Interrupted),
            }))
        .OrderBy(x => Array.IndexOf(["standard", "rush", "marathon"], x.Name))
        .ToArray();
    foreach (var item in calibrationResults)
    {
        Console.WriteLine($"=== {item.Name} ===");
        Console.WriteLine(item.Result.Format());
    }
    return calibrationResults.All(x => !x.Result.Interrupted) ? 0 : 1;
}

// M1/M2 红线：关闭三项默认变化后必须回到 v3.1 哈希。
var legacy = new BalanceConfig
{
    SmallPackThreshold = 0,
    AmmoQueueGuard = 512,
    ShieldRegenPerSecond = 1,
};
var legacyHash = RunHash(legacy, 42, 3600, out var legacyRun);
const string V31Hash = "6381A3898C0FAD65B57D43C140917A010713AA3015F601BACE14C7E5B88333F3";
Check("v3.1 rollback hash", legacyHash == V31Hash, legacyHash);

var defaultHashA = RunHash(new BalanceConfig(), 42, 3600, out var defaultRunA);
var defaultHashB = RunHash(new BalanceConfig(), 42, 3600, out _);
var defaultHashC = RunHash(new BalanceConfig(), 43, 3600, out _);
Check("v3.2 same-seed deterministic", defaultHashA == defaultHashB, defaultHashA);
Check("v3.2 different seed differs", defaultHashA != defaultHashC, defaultHashC);
Check("v3.2 default intentionally changed", defaultHashA != V31Hash);
Check("territory changed", defaultRunA.Battle.TerritoryChecksum() != defaultRunA.InitialTerritoryChecksum);

// BP-07：等比升格档形。
var pack = new Harness(dataRoot, log, new BalanceConfig());
Check("pack below threshold", pack.Battle.SmallPackValue(39_999) == 1);
Check("pack 40k", pack.Battle.SmallPackValue(40_000) == 2);
Check("pack 80k", pack.Battle.SmallPackValue(80_000) == 4);
Check("pack 160k", pack.Battle.SmallPackValue(160_000) == 8);
Check("pack cap", pack.Battle.SmallPackValue(long.MaxValue) == 64);

// BP-08：超过旧 512 上限仍入队，增量总值与队列一致。
var queue = new Harness(dataRoot, log, new BalanceConfig { AmmoQueueGuard = 1000 });
var owner = queue.Battle.Turrets[0];
var economyBall = new Ball { Id = "verify", Color = owner.Color };
for (var i = 0; i < 600; i++)
    queue.Bridge.TrySettle("大球", queue.EconomyWorld, economyBall, 1, null);
Check("queue exceeds 512", owner.Ammo.Count >= 600, $"count={owner.Ammo.Count}");
Check("queued ammo incremental total", owner.QueuedAmmoValue == owner.Ammo.Sum(x => x.Value),
    $"incremental={owner.QueuedAmmoValue}");

// BP-09：默认无自然再生，显式 regen=1 可恢复旧行为。
var noRegen = new Harness(dataRoot, log, new BalanceConfig());
var noRegenTarget = noRegen.Battle.Turrets[0];
noRegenTarget.Shield = 100;
noRegen.Battle.Step(0.1);
Check("shield regen disabled by default", Math.Abs(noRegenTarget.Shield - 100) < 1e-9);
var withRegen = new Harness(dataRoot, log, new BalanceConfig { ShieldRegenPerSecond = 1 });
var regenTarget = withRegen.Battle.Turrets[0];
regenTarget.Shield = 100;
withRegen.Battle.Step(0.1);
Check("shield regen opt-in", regenTarget.Shield > 100, $"shield={regenTarget.Shield:0.###}");

// 破盾直入与触杀必须是硬断言，而不是只打印结果。
Check("big ball kills full-shield turret", BigBallKillsShieldedTurret(dataRoot, log));

// 硬性时限应保证定时收敛。
var limited = new Harness(dataRoot, log, new BalanceConfig { HardTimeLimitSeconds = 1 });
limited.Director.Start(9, countdownSeconds: 0);
limited.Director.AdvanceSteps(90);
Check("hard time limit concludes", limited.Battle.WinnerId != null, limited.Battle.WinnerId ?? "-");
Check("hard time limit does not overrun", limited.Battle.ElapsedSeconds <= 1 + 1e-9,
    $"seconds={limited.Battle.ElapsedSeconds:0.######}");

// 预设只往返 arena + balance。
var presets = new PresetStore(dataRoot, log);
var customArena = new ArenaLayoutConfig { Width = 1200, Height = 800 };
var customBalance = new BalanceConfig { SmallRateBase = 33, WallRestitution = 0.2 };
presets.Save("verify", customArena, customBalance);
var loadedPreset = presets.Load("verify");
Check("preset arena roundtrip", loadedPreset.Arena.Width == 1200 && loadedPreset.Arena.Height == 800);
Check("preset balance roundtrip", loadedPreset.Balance.SmallRateBase == 33 && loadedPreset.Balance.WallRestitution == 0.2);

// 剧本携带 balance，老剧本 null 则由 Apply 回落新默认。
var scenarioRoot = Path.Combine(dataRoot, "workspace");
var scenarioStore = new ScenarioStore(scenarioRoot, log);
var scenarioHarness = new Harness(dataRoot, log, customBalance, customArena);
var snapshot = scenarioStore.Capture(
    "verify", 12, scenarioHarness.BattleConfig, scenarioHarness.BalanceStore,
    scenarioHarness.Weapons, scenarioHarness.EconomyWorld.LastScenePath);
Check("scenario captures balance", snapshot.Balance?.SmallRateBase == 33);
var targetBalance = BalanceConfigStore.CreateMemory(new BalanceConfig(), log);
var targetArena = BattleConfigStore.CreateMemory(
    scenarioHarness.BattleConfig.Turrets, new ArenaLayoutConfig(), log);
scenarioStore.Apply(snapshot, targetArena, targetBalance, scenarioHarness.Weapons);
Check("scenario applies balance", targetBalance.Current.SmallRateBase == 33);

// BS：独立试跑同参数结果逐字一致，且不改变现场哈希。
var simHarness = new Harness(dataRoot, log, new BalanceConfig());
simHarness.Director.Start(42, countdownSeconds: 0);
simHarness.Director.AdvanceSteps(120);
var beforeSim = simHarness.Director.DeterministicHash();
var simulator = new BalanceSimulator(
    simHarness.EconomyWorld, simHarness.BattleConfig, simHarness.Weapons, log);
var simA = simulator.Run([3, 4], 5, simHarness.BattleConfig.Arena, simHarness.BalanceStore.Current,
    TimeSpan.FromSeconds(30), null, CancellationToken.None).Format();
var simB = simulator.Run([3, 4], 5, simHarness.BattleConfig.Arena, simHarness.BalanceStore.Current,
    TimeSpan.FromSeconds(30), null, CancellationToken.None).Format();
Check("headless sim deterministic", simA == simB);
Check("headless sim does not mutate live battle", beforeSim == simHarness.Director.DeterministicHash());

// 取消发生在种子边界时，须返回已完成部分而不是丢弃整批结果。
using var cancelAfterFirst = new CancellationTokenSource();
var partial = simulator.Run(
    [5, 6], 1, simHarness.BattleConfig.Arena, simHarness.BalanceStore.Current,
    TimeSpan.FromSeconds(30), new CallbackProgress(_ => cancelAfterFirst.Cancel()), cancelAfterFirst.Token);
Check("headless sim cancellation keeps partial rows", partial.Interrupted && partial.Rows.Count == 1,
    $"rows={partial.Rows.Count} interrupted={partial.Interrupted}");

// BK/PS：走真实 CommandRegistry + CommandBus，而不是直接调用存储层。
var registry = new CommandRegistry();
BalanceCommands.Register(
    registry,
    simHarness.BalanceStore,
    simHarness.BattleConfig,
    presets,
    simulator,
    simHarness.Battle,
    simHarness.Director,
    simHarness.BattleWorld);
var bus = new CommandBus(registry, log);
var configResult = await bus.ExecuteAsync("balance.config", "verify");
Check("balance.config command", configResult.Success && configResult.Message.Contains("smallRateBase="));

var emberBefore = BalanceConfigStore.Clone(simHarness.BalanceStore.Current);
var invalidEmber = await bus.ExecuteAsync("balance.ember speedMin=500 speedMax=100", "verify");
Check("balance.ember invalid is transactional",
    !invalidEmber.Success
    && simHarness.BalanceStore.Current.EmberSpeedMin == emberBefore.EmberSpeedMin
    && simHarness.BalanceStore.Current.EmberSpeedMax == emberBefore.EmberSpeedMax);
var validEmber = await bus.ExecuteAsync("balance.ember speedMin=175 speedMax=350", "verify");
Check("balance.ember command", validEmber.Success
    && simHarness.BalanceStore.Current.EmberSpeedMin == 175
    && simHarness.BalanceStore.Current.EmberSpeedMax == 350);

var presetSave = await bus.ExecuteAsync("preset.save name=smoke", "verify");
await bus.ExecuteAsync("balance.ember speedMin=200 speedMax=300", "verify");
var presetLoad = await bus.ExecuteAsync("preset.load name=smoke", "verify");
Check("preset.save/load commands", presetSave.Success && presetLoad.Success
    && simHarness.BalanceStore.Current.EmberSpeedMin == 175
    && simHarness.BalanceStore.Current.EmberSpeedMax == 350);
var simCommand = await bus.ExecuteAsync("balance.sim seeds=7 seconds=1 timeoutMs=30000 format=table", "verify");
Check("balance.sim command", simCommand.Success && simCommand.Message.Contains("seed  seconds"));

Console.WriteLine($"v3.2 hash seed=42 @60s: {defaultHashA}");
Console.WriteLine($"v3.2 hash seed=43 @60s: {defaultHashC}");
Console.WriteLine(failures.Count == 0 ? "VERIFY PASS" : $"VERIFY FAIL ({failures.Count})");
if (failures.Count > 0)
    Console.WriteLine(string.Join(Environment.NewLine, failures.Select(x => "  " + x)));
return failures.Count == 0 ? 0 : 1;

static bool BigBallKillsShieldedTurret(string dataRoot, IShellLog log)
{
    var harness = new Harness(dataRoot, log, new BalanceConfig());
    var attacker = harness.Battle.Turrets[0];
    var target = harness.Battle.Turrets[1];
    target.Shield = target.MaxShield;
    var dx = target.TurretX - attacker.TurretX;
    var dy = target.TurretY - attacker.TurretY;
    var len = Math.Sqrt(dx * dx + dy * dy);
    harness.BattleWorld.Balls.Add(new Ball
    {
        Id = harness.BattleWorld.NextBallId(),
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
        harness.Battle.Step(1.0 / 60);
    return !target.Alive;
}

sealed class Harness
{
    public Harness(
        string dataRoot,
        IShellLog log,
        BalanceConfig balance,
        ArenaLayoutConfig? arena = null)
    {
        BattleConfig = BattleConfigStore.CreateMemory(
            Demo4Turrets(), arena ?? new ArenaLayoutConfig(), log);
        BalanceStore = BalanceConfigStore.CreateMemory(balance, log);
        Weapons = new WeaponCatalog(dataRoot, log);
        EconomyWorld = new SceneWorld();
        var scenes = Path.Combine(dataRoot, "scenes");
        SceneStore.Load(EconomyWorld, PlinkoDemoSeeder.EnsureScene(scenes, log));
        Bridge = new EconomyBridge(Weapons, log, BalanceStore);
        EconomyWorld.Settlements = Bridge;
        BattleWorld = new SceneWorld { Defaults = EconomyWorld.Defaults, GravityG = 0 };
        Battle = new BattleRuntime(EconomyWorld, BattleWorld, BattleConfig, Weapons, log, BalanceStore);
        Director = new BattleDirector(
            EconomyWorld, BattleWorld, Battle, Weapons, Bridge, new StageState(), log, BalanceStore);
        InitialTerritoryChecksum = Battle.TerritoryChecksum();
    }

    public SceneWorld EconomyWorld { get; }
    public SceneWorld BattleWorld { get; }
    public BattleConfigStore BattleConfig { get; }
    public BalanceConfigStore BalanceStore { get; }
    public WeaponCatalog Weapons { get; }
    public EconomyBridge Bridge { get; }
    public BattleRuntime Battle { get; }
    public BattleDirector Director { get; }
    public int InitialTerritoryChecksum { get; }

    private static IReadOnlyList<TurretDefinition> Demo4Turrets() =>
    [
        new() { Id = "green", Name = "Caleb", Color = "#22C55E", Quadrant = 2, InitialBalls = 4, InitialShield = 2_000_000 },
        new() { Id = "cyan", Name = "Xiaolin", Color = "#06B6D4", Quadrant = 1, InitialBalls = 4, InitialShield = 2_000_000 },
        new() { Id = "orange", Name = "Diu", Color = "#F97316", Quadrant = 3, InitialBalls = 4, InitialShield = 2_000_000 },
        new() { Id = "magenta", Name = "Wemmbu", Color = "#EC4899", Quadrant = 4, InitialBalls = 4, InitialShield = 2_000_000 },
    ];
}

sealed class NullLog : IShellLog
{
    public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
    public void Log(ShellLogLevel level, string category, string message) { }
    public IReadOnlyList<ShellLogEntry> Snapshot() => [];
}

sealed class CallbackProgress(Action<string> callback) : IProgress<string>
{
    public void Report(string value) => callback(value);
}
