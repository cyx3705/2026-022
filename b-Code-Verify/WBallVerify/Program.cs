using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AppShell.Core.Commands;
using AppShell.Core.Logging;
using WBall.Battle;
using WBall.Commands;
using WBall.Game;
using WBall.Model;
using WBall.Presentation;
using WBall.Recording;
using WBall.Stage;
using WBall.Verify;
using WBall.Verify.Suites;

// v3.4 V34-02:产物根由 using(try/finally)托管 —— 通过即清理,失败保留并打印路径。
// --keep-artifacts 强制保留;--artifact-root <path> 换根。
// v3.4 V34-09:断言与共享上下文搬进 VerifyRun;suite 正逐个搬进 Suites/(先 timeline 与 page)。
using var artifacts = VerifyArtifacts.Create(args);
var verificationTimer = Stopwatch.StartNew();
var run = new VerifyRun(artifacts.Root, artifacts);
var failures = run.Failures;
var dataRoot = run.Root;
var log = run.Log;

void Check(string name, bool passed, string? detail = null) => run.Check(name, passed, detail);

string RunHash(
    BalanceConfig balance,
    int seed,
    int frames,
    out Harness harness,
    ArenaLayoutConfig? arena = null)
{
    harness = new Harness(dataRoot, log, balance, arena);
    harness.Director.Start(seed, countdownSeconds: 0);
    harness.Director.AdvanceSteps(frames);
    return harness.Director.DeterministicHash();
}

ArchitectureSuite.Run(run);
TimelineSuite.Run(run);

if (args.Contains("--assist-fixes", StringComparer.OrdinalIgnoreCase))
{
    AssistSuite.VerifyV352Fixes(run);
    return artifacts.Complete(run.Conclude("ASSIST FIXES"));
}

if (args.Contains("--friendly-absorb-smoke", StringComparer.OrdinalIgnoreCase))
    return artifacts.Complete(AssistSuite.RunFriendlyAbsorbSmoke(run));

if (args.Contains("--gameplay-fixes", StringComparer.OrdinalIgnoreCase))
    return artifacts.Complete(GameplayRegressionSuite.Run(run));

if (args.Contains("--render-page-smoke", StringComparer.OrdinalIgnoreCase))
    return artifacts.Complete(WBall.Verify.Suites.PageSuite.Run(run));

if (args.Contains("--render-smoke", StringComparer.OrdinalIgnoreCase)
    || args.Contains("--render-v36", StringComparer.OrdinalIgnoreCase))
    return artifacts.Complete(RenderV36Suite.Run(run));


if (args.Contains("--assist-performance", StringComparer.OrdinalIgnoreCase))
    return artifacts.Complete(WBall.Verify.Suites.AssistSuite.RunPerformance(run));

if (args.Contains("--runtime-performance", StringComparer.OrdinalIgnoreCase))
    return artifacts.Complete(RuntimePerformanceSuite.Run(run, args));

if (args.Contains("--calibrate", StringComparer.OrdinalIgnoreCase))
{
    var calibrationHarness = new Harness(dataRoot, log, new BalanceConfig());
    var calibrationPresets = new PresetStore(artifacts.Suite("calibration"), log);
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
    return artifacts.Complete(calibrationResults.All(x => !x.Result.Interrupted) ? 0 : 1);
}

// M1/M2 红线：关闭三项默认变化后必须回到 v3.1 哈希。
var legacy = new BalanceConfig
{
    SmallPackThreshold = 0,
    AmmoQueueGuard = 512,
    ShieldRegenPerSecond = 1,
    FriendlyAssistEnabled = false,
    ShieldBreakthrough = true,
};
var legacyHash = RunHash(legacy, 42, 3600, out var legacyRun, new ArenaLayoutConfig { BallCollision = true });
const string V31Hash = "7231013A2B055BF00CA51012343A071055178F697C947792A1A7BFA96254DD65";
Check("v3.1 rollback hash", legacyHash == V31Hash, legacyHash);

var v32Rollback = new BalanceConfig { FriendlyAssistEnabled = false, ShieldBreakthrough = true };
var v32RollbackHash = RunHash(v32Rollback, 42, 3600, out _, new ArenaLayoutConfig { BallCollision = true });
const string V32Hash = "AAD5428D2F251BE5F31451B4B94971D2ADBBC8B1F6979340CCA2D2CB058C1D74";
Check("v3.2 rollback hash", v32RollbackHash == V32Hash, v32RollbackHash);

var defaultHashA = RunHash(new BalanceConfig(), 42, 3600, out var defaultRunA);
var defaultHashB = RunHash(new BalanceConfig(), 42, 3600, out _);
var defaultHashC = RunHash(new BalanceConfig(), 43, 3600, out _);
const string V352Seed42Hash = "D87DCBA51531D804F86D913506324A485FC8C0B4929909A3FB878F033329D3CA";
const string V352Seed43Hash = "43FC8561CF7AABA35D33EC93DB0DCC3165A3C39F1B1697EA370B0AEFB3734B78";
Check("v3.5.2 same-seed deterministic", defaultHashA == defaultHashB, defaultHashA);
Check("v3.5.2 seed 42 hash", defaultHashA == V352Seed42Hash, defaultHashA);
Check("v3.5.2 seed 43 hash", defaultHashC == V352Seed43Hash, defaultHashC);
Check("v3.5.2 different seed differs", defaultHashA != defaultHashC, defaultHashC);
Check("v3.5.2 default intentionally changed", defaultHashA != V32Hash);
Check("territory changed", defaultRunA.Battle.TerritoryChecksum() != defaultRunA.InitialTerritoryChecksum);

// BP-07：等比升格档形。
var pack = new Harness(dataRoot, log, new BalanceConfig());
Check("pack below threshold", pack.Battle.SmallPackValue(39_999) == 1);
Check("pack 40k", pack.Battle.SmallPackValue(40_000) == 2);
Check("pack 80k", pack.Battle.SmallPackValue(80_000) == 4);
Check("pack 160k", pack.Battle.SmallPackValue(160_000) == 8);
Check("pack cap", pack.Battle.SmallPackValue(long.MaxValue) == 64);

// v3.3：身份与积分正交；升格小球可低速回收，且每个接收大球共享速率预算。
var reclaim = AssistSuite.NewHarness(run, new BalanceConfig());
var reclaimShell = AssistSuite.AddBall(reclaim, "receiver", ProjectileRole.Shell, 10);
var packedSmall = AssistSuite.AddBall(reclaim, "packed-small", ProjectileRole.SmallShot, 2);
AssistSuite.Advance(reclaim, 9);
Check("v3.3 packed value-2 small shot is reclaimed",
    !reclaim.BattleWorld.Balls.Contains(packedSmall) && reclaimShell.Projectile!.CapturesLeft == 12,
    $"smallAlive={reclaim.BattleWorld.Balls.Contains(packedSmall)} receiver={reclaimShell.Projectile!.CapturesLeft}");

var manySmall = AssistSuite.NewHarness(run, new BalanceConfig());
var sharedReceiver = AssistSuite.AddBall(manySmall, "receiver", ProjectileRole.Shell, 100);
for (var i = 0; i < 100; i++)
    AssistSuite.AddBall(manySmall, $"small-{i:D3}", ProjectileRole.SmallShot, 1);
var totalBefore = AssistSuite.ProjectileValue(manySmall);
manySmall.Battle.Step(1.0 / 60);
var absorbed = sharedReceiver.Projectile!.CapturesLeft - 100;
Check("v3.5.2 small assist adds every overlapping point immediately", absorbed == 100,
    $"absorbed={absorbed}");
Check("v3.3 small assist conserves value", AssistSuite.ProjectileValue(manySmall) == totalBefore,
    $"before={totalBefore} after={AssistSuite.ProjectileValue(manySmall)}");

var shellAssist = AssistSuite.NewHarness(run, new BalanceConfig());
var largeShell = AssistSuite.AddBall(shellAssist, "large", ProjectileRole.Shell, 10);
var smallShell = AssistSuite.AddBall(shellAssist, "small", ProjectileRole.Shell, 6);
AssistSuite.Advance(shellAssist, 60);
Check("v3.3 larger shell receives friendly value",
    largeShell.Projectile!.CapturesLeft == 16 && !shellAssist.BattleWorld.Balls.Contains(smallShell),
    $"large={largeShell.Projectile!.CapturesLeft} smallAlive={shellAssist.BattleWorld.Balls.Contains(smallShell)}");
Check("v3.3 shell assist conserves value", AssistSuite.ProjectileValue(shellAssist) == 16);

var equalWinners = new HashSet<string>(StringComparer.Ordinal);
var equal = AssistSuite.NewHarness(run, new BalanceConfig { FriendlyShellTransferRate = 10 }, 0);
for (var seed = 0; seed < 100; seed++)
{
    equal.Battle.Reset(seed);
    AssistSuite.AddBall(equal, "equal-a", ProjectileRole.Shell, 10);
    AssistSuite.AddBall(equal, "equal-b", ProjectileRole.Shell, 10);
    AssistSuite.Advance(equal, 2);
    equalWinners.Add(equal.BattleWorld.Balls.Single().Id);
}
Check("v3.3 equal-shell choice spans both sides", equalWinners.SetEquals(["equal-a", "equal-b"]),
    string.Join(",", equalWinners.Order()));

equal.Battle.Reset(42);
AssistSuite.AddBall(equal, "equal-a", ProjectileRole.Shell, 10);
AssistSuite.AddBall(equal, "equal-b", ProjectileRole.Shell, 10);
AssistSuite.Advance(equal, 2);
var equalRepeatA = equal.BattleWorld.Balls.Single().Id;
equal.Battle.Reset(42);
AssistSuite.AddBall(equal, "equal-a", ProjectileRole.Shell, 10);
AssistSuite.AddBall(equal, "equal-b", ProjectileRole.Shell, 10);
AssistSuite.Advance(equal, 2);
var equalRepeatB = equal.BattleWorld.Balls.Single().Id;
Check("v3.3 equal-shell choice is seed deterministic",
    equalRepeatA == equalRepeatB);

var capped = AssistSuite.NewHarness(run, new BalanceConfig
{
    FriendlyShellTransferRate = 10,
    FriendlyAssistMaxValue = 12,
});
var cappedReceiver = AssistSuite.AddBall(capped, "cap-large", ProjectileRole.Shell, 10);
var cappedDonor = AssistSuite.AddBall(capped, "cap-small", ProjectileRole.Shell, 6);
AssistSuite.Advance(capped, 1);
Check("v3.3 receiver cap keeps donor remainder",
    cappedReceiver.Projectile!.CapturesLeft == 12 && cappedDonor.Projectile!.CapturesLeft == 4
    && AssistSuite.ProjectileValue(capped) == 16,
    $"receiver={cappedReceiver.Projectile!.CapturesLeft} donor={cappedDonor.Projectile!.CapturesLeft}");

var disabledAssist = AssistSuite.NewHarness(run, new BalanceConfig { FriendlyAssistEnabled = false });
var disabledReceiver = AssistSuite.AddBall(disabledAssist, "off-large", ProjectileRole.Shell, 10);
var disabledDonor = AssistSuite.AddBall(disabledAssist, "off-small", ProjectileRole.SmallShot, 2);
AssistSuite.Advance(disabledAssist, 10);
Check("v3.3 assist master switch disables transfers",
    disabledReceiver.Projectile!.CapturesLeft == 10 && disabledDonor.Projectile!.CapturesLeft == 2);

var zeroRate = AssistSuite.NewHarness(run, new BalanceConfig
{
    FriendlyAbsorbSmallRate = 0,
    FriendlyShellTransferRate = 0,
});
var zeroReceiver = AssistSuite.AddBall(zeroRate, "zero-receiver", ProjectileRole.Shell, 10);
var zeroSmall = AssistSuite.AddBall(zeroRate, "zero-small", ProjectileRole.SmallShot, 2);
var zeroShell = AssistSuite.AddBall(zeroRate, "zero-shell", ProjectileRole.Shell, 6);
AssistSuite.Advance(zeroRate, 60);
Check("v3.5.2 small absorption is immediate and independent of legacy rate",
    zeroReceiver.Projectile!.CapturesLeft == 12
    && !zeroRate.BattleWorld.Balls.Contains(zeroSmall)
    && zeroShell.Projectile!.CapturesLeft == 6);

foreach (var packedValue in new[] { 2, 4, 8, 64 })
{
    var packed = AssistSuite.NewHarness(run, new BalanceConfig { FriendlyAbsorbSmallRate = 10 });
    var receiver = AssistSuite.AddBall(packed, "packed-receiver", ProjectileRole.Shell, 10);
    var donor = AssistSuite.AddBall(packed, $"packed-{packedValue}", ProjectileRole.SmallShot, packedValue);
    AssistSuite.Advance(packed, packedValue / 10.0 + 0.2);
    Check($"v3.3 promoted small value {packedValue} reclaims by role",
        !packed.BattleWorld.Balls.Contains(donor)
        && receiver.Projectile!.CapturesLeft == 10 + packedValue);
}

foreach (var packedValue in new[] { 2, 4, 8, 64 })
{
    var territory = AssistSuite.NewHarness(run, new BalanceConfig(), protectFriendlyValue: false);
    var territoryOwner = territory.Battle.Turrets[0];
    var ownerIndex = territory.Battle.TerritoryFactionIds
        .Select((id, index) => (id, index))
        .Single(x => x.id.Equals(territoryOwner.Id, StringComparison.OrdinalIgnoreCase)).index;
    var cell = Enumerable.Range(0, territory.Battle.TerritoryOwners.Length)
        .First(index => territory.Battle.TerritoryOwners[index] != ownerIndex
                        && territory.Battle.Turrets.All(t =>
                        {
                            var x = (index % territory.Battle.TerritoryCols + 0.5) * territory.Battle.TerritoryCellSize;
                            var y = (index / territory.Battle.TerritoryCols + 0.5) * territory.Battle.TerritoryCellSize;
                            return Math.Sqrt((x - t.TurretX) * (x - t.TurretX) + (y - t.TurretY) * (y - t.TurretY))
                                   > t.TurretRadius * territory.Battle.ShieldRingScale + 20;
                        }));
    var shot = AssistSuite.AddBall(territory, $"territory-{packedValue}", ProjectileRole.SmallShot, packedValue);
    shot.X = (cell % territory.Battle.TerritoryCols + 0.5) * territory.Battle.TerritoryCellSize;
    shot.Y = (cell / territory.Battle.TerritoryCols + 0.5) * territory.Battle.TerritoryCellSize;
    var originalSize = shot.Size;
    var territorySpentBefore = territory.Battle.ValueLedger.TerritorySpent;
    territory.Battle.Step(1.0 / 60);
    var territorySpent = territory.Battle.ValueLedger.TerritorySpent - territorySpentBefore;
    Check($"v3.3 promoted small value {packedValue} captures without becoming shell geometry",
        shot.Projectile!.Role == ProjectileRole.SmallShot
        && shot.Projectile.CapturesLeft < packedValue
        && Math.Abs(shot.Size - originalSize) < 1e-9
        && territorySpent == packedValue - shot.Projectile.CapturesLeft);
}

foreach (var packedValue in new[] { 2, 4, 8, 64 })
{
    var grind = AssistSuite.NewHarness(run, new BalanceConfig(), protectFriendlyValue: false);
    var grindOwner = grind.Battle.Turrets[0];
    var ownerIndex = grind.Battle.TerritoryFactionIds
        .Select((id, index) => (id, index))
        .Single(x => x.id.Equals(grindOwner.Id, StringComparison.OrdinalIgnoreCase)).index;
    var cell = Enumerable.Range(0, grind.Battle.TerritoryOwners.Length)
        .First(index => grind.Battle.TerritoryOwners[index] == ownerIndex
                        && grind.Battle.Turrets.All(t =>
                        {
                            var x = (index % grind.Battle.TerritoryCols + 0.5) * grind.Battle.TerritoryCellSize;
                            var y = (index / grind.Battle.TerritoryCols + 0.5) * grind.Battle.TerritoryCellSize;
                            return Math.Sqrt((x - t.TurretX) * (x - t.TurretX) + (y - t.TurretY) * (y - t.TurretY))
                                   > t.TurretRadius * grind.Battle.ShieldRingScale + 20;
                        }));
    var shell = AssistSuite.AddBall(grind, $"enemy-shell-{packedValue}", ProjectileRole.Shell, packedValue, ownerIndex: 1);
    var small = AssistSuite.AddBall(grind, $"enemy-small-{packedValue}", ProjectileRole.SmallShot, packedValue);
    var x = (cell % grind.Battle.TerritoryCols + 0.5) * grind.Battle.TerritoryCellSize;
    var y = (cell / grind.Battle.TerritoryCols + 0.5) * grind.Battle.TerritoryCellSize;
    shell.X = small.X = x;
    shell.Y = small.Y = y;
    var ledgerBefore = grind.Battle.ValueLedger;
    grind.Battle.Step(1.0 / 60);
    var expectedDrain = Math.Min(
        packedValue,
        Math.Max(1, (int)Math.Round(packedValue * grind.BalanceStore.Current.GrindRatePerSecond / 60)));
    var ledgerAfter = grind.Battle.ValueLedger;
    Check($"v3.3 promoted small value {packedValue} keeps role through enemy grinding",
        small.Projectile!.Role == ProjectileRole.SmallShot
        && small.Projectile.CapturesLeft == packedValue - expectedDrain
        && ledgerAfter.EnemyGround - ledgerBefore.EnemyGround == expectedDrain * 2L,
        $"small={small.Projectile.CapturesLeft} expected={packedValue - expectedDrain} enemyGround={ledgerAfter.EnemyGround - ledgerBefore.EnemyGround}");
}

var packedShield = AssistSuite.NewHarness(run, new BalanceConfig());
var packedShieldOwner = packedShield.Battle.Turrets[0];
var packedShieldTarget = packedShield.Battle.Turrets[1];
packedShieldTarget.Shield = packedShield.BattleConfig.Arena.ShieldCostPerValue;
var packedShieldShot = AssistSuite.AddBall(packedShield, "packed-shield", ProjectileRole.SmallShot, 4);
packedShieldShot.X = packedShieldTarget.TurretX;
packedShieldShot.Y = packedShieldTarget.TurretY;
var shieldBeforePacked = packedShieldTarget.Shield;
packedShield.Battle.Step(1.0 / 60);
Check("v3.3 promoted small uses small-shot shield path",
    packedShield.BattleWorld.Balls.Contains(packedShieldShot)
    && packedShieldShot.Projectile!.CapturesLeft == 3
    && packedShieldTarget.Shield == 0,
    $"alive={packedShield.BattleWorld.Balls.Contains(packedShieldShot)} "
    + $"value={packedShieldShot.Projectile!.CapturesLeft} shield={packedShieldTarget.Shield:0.###} "
    + $"before={shieldBeforePacked:0.###}");

var twentyShells = AssistSuite.NewHarness(run, new BalanceConfig());
var twentyReceiver = AssistSuite.AddBall(twentyShells, "twenty-receiver", ProjectileRole.Shell, 100);
for (var i = 0; i < 20; i++)
    AssistSuite.AddBall(twentyShells, $"twenty-{i:D2}", ProjectileRole.Shell, 1);
var twentyTotal = AssistSuite.ProjectileValue(twentyShells);
AssistSuite.Advance(twentyShells, 60);
Check("v3.5.2 twenty shell donors share initial contact plus 60-second receiver budget",
    twentyReceiver.Projectile!.CapturesLeft == 107 && AssistSuite.ProjectileValue(twentyShells) == twentyTotal,
    $"receiver={twentyReceiver.Projectile!.CapturesLeft} total={AssistSuite.ProjectileValue(twentyShells)}");

var visualAssist = AssistSuite.NewHarness(run, new BalanceConfig
{
    FriendlyAbsorbSmallRate = 10,
    FriendlyAssistVisualEnabled = true,
});
AssistSuite.AddBall(visualAssist, "visual-receiver", ProjectileRole.Shell, 10);
AssistSuite.AddBall(visualAssist, "visual-small", ProjectileRole.SmallShot, 2);
AssistSuite.Advance(visualAssist, 0.2);
Check("v3.3 transfer feedback is aggregated and entity-free",
    visualAssist.Battle.AssistVisuals.Count == 1
    && visualAssist.Battle.AssistVisuals[0].Amount == 2
    && visualAssist.BattleWorld.Balls.Count == 1);

var hiddenVisual = AssistSuite.NewHarness(run, new BalanceConfig
{
    FriendlyAbsorbSmallRate = 10,
    FriendlyAssistVisualEnabled = false,
});
AssistSuite.AddBall(hiddenVisual, "hidden-receiver", ProjectileRole.Shell, 10);
AssistSuite.AddBall(hiddenVisual, "hidden-small", ProjectileRole.SmallShot, 2);
AssistSuite.Advance(hiddenVisual, 0.2);
Check("v3.3 transfer feedback can be disabled", hiddenVisual.Battle.AssistVisuals.Count == 0);

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
regenTarget.Shield = regenTarget.MaxShield;
withRegen.Battle.Step(0.1);
Check("shield regen opt-in exceeds legacy maximum",
    regenTarget.Shield > regenTarget.MaxShield,
    $"shield={regenTarget.Shield:0.###} legacyMax={regenTarget.MaxShield:0.###}");

// 破盾直入与触杀必须是硬断言，而不是只打印结果。
AssistSuite.VerifyV352Fixes(run);

// 硬性时限应保证定时收敛。
var limited = new Harness(dataRoot, log, new BalanceConfig { HardTimeLimitSeconds = 1 });
limited.Director.Start(9, countdownSeconds: 0);
limited.Director.AdvanceSteps(90);
Check("hard time limit concludes", limited.Battle.WinnerId != null, limited.Battle.WinnerId ?? "-");
Check("hard time limit does not overrun", limited.Battle.ElapsedSeconds <= 1 + 1e-9,
    $"seconds={limited.Battle.ElapsedSeconds:0.######}");

// v3.4 V34-05:字段描述符必须覆盖 BalanceConfig 全部属性 —— 以后加字段忘登记,这里直接红,
// 而不是等到预设/剧本/试跑某条路径静默丢值。
var coverageProblems = BalanceFields.AuditCoverage();
Check("balance field registry covers every config property", coverageProblems.Count == 0,
    coverageProblems.Count == 0
        ? $"fields={BalanceFields.All.Count}"
        : string.Join(" ｜ ", coverageProblems));

// Clone 走描述符后必须仍是逐字段深拷贝:改一个字段不许串到源对象。
var cloneSource = new BalanceConfig();
foreach (var field in BalanceFields.All.Where(x => !x.IsBoolean))
    field.SetNumber(cloneSource, field.Min!.Value == 0 ? field.Max!.Value : field.Min!.Value);
foreach (var field in BalanceFields.All.Where(x => x.IsBoolean))
    field.SetBool(cloneSource, !field.GetBool(cloneSource));
var cloned = BalanceConfigStore.Clone(cloneSource);
var cloneMismatch = BalanceFields.All
    .Where(f => f.IsBoolean
        ? f.GetBool(cloned) != f.GetBool(cloneSource)
        : Math.Abs(f.GetNumber(cloned) - f.GetNumber(cloneSource)) > 1e-9)
    .Select(f => f.Property)
    .ToList();
Check("balance clone copies every field", cloneMismatch.Count == 0, string.Join(", ", cloneMismatch));

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
var assistResult = await bus.ExecuteAsync("balance.assist smallRate=0.3 shellRate=0.15 reach=1.25 max=90000", "verify");
Check("balance.assist command", assistResult.Success
    && simHarness.BalanceStore.Current.FriendlyAbsorbSmallRate == 0.3
    && assistResult.Message.Contains("在场 small="));
var retiredBreakthrough = await bus.ExecuteAsync("balance.shield breakthrough=true", "verify");
Check("balance.shield rejects retired breakthrough option", !retiredBreakthrough.Success
    && !simHarness.BalanceStore.Current.ShieldBreakthrough);

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

await EditorCommandSuite.RunAsync(run);
GameplayRegressionSuite.Run(run);

Console.WriteLine($"v3.5.2 hash seed=42 @60s: {defaultHashA}");
Console.WriteLine($"v3.5.2 hash seed=43 @60s: {defaultHashC}");
verificationTimer.Stop();
Console.WriteLine("FULL_SUMMARY " + JsonSerializer.Serialize(new
{
    Version = "3.6.0",
    Suite = "full",
    ElapsedMilliseconds = verificationTimer.Elapsed.TotalMilliseconds,
    Passed = run.PassedCount,
    Failed = failures.Count,
    Hashes = new
    {
        V31 = legacyHash,
        V32 = v32RollbackHash,
        V352Seed42 = defaultHashA,
        V352Seed43 = defaultHashC,
    },
    ArtifactRoot = artifacts.Root,
}));
Console.WriteLine(failures.Count == 0 ? "VERIFY PASS" : $"VERIFY FAIL ({failures.Count})");
if (failures.Count > 0)
    Console.WriteLine(string.Join(Environment.NewLine, failures.Select(x => "  " + x)));
return artifacts.Complete(failures.Count == 0 ? 0 : 1);
