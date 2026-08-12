using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AppShell.Core.Commands;
using WBall.Battle;
using WBall.Commands;
using WBall.DropZone;
using WBall.Game;
using WBall.Model;
using WBall.Sim;
using WBall.Stage;

namespace WBall.Verify.Suites;

internal static class RuntimePerformanceSuite
{
    public static int Run(VerifyRun run, string[] args)
    {
        var smoke = args.Contains("--perf-smoke", StringComparer.OrdinalIgnoreCase);
        var probeSeconds = NumericArg(args, "--perf-probe-seconds");
        var probeWarmupSeconds = NumericArg(args, "--perf-warmup-seconds");
        var developmentProbe = probeSeconds != null || probeWarmupSeconds != null;
        var measuredSteps = smoke ? 5 : 60;

        var lod = new VisualLodController();
        run.Check("v3.7 LOD enters simplified at 5k",
            lod.Update(4_999) == VisualLodLevel.Full
            && lod.Update(5_000) == VisualLodLevel.Simplified);
        run.Check("v3.7 LOD enters minimal at 10k",
            lod.Update(10_000) == VisualLodLevel.Minimal);
        run.Check("v3.7 LOD keeps 10 percent return hysteresis",
            lod.Update(9_000) == VisualLodLevel.Minimal
            && lod.Update(8_999) == VisualLodLevel.Simplified
            && lod.Update(4_500) == VisualLodLevel.Simplified
            && lod.Update(4_499) == VisualLodLevel.Full);
        VerifyFrameSnapshots(run);
        VerifyCoordinator(run).GetAwaiter().GetResult();

        var fullSpeed = MeasureCombined(run, economyBalls: 1_000, battleBalls: 1_000, measuredSteps);
        var pressure = MeasureCombined(run, economyBalls: 10_000, battleBalls: 10_000, measuredSteps);
        var economyOnly = MeasureEconomy(run, ballCount: 10_000, measuredSteps);
        var battleOnly = MeasureBattle(run, ballCount: 10_000, measuredSteps);
        var collision = MeasureCollision(run, ballCount: 10_000, measuredSteps: smoke ? 1 : 3);
        var snapshot = MeasureSnapshot(run, frames: smoke ? 5 : 60);
        var wpfRender = MeasureWpfComposite(run, frames: smoke ? 5 : 60);
        var visibleRuntime = VisibleRuntimeProbe.Run(
            run, smoke, probeWarmupSeconds, probeSeconds);

        var process = Process.GetCurrentProcess();
        process.Refresh();
        var commit = CommandOutput("git", "rev-parse", "HEAD");
        var sourceStatus = CommandOutput("git", "status", "--porcelain", "-uno");
        var videoControllers = CommandOutput(
            "powershell.exe",
            "-NoProfile",
            "-Command",
            "Get-CimInstance Win32_VideoController | Where-Object { $_.CurrentHorizontalResolution -gt 0 } | "
            + "Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate | "
            + "ConvertTo-Json -Compress");
        var report = new
        {
            Version = "3.7.0-planned",
            Suite = "runtime-performance",
            Strict = !smoke && !developmentProbe,
            Coverage = "headless fixed-step, allocation, deterministic hash, LOD policy and visible 1080p WPF runtime",
            Environment = new
            {
                OS = RuntimeInformation.OSDescription,
                Framework = RuntimeInformation.FrameworkDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                ProcessorCount = Environment.ProcessorCount,
                Processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
                BuildConfiguration = "Release x64",
                Commit = string.IsNullOrWhiteSpace(commit) ? null : commit,
                SourceDirty = !string.IsNullOrWhiteSpace(sourceStatus),
                PrimaryDisplay = $"{SystemParameters.PrimaryScreenWidth:0}x{SystemParameters.PrimaryScreenHeight:0}",
                VideoControllers = string.IsNullOrWhiteSpace(videoControllers) ? null : videoControllers,
                ActivePowerSchemeGuid = CommandOutput(
                    "powershell.exe", "-NoProfile", "-Command",
                    "$value=powercfg /getactivescheme; "
                    + "[regex]::Match(($value -join ' '), '[0-9a-fA-F-]{36}').Value"),
                SessionName = Environment.GetEnvironmentVariable("SESSIONNAME"),
                IsRemoteSession = Environment.GetEnvironmentVariable("SESSIONNAME")?.StartsWith(
                    "RDP", StringComparison.OrdinalIgnoreCase) == true,
            },
            FullSpeed2k = fullSpeed,
            Pressure20k = pressure,
            Economy10k = economyOnly,
            Battle10k = battleOnly,
            Collision10k = collision,
            Snapshot20k = snapshot,
            WpfComposite20k = wpfRender,
            VisibleRuntime20k = visibleRuntime,
            WorkingSetBytes = process.WorkingSet64,
            PrivateBytes = process.PrivateMemorySize64,
            ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
            Gen0 = GC.CollectionCount(0),
            Gen1 = GC.CollectionCount(1),
            Gen2 = GC.CollectionCount(2),
            TimestampUtc = DateTime.UtcNow,
        };
        var reportPath = Path.Combine(run.Artifacts.Suite("runtime-performance"), "runtime-performance.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        run.Check("v3.7 runtime performance report is written", File.Exists(reportPath), reportPath);
        if (!smoke && !developmentProbe)
        {
            run.Check("v3.7 2k full-speed fixed step p95 stays within 16.7ms",
                fullSpeed.P95Milliseconds <= 16.7,
                $"p95={fullSpeed.P95Milliseconds:0.###}ms alloc={fullSpeed.AllocatedBytesPerStep:0}B/step");
            run.Check("v3.7 20k pressure fixed step p95 stays within 50ms",
                pressure.P95Milliseconds <= 50,
                $"p95={pressure.P95Milliseconds:0.###}ms alloc={pressure.AllocatedBytesPerStep:0}B/step");
            run.Check("v3.7 performance process stays below 1.5GiB",
                process.WorkingSet64 <= 1536L * 1024 * 1024,
                $"workingSet={process.WorkingSet64 / 1024d / 1024:0.###}MiB");
            run.Check("v3.7 auxiliary offscreen WPF composition stays bounded",
                wpfRender.P95Milliseconds <= 50,
                $"p95={wpfRender.P95Milliseconds:0.###}ms alloc={wpfRender.AllocatedBytesPerStep:0}B/frame");
            run.Check("v3.7 20k double-buffer snapshot stays within capture budget",
                snapshot.P95Milliseconds <= 10,
                $"p95={snapshot.P95Milliseconds:0.###}ms alloc={snapshot.AllocatedBytesPerStep:0}B/frame");
            run.Check("v3.7 visible 20k stage sustains 30fps",
                visibleRuntime.AverageFramesPerSecond >= 29.5,
                $"fps={visibleRuntime.AverageFramesPerSecond:0.###}");
            run.Check("v3.7 visible stage p95 interval stays within 50ms",
                visibleRuntime.P95FrameIntervalMilliseconds <= 50,
                $"p95={visibleRuntime.P95FrameIntervalMilliseconds:0.###}ms");
            run.Check("v3.7 visible stage has no 200ms UI stalls",
                visibleRuntime.UiStallsOver200Milliseconds == 0,
                $"stalls={visibleRuntime.UiStallsOver200Milliseconds} max={visibleRuntime.MaximumFrameIntervalMilliseconds:0.###}ms");
            run.Check("v3.7 visible CommandBus p95 stays within 100ms",
                visibleRuntime.CommandSamples > 0 && visibleRuntime.CommandP95Milliseconds <= 100,
                $"samples={visibleRuntime.CommandSamples} p95={visibleRuntime.CommandP95Milliseconds:0.###}ms");
            run.Check("v3.7 visible stage working set stays below 1.5GiB",
                visibleRuntime.PeakWorkingSetBytes <= 1536L * 1024 * 1024,
                $"peak={visibleRuntime.PeakWorkingSetBytes / 1024d / 1024:0.###}MiB");
            run.Check("v3.7 visible stage memory slope stays below 5MiB/min",
                visibleRuntime.WorkingSetSlopeMiBPerMinute <= 5,
                $"slope={visibleRuntime.WorkingSetSlopeMiBPerMinute:0.###}MiB/min");
            run.Check("v3.7 visible stage Gen2 collections stay bounded",
                visibleRuntime.Gen2Collections <= 2,
                $"gen2={visibleRuntime.Gen2Collections}");
            run.Check("v3.7 visible stage has no sustained LOH growth",
                visibleRuntime.LohGrowthBytes <= 16L * 1024 * 1024,
                $"lohGrowth={visibleRuntime.LohGrowthBytes / 1024d / 1024:0.###}MiB");
        }
        else
        {
            Console.WriteLine(
                $"PERF_SMOKE 2k={fullSpeed.P95Milliseconds:0.###}ms "
                + $"20k={pressure.P95Milliseconds:0.###}ms economy10k={economyOnly.P95Milliseconds:0.###}ms "
                + $"battle10k={battleOnly.P95Milliseconds:0.###}ms collision10k={collision.P95Milliseconds:0.###}ms "
                + $"snapshot20k={snapshot.P95Milliseconds:0.###}ms wpf20k={wpfRender.P95Milliseconds:0.###}ms "
                + $"visibleFps={visibleRuntime.AverageFramesPerSecond:0.###} visibleP95={visibleRuntime.P95FrameIntervalMilliseconds:0.###}ms");
        }

        return run.Conclude("RUNTIME PERFORMANCE");
    }

    private static double? NumericArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(args[i][(name.Length + 1)..],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var inline))
                return Math.Max(0.1, inline);
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)
                && i + 1 < args.Length
                && double.TryParse(args[i + 1],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var following))
                return Math.Max(0.1, following);
        }
        return null;
    }

    private static string? CommandOutput(string fileName, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.Default,
                StandardErrorEncoding = Encoding.Default,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process == null)
                return null;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5_000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void VerifyFrameSnapshots(VerifyRun run)
    {
        var harness = run.NewHarness(new BalanceConfig(), new ArenaLayoutConfig { BallCollision = false });
        harness.Battle.Reset(seed: 42);
        SeedEconomy(harness.EconomyWorld, 12);
        SeedBattle(harness, 8, projectile: true);
        harness.EconomyWorld.Balls[0].PushTrail(1, 2, 8);
        harness.EconomyWorld.Balls[0].PushTrail(3, 4, 8);

        var front = new RealtimeFrameSnapshot();
        var back = new RealtimeFrameSnapshot();
        front.Capture(1, harness.Stage, harness.EconomyWorld, harness.BattleWorld,
            harness.Battle, harness.Director);
        var frontBalls = front.EconomyBalls;
        var firstX = front.EconomyBalls[0].X;
        harness.EconomyWorld.Balls[0].X += 25;
        back.Capture(2, harness.Stage, harness.EconomyWorld, harness.BattleWorld,
            harness.Battle, harness.Director);

        run.Check("v3.7 frame slots keep independent published data",
            front.Sequence == 1 && back.Sequence == 2
            && Math.Abs(front.EconomyBalls[0].X - firstX) < 1e-9
            && Math.Abs(back.EconomyBalls[0].X - firstX - 25) < 1e-9);
        run.Check("v3.7 frame captures one coherent dynamic sequence",
            front.EconomyBallCount == 12 && front.BattleBallCount == 8
            && front.TurretCount == harness.Battle.Turrets.Count
            && front.TerritoryOwnerCount == harness.Battle.TerritoryOwners.Length
            && front.EconomyBalls[0].TrailCount == 2
            && front.EconomyTrails[0] == new RealtimeTrailPoint(1, 2)
            && front.EconomyTrails[1] == new RealtimeTrailPoint(3, 4));
        front.Capture(3, harness.Stage, harness.EconomyWorld, harness.BattleWorld,
            harness.Battle, harness.Director);
        run.Check("v3.7 frame buffers reuse grown storage",
            ReferenceEquals(frontBalls, front.EconomyBalls),
            $"capacity={front.EconomyBalls.Length} count={front.EconomyBallCount}");
    }

    private static PerformanceMetric MeasureSnapshot(VerifyRun run, int frames)
    {
        var harness = run.NewHarness(new BalanceConfig { FriendlyAssistVisualEnabled = false },
            new ArenaLayoutConfig { BallCollision = false });
        harness.Battle.Reset(seed: 42);
        SeedEconomy(harness.EconomyWorld, 10_000);
        SeedBattle(harness, 10_000, projectile: true);
        var snapshots = new[] { new RealtimeFrameSnapshot(), new RealtimeFrameSnapshot() };
        snapshots[0].Capture(0, harness.Stage, harness.EconomyWorld, harness.BattleWorld,
            harness.Battle, harness.Director);
        snapshots[1].Capture(1, harness.Stage, harness.EconomyWorld, harness.BattleWorld,
            harness.Battle, harness.Director);
        var index = 0;
        long sequence = 1;
        return MeasureSteps(
            frames,
            () =>
            {
                index = 1 - index;
                snapshots[index].Capture(++sequence, harness.Stage, harness.EconomyWorld,
                    harness.BattleWorld, harness.Battle, harness.Director);
            },
            harness.Director.DeterministicHash);
    }

    private static PerformanceMetric MeasureCombined(
        VerifyRun run,
        int economyBalls,
        int battleBalls,
        int measuredSteps)
    {
        var harness = run.NewHarness(new BalanceConfig
        {
            FriendlyAssistVisualEnabled = false,
        }, new ArenaLayoutConfig { BallCollision = false });
        harness.Battle.Reset(seed: 42);
        harness.Battle.AutomaticFire = false;
        SeedEconomy(harness.EconomyWorld, economyBalls);
        SeedBattle(harness, battleBalls, projectile: true);
        PhysicsEngine.Step(harness.EconomyWorld, BattleDirector.FixedStepSeconds);
        harness.Battle.Step(BattleDirector.FixedStepSeconds);
        return MeasureSteps(
            measuredSteps,
            () =>
            {
                PhysicsEngine.Step(harness.EconomyWorld, BattleDirector.FixedStepSeconds);
                harness.Battle.Step(BattleDirector.FixedStepSeconds);
            },
            harness.Director.DeterministicHash);
    }

    private static async Task VerifyCoordinator(VerifyRun run)
    {
        var harness = run.NewHarness(new BalanceConfig(), new ArenaLayoutConfig { BallCollision = false });
        using var coordinator = new RealtimeSimulationCoordinator(
            harness.Stage,
            harness.EconomyWorld,
            harness.BattleWorld,
            harness.Director,
            new WBall.Recording.RenderTimeConfig { PreviewAutoSlow = false });
        var registry = new CommandRegistry();
        DirectorCommands.Register(registry, harness.Director, harness.Weapons);
        coordinator.WrapNewCommands(registry, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var bus = new CommandBus(registry, run.Log);

        var startWatch = Stopwatch.StartNew();
        var start = await bus.ExecuteAsync("battle.start seed=42", "perf");
        startWatch.Stop();
        await Task.Delay(150);
        var runningFrame = harness.Director.Frame;
        var pause = await bus.ExecuteAsync("battle.pause", "perf");
        var pausedFrame = harness.Director.Frame;
        await Task.Delay(80);
        var stablePausedFrame = harness.Director.Frame;
        var resume = await bus.ExecuteAsync("battle.resume", "perf");
        await Task.Delay(80);
        var resumedFrame = harness.Director.Frame;
        var reset = await bus.ExecuteAsync("battle.reset", "perf");

        run.Check("v3.7 coordinator advances live simulation off the UI timer",
            start.Success && runningFrame > 0,
            $"start={start.Success} frame={runningFrame}");
        run.Check("v3.7 command gate commits pause and resume at step boundaries",
            pause.Success && resume.Success && pausedFrame == stablePausedFrame && resumedFrame > stablePausedFrame,
            $"paused={pausedFrame}/{stablePausedFrame} resumed={resumedFrame}");
        run.Check("v3.7 command boundary stays responsive",
            startWatch.ElapsedMilliseconds <= 100 && reset.Success && harness.Stage.Mode == StageMode.Edit,
            $"start={startWatch.Elapsed.TotalMilliseconds:0.###}ms reset={reset.Success} mode={harness.Stage.Mode}");
    }

    private static PerformanceMetric MeasureCollision(VerifyRun run, int ballCount, int measuredSteps)
    {
        var harness = run.NewHarness(new BalanceConfig(), new ArenaLayoutConfig { BallCollision = true });
        harness.Battle.Reset(seed: 42);
        harness.Battle.AutomaticFire = false;
        SeedBattle(harness, ballCount, projectile: false);
        PhysicsEngine.Step(harness.BattleWorld, BattleDirector.FixedStepSeconds);
        return MeasureSteps(
            measuredSteps,
            () => PhysicsEngine.Step(harness.BattleWorld, BattleDirector.FixedStepSeconds),
            harness.Director.DeterministicHash);
    }

    private static PerformanceMetric MeasureEconomy(VerifyRun run, int ballCount, int measuredSteps)
    {
        var harness = run.NewHarness(new BalanceConfig());
        SeedEconomy(harness.EconomyWorld, ballCount);
        PhysicsEngine.Step(harness.EconomyWorld, BattleDirector.FixedStepSeconds);
        return MeasureSteps(
            measuredSteps,
            () => PhysicsEngine.Step(harness.EconomyWorld, BattleDirector.FixedStepSeconds),
            harness.Director.DeterministicHash);
    }

    private static PerformanceMetric MeasureBattle(VerifyRun run, int ballCount, int measuredSteps)
    {
        var harness = run.NewHarness(new BalanceConfig { FriendlyAssistVisualEnabled = false },
            new ArenaLayoutConfig { BallCollision = false });
        harness.Battle.Reset(seed: 42);
        harness.Battle.AutomaticFire = false;
        SeedBattle(harness, ballCount, projectile: true);
        harness.Battle.Step(BattleDirector.FixedStepSeconds);
        return MeasureSteps(
            measuredSteps,
            () => harness.Battle.Step(BattleDirector.FixedStepSeconds),
            harness.Director.DeterministicHash);
    }

    private static PerformanceMetric MeasureWpfComposite(VerifyRun run, int frames)
    {
        PerformanceMetric? result = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var harness = run.NewHarness(new BalanceConfig { FriendlyAssistVisualEnabled = false },
                    new ArenaLayoutConfig { BallCollision = false });
                harness.Battle.Reset(seed: 42);
                SeedEconomy(harness.EconomyWorld, 10_000);
                SeedBattle(harness, 10_000, projectile: true);
                var economy = new DropZoneView(harness.EconomyWorld, run.Log)
                {
                    AutoStepEnabled = false,
                    Width = 960,
                    Height = 1080,
                };
                economy.SetVisualLod(VisualLodLevel.Minimal);
                var arena = new ArenaView(harness.BattleWorld, harness.Battle)
                {
                    Width = 960,
                    Height = 1080,
                };
                arena.SetVisualLod(VisualLodLevel.Minimal);
                var frame = new RealtimeFrameSnapshot();
                frame.Capture(1, harness.Stage, harness.EconomyWorld, harness.BattleWorld,
                    harness.Battle, harness.Director);
                economy.SetRealtimeFrame(frame);
                arena.SetRealtimeFrame(frame);
                var root = new Grid { Width = 1920, Height = 1080 };
                root.ColumnDefinitions.Add(new ColumnDefinition());
                root.ColumnDefinitions.Add(new ColumnDefinition());
                root.Children.Add(economy);
                root.Children.Add(arena);
                Grid.SetColumn(arena, 1);
                root.Measure(new Size(1920, 1080));
                root.Arrange(new Rect(0, 0, 1920, 1080));
                root.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1920, 1080, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(root);
                result = MeasureSteps(frames, () => bitmap.Render(root), harness.Director.DeterministicHash);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
            throw new InvalidOperationException("WPF performance probe failed", failure);
        return result ?? throw new InvalidOperationException("WPF performance probe produced no result");
    }

    private static PerformanceMetric MeasureSteps(
        int measuredSteps,
        Action step,
        Func<string> finalHash)
    {
        var samples = new double[measuredSteps];
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < measuredSteps; index++)
        {
            var started = Stopwatch.GetTimestamp();
            step();
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Array.Sort(samples);
        return new PerformanceMetric(
            samples.Average(),
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            Percentile(samples, 0.99),
            samples[^1],
            allocated / (double)Math.Max(1, measuredSteps),
            measuredSteps,
            finalHash());
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
            return 0;
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static void SeedEconomy(SceneWorld world, int count)
    {
        world.Balls.Clear();
        for (var index = 0; index < count; index++)
        {
            var ball = NewGridBall(world, index, count);
            ball.Color = index % 2 == 0 ? "#22C55E" : "#06B6D4";
            world.Balls.Add(ball);
        }
    }

    internal static void SeedEconomyForProbe(SceneWorld world, int count) =>
        SeedEconomy(world, count);

    private static void SeedBattle(Harness harness, int count, bool projectile)
    {
        var world = harness.BattleWorld;
        world.Balls.Clear();
        var turrets = harness.Battle.Turrets;
        for (var index = 0; index < count; index++)
        {
            var ball = NewGridBall(world, index, count);
            var owner = turrets[index % turrets.Count];
            ball.Color = owner.Color;
            if (projectile)
            {
                ball.Projectile = new ProjectileState
                {
                    OwnerFactionId = owner.Id,
                    WeaponName = "小球",
                    Damage = 1,
                    CapturesLeft = 1,
                    Role = ProjectileRole.SmallShot,
                };
            }
            world.Balls.Add(ball);
        }
    }

    internal static void SeedBattleForProbe(Harness harness, int count, bool projectile) =>
        SeedBattle(harness, count, projectile);

    private static Ball NewGridBall(SceneWorld world, int index, int count)
    {
        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(
            count * world.WorldWidth / Math.Max(1, world.WorldHeight))));
        var rows = Math.Max(1, (int)Math.Ceiling(count / (double)columns));
        return new Ball
        {
            Id = $"perf-{index:D5}",
            X = (index % columns + 0.5) * world.WorldWidth / columns,
            Y = (index / columns + 0.5) * world.WorldHeight / rows,
            Size = 1,
            Weight = 1,
        };
    }

    private sealed record PerformanceMetric(
        double AverageMilliseconds,
        double P50Milliseconds,
        double P95Milliseconds,
        double P99Milliseconds,
        double MaximumMilliseconds,
        double AllocatedBytesPerStep,
        int Samples,
        string FinalHash);
}
