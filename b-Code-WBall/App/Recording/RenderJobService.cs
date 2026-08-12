using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AppShell.Core.Logging;
using WBall.Battle;
using WBall.Game;
using WBall.Model;
using WBall.Stage;

namespace WBall.Recording;

public sealed record RenderJobRequest(
    int Seed,
    string Name,
    string? Scenario = null);

public sealed record RenderJobStatus(
    string JobId,
    string Stage,
    long Frame,
    double VideoTime,
    double SimulationTime,
    double WallElapsed,
    int BallCount,
    double SimulationScale,
    string? OutputDirectory,
    string? Mp4Path,
    string? Error,
    double GeneratedFps = 0,
    long WorkingSetBytes = 0,
    int QueueDepth = 0,
    int PeakQueueDepth = 0,
    string? ManifestPath = null,
    string? FinalHash = null)
{
    public bool Active => Stage is "starting" or "simulating" or "rendering" or "animating" or "paused" or "finalizing";
}

public sealed class RenderJobManifest
{
    public int SchemaVersion { get; set; } = 2;
    public string AppVersion { get; set; } = "3.6.0";
    public string JobId { get; set; } = "";
    public string Status { get; set; } = "starting";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public RenderJobRequest Request { get; set; } = new(42, "battle");
    public RenderTimeConfig Config { get; set; } = new();
    public string SceneHash { get; set; } = "";
    public string ArenaHash { get; set; } = "";
    public string BalanceHash { get; set; } = "";
    public string WeaponsHash { get; set; } = "";
    public string StageHash { get; set; } = "";
    public string? FfmpegVersion { get; set; }
    public string? FfmpegSha256 { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; }
    public long Frames { get; set; }
    public double VideoTime { get; set; }
    public double SimulationTime { get; set; }
    public double WallElapsed { get; set; }
    public double StepCredit { get; set; }
    public double GeneratedFps { get; set; }
    public long PeakWorkingSetBytes { get; set; }
    public int PeakQueueDepth { get; set; }
    public int PeakBgraFrames { get; set; }
    public ProjectileValueLedger? ValueLedger { get; set; }
    public FriendlyAssistSnapshot? FinalAssist { get; set; }
    public IReadOnlyList<FactionCombatValue>? FinalCombatValues { get; set; }
    public string? WinnerId { get; set; }
    public string? WinnerName { get; set; }
    public double? WinnerLockedAtSimulationTime { get; set; }
    public IReadOnlyDictionary<string, double>? EliminationSimulationTimes { get; set; }
    public long? VictoryAnimationStartFrame { get; set; }
    public long? VictoryAnimationEndFrameExclusive { get; set; }
    public string? FinalDirectorHash { get; set; }
    public string? Mp4Path { get; set; }
    public long OutputBytes { get; set; }
    public string? FailureReason { get; set; }
    public List<string> SampleFrameHashes { get; set; } = [];
    public List<RenderScaleSegment> ScaleSegments { get; set; } = [];
}

public sealed class RenderScaleSegment
{
    public long StartFrame { get; set; }
    public long EndFrame { get; set; }
    public double StartVideoTime { get; set; }
    public double StartSimulationTime { get; set; }
    public int BallCount { get; set; }
    public double Scale { get; set; }
}

/// <summary>冻结输入后，以独立世界逐帧模拟并经 FFmpeg 直接流式生成 MP4。</summary>
public sealed class RenderJobService : IDisposable
{
    internal const int VictoryAnimationSeconds = 3;
    internal const int SafetyVideoHours = 24;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _sync = new();
    private readonly SceneWorld _liveWorld;
    private readonly BattleConfigStore _battleConfig;
    private readonly BalanceConfigStore _balanceConfig;
    private readonly WeaponCatalog _weapons;
    private readonly StageState _liveStage;
    private readonly ScenarioStore _scenarios;
    private readonly RenderTimeConfigStore _timeStore;
    private readonly string _recordsRoot;
    private readonly string _ffmpegPath;
    private readonly IShellLog _log;
    private readonly ManualResetEventSlim _pauseGate = new(initialState: true);
    private CancellationTokenSource? _cancellation;
    private Thread? _simulationThread;
    private bool _paused;
    private RenderJobStatus _status = new("-", "idle", 0, 0, 0, 0, 0, 1, null, null, null);

    public RenderJobService(
        SceneWorld liveWorld,
        BattleConfigStore battleConfig,
        BalanceConfigStore balanceConfig,
        WeaponCatalog weapons,
        StageState liveStage,
        ScenarioStore scenarios,
        RenderTimeConfigStore timeStore,
        string dataRoot,
        string workspaceRoot,
        IShellLog log,
        string? ffmpegPath = null)
    {
        _liveWorld = liveWorld;
        _battleConfig = battleConfig;
        _balanceConfig = balanceConfig;
        _weapons = weapons;
        _liveStage = liveStage;
        _scenarios = scenarios;
        _timeStore = timeStore;
        _recordsRoot = Path.Combine(workspaceRoot, "records");
        _ffmpegPath = ffmpegPath ?? FfmpegEncoder.ResolveBundledPath();
        _log = log;
        Directory.CreateDirectory(_recordsRoot);
    }

    public event Action? StatusChanged;

    public RenderJobStatus Status
    {
        get { lock (_sync) return _status; }
    }

    public RenderTimeConfig Config => _timeStore.Current;
    public IReadOnlyList<string> Scenarios => _scenarios.List();
    public int ResolveSeed(string? scenario, int fallback = 42) =>
        string.IsNullOrWhiteSpace(scenario) ? fallback : _scenarios.Load(scenario).Seed;

    public void SaveConfig() => _timeStore.Save();
    public void UpdateConfig(RenderTimeConfig config) => _timeStore.Apply(config);

    public RenderJobStatus Start(RenderJobRequest request)
    {
        lock (_sync)
        {
            if (_status.Active)
                throw new InvalidOperationException("已有出片任务在运行");
        }

        var snapshot = CaptureInput(request.Scenario);
        ValidateInput(snapshot);
        RenderTimeConfigStore.Validate(snapshot.Time);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var safeName = Sanitize(request.Name);
        var jobId = $"{safeName}_{request.Seed}_{stamp}";
        var directory = Path.Combine(_recordsRoot, jobId);
        Directory.CreateDirectory(directory);
        var manifestPath = Path.Combine(directory, "manifest.json");

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        _paused = false;
        _pauseGate.Set();
        SetStatus(new RenderJobStatus(
            jobId, "starting", 0, 0, 0, 0, 0, 1,
            directory, null, null, ManifestPath: manifestPath));
        var effectiveRequest = request with { Name = safeName };
        var cancellation = _cancellation;
        _simulationThread = new Thread(() => RunPipeline(jobId, snapshot, effectiveRequest, directory, cancellation))
        {
            IsBackground = true,
            Name = $"WBall Simulation {jobId}",
        };
        _simulationThread.Start();
        return Status;
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (!_status.Active)
                return;
            _paused = true;
            _pauseGate.Reset();
            _status = _status with { Stage = "paused" };
        }
        StatusChanged?.Invoke();
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (!_status.Active)
                return;
            _paused = false;
            _status = _status with { Stage = "rendering" };
            _pauseGate.Set();
        }
        StatusChanged?.Invoke();
    }

    public void Cancel()
    {
        _cancellation?.Cancel();
        _pauseGate.Set();
    }

    public IReadOnlyList<string> List(int limit = 20) => Directory.EnumerateDirectories(_recordsRoot)
        .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
        .Take(Math.Clamp(limit, 1, 200))
        .Select(Path.GetFileName)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(x => x!)
        .ToList();

    public void Dispose()
    {
        Cancel();
        if (_simulationThread?.Join(TimeSpan.FromSeconds(2)) != false)
            _pauseGate.Dispose();
        _cancellation?.Dispose();
    }

    private void RunPipeline(
        string jobId,
        RenderInputSnapshot input,
        RenderJobRequest request,
        string directory,
        CancellationTokenSource cancellationSource)
    {
        var cancellation = cancellationSource.Token;
        var channel = Channel.CreateBounded<RenderFrameData>(new BoundedChannelOptions(input.Time.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });
        var producerResult = new TaskCompletionSource<RenderProducerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rendererDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renderThread = new Thread(() =>
        {
            try
            {
                ConsumeFrames(jobId, input, request, directory, channel.Reader, producerResult.Task, cancellationSource);
                rendererDone.TrySetResult();
            }
            catch (Exception ex)
            {
                rendererDone.TrySetException(ex);
                cancellationSource.Cancel();
            }
        })
        {
            IsBackground = true,
            Name = $"WBall Renderer {jobId}",
        };
        renderThread.SetApartmentState(ApartmentState.STA);
        renderThread.Start();

        try
        {
            UpdateStatus(jobId, status => status with { Stage = _paused ? "paused" : "simulating" });
            var economy = new SceneWorld { Defaults = input.Defaults };
            SceneStore.Apply(economy, input.Scene);
            var battleConfig = BattleConfigStore.CreateMemory(input.Turrets, input.Arena, _log);
            var balance = BalanceConfigStore.CreateMemory(input.Balance, _log);
            var weapons = WeaponCatalog.CreateMemory(input.Weapons, _log);
            var bridge = new EconomyBridge(weapons, _log, balance, battleConfig);
            economy.Settlements = bridge;
            var battleWorld = new SceneWorld
            {
                Defaults = input.Defaults,
                GravityG = 0,
                BallCollisionEnabled = input.Arena.BallCollision,
                Seed = request.Seed,
                WallRestitution = input.Balance.WallRestitution,
                BallRestitution = input.Balance.BallRestitution,
            };
            var battle = new BattleRuntime(economy, battleWorld, battleConfig, weapons, _log, balance);
            var stage = input.Stage.CreateState(input.Time.Width, input.Time.Height);
            var director = new BattleDirector(economy, battleWorld, battle, weapons, bridge, stage, _log, balance);
            director.Start(request.Seed, countdownSeconds: 0);
            var timeline = new TimelineClock(input.Time, input.Time.RenderAutoSlow);
            var maxFrames = (long)SafetyVideoHours * 60 * 60 * input.Time.Fps;
            var previousTerritoryVersion = -1;
            long nextFrame = 0;
            string? winnerId = null;
            string? winnerName = null;
            double winnerTime = 0;
            long animationStart = 0;

            while (nextFrame < maxFrames)
            {
                cancellation.ThrowIfCancellationRequested();
                _pauseGate.Wait(cancellation);
                var ballCount = economy.Balls.Count + battleWorld.Balls.Count;
                var steps = timeline.AdvanceOutputFrame(input.Time.Fps, ballCount);
                director.AdvanceSteps(steps);
                var data = ProjectFrame(
                    nextFrame, input.Time.Fps, timeline, economy, battleWorld, battle, director,
                    ref previousTerritoryVersion, null);
                channel.Writer.WriteAsync(data, cancellation).AsTask().GetAwaiter().GetResult();
                nextFrame++;

                if (battle.WinnerId == null)
                    continue;
                if (battle.WinnerId.Equals("draw", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("战局没有产生唯一胜者");
                var winner = battle.Turrets.FirstOrDefault(x =>
                    x.Id.Equals(battle.WinnerId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("胜者不存在于冻结阵营表");
                winnerId = winner.Id;
                winnerName = winner.Name;
                winnerTime = timeline.SimulationTime;
                animationStart = nextFrame;
                var animationFrames = VictoryAnimationSeconds * input.Time.Fps;
                for (var animationFrame = 0; animationFrame < animationFrames; animationFrame++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    _pauseGate.Wait(cancellation);
                    var victory = new VictoryAnimationState(
                        winner.Id,
                        winner.Name,
                        winner.Color,
                        animationFrame,
                        animationFrames,
                        (animationFrame + 1d) / animationFrames);
                    var victoryData = ProjectFrame(
                        nextFrame, input.Time.Fps, timeline, economy, battleWorld, battle, director,
                        ref previousTerritoryVersion, victory);
                    channel.Writer.WriteAsync(victoryData, cancellation).AsTask().GetAwaiter().GetResult();
                    nextFrame++;
                }
                break;
            }

            if (winnerId == null)
                throw new InvalidOperationException("战局未收敛");

            producerResult.TrySetResult(new RenderProducerResult(
                director.DeterministicHash(), timeline.StepCredit,
                battle.ValueLedger, battle.FriendlyAssistStatus(), battle.RemainingCombatValues(),
                battle.EliminationTimes.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
                winnerId, winnerName!, winnerTime, animationStart, nextFrame));
            channel.Writer.TryComplete();
        }
        catch (OperationCanceledException ex)
        {
            producerResult.TrySetCanceled(cancellation);
            channel.Writer.TryComplete(ex);
        }
        catch (Exception ex)
        {
            producerResult.TrySetException(ex);
            channel.Writer.TryComplete(ex);
        }
        finally
        {
            try { rendererDone.Task.GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                if (Status.JobId == jobId && Status.Active)
                {
                    UpdateStatus(jobId, status => status with
                    {
                        Stage = "failed",
                        Error = ex.GetBaseException().Message,
                    });
                    _log.Error("render", $"出片流水线失败: {ex}");
                }
            }
        }
    }

    private void ConsumeFrames(
        string jobId,
        RenderInputSnapshot input,
        RenderJobRequest request,
        string directory,
        ChannelReader<RenderFrameData> reader,
        Task<RenderProducerResult> producerResult,
        CancellationTokenSource cancellationSource)
    {
        var cancellation = cancellationSource.Token;
        var watch = Stopwatch.StartNew();
        var manifestPath = Path.Combine(directory, "manifest.json");
        var partialMp4 = Path.Combine(directory, "output.partial.mp4");
        var finalMp4 = Path.Combine(directory, "output.mp4");
        var manifest = CreateManifest(jobId, input, request);
        FfmpegEncoder? writer = null;

        try
        {
            SafeDelete(partialMp4);
            var identity = FfmpegEncoder.Identify(_ffmpegPath);
            manifest.FfmpegVersion = identity.Version;
            manifest.FfmpegSha256 = identity.Sha256;
            manifest.Status = "running";
            manifest.StartedAt = DateTimeOffset.Now;
            WriteManifest(manifestPath, manifest);

            var renderer = new StageFrameRenderer(CreateStaticData(input), input.Time.Width, input.Time.Height);
            writer = FfmpegEncoder.Open(_ffmpegPath, partialMp4, input.Time.Fps, input.Time.Width, input.Time.Height);
            UpdateStatus(jobId, status => status with
            {
                Stage = _paused ? "paused" : "rendering",
                ManifestPath = manifestPath,
            });

            while (reader.WaitToReadAsync(cancellation).AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out var frame))
                {
                    cancellation.ThrowIfCancellationRequested();
                    _pauseGate.Wait(cancellation);
                    var pixels = renderer.Render(frame);
                    manifest.PeakBgraFrames = Math.Max(manifest.PeakBgraFrames, 1);
                    writer.WriteFrame(pixels);
                    UpdateManifestFrame(manifest, frame, pixels, input.Time.Fps, watch.Elapsed.TotalSeconds);
                    var queueDepth = reader.CanCount ? reader.Count : 0;
                    manifest.PeakQueueDepth = Math.Max(manifest.PeakQueueDepth, queueDepth);
                    manifest.PeakWorkingSetBytes = Math.Max(
                        manifest.PeakWorkingSetBytes, Process.GetCurrentProcess().WorkingSet64);
                    var generatedFps = manifest.Frames / Math.Max(0.001, watch.Elapsed.TotalSeconds);
                    UpdateStatus(jobId, status => status with
                    {
                        Stage = _paused ? "paused" : frame.Victory == null ? "rendering" : "animating",
                        Frame = manifest.Frames,
                        VideoTime = manifest.VideoTime,
                        SimulationTime = manifest.SimulationTime,
                        WallElapsed = watch.Elapsed.TotalSeconds,
                        BallCount = frame.BallCount,
                        SimulationScale = frame.SimulationScale,
                        GeneratedFps = generatedFps,
                        WorkingSetBytes = Process.GetCurrentProcess().WorkingSet64,
                        QueueDepth = queueDepth,
                        PeakQueueDepth = manifest.PeakQueueDepth,
                        ManifestPath = manifestPath,
                    });
                    if (manifest.Frames % Math.Max(1, input.Time.Fps * 10) == 1)
                        WriteManifest(manifestPath, manifest);
                }
            }

            UpdateStatus(jobId, status => status with { Stage = "finalizing" });
            var result = producerResult.GetAwaiter().GetResult();
            cancellation.ThrowIfCancellationRequested();
            writer.CompleteAndValidate(partialMp4);
            cancellation.ThrowIfCancellationRequested();
            writer.Dispose();
            writer = null;
            File.Move(partialMp4, finalMp4, overwrite: false);

            manifest.Status = "completed";
            manifest.FinishedAt = DateTimeOffset.Now;
            manifest.WallElapsed = watch.Elapsed.TotalSeconds;
            manifest.GeneratedFps = manifest.Frames / Math.Max(0.001, manifest.WallElapsed);
            manifest.FinalDirectorHash = result.FinalHash;
            manifest.StepCredit = result.StepCredit;
            manifest.ValueLedger = result.ValueLedger;
            manifest.FinalAssist = result.FinalAssist;
            manifest.FinalCombatValues = result.FinalCombatValues;
            manifest.WinnerId = result.WinnerId;
            manifest.WinnerName = result.WinnerName;
            manifest.WinnerLockedAtSimulationTime = result.WinnerLockedAtSimulationTime;
            manifest.EliminationSimulationTimes = result.EliminationSimulationTimes;
            manifest.VictoryAnimationStartFrame = result.VictoryAnimationStartFrame;
            manifest.VictoryAnimationEndFrameExclusive = result.VictoryAnimationEndFrameExclusive;
            manifest.Mp4Path = finalMp4;
            manifest.OutputBytes = new FileInfo(finalMp4).Length;
            WriteManifest(manifestPath, manifest);
            UpdateStatus(jobId, status => status with
            {
                Stage = "completed",
                WallElapsed = manifest.WallElapsed,
                GeneratedFps = manifest.GeneratedFps,
                Mp4Path = finalMp4,
                Error = null,
                ManifestPath = manifestPath,
                FinalHash = result.FinalHash,
            });
        }
        catch (OperationCanceledException)
        {
            writer?.Dispose();
            writer = null;
            SafeDelete(partialMp4);
            manifest.Status = "canceled";
            manifest.FailureReason = "任务已取消";
            manifest.FinishedAt = DateTimeOffset.Now;
            manifest.WallElapsed = watch.Elapsed.TotalSeconds;
            WriteManifest(manifestPath, manifest);
            UpdateStatus(jobId, status => status with
            {
                Stage = "canceled",
                WallElapsed = manifest.WallElapsed,
                Error = manifest.FailureReason,
                ManifestPath = manifestPath,
            });
        }
        catch (Exception ex)
        {
            writer?.Dispose();
            writer = null;
            SafeDelete(partialMp4);
            manifest.Status = "failed";
            manifest.FailureReason = ex.GetBaseException().Message;
            manifest.FinishedAt = DateTimeOffset.Now;
            manifest.WallElapsed = watch.Elapsed.TotalSeconds;
            WriteManifest(manifestPath, manifest);
            UpdateStatus(jobId, status => status with
            {
                Stage = "failed",
                WallElapsed = manifest.WallElapsed,
                Error = manifest.FailureReason,
                ManifestPath = manifestPath,
            });
            cancellationSource.Cancel();
            _log.Error("render", $"出片任务失败: {ex}");
        }
        finally
        {
            writer?.Dispose();
            if (!string.Equals(manifest.Status, "completed", StringComparison.Ordinal))
                SafeDelete(partialMp4);
        }
    }

    private RenderJobManifest CreateManifest(
        string jobId,
        RenderInputSnapshot input,
        RenderJobRequest request) => new()
        {
            JobId = jobId,
            CreatedAt = DateTimeOffset.Now,
            Request = request,
            Config = input.Time,
            Width = input.Time.Width,
            Height = input.Time.Height,
            Fps = input.Time.Fps,
            SceneHash = HashObject(input.Scene),
            ArenaHash = HashObject(new { input.Turrets, input.Arena }),
            BalanceHash = HashObject(input.Balance),
            WeaponsHash = HashObject(input.Weapons),
            StageHash = HashObject(input.Stage),
        };

    private RenderInputSnapshot CaptureInput(string? scenarioName)
    {
        var defaults = CloneDefaults(_liveWorld.Defaults);
        RenderInputSnapshot snapshot;
        if (string.IsNullOrWhiteSpace(scenarioName))
        {
            snapshot = new RenderInputSnapshot(
                SceneStore.Capture(_liveWorld),
                defaults,
                _battleConfig.Turrets.Select(CloneTurret).ToArray(),
                PresetStore.CloneArena(_battleConfig.Arena),
                BalanceConfigStore.Clone(_balanceConfig.Current),
                WeaponCatalog.CloneDefinitions(_weapons.Weapons).ToArray(),
                CloneStage(_liveStage),
                RenderTimeConfigStore.Clone(_timeStore.Current));
        }
        else
        {
            var scenario = _scenarios.Load(scenarioName);
            var economy = new SceneWorld { Defaults = defaults };
            _scenarios.TryLoadEconomyScene(scenario, economy);
            snapshot = new RenderInputSnapshot(
                SceneStore.Capture(economy),
                defaults,
                scenario.Turrets.Select(CloneTurret).ToArray(),
                PresetStore.CloneArena(scenario.Arena),
                BalanceConfigStore.Clone(scenario.Balance ?? _balanceConfig.Current),
                WeaponCatalog.CloneDefinitions(
                    scenario.Weapons.Count > 0 ? scenario.Weapons : _weapons.Weapons).ToArray(),
                CloneStage(_liveStage),
                RenderTimeConfigStore.Clone(_timeStore.Current));
        }

        // 出片不允许按硬时限、领地或生命排名制造伪胜者。
        snapshot.Balance.HardTimeLimitSeconds = 0;
        return snapshot;
    }

    private static void ValidateInput(RenderInputSnapshot input)
    {
        var participants = input.Turrets
            .Where(x => !string.IsNullOrWhiteSpace(x.Id) && x.MaxHp > 0)
            .Select(x => x.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (participants < 2)
            throw new InvalidOperationException("出片至少需要两个有效对战阵营");
    }

    private static RenderStaticData CreateStaticData(RenderInputSnapshot input) => new(
        input.Scene,
        input.Arena.Width,
        input.Arena.Height,
        input.Arena.ShieldRingScale,
        input.Arena.ShieldCostPerValue,
        input.Arena.ShellLabelFontFactor,
        input.Arena.ShellLabelFontMin,
        input.Arena.ShellLabelFontMax,
        input.Arena.ShellLabelOutsideOpacity,
        input.Stage);

    private static RenderFrameData ProjectFrame(
        long frame,
        int fps,
        TimelineClock timeline,
        SceneWorld economy,
        SceneWorld battleWorld,
        BattleRuntime battle,
        BattleDirector director,
        ref int previousTerritoryVersion,
        VictoryAnimationState? victory)
    {
        ImmutableArray<int>? territory = null;
        if (battle.TerritoryVersion != previousTerritoryVersion)
        {
            territory = battle.TerritoryOwners.ToImmutableArray();
            previousTerritoryVersion = battle.TerritoryVersion;
        }
        var economyBalls = economy.Balls.Select(x =>
            new RenderBallData(x.Id, x.X, x.Y, x.Size, x.Color, x.Multiplier)).ToImmutableArray();
        var projectiles = battleWorld.Balls.Where(x => x.Projectile != null).Select(x =>
            new RenderProjectileData(
                x.Id, x.X, x.Y, x.Size, x.Color,
                x.Projectile!.OwnerFactionId, x.Projectile.Role, x.Projectile.CapturesLeft,
                x.Projectile.IsPromotedSmall)).ToImmutableArray();
        var turrets = battle.Turrets.Select(x => new RenderTurretData(
            x.Id, x.Name, x.Color, x.TurretX, x.TurretY, x.TurretRadius, x.BarrelAngleDeg,
            x.Hp, x.MaxHp, x.Shield, x.MaxShield, x.Alive)).ToImmutableArray();
        var assists = battle.AssistVisuals.Select(x => new RenderAssistData(
            x.FromX, x.FromY, x.ToX, x.ToY, x.Color, x.Amount, x.RemainingSeconds)).ToImmutableArray();
        return new RenderFrameData(
            frame,
            frame / (double)fps,
            timeline.SimulationTime,
            timeline.StepCredit,
            economyBalls.Length + projectiles.Length,
            timeline.CurrentScale,
            director.State.ToString(),
            battle.WinnerId,
            economyBalls,
            projectiles,
            turrets,
            assists,
            battle.TerritoryCols,
            battle.TerritoryRows,
            battle.TerritoryVersion,
            territory,
            battle.TerritoryFactionIds.ToImmutableArray(),
            victory);
    }

    private static void UpdateManifestFrame(
        RenderJobManifest manifest,
        RenderFrameData frame,
        byte[] pixels,
        int fps,
        double wallElapsed)
    {
        if (manifest.ScaleSegments.Count == 0
            || Math.Abs(manifest.ScaleSegments[^1].Scale - frame.SimulationScale) > 1e-12)
        {
            if (manifest.ScaleSegments.Count > 0)
                manifest.ScaleSegments[^1].EndFrame = frame.FrameIndex;
            manifest.ScaleSegments.Add(new RenderScaleSegment
            {
                StartFrame = frame.FrameIndex,
                EndFrame = frame.FrameIndex + 1,
                StartVideoTime = frame.OutputTime,
                StartSimulationTime = frame.SimulationTime,
                BallCount = frame.BallCount,
                Scale = frame.SimulationScale,
            });
        }
        else
        {
            manifest.ScaleSegments[^1].EndFrame = frame.FrameIndex + 1;
        }
        if (frame.FrameIndex % Math.Max(1, fps * 10) == 0)
            manifest.SampleFrameHashes.Add(Convert.ToHexString(SHA256.HashData(pixels)));
        manifest.Frames = frame.FrameIndex + 1;
        manifest.VideoTime = manifest.Frames / (double)fps;
        manifest.SimulationTime = frame.SimulationTime;
        manifest.StepCredit = frame.StepCredit;
        manifest.WallElapsed = wallElapsed;
    }

    private void SetStatus(RenderJobStatus status)
    {
        lock (_sync)
            _status = status;
        StatusChanged?.Invoke();
    }

    private bool UpdateStatus(string jobId, Func<RenderJobStatus, RenderJobStatus> update)
    {
        lock (_sync)
        {
            if (!string.Equals(_status.JobId, jobId, StringComparison.Ordinal))
                return false;
            var next = update(_status);
            if (!_status.Active && next.Active)
                return false;
            _status = next;
        }
        StatusChanged?.Invoke();
        return true;
    }

    private static void WriteManifest(string path, RenderJobManifest manifest)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(manifest, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private static string HashObject(object value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions))));

    private static PublicDefaults CloneDefaults(PublicDefaults value) => new()
    {
        SizeBase = value.SizeBase,
        SizeScale = value.SizeScale,
        WeightBase = value.WeightBase,
        WeightScale = value.WeightScale,
        InitialMultiplier = value.InitialMultiplier,
    };

    private static TurretDefinition CloneTurret(TurretDefinition value) => new()
    {
        Id = value.Id,
        Name = value.Name,
        Color = value.Color,
        Quadrant = value.Quadrant,
        InitialBalls = value.InitialBalls,
        InitialMultiplier = value.InitialMultiplier,
        MaxHp = value.MaxHp,
        MaxShield = value.MaxShield,
        InitialShield = value.InitialShield,
        ProjectileSize = value.ProjectileSize,
        ProjectileCount = value.ProjectileCount,
        FireIntervalSec = value.FireIntervalSec,
        BarrelRpm = value.BarrelRpm,
    };

    private static RenderStageLayout CloneStage(StageState value) => new(
        value.Orientation, value.CompositeVisible, value.HudVisible, value.Background, value.Split);

    private static string Sanitize(string name)
    {
        var value = string.IsNullOrWhiteSpace(name) ? "battle" : name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return value;
    }

    private sealed record RenderInputSnapshot(
        SceneSnapshot Scene,
        PublicDefaults Defaults,
        IReadOnlyList<TurretDefinition> Turrets,
        ArenaLayoutConfig Arena,
        BalanceConfig Balance,
        IReadOnlyList<WeaponDefinition> Weapons,
        RenderStageLayout Stage,
        RenderTimeConfig Time);

    private sealed record RenderProducerResult(
        string FinalHash,
        double StepCredit,
        ProjectileValueLedger ValueLedger,
        FriendlyAssistSnapshot FinalAssist,
        IReadOnlyList<FactionCombatValue> FinalCombatValues,
        IReadOnlyDictionary<string, double> EliminationSimulationTimes,
        string WinnerId,
        string WinnerName,
        double WinnerLockedAtSimulationTime,
        long VictoryAnimationStartFrame,
        long VictoryAnimationEndFrameExclusive);
}

internal static class RenderStageLayoutExtensions
{
    public static StageState CreateState(this RenderStageLayout layout, int width, int height)
    {
        var state = new StageState();
        state.SetCompositeVisible(layout.CompositeVisible);
        state.Configure(layout.Split, layout.Orientation, layout.HudVisible, layout.Background, width, height);
        state.SetMode(StageMode.Record);
        return state;
    }
}
