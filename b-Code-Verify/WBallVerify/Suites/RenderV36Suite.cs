using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AppShell.Core.Commands;
using WBall.Battle;
using WBall.Commands;
using WBall.Game;
using WBall.Model;
using WBall.Recording;
using WBall.Stage;

namespace WBall.Verify.Suites;

internal static class RenderV36Suite
{
    public static int Run(VerifyRun run)
    {
        VerifyCombatValueGate(run);
        VerifyConfigContract(run);
        VerifyMp4Pipeline(run);
        return run.Conclude("RENDER V3.6");
    }

    private static void VerifyCombatValueGate(VerifyRun run)
    {
        var harness = run.NewHarness(new BalanceConfig());
        harness.EconomyWorld.Balls.Clear();
        foreach (var faction in harness.Battle.Turrets)
        {
            faction.ClearAmmo();
            faction.SmallAmmo = 0;
            faction.Alive = faction.Id.Equals("green", StringComparison.OrdinalIgnoreCase);
        }
        var residual = new Ball
        {
            Id = harness.EconomyWorld.NextBallId(),
            Color = harness.Battle.FindRequired("cyan").Color,
            Multiplier = 17,
        };
        harness.EconomyWorld.Balls.Add(residual);
        var cyan = harness.Battle.FindRequired("cyan");
        cyan.SmallAmmo = 3;
        cyan.EnqueueAmmo(new AmmoShell(5, "大球"));
        var ember = new Ball
        {
            Id = harness.BattleWorld.NextBallId(),
            X = 480,
            Y = 450,
            Color = cyan.Color,
            Size = 8,
            Projectile = new ProjectileState
            {
                OwnerFactionId = cyan.Id,
                WeaponName = "大球",
                CapturesLeft = 7,
                FriendlyPendingSmallValue = 11,
                Role = ProjectileRole.Ember,
            },
        };
        harness.BattleWorld.Balls.Add(ember);
        var cyanValue = harness.Battle.RemainingCombatValues().Single(x => x.FactionId == "cyan");
        harness.Battle.Step(1d / 60);
        run.Check("V3.6 dead turret keeps every combat-value carrier",
            harness.Battle.WinnerId == null
            && cyanValue.EconomyBalls == 17
            && cyanValue.SmallAmmo == 3
            && cyanValue.QueuedAmmo == 5
            && cyanValue.Projectiles == 7
            && cyanValue.PendingAbsorption == 11
            && cyanValue.Total == 43,
            $"winner={harness.Battle.WinnerId ?? "-"} value={cyanValue.Total}");
        harness.EconomyWorld.Balls.Remove(residual);
        harness.Battle.Step(1d / 60);
        run.Check("V3.6 inventory and projectile value still prevent elimination",
            harness.Battle.WinnerId == null);
        cyan.SmallAmmo = 0;
        cyan.ClearAmmo();
        harness.Battle.Step(1d / 60);
        run.Check("V3.6 projectile and pending absorption still prevent elimination",
            harness.Battle.WinnerId == null);
        harness.BattleWorld.Balls.Remove(ember);
        harness.Battle.Step(1d / 60);
        run.Check("V3.6 unique winner locks after final combat value disappears",
            harness.Battle.WinnerId == "green", harness.Battle.WinnerId ?? "-");
    }

    private static void VerifyConfigContract(VerifyRun run)
    {
        var root = run.Artifacts.Suite("render-v36-config");
        var store = new RenderTimeConfigStore(Path.Combine(root, "config"), run.Log);
        var harness = run.NewHarness(new BalanceConfig());
        var workspace = Path.Combine(root, "workspace");
        var scenarios = new ScenarioStore(workspace, run.Log);
        using var jobs = CreateService(run, harness, scenarios, store, workspace);
        var registry = new CommandRegistry();
        RecordCommands.Register(registry, jobs);
        var bus = new CommandBus(registry, run.Log);

        var before = JsonSerializer.Serialize(jobs.Config);
        var invalid = bus.ExecuteAsync("render.config w=321 h=240", "verify").GetAwaiter().GetResult();
        run.Check("V3.6 odd resolution fails transactionally",
            !invalid.Success && JsonSerializer.Serialize(jobs.Config) == before,
            invalid.Message);
        var oldStart = bus.ExecuteAsync("render.start seconds=1", "verify").GetAwaiter().GetResult();
        var oldConfig = bus.ExecuteAsync("render.config mp4=true", "verify").GetAwaiter().GetResult();
        var estimate = bus.ExecuteAsync("render.estimate seconds=1", "verify").GetAwaiter().GetResult();
        run.Check("V3.6 retired duration mode and PNG parameters fail explicitly",
            !oldStart.Success && !oldConfig.Success && !estimate.Success,
            $"start={oldStart.Message}; config={oldConfig.Message}; estimate={estimate.Message}");

        var valid = bus.ExecuteAsync(
            "render.config w=320 h=240 fps=5 queue=2 autoSlow=false manualScale=1",
            "verify").GetAwaiter().GetResult();
        run.Check("V3.6 valid custom even resolution commits", valid.Success
            && jobs.Config.Width == 320 && jobs.Config.Height == 240 && jobs.Config.Fps == 5,
            valid.Message);

        var legacyJson = """
            {
              "width": 640,
              "height": 360,
              "fps": 10,
              "preferMp4": false,
              "keepPng": true,
              "maxOutputSeconds": 9,
              "mode": "output",
              "seconds": 3
            }
            """;
        var legacyRoot = Path.Combine(root, "legacy");
        Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(Path.Combine(legacyRoot, "render_time.json"), legacyJson);
        var migrated = new RenderTimeConfigStore(legacyRoot, run.Log);
        migrated.Save();
        var migratedJson = File.ReadAllText(migrated.Path);
        run.Check("V3.6 old JSON fields load and disappear on save",
            migrated.Current.Width == 640
            && !migratedJson.Contains("preferMp4", StringComparison.OrdinalIgnoreCase)
            && !migratedJson.Contains("maxOutputSeconds", StringComparison.OrdinalIgnoreCase));

        var invalidScenario = new ScenarioSnapshot
        {
            Name = "v36-no-opponents",
            Turrets =
            [
                new TurretDefinition { Id = "a", Name = "A", Color = "#111111", MaxHp = 0 },
                new TurretDefinition { Id = "b", Name = "B", Color = "#222222", MaxHp = 0 },
            ],
            Arena = new ArenaLayoutConfig(),
            Weapons = WeaponCatalog.CloneDefinitions(harness.Weapons.Weapons).ToList(),
        };
        scenarios.Save(invalidScenario);
        var taskCount = jobs.List(200).Count;
        string? invalidScenarioError = null;
        try { jobs.Start(new RenderJobRequest(42, "invalid", invalidScenario.Name)); }
        catch (Exception ex) { invalidScenarioError = ex.Message; }
        run.Check("V3.6 scenario without effective opponents is rejected before task creation",
            invalidScenarioError != null && jobs.List(200).Count == taskCount,
            invalidScenarioError);
    }

    private static void VerifyMp4Pipeline(VerifyRun run)
    {
        var root = run.Artifacts.Suite("render-v36-mp4");
        var harness = run.NewHarness(new BalanceConfig());
        var config = new RenderTimeConfigStore(Path.Combine(root, "config"), run.Log);
        config.Apply(new RenderTimeConfig
        {
            Width = 320,
            Height = 240,
            Fps = 5,
            QueueCapacity = 2,
            PreviewAutoSlow = false,
            RenderAutoSlow = false,
            ManualSimulationScale = 1,
        });
        var workspace = Path.Combine(root, "workspace");
        var scenarios = new ScenarioStore(workspace, run.Log);
        var quick = CreateQuickWinnerScenario(harness);
        scenarios.Save(quick);
        using var jobs = CreateService(run, harness, scenarios, config, workspace);
        var liveHash = harness.Director.DeterministicHash();
        var liveConfig = JsonSerializer.Serialize(new
        {
            harness.BattleConfig.Turrets,
            harness.BattleConfig.Arena,
            Balance = harness.BalanceStore.Current,
        });

        jobs.Start(new RenderJobRequest(quick.Seed, "winner-mp4", quick.Name));
        WaitForTerminal(jobs, TimeSpan.FromSeconds(60));
        var status = jobs.Status;
        run.Check("V3.6 winner-only render completes as MP4", status.Stage == "completed"
            && !string.IsNullOrWhiteSpace(status.Mp4Path)
            && File.Exists(status.Mp4Path), status.Error);
        if (status.Stage != "completed" || status.ManifestPath == null)
            return;

        using var document = JsonDocument.Parse(File.ReadAllText(status.ManifestPath));
        var manifest = document.RootElement;
        var animationStart = manifest.GetProperty("victoryAnimationStartFrame").GetInt64();
        var animationEnd = manifest.GetProperty("victoryAnimationEndFrameExclusive").GetInt64();
        run.Check("V3.6 appends exactly three seconds of victory animation",
            animationEnd - animationStart == 3 * config.Current.Fps
            && manifest.GetProperty("winnerId").GetString() == "blue",
            $"frames={animationEnd - animationStart} winner={manifest.GetProperty("winnerId").GetString()}");
        run.Check("V3.6 manifest records frozen FFmpeg identity and final result",
            manifest.GetProperty("ffmpegVersion").GetString()!.Contains("8.0.1", StringComparison.Ordinal)
            && manifest.GetProperty("ffmpegSha256").GetString() ==
               "5AF82A0D4FE2B9EAE211B967332EA97EDFC51C6B328CA35B827E73EAC560DC0D"
            && manifest.GetProperty("eliminationSimulationTimes").TryGetProperty("red", out _)
            && !string.IsNullOrWhiteSpace(manifest.GetProperty("finalDirectorHash").GetString()));
        var files = Directory.EnumerateFiles(status.OutputDirectory!, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        run.Check("V3.6 successful output contains only MP4 and manifest",
            files.SequenceEqual(new[] { "manifest.json", "output.mp4" }, StringComparer.OrdinalIgnoreCase),
            string.Join(",", files));
        var probe = Probe(run, status.Mp4Path!);
        run.Check("V3.6 MP4 is readable H.264 at configured dimensions and FPS",
            probe.ExitCode == 0
            && probe.Output.Contains("Video: h264", StringComparison.OrdinalIgnoreCase)
            && probe.Output.Contains("320x240", StringComparison.OrdinalIgnoreCase)
            && probe.Output.Contains("5 fps", StringComparison.OrdinalIgnoreCase),
            probe.Output);
        run.Check("V3.6 offline render leaves live world and configuration untouched",
            liveHash == harness.Director.DeterministicHash()
            && liveConfig == JsonSerializer.Serialize(new
            {
                harness.BattleConfig.Turrets,
                harness.BattleConfig.Arena,
                Balance = harness.BalanceStore.Current,
            }));

        var firstFingerprint = Fingerprint(manifest);
        jobs.Start(new RenderJobRequest(quick.Seed, "winner-repeat", quick.Name));
        WaitForTerminal(jobs, TimeSpan.FromSeconds(60));
        using var repeatDocument = JsonDocument.Parse(File.ReadAllText(jobs.Status.ManifestPath!));
        run.Check("V3.6 same frozen input remains deterministic",
            jobs.Status.Stage == "completed" && firstFingerprint == Fingerprint(repeatDocument.RootElement));

        var presetResults = new List<string>();
        foreach (var (width, height) in new[] { (1280, 720), (1920, 1080), (2560, 1440), (3840, 2160) })
        {
            var next = jobs.Config.Clone();
            next.Width = width;
            next.Height = height;
            next.Fps = 1;
            jobs.UpdateConfig(next);
            jobs.Start(new RenderJobRequest(quick.Seed, $"preset-{width}x{height}", quick.Name));
            WaitForTerminal(jobs, TimeSpan.FromMinutes(3));
            var presetProbe = jobs.Status.Stage == "completed"
                ? Probe(run, jobs.Status.Mp4Path!)
                : (ExitCode: -1, Output: jobs.Status.Error ?? "failed");
            var passed = jobs.Status.Stage == "completed"
                         && presetProbe.ExitCode == 0
                         && presetProbe.Output.Contains($"{width}x{height}", StringComparison.OrdinalIgnoreCase);
            presetResults.Add($"{width}x{height}={passed}");
        }
        run.Check("V3.6 720p 1080p 1440p and 4K presets encode successfully",
            presetResults.All(x => x.EndsWith("=True", StringComparison.Ordinal)),
            string.Join(" ", presetResults));

        var restore = jobs.Config.Clone();
        restore.Width = 320;
        restore.Height = 240;
        restore.Fps = 5;
        jobs.UpdateConfig(restore);

        jobs.Start(new RenderJobRequest(42, "cancel", "demo4"));
        jobs.Pause();
        Thread.Sleep(150);
        var pausedFrame = jobs.Status.Frame;
        Thread.Sleep(150);
        run.Check("V3.6 pause freezes frame progress", jobs.Status.Stage == "paused" && jobs.Status.Frame == pausedFrame);
        jobs.Cancel();
        WaitForTerminal(jobs, TimeSpan.FromSeconds(5));
        run.Check("V3.6 cancel removes partial MP4 and writes canceled manifest",
            jobs.Status.Stage == "canceled"
            && !Directory.EnumerateFiles(jobs.Status.OutputDirectory!, "*.partial.mp4", SearchOption.AllDirectories).Any()
            && JsonDocument.Parse(File.ReadAllText(jobs.Status.ManifestPath!)).RootElement
                .GetProperty("status").GetString() == "canceled",
            jobs.Status.Error);

        var missingRoot = Path.Combine(root, "missing-ffmpeg-workspace");
        using var missingJobs = new RenderJobService(
            harness.EconomyWorld, harness.BattleConfig, harness.BalanceStore, harness.Weapons,
            new StageState(), scenarios, config, run.Root, missingRoot, run.Log,
            Path.Combine(root, "missing", "ffmpeg.exe"));
        missingJobs.Start(new RenderJobRequest(quick.Seed, "missing-ffmpeg", quick.Name));
        WaitForTerminal(missingJobs, TimeSpan.FromSeconds(10));
        run.Check("V3.6 missing encoder fails without PNG or successful MP4",
            missingJobs.Status.Stage == "failed"
            && !Directory.EnumerateFiles(missingJobs.Status.OutputDirectory!, "*.png", SearchOption.AllDirectories).Any()
            && !Directory.EnumerateFiles(missingJobs.Status.OutputDirectory!, "*.mp4", SearchOption.AllDirectories).Any()
            && File.Exists(missingJobs.Status.ManifestPath),
            missingJobs.Status.Error);
    }

    private static ScenarioSnapshot CreateQuickWinnerScenario(Harness harness)
    {
        var weapons = WeaponCatalog.CloneDefinitions(harness.Weapons.Weapons).ToList();
        foreach (var weapon in weapons)
        {
            weapon.Speed = 1_200;
            weapon.UnlockAtSeconds = 0;
        }
        return new ScenarioSnapshot
        {
            Name = "v36-quick-winner",
            Seed = 360,
            Turrets =
            [
                new TurretDefinition
                {
                    Id = "blue", Name = "Azure", Color = "#3B82F6", Quadrant = 2,
                    InitialBalls = 0, MaxHp = 1_000_000_000, MaxShield = 0, InitialShield = 0,
                    ProjectileSize = 60, ProjectileCount = 1, FireIntervalSec = 60,
                },
                new TurretDefinition
                {
                    Id = "red", Name = "Crimson", Color = "#EF4444", Quadrant = 1,
                    InitialBalls = 0, MaxHp = 1, MaxShield = 0, InitialShield = 0,
                    ProjectileSize = 2, ProjectileCount = 1, FireIntervalSec = 60,
                },
            ],
            Arena = new ArenaLayoutConfig
            {
                Name = "v36-direct",
                Mode = "direct",
                Targeting = "nearest",
                Width = 240,
                Height = 200,
                TurretRadius = 12,
                TurretMarginXRatio = 0.20,
                TurretMarginYRatio = 0.25,
                ProjectileLifetimeSec = 2,
                MaxProjectiles = 32,
            },
            Balance = new BalanceConfig { CountdownSeconds = 0, SettleSeconds = 0 },
            Weapons = weapons,
        };
    }

    private static RenderJobService CreateService(
        VerifyRun run,
        Harness harness,
        ScenarioStore scenarios,
        RenderTimeConfigStore config,
        string workspace) => new(
            harness.EconomyWorld,
            harness.BattleConfig,
            harness.BalanceStore,
            harness.Weapons,
            new StageState(),
            scenarios,
            config,
            run.Root,
            workspace,
            run.Log,
            ResolveFfmpeg());

    private static string ResolveFfmpeg()
    {
        var source = Path.GetFullPath(Path.Combine(
            Environment.CurrentDirectory,
            "b-Code-WBall", "App", "ThirdParty", "ffmpeg", "ffmpeg.exe"));
        return File.Exists(source)
            ? source
            : Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
    }

    private static void WaitForTerminal(RenderJobService service, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (service.Status.Active && DateTime.UtcNow < deadline)
            Thread.Sleep(25);
        if (service.Status.Active)
            service.Cancel();
    }

    private static (int ExitCode, string Output) Probe(VerifyRun run, string mp4)
    {
        var start = new ProcessStartInfo
        {
            FileName = ResolveFfmpeg(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-hide_banner");
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(mp4);
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add("null");
        start.ArgumentList.Add("-");
        using var process = Process.Start(start)!;
        var output = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    private static string Fingerprint(JsonElement manifest) =>
        $"{manifest.GetProperty("finalDirectorHash").GetString()}|"
        + manifest.GetProperty("sampleFrameHashes").GetRawText() + "|"
        + manifest.GetProperty("scaleSegments").GetRawText();
}
