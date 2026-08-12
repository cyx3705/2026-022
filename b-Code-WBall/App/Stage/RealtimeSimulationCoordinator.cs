using System.Diagnostics;
using AppShell.Core.Commands;
using WBall.Battle;
using WBall.Model;
using WBall.Recording;

namespace WBall.Stage;

/// <summary>
/// 现场固定步的单线程所有者。UI 命令通过同一闸口在固定步之间串行提交，
/// 出片服务不引用本协调器。
/// </summary>
public sealed class RealtimeSimulationCoordinator : IDisposable
{
    private readonly StageState _stage;
    private readonly SceneWorld _economyWorld;
    private readonly SceneWorld _battleWorld;
    private readonly BattleDirector _director;
    private readonly CancellationTokenSource _stop = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private RenderTimeConfig? _pendingConfig;
    private TimelineClock _timeline;
    private bool _disposed;

    public RealtimeSimulationCoordinator(
        StageState stage,
        SceneWorld economyWorld,
        SceneWorld battleWorld,
        BattleDirector director,
        RenderTimeConfig config)
    {
        _stage = stage;
        _economyWorld = economyWorld;
        _battleWorld = battleWorld;
        _director = director;
        _timeline = new TimelineClock(config, config.PreviewAutoSlow);
        Gate = new SemaphoreSlim(1, 1);
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "WBall.RealtimeSimulation",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>模拟、业务命令与迁移期视图读取共用的边界闸口。</summary>
    public SemaphoreSlim Gate { get; }
    public event Action? Disposed;

    public TimelineClock Timeline => _timeline;

    public void ApplyTimeConfig(RenderTimeConfig config)
    {
        Interlocked.Exchange(ref _pendingConfig, config.Clone());
        _wake.Set();
    }

    public async Task<CommandResult> ExecuteCommandAsync(
        Func<Task<CommandResult>> action,
        CancellationToken cancellation)
    {
        await Gate.WaitAsync(cancellation).ConfigureAwait(true);
        try
        {
            return await action().ConfigureAwait(true);
        }
        finally
        {
            Gate.Release();
            _wake.Set();
        }
    }

    /// <summary>将本次 ConfigureCommands 新增的 WBall 命令统一套上固定步边界。</summary>
    public void WrapNewCommands(CommandRegistry registry, IReadOnlySet<string> namesBeforeRegistration)
    {
        var descriptors = registry.All()
            .Where(descriptor => !namesBeforeRegistration.Contains(descriptor.Name))
            .ToArray();
        foreach (var descriptor in descriptors)
        {
            var source = registry.GetSource(descriptor.Name);
            registry.Unregister(descriptor.Name);
            registry.Register(CloneWithGate(descriptor), source);
        }
    }

    private CommandDescriptor CloneWithGate(CommandDescriptor descriptor) => new()
    {
        Name = descriptor.Name,
        Summary = descriptor.Summary,
        Example = descriptor.Example,
        Parameters = descriptor.Parameters,
        ConfirmPrompt = descriptor.ConfirmPrompt,
        SupportsUndo = descriptor.SupportsUndo,
        Dangerous = descriptor.Dangerous,
        Readonly = descriptor.Readonly,
        RequiresUiThread = descriptor.RequiresUiThread,
        ExecutionSite = descriptor.ExecutionSite,
        AllowMcpExecution = descriptor.AllowMcpExecution,
        AllowUnspecifiedParameters = descriptor.AllowUnspecifiedParameters,
        Handler = context => ExecuteCommandAsync(
            () => descriptor.Handler(context),
            context.Cancellation),
    };

    private void Run()
    {
        var stopwatch = Stopwatch.StartNew();
        var last = stopwatch.Elapsed.TotalSeconds;
        while (!_stop.IsCancellationRequested)
        {
            var pending = Interlocked.Exchange(ref _pendingConfig, null);
            if (pending != null)
                _timeline = new TimelineClock(pending, pending.PreviewAutoSlow);

            var now = stopwatch.Elapsed.TotalSeconds;
            var elapsed = Math.Min(0.1, Math.Max(0, now - last));
            last = now;
            if (_stage.Mode != StageMode.Play)
            {
                _timeline.Reset();
                _wake.WaitOne(10);
                continue;
            }

            var ballCount = 0;
            var entered = false;
            try
            {
                Gate.Wait(_stop.Token);
                entered = true;
                ballCount = _economyWorld.Balls.Count + _battleWorld.Balls.Count;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            finally
            {
                if (entered)
                    Gate.Release();
            }

            var steps = _timeline.AdvanceWallTime(elapsed, ballCount);
            if (steps == 0)
            {
                _wake.WaitOne(1);
                continue;
            }

            for (var index = 0; index < steps && !_stop.IsCancellationRequested; index++)
            {
                entered = false;
                try
                {
                    Gate.Wait(_stop.Token);
                    entered = true;
                    _director.AdvanceFixedStep();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                finally
                {
                    if (entered)
                        Gate.Release();
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stop.Cancel();
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _wake.Dispose();
        Gate.Dispose();
        _stop.Dispose();
        Disposed?.Invoke();
    }
}
