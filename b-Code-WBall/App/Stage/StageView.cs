using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WBall.Battle;
using WBall.DropZone;
using WBall.Model;

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
    private readonly DispatcherTimer _timer;
    private readonly Grid _content;
    private DateTime _lastTick = DateTime.UtcNow;
    private double _accumulator;

    public StageView(
        StageState state,
        SceneWorld economyWorld,
        SceneWorld battleWorld,
        BattleDirector director,
        DropZoneView economyView,
        ArenaView arenaView,
        WeaponCatalog weapons)
    {
        _state = state;
        _economyWorld = economyWorld;
        _battleWorld = battleWorld;
        _director = director;
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
        _content.Children.Add(_hud);
        Children.Add(_content);
        SizeChanged += (_, _) => ApplyAspect();

        _state.Changed += ApplyState;
        _economyWorld.Changed += UpdateLogicalCanvasSizes;
        _battleWorld.Changed += UpdateLogicalCanvasSizes;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
        _timer.Start();
        ApplyState();
    }

    public StageHudView Hud => _hud;

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var elapsed = Math.Min(0.1, (now - _lastTick).TotalSeconds);
        _lastTick = now;
        if (_state.Mode is not (StageMode.Play or StageMode.Record))
        {
            _accumulator = 0;
            return;
        }

        // 录制由 record.start 同步推进;Play 才用 timer 驱动导演
        if (_state.Mode == StageMode.Record)
            return;

        const double fixedStep = 1.0 / 60.0;
        _accumulator += elapsed;
        var steps = 0;
        while (_accumulator >= fixedStep && steps++ < 6)
        {
            _director.AdvanceFixedStep();
            _accumulator -= fixedStep;
        }
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
