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

string RunHash(BalanceConfig balance, int seed, int frames, out Harness harness)
{
    harness = new Harness(dataRoot, log, balance);
    harness.Director.Start(seed, countdownSeconds: 0);
    harness.Director.AdvanceSteps(frames);
    return harness.Director.DeterministicHash();
}

ArchitectureSuite.Run(run);
TimelineSuite.Run(run);

if (args.Contains("--render-page-smoke", StringComparer.OrdinalIgnoreCase))
    return artifacts.Complete(WBall.Verify.Suites.PageSuite.Run(run));

if (args.Contains("--render-smoke", StringComparer.OrdinalIgnoreCase))
{
    var renderHarness = new Harness(dataRoot, log, new BalanceConfig());
    var renderRoot = artifacts.Suite("render-smoke");
    var renderTime = new RenderTimeConfigStore(Path.Combine(renderRoot, "config"), log);
    renderTime.Current.Width = 640;
    renderTime.Current.Height = 360;
    renderTime.Current.Fps = 2;
    renderTime.Current.PreferMp4 = false;
    renderTime.Current.KeepPng = true;
    renderTime.Current.RenderAutoSlow = false;
    renderTime.Save();
    var renderWorkspace = Path.Combine(renderRoot, "workspace");
    var renderScenarios = new ScenarioStore(renderWorkspace, log);
    using var renderJobs = new RenderJobService(
        renderHarness.EconomyWorld,
        renderHarness.BattleConfig,
        renderHarness.BalanceStore,
        renderHarness.Weapons,
        new StageState(),
        renderScenarios,
        renderTime,
        dataRoot,
        renderWorkspace,
        log);
    var liveHash = renderHarness.Director.DeterministicHash();
    var liveConfigSnapshot = JsonSerializer.Serialize(new
    {
        renderHarness.BattleConfig.Turrets,
        renderHarness.BattleConfig.Arena,
        Balance = renderHarness.BalanceStore.Current,
    });
    renderJobs.Start(new RenderJobRequest(RenderEndMode.Output, 1, 42, "smoke"));
    var deadline = DateTime.UtcNow.AddSeconds(30);
    while (renderJobs.Status.Active && DateTime.UtcNow < deadline)
        Thread.Sleep(50);
    var renderStatus = renderJobs.Status;
    Check("render smoke completes", renderStatus.Stage == "completed", renderStatus.Error);
    Check("render smoke writes exact frames", renderStatus.Frame == 2, $"frames={renderStatus.Frame}");
    Check("render smoke writes manifest", File.Exists(Path.Combine(renderStatus.OutputDirectory ?? "", "manifest.json")));
    Check("render smoke writes PNG stream",
        Directory.Exists(Path.Combine(renderStatus.OutputDirectory ?? "", "frames"))
        && Directory.EnumerateFiles(Path.Combine(renderStatus.OutputDirectory!, "frames"), "*.png").Count() == 2);
    Check("render smoke does not mutate live world", liveHash == renderHarness.Director.DeterministicHash());

    var firstFingerprint = RenderFingerprint(renderStatus.ManifestPath!);
    renderJobs.Start(new RenderJobRequest(RenderEndMode.Output, 1, 42, "smoke-repeat"));
    deadline = DateTime.UtcNow.AddSeconds(30);
    while (renderJobs.Status.Active && DateTime.UtcNow < deadline)
        Thread.Sleep(50);
    var repeatStatus = renderJobs.Status;
    var repeatFingerprint = RenderFingerprint(repeatStatus.ManifestPath!);
    Check("render same input has identical result and sampled-frame hashes",
        repeatStatus.Stage == "completed" && firstFingerprint == repeatFingerprint,
        $"first={firstFingerprint} repeat={repeatFingerprint}");

    var scenarioSeed = renderJobs.ResolveSeed("demo2");
    renderJobs.Start(new RenderJobRequest(RenderEndMode.Output, 0.5, scenarioSeed, "scenario", Scenario: "demo2"));
    deadline = DateTime.UtcNow.AddSeconds(30);
    while (renderJobs.Status.Active && DateTime.UtcNow < deadline)
        Thread.Sleep(50);
    var scenarioStatus = renderJobs.Status;
    using (var scenarioManifest = JsonDocument.Parse(File.ReadAllText(scenarioStatus.ManifestPath!)))
    {
        var request = scenarioManifest.RootElement.GetProperty("request");
        Check("render scenario freezes named source and uses scenario seed",
            scenarioStatus.Stage == "completed"
            && request.GetProperty("scenario").GetString() == "demo2"
            && request.GetProperty("seed").GetInt32() == 7);
    }
    Check("render scenario does not mutate live world", liveHash == renderHarness.Director.DeterministicHash());

    renderJobs.Start(new RenderJobRequest(RenderEndMode.Output, 60, 43, "cancel"));
    renderJobs.Pause();
    Thread.Sleep(150);
    var pausedFrame = renderJobs.Status.Frame;
    Thread.Sleep(150);
    Check("render pause stops frame progress", renderJobs.Status.Stage == "paused" && renderJobs.Status.Frame == pausedFrame,
        $"stage={renderJobs.Status.Stage} frame={renderJobs.Status.Frame}");
    var cancelWatch = Stopwatch.StartNew();
    renderJobs.Cancel();
    deadline = DateTime.UtcNow.AddSeconds(5);
    while (renderJobs.Status.Active && DateTime.UtcNow < deadline)
        Thread.Sleep(50);
    cancelWatch.Stop();
    var cancelStatus = renderJobs.Status;
    Check("render cancel reaches terminal state within 2 seconds",
        cancelStatus.Stage == "canceled" && cancelWatch.Elapsed < TimeSpan.FromSeconds(2),
        $"stage={cancelStatus.Stage} elapsed={cancelWatch.Elapsed.TotalMilliseconds:0.###}ms");
    using (var cancelManifest = JsonDocument.Parse(File.ReadAllText(cancelStatus.ManifestPath!)))
    {
        Check("render cancel manifest and partial output agree",
            cancelManifest.RootElement.GetProperty("status").GetString() == "canceled"
            && !Directory.EnumerateFiles(cancelStatus.OutputDirectory!, "*.partial*", SearchOption.AllDirectories).Any());
    }

    renderTime.Current.Fps = 1;
    renderTime.Current.PreferMp4 = true;
    renderTime.Save();
    renderJobs.Start(new RenderJobRequest(RenderEndMode.Output, 1, 44, "mp4"));
    deadline = DateTime.UtcNow.AddSeconds(30);
    while (renderJobs.Status.Active && DateTime.UtcNow < deadline)
        Thread.Sleep(50);
    var mp4Status = renderJobs.Status;
    var mp4Ok = !string.IsNullOrWhiteSpace(mp4Status.Mp4Path)
                && File.Exists(mp4Status.Mp4Path)
                && new FileInfo(mp4Status.Mp4Path).Length > 0;
    var pngFallback = !string.IsNullOrWhiteSpace(mp4Status.Error)
                      && Directory.EnumerateFiles(Path.Combine(mp4Status.OutputDirectory!, "frames"), "*.png").Any();
    Check("render MP4 streams or falls back explicitly",
        mp4Status.Stage == "completed" && (mp4Ok || pngFallback),
        $"mp4={mp4Status.Mp4Path ?? "-"} error={mp4Status.Error ?? "-"}");

    renderTime.Current.Width = 320;
    renderTime.Current.Height = 240;
    renderTime.Current.Fps = 1;
    renderTime.Current.PreferMp4 = false;
    renderTime.Current.RenderAutoSlow = true;
    renderTime.Save();
    var stressScenario = renderScenarios.Load("demo4");
    stressScenario.Name = "stress10k";
    foreach (var turret in stressScenario.Turrets)
        turret.InitialBalls = 2_500;
    renderScenarios.Save(stressScenario);
    var baselineMemory = Process.GetCurrentProcess().WorkingSet64;
    renderJobs.Start(new RenderJobRequest(
        RenderEndMode.Output, 1, stressScenario.Seed, "stress-10k", Scenario: stressScenario.Name));
    deadline = DateTime.UtcNow.AddSeconds(60);
    while (renderJobs.Status.Active && DateTime.UtcNow < deadline)
        Thread.Sleep(50);
    var stressStatus = renderJobs.Status;
    using (var stressManifest = JsonDocument.Parse(File.ReadAllText(stressStatus.ManifestPath!)))
    {
        var peak = stressManifest.RootElement.GetProperty("peakWorkingSetBytes").GetInt64();
        Check("render 10k balls reaches minimum simulation scale without dropping frames",
            stressStatus.Stage == "completed" && stressStatus.Frame == 1
            && Math.Abs(stressStatus.SimulationScale - 0.25) < 1e-9,
            $"stage={stressStatus.Stage} frame={stressStatus.Frame} scale={stressStatus.SimulationScale}");
        Check("render 10k working-set growth stays below 512 MiB",
            peak - baselineMemory < 512L * 1024 * 1024,
            $"growth={(peak - baselineMemory) / 1024.0 / 1024:0.##} MiB");
        Check("render BGRA and projection queue stay bounded",
            stressManifest.RootElement.GetProperty("peakBgraFrames").GetInt32() <= 1
            && stressManifest.RootElement.GetProperty("peakQueueDepth").GetInt32() <= renderTime.Current.QueueCapacity);
    }

    renderTime.Current.RenderAutoSlow = false;
    renderTime.Current.ManualSimulationScale = 0.10;
    renderTime.Save();
    renderJobs.Start(new RenderJobRequest(
        RenderEndMode.Output, 1, stressScenario.Seed, "stress-10k-manual", Scenario: stressScenario.Name));
    deadline = DateTime.UtcNow.AddSeconds(60);
    while (renderJobs.Status.Active && DateTime.UtcNow < deadline)
        Thread.Sleep(50);
    var manualStressStatus = renderJobs.Status;
    Check("render 10k manual scale ignores pressure when auto slow is disabled",
        manualStressStatus.Stage == "completed"
        && Math.Abs(manualStressStatus.SimulationScale - 0.10) < 1e-9
        && Math.Abs(manualStressStatus.SimulationTime - 0.10) <= 1.0 / 60,
        $"stage={manualStressStatus.Stage} scale={manualStressStatus.SimulationScale} simulation={manualStressStatus.SimulationTime}");

    var recordRegistry = new CommandRegistry();
    RecordCommands.Register(recordRegistry, renderJobs);
    var recordBus = new CommandBus(recordRegistry, log);
    var aliasConfig = await recordBus.ExecuteAsync(
        "record.config w=320 h=240 fps=1 mp4=false keeppng=true autoSlow=false manualScale=0.1",
        "verify");
    var aliasStart = await recordBus.ExecuteAsync(
        "record.start mode=output seconds=1 seed=46 name=record-alias",
        "verify");
    deadline = DateTime.UtcNow.AddSeconds(30);
    while (renderJobs.Status.Active && DateTime.UtcNow < deadline)
        Thread.Sleep(50);
    var renderStatusCommand = await recordBus.ExecuteAsync("render.status", "verify");
    var recordStatusCommand = await recordBus.ExecuteAsync("record.status", "verify");
    Check("record.config/start/status preserve render semantics",
        aliasConfig.Success && aliasStart.Success && renderJobs.Status.Stage == "completed"
        && recordStatusCommand.Success && renderStatusCommand.Success
        && recordStatusCommand.Message.EndsWith(renderStatusCommand.Message, StringComparison.Ordinal));
    await recordBus.ExecuteAsync(
        "record.start mode=output seconds=60 seed=47 name=record-stop",
        "verify");
    var aliasStop = await recordBus.ExecuteAsync("record.stop", "verify");
    deadline = DateTime.UtcNow.AddSeconds(5);
    while (renderJobs.Status.Active && DateTime.UtcNow < deadline)
        Thread.Sleep(25);
    Check("record.stop cancels and a clean render task can restart",
        aliasStop.Success && renderJobs.Status.Stage == "canceled",
        $"stop={aliasStop.Success} stage={renderJobs.Status.Stage}");
    var restart = await recordBus.ExecuteAsync(
        "render.start mode=output seconds=1 seed=48 name=after-record-stop",
        "verify");
    deadline = DateTime.UtcNow.AddSeconds(30);
    while (renderJobs.Status.Active && DateTime.UtcNow < deadline)
        Thread.Sleep(50);
    Check("render restart after record.stop completes", restart.Success && renderJobs.Status.Stage == "completed",
        $"start={restart.Success} stage={renderJobs.Status.Stage} message={restart.Message}");

    var liveConfigAfter = JsonSerializer.Serialize(new
    {
        renderHarness.BattleConfig.Turrets,
        renderHarness.BattleConfig.Arena,
        Balance = renderHarness.BalanceStore.Current,
    });
    Check("render leaves battle arena and balance configuration unchanged",
        liveConfigSnapshot == liveConfigAfter && liveHash == renderHarness.Director.DeterministicHash());

    Console.WriteLine(failures.Count == 0 ? "RENDER SMOKE PASS" : $"RENDER SMOKE FAIL ({failures.Count})");
    return artifacts.Complete(failures.Count == 0 ? 0 : 1);
}

if (args.Contains("--render-long-acceptance", StringComparer.OrdinalIgnoreCase))
{
    var longHarness = new Harness(dataRoot, log, new BalanceConfig());
    var longSuite = artifacts.Suite("render-long");
    var longConfig = new RenderTimeConfigStore(Path.Combine(longSuite, "config"), log);
    longConfig.Current.Width = 1920;
    longConfig.Current.Height = 1080;
    longConfig.Current.Fps = 30;
    longConfig.Current.QueueCapacity = 4;
    longConfig.Current.PreferMp4 = false;
    longConfig.Current.KeepPng = true;
    longConfig.Current.RenderAutoSlow = true;
    longConfig.Current.ManualSimulationScale = 1;
    longConfig.Current.SlowStartBalls = 100;
    longConfig.Current.SlowFullBalls = 500;
    longConfig.Save();
    var longWorkspace = Path.Combine(longSuite, "workspace");
    var longScenarios = new ScenarioStore(longWorkspace, log);
    var jointScenario = longScenarios.Load("demo4");
    jointScenario.Name = "joint-v33-render";
    jointScenario.Balance ??= new BalanceConfig();
    jointScenario.Balance.FriendlyAssistEnabled = true;
    jointScenario.Balance.FriendlyAbsorbSmallRate = 10;
    jointScenario.Balance.FriendlyAssistReachFactor = 3;
    jointScenario.Balance.SmallPackThreshold = 2;
    jointScenario.Balance.SmallPackRatio = 2;
    jointScenario.Balance.SmallPackMax = 64;
    jointScenario.Arena.InitialShellCount = 100;
    jointScenario.Arena.InitialShellValue = 100;
    jointScenario.Arena.SmallBallSpeed = 400;
    var jointShellWeapon = jointScenario.Weapons.First(x => x.Kind == WeaponKind.Size);
    jointShellWeapon.Speed = 60;
    longScenarios.Save(jointScenario);
    using var longJobs = new RenderJobService(
        longHarness.EconomyWorld, longHarness.BattleConfig, longHarness.BalanceStore,
        longHarness.Weapons, new StageState(), longScenarios, longConfig,
        dataRoot, longWorkspace, log);

    longJobs.Start(new RenderJobRequest(
        RenderEndMode.Output, 5, 51, "1080p-short", Scenario: jointScenario.Name));
    var deadline = DateTime.UtcNow.AddMinutes(5);
    while (longJobs.Status.Active && DateTime.UtcNow < deadline)
        Thread.Sleep(25);
    var shortStatus = longJobs.Status;
    using var shortManifest = JsonDocument.Parse(File.ReadAllText(shortStatus.ManifestPath!));
    var shortPeak = shortManifest.RootElement.GetProperty("peakWorkingSetBytes").GetInt64();

    System.Windows.Threading.Dispatcher? uiDispatcher = null;
    var uiReady = new ManualResetEventSlim();
    var uiTicks = 0;
    var uiMaxGapMs = 0d;
    var uiThread = new Thread(() =>
    {
        uiDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var lastTick = Stopwatch.GetTimestamp();
        var timer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(50),
            System.Windows.Threading.DispatcherPriority.Background,
            (_, _) =>
            {
                var now = Stopwatch.GetTimestamp();
                uiMaxGapMs = Math.Max(uiMaxGapMs, Stopwatch.GetElapsedTime(lastTick, now).TotalMilliseconds);
                lastTick = now;
                _ = longJobs.Status;
                uiTicks++;
            },
            uiDispatcher);
        timer.Start();
        uiReady.Set();
        System.Windows.Threading.Dispatcher.Run();
        timer.Stop();
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    uiReady.Wait();
    var startWatch = Stopwatch.StartNew();
    uiDispatcher!.Invoke(() =>
        longJobs.Start(new RenderJobRequest(
            RenderEndMode.Output, 60, 52, "1080p-60s", Scenario: jointScenario.Name)));
    startWatch.Stop();
    deadline = DateTime.UtcNow.AddMinutes(15);
    while (longJobs.Status.Active && DateTime.UtcNow < deadline)
        Thread.Sleep(100);
    uiDispatcher.InvokeShutdown();
    uiThread.Join();

    var longStatus = longJobs.Status;
    using var longManifest = JsonDocument.Parse(File.ReadAllText(longStatus.ManifestPath!));
    var longRoot = longManifest.RootElement;
    var longPeak = longRoot.GetProperty("peakWorkingSetBytes").GetInt64();
    var framesDirectory = longStatus.PngDirectory!;
    var frameFiles = Directory.EnumerateFiles(framesDirectory, "frame_*.png")
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToArray();
    Check("render 60s 1080p30 completes all continuous frames",
        longStatus.Stage == "completed" && longStatus.Frame == 1_800
        && frameFiles.Length == 1_800
        && Path.GetFileName(frameFiles[0]) == "frame_000000.png"
        && Path.GetFileName(frameFiles[^1]) == "frame_001799.png",
        $"stage={longStatus.Stage} frames={longStatus.Frame}/{frameFiles.Length} error={longStatus.Error ?? "-"}");
    Check("render 60s 1080p keeps UI dispatcher responsive",
        startWatch.Elapsed < TimeSpan.FromSeconds(2) && uiTicks >= 10 && uiMaxGapMs < 2_000,
        $"start={startWatch.Elapsed.TotalMilliseconds:0.###}ms ticks={uiTicks} maxGap={uiMaxGapMs:0.###}ms");
    Check("render 1080p memory is bounded rather than duration-linear",
        longRoot.GetProperty("peakBgraFrames").GetInt32() <= 1
        && longRoot.GetProperty("peakQueueDepth").GetInt32() <= longConfig.Current.QueueCapacity
        && longPeak - shortPeak < 256L * 1024 * 1024,
        $"shortPeak={shortPeak / 1024.0 / 1024:0.##}MiB longPeak={longPeak / 1024.0 / 1024:0.##}MiB delta={(longPeak - shortPeak) / 1024.0 / 1024:0.##}MiB");
    var ledger = longRoot.GetProperty("valueLedger");
    Check("render long pressure reclaims promoted small shots without dropping frames",
        longRoot.GetProperty("peakPromotedSmallShots").GetInt32() > 0
        && ledger.GetProperty("friendlyPromotedSmallReclaimed").GetInt64() > 0
        && longRoot.GetProperty("scaleSegments").GetArrayLength() > 0
        && longStatus.Frame == 1_800,
        $"peakPromoted={longRoot.GetProperty("peakPromotedSmallShots").GetInt32()} "
        + $"reclaimed={ledger.GetProperty("friendlyPromotedSmallReclaimed").GetInt64()} "
        + $"scaleSegments={longRoot.GetProperty("scaleSegments").GetArrayLength()}");
    Console.WriteLine(failures.Count == 0 ? "RENDER LONG ACCEPTANCE PASS" : "RENDER LONG ACCEPTANCE FAIL");
    return artifacts.Complete(failures.Count == 0 ? 0 : 1);
}

static string RenderFingerprint(string manifestPath)
{
    using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
    var root = document.RootElement;
    return $"{root.GetProperty("finalDirectorHash").GetString()}|"
           + root.GetProperty("sampleFrameHashes").GetRawText() + "|"
           + root.GetProperty("scaleSegments").GetRawText();
}

if (args.Contains("--assist-performance", StringComparer.OrdinalIgnoreCase))
    return artifacts.Complete(WBall.Verify.Suites.AssistSuite.RunPerformance(run));

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
var legacyHash = RunHash(legacy, 42, 3600, out var legacyRun);
const string V31Hash = "6381A3898C0FAD65B57D43C140917A010713AA3015F601BACE14C7E5B88333F3";
Check("v3.1 rollback hash", legacyHash == V31Hash, legacyHash);

var v32Rollback = new BalanceConfig { FriendlyAssistEnabled = false, ShieldBreakthrough = true };
var v32RollbackHash = RunHash(v32Rollback, 42, 3600, out _);
const string V32Hash = "E24FD280C34B54F79DAFCAE466DE299B4B76F56B69D83EF63757B96F81BF9184";
Check("v3.2 rollback hash", v32RollbackHash == V32Hash, v32RollbackHash);

var defaultHashA = RunHash(new BalanceConfig(), 42, 3600, out var defaultRunA);
var defaultHashB = RunHash(new BalanceConfig(), 42, 3600, out _);
var defaultHashC = RunHash(new BalanceConfig(), 43, 3600, out _);
const string V351Seed42Hash = "8E88A3C73371C02D1FDACCD14590693111DF7049B2FA25C5F54D85FCA9C3D012";
const string V351Seed43Hash = "EF25BCDD0D2E7A3FA42AE08D1038001290011856E6644F00558F6EF124447F4F";
Check("v3.5.1 same-seed deterministic", defaultHashA == defaultHashB, defaultHashA);
Check("v3.5.1 seed 42 hash", defaultHashA == V351Seed42Hash, defaultHashA);
Check("v3.5.1 seed 43 hash", defaultHashC == V351Seed43Hash, defaultHashC);
Check("v3.5.1 different seed differs", defaultHashA != defaultHashC, defaultHashC);
Check("v3.5.1 default intentionally changed", defaultHashA != V32Hash);
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
AssistSuite.Advance(manySmall, 60);
var absorbed = sharedReceiver.Projectile!.CapturesLeft - 100;
Check("v3.3 small assist budget is shared", absorbed is >= 14 and <= 15,
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
Check("v3.3 zero rates disable both transfer paths",
    zeroReceiver.Projectile!.CapturesLeft == 10
    && zeroSmall.Projectile!.CapturesLeft == 2
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
    var territory = AssistSuite.NewHarness(run, new BalanceConfig());
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
    var grind = AssistSuite.NewHarness(run, new BalanceConfig());
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
packedShieldTarget.Shield = 100;
var packedShieldShot = AssistSuite.AddBall(packedShield, "packed-shield", ProjectileRole.SmallShot, 4);
packedShieldShot.X = packedShieldTarget.TurretX;
packedShieldShot.Y = packedShieldTarget.TurretY;
var shieldBeforePacked = packedShieldTarget.Shield;
packedShield.Battle.Step(1.0 / 60);
Check("v3.3 promoted small uses small-shot shield path",
    !packedShield.BattleWorld.Balls.Contains(packedShieldShot)
    && Math.Abs(packedShieldTarget.Shield
                - Math.Max(0, shieldBeforePacked - packedShield.BattleConfig.Arena.ShieldCostPerValue * 4)) < 1e-9,
    $"alive={packedShield.BattleWorld.Balls.Contains(packedShieldShot)} shield={packedShieldTarget.Shield:0.###}");

var twentyShells = AssistSuite.NewHarness(run, new BalanceConfig());
var twentyReceiver = AssistSuite.AddBall(twentyShells, "twenty-receiver", ProjectileRole.Shell, 100);
for (var i = 0; i < 20; i++)
    AssistSuite.AddBall(twentyShells, $"twenty-{i:D2}", ProjectileRole.Shell, 1);
var twentyTotal = AssistSuite.ProjectileValue(twentyShells);
AssistSuite.Advance(twentyShells, 60);
Check("v3.3 twenty shell donors share one 60-second receiver budget",
    twentyReceiver.Projectile!.CapturesLeft == 106 && AssistSuite.ProjectileValue(twentyShells) == twentyTotal,
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
regenTarget.Shield = 100;
withRegen.Battle.Step(0.1);
Check("shield regen opt-in", regenTarget.Shield > 100, $"shield={regenTarget.Shield:0.###}");

// 破盾直入与触杀必须是硬断言，而不是只打印结果。
AssistSuite.VerifyV351Fixes(run);

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

Console.WriteLine($"v3.5.1 hash seed=42 @60s: {defaultHashA}");
Console.WriteLine($"v3.5.1 hash seed=43 @60s: {defaultHashC}");
verificationTimer.Stop();
Console.WriteLine("FULL_SUMMARY " + JsonSerializer.Serialize(new
{
    Version = "3.5.1",
    Suite = "full",
    ElapsedMilliseconds = verificationTimer.Elapsed.TotalMilliseconds,
    Passed = run.PassedCount,
    Failed = failures.Count,
    Hashes = new
    {
        V31 = legacyHash,
        V32 = v32RollbackHash,
        V351Seed42 = defaultHashA,
        V351Seed43 = defaultHashC,
    },
    ArtifactRoot = artifacts.Root,
}));
Console.WriteLine(failures.Count == 0 ? "VERIFY PASS" : $"VERIFY FAIL ({failures.Count})");
if (failures.Count > 0)
    Console.WriteLine(string.Join(Environment.NewLine, failures.Select(x => "  " + x)));
return artifacts.Complete(failures.Count == 0 ? 0 : 1);
