using System.Diagnostics;
using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AppShell.Core.Logging;
using WBall.Battle;
using WBall.Game;
using WBall.Model;
using WBall.Stage;

namespace WBall.Recording;

public enum RenderEndMode
{
    Output,
    Simulation,
    Winner,
}

public sealed record RenderJobRequest(
    RenderEndMode Mode,
    double Seconds,
    int Seed,
    string Name,
    int? MaxOutputSeconds = null,
    string? Scenario = null);

public sealed record RenderJobStatus(
    string JobId,
    string Stage,
    long Frame,
    long TotalFrames,
    double OutputTime,
    double SimulationTime,
    double WallElapsed,
    int BallCount,
    double SimulationScale,
    string? OutputDirectory,
    string? Mp4Path,
    string? Error,
    double GeneratedFps = 0,
    double EtaSeconds = 0,
    long WorkingSetBytes = 0,
    int QueueDepth = 0,
    int PeakQueueDepth = 0,
    string? ManifestPath = null,
    string? FinalHash = null,
    string? PngDirectory = null)
{
    public bool Active => Stage is "starting" or "simulating" or "rendering" or "paused" or "finalizing";
}

public sealed class RenderJobManifest
{
    public string AppVersion { get; set; } = "3.3.0";
    public string JobId { get; set; } = "";
    public string Status { get; set; } = "starting";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public RenderJobRequest Request { get; set; } = new(RenderEndMode.Output, 5, 42, "battle");
    public RenderTimeConfig Config { get; set; } = new();
    public string SceneHash { get; set; } = "";
    public string ArenaHash { get; set; } = "";
    public string BalanceHash { get; set; } = "";
    public string WeaponsHash { get; set; } = "";
    public string StageHash { get; set; } = "";
    public long Frames { get; set; }
    public double OutputTime { get; set; }
    public double SimulationTime { get; set; }
    public double WallElapsed { get; set; }
    public double StepCredit { get; set; }
    public double GeneratedFps { get; set; }
    public long PeakWorkingSetBytes { get; set; }
    public int PeakQueueDepth { get; set; }
    public int PeakBgraFrames { get; set; }
    public bool Truncated { get; set; }
    public ProjectileValueLedger? ValueLedger { get; set; }
    public FriendlyAssistSnapshot? FinalAssist { get; set; }
    public int PeakPromotedSmallShots { get; set; }
    public int FinalPromotedSmallShots { get; set; }
    public string? FinalDirectorHash { get; set; }
    public string? Mp4Path { get; set; }
    public long OutputBytes { get; set; }
    public string? PngDirectory { get; set; }
    public string? Error { get; set; }
    public List<string> SampleFrameHashes { get; set; } = [];
    public List<RenderScaleSegment> ScaleSegments { get; set; } = [];
}

public sealed class RenderScaleSegment
{
    public long StartFrame { get; set; }
    public long EndFrame { get; set; }
    public double StartOutputTime { get; set; }
    public double StartSimulationTime { get; set; }
    public int BallCount { get; set; }
    public double Scale { get; set; }
}

/// <summary>冻结输入后，以模拟生产者 + 有界帧队列 + STA 合成消费者离线出片。</summary>
public sealed class RenderJobService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
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
    private readonly IShellLog _log;
    private readonly ManualResetEventSlim _pauseGate = new(initialState: true);
    private CancellationTokenSource? _cancellation;
    private Thread? _simulationThread;
    private bool _paused;
    private RenderJobStatus _status = new("-", "idle", 0, 0, 0, 0, 0, 0, 1, null, null, null);

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
        IShellLog log)
    {
        _liveWorld = liveWorld;
        _battleConfig = battleConfig;
        _balanceConfig = balanceConfig;
        _weapons = weapons;
        _liveStage = liveStage;
        _scenarios = scenarios;
        _timeStore = timeStore;
        _recordsRoot = System.IO.Path.Combine(workspaceRoot, "records");
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

    public RenderJobStatus Start(RenderJobRequest request)
    {
        if (!double.IsFinite(request.Seconds) || request.Seconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.Seconds));
        lock (_sync)
        {
            if (_status.Active)
                throw new InvalidOperationException("已有出片任务在运行");
        }

        var snapshot = CaptureInput(request.Scenario);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var safeName = Sanitize(request.Name);
        var jobId = $"{safeName}_{request.Seed}_{stamp}";
        var directory = System.IO.Path.Combine(_recordsRoot, jobId);
        Directory.CreateDirectory(directory);
        var maxSeconds = Math.Clamp(request.MaxOutputSeconds ?? snapshot.Time.MaxOutputSeconds, 1, 86_400);
        var totalFrames = request.Mode == RenderEndMode.Output
            ? Math.Min((long)maxSeconds * snapshot.Time.Fps,
                (long)Math.Ceiling(request.Seconds * snapshot.Time.Fps))
            : (long)maxSeconds * snapshot.Time.Fps;
        var manifestPath = System.IO.Path.Combine(directory, "manifest.json");

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        _paused = false;
        _pauseGate.Set();
        SetStatus(new RenderJobStatus(
            jobId, "starting", 0, totalFrames, 0, 0, 0, 0, 1,
            directory, null, null, ManifestPath: manifestPath,
            PngDirectory: System.IO.Path.Combine(directory, "frames")));
        var effectiveRequest = request with { Name = safeName, MaxOutputSeconds = maxSeconds };
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
        .OrderByDescending(System.IO.Path.GetFileName, StringComparer.OrdinalIgnoreCase)
        .Take(Math.Clamp(limit, 1, 200))
        .Select(System.IO.Path.GetFileName)
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
            var bridge = new EconomyBridge(weapons, _log, balance);
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
            var maxFrames = (long)(request.MaxOutputSeconds ?? input.Time.MaxOutputSeconds) * input.Time.Fps;
            var outputFrames = request.Mode == RenderEndMode.Output
                ? Math.Min(maxFrames, (long)Math.Ceiling(request.Seconds * input.Time.Fps))
                : maxFrames;
            var previousTerritoryVersion = -1;
            var truncated = false;

            for (long frame = 0; frame < outputFrames; frame++)
            {
                cancellation.ThrowIfCancellationRequested();
                _pauseGate.Wait(cancellation);
                var ballCount = economy.Balls.Count + battleWorld.Balls.Count;
                var steps = timeline.AdvanceOutputFrame(input.Time.Fps, ballCount);
                director.AdvanceSteps(steps);
                var data = ProjectFrame(
                    frame, input.Time.Fps, timeline, economy, battleWorld, battle, director,
                    ref previousTerritoryVersion);
                channel.Writer.WriteAsync(data, cancellation).AsTask().GetAwaiter().GetResult();

                if (request.Mode == RenderEndMode.Simulation && timeline.SimulationTime + 1e-12 >= request.Seconds)
                    break;
                if (request.Mode == RenderEndMode.Winner && director.State == DirectorState.Ended)
                    break;
                if (frame + 1 == outputFrames)
                {
                    truncated = request.Mode switch
                    {
                        RenderEndMode.Output => (frame + 1) / (double)input.Time.Fps + 1e-12 < request.Seconds,
                        RenderEndMode.Simulation => timeline.SimulationTime + 1e-12 < request.Seconds,
                        RenderEndMode.Winner => director.State != DirectorState.Ended,
                        _ => false,
                    };
                }
            }

            producerResult.TrySetResult(new RenderProducerResult(
                director.DeterministicHash(), timeline.StepCredit, truncated,
                battle.ValueLedger, battle.FriendlyAssistStatus()));
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
        var manifestPath = System.IO.Path.Combine(directory, "manifest.json");
        var pngDirectory = System.IO.Path.Combine(directory, "frames");
        var partialMp4 = System.IO.Path.Combine(directory, "output.partial.mp4");
        var finalMp4 = System.IO.Path.Combine(directory, "output.mp4");
        var manifest = CreateManifest(jobId, input, request, directory);
        MediaFoundationEncoder.FrameWriter? writer = null;

        try
        {
            Directory.CreateDirectory(pngDirectory);
            var renderer = new StageFrameRenderer(CreateStaticData(input), input.Time.Width, input.Time.Height);
            if (input.Time.PreferMp4)
            {
                try { writer = MediaFoundationEncoder.Open(partialMp4, input.Time.Fps, input.Time.Width, input.Time.Height, _log); }
                catch (Exception ex) { manifest.Error = $"MP4 初始化失败,继续输出 PNG: {ex.Message}"; }
            }

            manifest.Status = "running";
            manifest.StartedAt = DateTimeOffset.Now;
            WriteManifest(manifestPath, manifest);
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
                    SavePng(pixels, input.Time.Width, input.Time.Height,
                        System.IO.Path.Combine(pngDirectory, $"frame_{frame.FrameIndex:D6}.png"));
                    if (writer != null)
                    {
                        try { writer.WriteFrame(pixels); }
                        catch (Exception ex)
                        {
                            writer.Dispose();
                            writer = null;
                            manifest.Error = $"MP4 写入失败,保留完整 PNG: {ex.Message}";
                        }
                    }

                    UpdateManifestFrame(manifest, frame, pixels, input.Time.Fps, watch.Elapsed.TotalSeconds);
                    var queueDepth = reader.CanCount ? reader.Count : 0;
                    manifest.PeakQueueDepth = Math.Max(manifest.PeakQueueDepth, queueDepth);
                    manifest.PeakWorkingSetBytes = Math.Max(
                        manifest.PeakWorkingSetBytes, Process.GetCurrentProcess().WorkingSet64);
                    var generatedFps = manifest.Frames / Math.Max(0.001, watch.Elapsed.TotalSeconds);
                    var remaining = Math.Max(0, Status.TotalFrames - manifest.Frames);
                    var eta = generatedFps > 0 ? remaining / generatedFps : 0;
                    UpdateStatus(jobId, status => status with
                    {
                        Stage = _paused ? "paused" : "rendering",
                        Frame = manifest.Frames,
                        OutputTime = manifest.OutputTime,
                        SimulationTime = manifest.SimulationTime,
                        WallElapsed = watch.Elapsed.TotalSeconds,
                        BallCount = frame.BallCount,
                        SimulationScale = frame.SimulationScale,
                        GeneratedFps = generatedFps,
                        EtaSeconds = eta,
                        WorkingSetBytes = Process.GetCurrentProcess().WorkingSet64,
                        QueueDepth = queueDepth,
                        PeakQueueDepth = manifest.PeakQueueDepth,
                        ManifestPath = manifestPath,
                    });
                    if (manifest.Frames % 30 == 1)
                        WriteManifest(manifestPath, manifest);
                }
            }

            UpdateStatus(jobId, status => status with { Stage = "finalizing" });
            var result = producerResult.GetAwaiter().GetResult();
            if (writer != null)
            {
                writer.Complete();
                writer.Dispose();
                writer = null;
                File.Move(partialMp4, finalMp4, overwrite: true);
                manifest.Mp4Path = finalMp4;
                manifest.OutputBytes = new FileInfo(finalMp4).Length;
                if (!input.Time.KeepPng)
                {
                    Directory.Delete(pngDirectory, recursive: true);
                    manifest.PngDirectory = null;
                }
            }
            if (manifest.Mp4Path == null && Directory.Exists(pngDirectory))
                manifest.OutputBytes = Directory.EnumerateFiles(pngDirectory, "*.png").Sum(x => new FileInfo(x).Length);
            manifest.Status = "completed";
            manifest.FinishedAt = DateTimeOffset.Now;
            manifest.WallElapsed = watch.Elapsed.TotalSeconds;
            manifest.GeneratedFps = manifest.Frames / Math.Max(0.001, manifest.WallElapsed);
            manifest.FinalDirectorHash = result.FinalHash;
            manifest.StepCredit = result.StepCredit;
            manifest.Truncated = result.Truncated;
            manifest.ValueLedger = result.ValueLedger;
            manifest.FinalAssist = result.FinalAssist;
            WriteManifest(manifestPath, manifest);
            UpdateStatus(jobId, status => status with
            {
                Stage = "completed",
                WallElapsed = manifest.WallElapsed,
                GeneratedFps = manifest.GeneratedFps,
                EtaSeconds = 0,
                Mp4Path = manifest.Mp4Path,
                Error = manifest.Error,
                ManifestPath = manifestPath,
                FinalHash = result.FinalHash,
                PngDirectory = manifest.PngDirectory,
            });
        }
        catch (OperationCanceledException)
        {
            manifest.Status = "canceled";
            manifest.FinishedAt = DateTimeOffset.Now;
            manifest.WallElapsed = watch.Elapsed.TotalSeconds;
            WriteManifest(manifestPath, manifest);
            UpdateStatus(jobId, status => status with
            {
                Stage = "canceled",
                WallElapsed = manifest.WallElapsed,
                ManifestPath = manifestPath,
            });
        }
        catch (Exception ex)
        {
            manifest.Status = "failed";
            manifest.Error = ex.GetBaseException().ToString();
            manifest.FinishedAt = DateTimeOffset.Now;
            manifest.WallElapsed = watch.Elapsed.TotalSeconds;
            WriteManifest(manifestPath, manifest);
            UpdateStatus(jobId, status => status with
            {
                Stage = "failed",
                WallElapsed = manifest.WallElapsed,
                Error = ex.GetBaseException().Message,
                ManifestPath = manifestPath,
            });
            cancellationSource.Cancel();
            _log.Error("render", $"出片任务失败: {ex}");
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private RenderJobManifest CreateManifest(
        string jobId,
        RenderInputSnapshot input,
        RenderJobRequest request,
        string directory) => new()
        {
            JobId = jobId,
            CreatedAt = DateTimeOffset.Now,
            Request = request,
            Config = input.Time,
            SceneHash = HashObject(input.Scene),
            ArenaHash = HashObject(new { input.Turrets, input.Arena }),
            BalanceHash = HashObject(input.Balance),
            WeaponsHash = HashObject(input.Weapons),
            StageHash = HashObject(input.Stage),
            PngDirectory = System.IO.Path.Combine(directory, "frames"),
        };

    private RenderInputSnapshot CaptureInput(string? scenarioName)
    {
        var defaults = CloneDefaults(_liveWorld.Defaults);
        if (string.IsNullOrWhiteSpace(scenarioName))
        {
            return new RenderInputSnapshot(
                SceneStore.Capture(_liveWorld),
                defaults,
                _battleConfig.Turrets.Select(CloneTurret).ToArray(),
                PresetStore.CloneArena(_battleConfig.Arena),
                BalanceConfigStore.Clone(_balanceConfig.Current),
                WeaponCatalog.CloneDefinitions(_weapons.Weapons).ToArray(),
                CloneStage(_liveStage),
                RenderTimeConfigStore.Clone(_timeStore.Current));
        }

        var scenario = _scenarios.Load(scenarioName);
        var economy = new SceneWorld { Defaults = defaults };
        _scenarios.TryLoadEconomyScene(scenario, economy);
        return new RenderInputSnapshot(
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

    private static RenderStaticData CreateStaticData(RenderInputSnapshot input) => new(
        input.Scene,
        input.Arena.Width,
        input.Arena.Height,
        input.Arena.ShieldRingScale,
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
        ref int previousTerritoryVersion)
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
            battle.TerritoryFactionIds.ToImmutableArray());
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
                StartOutputTime = frame.OutputTime,
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
        manifest.OutputTime = manifest.Frames / (double)fps;
        manifest.SimulationTime = frame.SimulationTime;
        manifest.StepCredit = frame.StepCredit;
        manifest.WallElapsed = wallElapsed;
        manifest.FinalPromotedSmallShots = frame.Projectiles.Count(x => x.IsPromotedSmall);
        manifest.PeakPromotedSmallShots = Math.Max(
            manifest.PeakPromotedSmallShots, manifest.FinalPromotedSmallShots);
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

    private static void SavePng(byte[] pixels, int width, int height, string path)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void WriteManifest(string path, RenderJobManifest manifest)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(manifest, JsonOptions));
        File.Move(temp, path, overwrite: true);
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
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
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
        bool Truncated,
        ProjectileValueLedger ValueLedger,
        FriendlyAssistSnapshot FinalAssist);
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
