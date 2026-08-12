using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WBall.Battle;
using WBall.DropZone;
using WBall.Model;
using WBall.Recording;

namespace WBall.Stage;

/// <summary>经济世界、战场世界与 HUD 的合成主舞台。</summary>
public sealed class StageView : Grid
{
    private readonly StageState _state;
    private readonly SceneWorld _economyWorld;
    private readonly SceneWorld _battleWorld;
    private readonly Viewbox _economyHost;
    private readonly Viewbox _arenaHost;
    private readonly StageHudView _hud;
    private readonly BattleDirector _director;
    private readonly DropZoneView _economyView;
    private readonly ArenaView _arenaView;
    private readonly VisualLodController _visualLod = new();
    private readonly RealtimePresentationScheduler _presentation;
    private readonly Grid _content;
    private readonly StageVictoryOverlay _victoryOverlay = new();
    private readonly BattleRuntime _battle;
    private readonly RealtimeFrameSnapshot[] _frames =
        [new RealtimeFrameSnapshot(), new RealtimeFrameSnapshot()];
    private int _publishedFrameIndex;
    private long _frameSequence;
    private bool _layoutUpdateQueued;
    private string? _victoryWinnerId;
    private long _victoryStartSequence;

    public StageView(
        StageState state,
        SceneWorld economyWorld,
        SceneWorld battleWorld,
        BattleRuntime battle,
        BattleDirector director,
        DropZoneView economyView,
        ArenaView arenaView,
        WeaponCatalog weapons,
        RenderTimeConfig renderTime)
    {
        _state = state;
        _economyWorld = economyWorld;
        _battleWorld = battleWorld;
        _battle = battle;
        _director = director;
        _economyView = economyView;
        _arenaView = arenaView;
        Coordinator = new RealtimeSimulationCoordinator(
            state, economyWorld, battleWorld, director, renderTime);
        ClipToBounds = true;

        _economyHost = CreateHost(economyView, economyWorld);
        _arenaHost = CreateHost(arenaView, battleWorld);
        _hud = new StageHudView(state, economyWorld, director, weapons);

        // v2.11 AR-01:内容区锁定逻辑分辨率长宽比,窗口任意比例下信箱式居中
        _content = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true,
        };
        _content.Children.Add(_economyHost);
        _content.Children.Add(_arenaHost);
        _content.Children.Add(_victoryOverlay);
        _content.Children.Add(_hud);
        Children.Add(_content);
        SizeChanged += (_, _) => ApplyAspect();

        _state.Changed += QueueApplyState;
        _economyWorld.Changed += QueueLogicalCanvasSizeUpdate;
        _battleWorld.Changed += QueueLogicalCanvasSizeUpdate;
        _presentation = new RealtimePresentationScheduler(Dispatcher, OnTick);
        Coordinator.Disposed += _presentation.Dispose;
        Loaded += (_, _) => _presentation.Start();
        Unloaded += (_, _) => _presentation.Stop();
        _presentation.Start();
        CaptureFrame(wait: true);
        ApplyState();
    }

    public StageHudView Hud => _hud;
    public TimelineClock Timeline => Coordinator.Timeline;
    public RealtimeSimulationCoordinator Coordinator { get; }
    public event Action<long>? FramePresented;

    public void ApplyTimeConfig(RenderTimeConfig config) =>
        Coordinator.ApplyTimeConfig(config);

    private void OnTick()
    {
        var captured = CaptureFrame(wait: false);
        var frame = _frames[_publishedFrameIndex];
        if (!captured)
            PublishFrame(frame);
        var ballCount = frame.EconomyBallCount + frame.BattleBallCount;
        var visualLod = _visualLod.Update(ballCount);
        _economyView.SetVisualLod(visualLod);
        _arenaView.SetVisualLod(visualLod);
        FramePresented?.Invoke(frame.Sequence);
    }

    private bool CaptureFrame(bool wait)
    {
        if (wait)
            Coordinator.Gate.Wait();
        else if (!Coordinator.Gate.Wait(0))
            return false;

        var next = 1 - _publishedFrameIndex;
        try
        {
            _frames[next].Capture(
                ++_frameSequence,
                _state,
                _economyWorld,
                _battleWorld,
                _battle,
                _director);
        }
        finally
        {
            Coordinator.Gate.Release();
        }

        _publishedFrameIndex = next;
        PublishFrame(_frames[next]);
        return true;
    }

    private void PublishFrame(RealtimeFrameSnapshot frame)
    {
        UpdateVictory(frame);
        _economyView.SetRealtimeFrame(frame);
        _arenaView.SetRealtimeFrame(frame);
        _victoryOverlay.SetRealtimeFrame(frame);
        _hud.SetRealtimeFrame(frame);
    }

    private void UpdateVictory(RealtimeFrameSnapshot frame)
    {
        if (frame.WinnerId == null || frame.WinnerName == null || frame.WinnerColor == null)
        {
            _victoryWinnerId = null;
            frame.SetVictory(null);
            return;
        }
        if (!string.Equals(_victoryWinnerId, frame.WinnerId, StringComparison.OrdinalIgnoreCase))
        {
            _victoryWinnerId = frame.WinnerId;
            _victoryStartSequence = frame.Sequence;
        }
        const int liveFps = 30;
        var total = RenderJobService.VictoryAnimationSeconds * liveFps;
        var index = (int)Math.Clamp(frame.Sequence - _victoryStartSequence, 0, total - 1);
        frame.SetVictory(new VictoryAnimationState(
            frame.WinnerId, frame.WinnerName, frame.WinnerColor,
            index, total, (index + 1d) / total));
    }

    private void QueueApplyState()
    {
        if (Dispatcher.CheckAccess())
            ApplyState();
        else
            Dispatcher.BeginInvoke(ApplyState);
    }

    private void QueueLogicalCanvasSizeUpdate()
    {
        if (_layoutUpdateQueued)
            return;
        _layoutUpdateQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _layoutUpdateQueued = false;
            UpdateLogicalCanvasSizes();
        });
    }

    private static Viewbox CreateHost(FrameworkElement content, SceneWorld world)
    {
        content.Width = world.WorldWidth;
        content.Height = world.WorldHeight;
        return new Viewbox
        {
            Child = content,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
        };
    }

    private void ApplyState()
    {
        Background = ParseBrush(_state.Background);
        _arenaHost.Visibility = _state.CompositeVisible ? Visibility.Visible : Visibility.Collapsed;
        _hud.Visibility = _state.HudVisible ? Visibility.Visible : Visibility.Collapsed;

        _content.RowDefinitions.Clear();
        _content.ColumnDefinitions.Clear();
        if (!_state.CompositeVisible)
        {
            _content.RowDefinitions.Add(new RowDefinition());
            _content.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetRow(_economyHost, 0);
            Grid.SetColumn(_economyHost, 0);
            Grid.SetRowSpan(_economyHost, 1);
            Grid.SetColumnSpan(_economyHost, 1);
        }
        else if (_state.Orientation == StageOrientation.Horizontal)
        {
            _content.RowDefinitions.Add(new RowDefinition());
            _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_state.Split, GridUnitType.Star) });
            _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - _state.Split, GridUnitType.Star) });
            Place(_economyHost, 0, 0);
            Place(_arenaHost, 0, 1);
        }
        else
        {
            _content.ColumnDefinitions.Add(new ColumnDefinition());
            _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_state.Split, GridUnitType.Star) });
            _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - _state.Split, GridUnitType.Star) });
            Place(_economyHost, 0, 0);
            Place(_arenaHost, 1, 0);
        }

        Grid.SetRow(_hud, 0);
        Grid.SetColumn(_hud, 0);
        Grid.SetRowSpan(_hud, Math.Max(1, _content.RowDefinitions.Count));
        Grid.SetColumnSpan(_hud, Math.Max(1, _content.ColumnDefinitions.Count));
        Grid.SetRow(_victoryOverlay, 0);
        Grid.SetColumn(_victoryOverlay, 0);
        Grid.SetRowSpan(_victoryOverlay, Math.Max(1, _content.RowDefinitions.Count));
        Grid.SetColumnSpan(_victoryOverlay, Math.Max(1, _content.ColumnDefinitions.Count));
        ApplyAspect();
        InvalidateVisual();
    }

    /// <summary>v2.11 AR-01:按逻辑分辨率比例信箱式定尺内容区。</summary>
    private void ApplyAspect()
    {
        var aspect = _state.AspectRatio;
        var width = ActualWidth;
        var height = ActualHeight;
        if (aspect <= 0 || width <= 1 || height <= 1)
            return;
        if (width / height > aspect)
        {
            _content.Height = height;
            _content.Width = height * aspect;
        }
        else
        {
            _content.Width = width;
            _content.Height = width / aspect;
        }
    }

    private void UpdateLogicalCanvasSizes()
    {
        if (_economyHost.Child is FrameworkElement economy)
        {
            economy.Width = _economyWorld.WorldWidth;
            economy.Height = _economyWorld.WorldHeight;
        }
        if (_arenaHost.Child is FrameworkElement arena)
        {
            arena.Width = _battleWorld.WorldWidth;
            arena.Height = _battleWorld.WorldHeight;
        }
    }

    private static void Place(UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetRowSpan(element, 1);
        Grid.SetColumnSpan(element, 1);
    }

    private static Brush ParseBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
