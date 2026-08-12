using System.Threading;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace WBall.Stage;

public enum VisualLodLevel
{
    Full,
    Simplified,
    Minimal,
}

/// <summary>只影响展示细节的确定性球数档位；不参与模拟和出片。</summary>
public sealed class VisualLodController
{
    public const int SimplifiedThreshold = 5_000;
    public const int MinimalThreshold = 10_000;
    public const int SimplifiedReturnThreshold = 4_500;
    public const int MinimalReturnThreshold = 9_000;

    public VisualLodLevel Current { get; private set; }

    public VisualLodLevel Update(int totalBallCount)
    {
        totalBallCount = Math.Max(0, totalBallCount);
        Current = Current switch
        {
            VisualLodLevel.Full when totalBallCount >= MinimalThreshold => VisualLodLevel.Minimal,
            VisualLodLevel.Full when totalBallCount >= SimplifiedThreshold => VisualLodLevel.Simplified,
            VisualLodLevel.Simplified when totalBallCount >= MinimalThreshold => VisualLodLevel.Minimal,
            VisualLodLevel.Simplified when totalBallCount < SimplifiedReturnThreshold => VisualLodLevel.Full,
            VisualLodLevel.Minimal when totalBallCount < SimplifiedReturnThreshold => VisualLodLevel.Full,
            VisualLodLevel.Minimal when totalBallCount < MinimalReturnThreshold => VisualLodLevel.Simplified,
            _ => Current,
        };
        return Current;
    }
}

/// <summary>
/// 将任意线程发出的高频变化合并到 30 Hz UI 刷新。只丢弃重复绘制请求，不丢模拟步骤。
/// </summary>
internal sealed class FrameInvalidationGate
{
    private readonly FrameworkElement _owner;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _dirty = 1;
    private double _nextFrameSeconds;

    public FrameInvalidationGate(FrameworkElement owner)
    {
        _owner = owner;
        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(15),
            DispatcherPriority.Render,
            OnTick,
            owner.Dispatcher);
        owner.Loaded += (_, _) => _timer.Start();
        owner.Unloaded += (_, _) => _timer.Stop();
        _timer.Start();
    }

    public void Request() => Interlocked.Exchange(ref _dirty, 1);

    private void OnTick(object? sender, EventArgs e)
    {
        const double interval = 1d / 30d;
        var now = _clock.Elapsed.TotalSeconds;
        if (now + 1e-6 < _nextFrameSeconds)
            return;
        if (now - _nextFrameSeconds > 0.2)
            _nextFrameSeconds = now;
        do
            _nextFrameSeconds += interval;
        while (_nextFrameSeconds <= now);
        if (Interlocked.Exchange(ref _dirty, 0) != 0)
            _owner.InvalidateVisual();
    }
}

/// <summary>
/// 绝对时间轴的 30Hz UI 提交器。后台节拍不随一次 WPF 绘制耗时向后漂移，
/// Dispatcher 中始终至多保留一个回调；忙碌时只丢展示请求，不积压任务。
/// </summary>
internal sealed class RealtimePresentationScheduler : IDisposable
{
    private const double FrameIntervalSeconds = 1d / 30d;
    private readonly Dispatcher _dispatcher;
    private readonly Action _present;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Timer _timer;
    private int _queued;
    private int _running;
    private double _nextFrameSeconds;

    public RealtimePresentationScheduler(Dispatcher dispatcher, Action present)
    {
        _dispatcher = dispatcher;
        _present = present;
        _timer = new Timer(OnTimer);
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
            return;
        _nextFrameSeconds = _clock.Elapsed.TotalSeconds;
        _timer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(5));
    }

    public void Stop()
    {
        Interlocked.Exchange(ref _running, 0);
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer(object? state)
    {
        if (Volatile.Read(ref _running) == 0)
            return;
        var now = _clock.Elapsed.TotalSeconds;
        if (now + 1e-6 < _nextFrameSeconds)
            return;
        if (now - _nextFrameSeconds > 0.2)
            _nextFrameSeconds = now;
        do
            _nextFrameSeconds += FrameIntervalSeconds;
        while (_nextFrameSeconds <= now);
        if (Interlocked.Exchange(ref _queued, 1) != 0)
            return;
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
            {
                Interlocked.Exchange(ref _queued, 0);
                if (Volatile.Read(ref _running) != 0)
                    _present();
            });
        }
        catch (TaskCanceledException)
        {
            Interlocked.Exchange(ref _queued, 0);
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _queued, 0);
        }
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
    }
}
