using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AppShell.Core.Commands;
using AppShell.Core.Logging;
using WBall.Model;
using WBall.Presentation;
using WBall.Sim;
using WBall.Stage;

namespace WBall.DropZone;

/// <summary>落球区画布:编辑四类对象 + 旋转/拉边手势 + 仿真渲染。</summary>
public sealed class DropZoneView : FrameworkElement, ICommandBusAware
{
    private enum DragKind
    {
        None,
        Move,
        Resize,
        Rotate,
    }

    private enum HandleKind
    {
        None,
        Body,
        N, S, E, W,
        NW, NE, SW, SE,
        Rotate,
    }

    private readonly SceneWorld _world;
    private CommandBus? _bus;
    private readonly IShellLog _log;
    private readonly DispatcherTimer _timer;
    private readonly FrameInvalidationGate _invalidation;
    private readonly VisualLodController _localLod = new();
    private readonly BallBitmapLayer _minimalBallLayer = new();
    private VisualLodLevel _visualLod;
    private bool _externalVisualLod;
    private SemaphoreSlim? _runtimeGate;
    private RealtimeFrameSnapshot? _frame;
    private DrawingGroup? _minimalStaticDrawing;
    private long _minimalStaticVersion = -1;
    private double _minimalStaticWidth;
    private double _minimalStaticHeight;
    private string? _hudCacheKey;
    private FormattedText? _hudCacheText;
    private DrawingGroup? _hudCacheDrawing;
    private long _hudCacheBucket = -1;
    private DateTime _lastTick = DateTime.UtcNow;

    private DragKind _dragKind;
    private HandleKind _activeHandle;
    private string? _dragId;
    private string? _dragSolidId;
    private Point _solidDragLast;
    private Point _dragOffset;
    private double _startRot;
    private double _startAngle;
    private double _startX, _startY, _startW, _startH;
    private bool _panning;
    private Point _panStart;
    private double _viewX;
    private double _viewY;
    private double _zoom = 1.0;

    private const double HandleHit = 8;
    private const double RotateOffset = 28;

    private static readonly Typeface LabelTypeface = new("Segoe UI Semibold");
    [ThreadStatic]
    private static Dictionary<(string Label, int FontPx), (FormattedText Fg, FormattedText Outline)>? _labelCache;
    [ThreadStatic]
    private static Dictionary<string, SolidColorBrush>? _brushCache;
    private static Dictionary<(string Label, int FontPx), (FormattedText Fg, FormattedText Outline)> LabelCache =>
        _labelCache ??= new();
    private static Dictionary<string, SolidColorBrush> BrushCache =>
        _brushCache ??= new(StringComparer.OrdinalIgnoreCase);
    private static readonly SolidColorBrush LabelOutlineBrush;
    private static readonly SolidColorBrush ScreenBackgroundBrush;
    private static readonly SolidColorBrush HudBackgroundBrush;

    static DropZoneView()
    {
        LabelOutlineBrush = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0));
        LabelOutlineBrush.Freeze();
        ScreenBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x31, 0x36));
        ScreenBackgroundBrush.Freeze();
        HudBackgroundBrush = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));
        HudBackgroundBrush.Freeze();
    }

    public DropZoneView(SceneWorld world, IShellLog log)
    {
        _world = world;
        _log = log;
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;

        _invalidation = new FrameInvalidationGate(this);
        _world.Changed += () =>
        {
            if (_frame == null)
                _invalidation.Request();
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public void AttachBus(CommandBus bus) => _bus = bus;

    public void AttachRuntimeGate(SemaphoreSlim gate) => _runtimeGate = gate;

    public void SetRealtimeFrame(RealtimeFrameSnapshot frame)
    {
        _frame = frame;
        InvalidateVisual();
    }

    public VisualLodLevel VisualLod => _visualLod;
    public long RenderAllocatedBytes { get; private set; }
    public long RenderCount { get; private set; }

    public void SetVisualLod(VisualLodLevel level)
    {
        _externalVisualLod = true;
        if (_visualLod == level)
            return;
        _visualLod = level;
        if (_frame == null)
            _invalidation.Request();
        else
            InvalidateVisual();
    }

    /// <summary>合成舞台由导演统一推进时关闭；纯落球模式保持默认自动推进。</summary>
    public bool AutoStepEnabled { get; set; } = true;

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastTick).TotalSeconds;
        _lastTick = now;

        if (AutoStepEnabled && _world.IsPlaying)
        {
            PhysicsEngine.Step(_world, dt, msg => _log.Warn("sim", msg));
            _invalidation.Request();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            var gate = _runtimeGate;
            if (gate == null)
            {
                RenderCore(dc);
                return;
            }
            gate.Wait();
            try
            {
                RenderCore(dc);
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            RenderAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            RenderCount++;
        }
    }

    private void RenderCore(DrawingContext dc)
    {
        if (!_externalVisualLod)
            _visualLod = _localLod.Update(_frame?.EconomyBallCount ?? _world.Balls.Count);
        dc.DrawRectangle(ScreenBackgroundBrush, null, new Rect(RenderSize));

        dc.PushTransform(new TranslateTransform(_viewX, _viewY));
        dc.PushTransform(new ScaleTransform(_zoom, _zoom));

        if (_frame != null && _visualLod == VisualLodLevel.Minimal)
            DrawMinimalStaticLayer(dc);
        else
        {
            DrawOutOfBoundsSolids(dc);
            DrawInterior(dc);
            DrawObjects(dc);
            DrawMeshSolids(dc);
            DrawWireframes(dc);
            DrawSketch(dc);
        }
        DrawBalls(dc);

        dc.Pop();
        dc.Pop();

        DrawHud(dc);
    }

    private void DrawMinimalStaticLayer(DrawingContext dc)
    {
        var width = Math.Max(1, _world.WorldWidth);
        var height = Math.Max(1, _world.WorldHeight);
        if (_minimalStaticDrawing == null
            || _minimalStaticVersion != _world.EditVersion
            || Math.Abs(_minimalStaticWidth - width) > 1e-6
            || Math.Abs(_minimalStaticHeight - height) > 1e-6)
        {
            var drawing = new DrawingGroup();
            using (var staticDc = drawing.Open())
            {
                DrawInterior(staticDc);
                DrawObjects(staticDc);
                DrawMeshSolids(staticDc);
                DrawWireframes(staticDc);
            }
            drawing.Freeze();
            _minimalStaticDrawing = drawing;
            _minimalStaticVersion = _world.EditVersion;
            _minimalStaticWidth = width;
            _minimalStaticHeight = height;
        }
        dc.DrawDrawing(_minimalStaticDrawing);
    }

    private void DrawOutOfBoundsSolids(DrawingContext dc)
    {
        var w = _world.WorldWidth;
        var h = _world.WorldHeight;
        var margin = Math.Max(2000, Math.Max(w, h) * 2);
        // v2.12.5 BE-03:界外暗色化
        var solid = new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x1F));
        solid.Freeze();
        var tilePen = new Pen(new SolidColorBrush(Color.FromRgb(0x1B, 0x21, 0x2B)), 1);
        tilePen.Freeze();

        dc.DrawRectangle(solid, null, new Rect(-margin, -margin, w + margin * 2, h + margin * 2));

        const double tile = 40;
        for (double x = -margin; x < w + margin; x += tile)
            dc.DrawLine(tilePen, new Point(x, -margin), new Point(x, h + margin));
        for (double y = -margin; y < h + margin; y += tile)
            dc.DrawLine(tilePen, new Point(-margin, y), new Point(w + margin, y));
    }

    private void DrawInterior(DrawingContext dc)
    {
        var w = _world.WorldWidth;
        var h = _world.WorldHeight;
        // v2.12.5 BE-01:暗底 + 弱网格 + 金色边墙(参考视频观感)
        var interior = new SolidColorBrush(Color.FromRgb(0x0B, 0x0E, 0x13));
        interior.Freeze();
        dc.DrawRectangle(interior, null, new Rect(0, 0, w, h));

        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(0x14, 0x1A, 0x24)), 1);
        gridPen.Freeze();
        const double step = 40;
        for (double x = 0; x <= w; x += step)
            dc.DrawLine(gridPen, new Point(x, 0), new Point(x, h));
        for (double y = 0; y <= h; y += step)
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));

        var wallGlow = new Pen(new SolidColorBrush(Color.FromArgb(70, 0xFF, 0xC8, 0x2E)), 10);
        wallGlow.Freeze();
        dc.DrawRectangle(null, wallGlow, new Rect(0, 0, w, h));
        var border = new Pen(new SolidColorBrush(Color.FromArgb(220, 0xFF, 0xC8, 0x2E)), 3);
        border.Freeze();
        dc.DrawRectangle(null, border, new Rect(0, 0, w, h));
    }

    private void DrawObjects(DrawingContext dc)
    {
        foreach (var obj in _world.Objects)
        {
            var selected = string.Equals(obj.Id, _world.SelectedId, StringComparison.OrdinalIgnoreCase);
            switch (obj.Type)
            {
                case SceneObjectType.Block:
                    DrawBlock(dc, obj, selected);
                    break;
                case SceneObjectType.Arrow:
                    if (_world.IsPlaying)
                        break;
                    DrawArrow(dc, obj, selected);
                    break;
                case SceneObjectType.Spawner:
                    DrawPortal(dc, obj, selected, Color.FromRgb(0x22, 0xC5, 0x5E), "生成");
                    break;
                case SceneObjectType.Despawner:
                    DrawDespawner(dc, obj, selected);
                    break;
            }

            if (selected && !_world.IsPlaying)
                DrawSelectionGizmos(dc, obj);
        }
    }

    private static void PushObjectTransform(DrawingContext dc, SceneObject obj)
    {
        dc.PushTransform(new TranslateTransform(obj.CenterX, obj.CenterY));
        dc.PushTransform(new RotateTransform(obj.Rotation));
    }

    private static void PopObjectTransform(DrawingContext dc)
    {
        dc.Pop();
        dc.Pop();
    }

    private static void DrawBlock(DrawingContext dc, SceneObject obj, bool selected)
    {
        // v2.12.5 BE-03:钉/方块浅灰蓝,暗底上清晰
        var fill = new SolidColorBrush(Color.FromRgb(0x9A, 0xA8, 0xBD));
        fill.Freeze();
        // VI-05:非选中无描边,与异形并置时融合
        var pen = selected ? new Pen(Brushes.DodgerBlue, 1.5) : null;
        pen?.Freeze();
        PushObjectTransform(dc, obj);
        dc.DrawRectangle(fill, pen, new Rect(-obj.W / 2, -obj.H / 2, obj.W, obj.H));
        PopObjectTransform(dc);
    }

    private static void DrawArrow(DrawingContext dc, SceneObject obj, bool selected)
    {
        var cx = obj.CenterX;
        var cy = obj.CenterY;
        var len = Math.Sqrt(obj.DirX * obj.DirX + obj.DirY * obj.DirY);
        var dx = len < 1e-6 ? 0 : obj.DirX / len;
        var dy = len < 1e-6 ? 1 : obj.DirY / len;
        var tip = new Point(cx + dx * 28, cy + dy * 28);
        var pen = new Pen(selected ? Brushes.OrangeRed : Brushes.DarkOrange, 3);
        pen.Freeze();
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(40, 255, 140, 0)),
            new Pen(Brushes.Orange, 1), new Point(cx, cy), obj.InfluenceRadius, obj.InfluenceRadius);

        PushObjectTransform(dc, obj);
        var framePen = new Pen(selected ? Brushes.DodgerBlue : Brushes.DarkOrange, selected ? 2 : 1);
        dc.DrawRectangle(null, framePen, new Rect(-obj.W / 2, -obj.H / 2, obj.W, obj.H));
        PopObjectTransform(dc);

        dc.DrawLine(pen, new Point(cx, cy), tip);
        var px = -dy;
        var py = dx;
        dc.DrawLine(pen, tip, new Point(tip.X - dx * 10 + px * 6, tip.Y - dy * 10 + py * 6));
        dc.DrawLine(pen, tip, new Point(tip.X - dx * 10 - px * 6, tip.Y - dy * 10 - py * 6));
    }

    private static void DrawPortal(DrawingContext dc, SceneObject obj, bool selected, Color color, string label)
    {
        var brush = new SolidColorBrush(Color.FromArgb(80, color.R, color.G, color.B));
        brush.Freeze();
        var pen = new Pen(new SolidColorBrush(color), selected ? 2.5 : 1.5);
        pen.Freeze();
        PushObjectTransform(dc, obj);
        dc.DrawRoundedRectangle(brush, pen, new Rect(-obj.W / 2, -obj.H / 2, obj.W, obj.H), 6, 6);
        var ft = new FormattedText(
            label,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11,
            new SolidColorBrush(color),
            1.25);
        dc.DrawText(ft, new Point(-obj.W / 2 + 4, -obj.H / 2 + 4));
        PopObjectTransform(dc);
    }

    /// <summary>销毁器(v1.6):非选中无描边;中心显示 name 或「销毁」。v2.12.5 BE-02:按语义配色+白字。</summary>
    private static void DrawDespawner(DrawingContext dc, SceneObject obj, bool selected)
    {
        var color = DespawnerAccent(obj.Name);
        var brush = new SolidColorBrush(Color.FromArgb(88, color.R, color.G, color.B));
        brush.Freeze();
        var pen = new Pen(
            new SolidColorBrush(Color.FromArgb(selected ? (byte)255 : (byte)150, color.R, color.G, color.B)),
            selected ? 2.5 : 1);
        pen.Freeze();

        PushObjectTransform(dc, obj);
        dc.DrawRoundedRectangle(brush, pen, new Rect(-obj.W / 2, -obj.H / 2, obj.W, obj.H), 6, 6);
        var label = string.IsNullOrWhiteSpace(obj.Name) ? "销毁" : obj.Name.Trim();
        var ft = new FormattedText(
            label,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            13,
            Brushes.White,
            1.25);
        dc.DrawText(ft, new Point(-ft.Width / 2, -ft.Height / 2));
        PopObjectTransform(dc);
    }

    /// <summary>v2.12.5 BE-02:槽位语义色 — Xn 金色,攻击槽按武器色,未知红。</summary>
    private static Color DespawnerAccent(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length >= 2 && (n[0] == 'X' || n[0] == 'x') && n[1..].All(char.IsDigit))
            return Color.FromRgb(0xFF, 0xC8, 0x2E);
        return n switch
        {
            "大球" or "BigBall" => Color.FromRgb(0xF5, 0x9E, 0x0B),
            "小球" or "SmallBall" => Color.FromRgb(0x38, 0xBD, 0xF8),
            "护盾" or "Shield" => Color.FromRgb(0x22, 0xD3, 0xEE),
            "直射" or "Direct" => Color.FromRgb(0xA7, 0x8B, 0xFA),
            "齐射" or "Volley" => Color.FromRgb(0x34, 0xD3, 0x99),
            "散弹" or "Shotgun" => Color.FromRgb(0xFB, 0x71, 0x85),
            "中子星" or "Neutron" => Color.FromRgb(0xC0, 0x84, 0xFC),
            "散血球" or "BloodSplit" => Color.FromRgb(0xEF, 0x44, 0x44),
            "RUN" or "通用积分" => Color.FromRgb(0xE2, 0xE8, 0xF0),
            _ => Color.FromRgb(0xEF, 0x44, 0x44),
        };
    }

    private void DrawSelectionGizmos(DrawingContext dc, SceneObject obj)
    {
        PushObjectTransform(dc, obj);
        var outline = new Pen(Brushes.DodgerBlue, 1.5) { DashStyle = DashStyles.Dash };
        outline.Freeze();
        dc.DrawRectangle(null, outline, new Rect(-obj.W / 2, -obj.H / 2, obj.W, obj.H));

        var handleFill = Brushes.White;
        var handlePen = new Pen(Brushes.DodgerBlue, 1.5);
        handlePen.Freeze();
        foreach (var (lx, ly) in CornerLocals(obj))
            dc.DrawRectangle(handleFill, handlePen, new Rect(lx - 4, ly - 4, 8, 8));
        foreach (var (lx, ly) in EdgeLocals(obj))
            dc.DrawEllipse(handleFill, handlePen, new Point(lx, ly), 4, 4);

        var rotY = -obj.H / 2 - RotateOffset;
        dc.DrawLine(handlePen, new Point(0, -obj.H / 2), new Point(0, rotY));
        dc.DrawEllipse(Brushes.Orange, handlePen, new Point(0, rotY), 6, 6);
        PopObjectTransform(dc);
    }

    private static (double lx, double ly)[] CornerLocals(SceneObject obj)
    {
        var hx = obj.W / 2;
        var hy = obj.H / 2;
        return [(-hx, -hy), (hx, -hy), (hx, hy), (-hx, hy)];
    }

    private static (double lx, double ly)[] EdgeLocals(SceneObject obj)
    {
        var hx = obj.W / 2;
        var hy = obj.H / 2;
        return [(0, -hy), (0, hy), (hx, 0), (-hx, 0)];
    }

    /// <summary>异形实体填充(v1.5.1 VI-03):常态无描边;选中仅淡色轮廓,无矩形手柄(ED-05)。</summary>
    private void DrawMeshSolids(DrawingContext dc)
    {
        var fallback = UiColor.Parse(MeshSolid.DefaultColor, Color.FromRgb(0x64, 0x74, 0x8B));
        foreach (var solid in _world.Solids)
        {
            if (solid.Points.Count < 3)
                continue;
            var selected = string.Equals(solid.Id, _world.SelectedSolidId, StringComparison.OrdinalIgnoreCase);
            var c = UiColor.Parse(solid.Color, fallback);
            var fill = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            fill.Freeze();

            var geo = BuildClosedGeometry(solid.Points);
            dc.DrawGeometry(fill, null, geo);

            if (selected && !_world.IsPlaying)
            {
                var outline = new Pen(new SolidColorBrush(Color.FromArgb(170, 30, 144, 255)), 1.2)
                {
                    DashStyle = DashStyles.Dash,
                };
                outline.Freeze();
                dc.DrawGeometry(null, outline, geo);
            }
        }
    }

    private static StreamGeometry BuildClosedGeometry(IReadOnlyList<WirePoint> pts)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(pts[0].X, pts[0].Y), true, true);
            for (var i = 1; i < pts.Count; i++)
                ctx.LineTo(new Point(pts[i].X, pts[i].Y), true, false);
        }

        geo.Freeze();
        return geo;
    }

    private void DrawWireframes(DrawingContext dc)
    {
        foreach (var wire in _world.Wireframes)
        {
            if (wire.Points.Count < 2)
                continue;
            var selected = string.Equals(wire.Id, _world.SelectedWireId, StringComparison.OrdinalIgnoreCase);
            // VI-02:闭合预览无描边半透明填充;选中允许淡轮廓(VI-04)
            var fill = selected
                ? new SolidColorBrush(Color.FromArgb(90, 30, 144, 255))
                : new SolidColorBrush(Color.FromArgb(70, 70, 130, 180));
            fill.Freeze();

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(wire.Points[0].X, wire.Points[0].Y), wire.Closed, wire.Closed);
                for (var i = 1; i < wire.Points.Count; i++)
                    ctx.LineTo(new Point(wire.Points[i].X, wire.Points[i].Y), true, false);
            }

            geo.Freeze();
            Pen? pen = null;
            if (selected)
            {
                pen = new Pen(new SolidColorBrush(Color.FromArgb(150, 30, 144, 255)), 1);
                pen.Freeze();
            }
            else if (!wire.Closed)
            {
                pen = new Pen(new SolidColorBrush(Color.FromArgb(140, 70, 130, 180)), 1.5);
                pen.Freeze();
            }

            dc.DrawGeometry(wire.Closed ? fill : null, pen, geo);

            // 保留极细顶点点,无描边
            var dot = new SolidColorBrush(Color.FromArgb(170, 70, 130, 180));
            dot.Freeze();
            foreach (var p in wire.Points)
                dc.DrawEllipse(dot, null, new Point(p.X, p.Y), 2, 2);
        }
    }

    private void DrawSketch(DrawingContext dc)
    {
        var pts = _world.Sketch.Points;
        if (pts.Count == 0)
            return;

        // VI-01:半透明折线,与线框/异形同色系,无粗黑边
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(150, 70, 130, 180)), 2);
        pen.Freeze();
        for (var i = 1; i < pts.Count; i++)
            dc.DrawLine(pen, new Point(pts[i - 1].X, pts[i - 1].Y), new Point(pts[i].X, pts[i].Y));

        var dot = new SolidColorBrush(Color.FromArgb(200, 70, 130, 180));
        dot.Freeze();
        foreach (var p in pts)
            dc.DrawEllipse(dot, null, new Point(p.X, p.Y), 3, 3);

        // 起点高亮(近合提示,虚线圈,非实体边框)
        if (pts.Count >= 3)
        {
            var r = _world.Sketch.CloseRadius;
            dc.DrawEllipse(null, new Pen(Brushes.LimeGreen, 1) { DashStyle = DashStyles.Dot },
                new Point(pts[0].X, pts[0].Y), r, r);
        }
    }

    private void DrawBalls(DrawingContext dc)
    {
        var frame = _frame;
        if (frame != null)
        {
            DrawSnapshotBalls(dc, frame);
            return;
        }
        if (_visualLod == VisualLodLevel.Minimal)
        {
            _minimalBallLayer.Draw(dc, _world.Balls, _world.WorldWidth, _world.WorldHeight);
            return;
        }

        if (_world.TrailEnabled && _visualLod != VisualLodLevel.Minimal)
        {
            foreach (var ball in _world.Balls)
            {
                if (ball.Trail.Count < 2)
                    continue;
                var color = UiColor.Parse(ball.Color, Colors.DodgerBlue);
                (double X, double Y)? prev = null;
                var idx = 0;
                var last = ball.Trail.Count - 1;
                foreach (var p in ball.Trail)
                {
                    if (prev is { } a
                        && (_visualLod == VisualLodLevel.Full || idx % 4 == 0 || idx == last))
                    {
                        var t = (double)idx / last;
                        var alpha = (byte)(24 + t * 180);
                        var trailPen = new Pen(GetBrush(Color.FromArgb(alpha, color.R, color.G, color.B)), 2);
                        trailPen.Freeze();
                        dc.DrawLine(trailPen, new Point(a.X, a.Y), new Point(p.X, p.Y));
                    }

                    prev = p;
                    idx++;
                }
            }
        }

        if (_visualLod != VisualLodLevel.Minimal)
        {
            var flashSec = Math.Max(1e-6, _world.TeleportFlashSeconds);
            foreach (var ball in _world.Balls)
            {
                if (ball.TeleportFlashT <= 0)
                    continue;
                var fade = Math.Clamp(ball.TeleportFlashT / flashSec, 0, 1);
                var a = (byte)(fade * 220);
                var flashPen = new Pen(GetBrush(Color.FromArgb(a, 255, 140, 0)), 2)
                {
                    DashStyle = DashStyles.Dash,
                };
                flashPen.Freeze();
                dc.DrawLine(flashPen,
                    new Point(ball.TeleportFromX, ball.TeleportFromY),
                    new Point(ball.TeleportToX, ball.TeleportToY));
            }
        }

        foreach (var ball in _world.Balls)
        {
            var color = UiColor.Parse(ball.Color, Colors.DodgerBlue);
            var fill = GetBrush(color);
            dc.DrawEllipse(fill, null, new Point(ball.X, ball.Y), ball.Size, ball.Size);

            if (_visualLod != VisualLodLevel.Full)
                continue;
            var label = PublicDefaults.FormatMultiplier(ball.Multiplier);
            var fontSize = Math.Clamp(ball.Size * 0.9, 8, 22);
            var fontPx = (int)Math.Round(fontSize);
            var (ft, outline) = GetLabelTexts(label, fontPx);
            var ox = ball.X - ft.Width / 2;
            var oy = ball.Y - ft.Height / 2;
            dc.DrawText(outline, new Point(ox + 0.8, oy + 0.8));
            dc.DrawText(ft, new Point(ox, oy));
        }
    }

    private void DrawSnapshotBalls(DrawingContext dc, RealtimeFrameSnapshot frame)
    {
        if (_visualLod == VisualLodLevel.Minimal)
        {
            _minimalBallLayer.Draw(
                dc, frame.EconomyBalls, frame.EconomyBallCount,
                frame.EconomyWidth, frame.EconomyHeight);
            return;
        }

        if (frame.EconomyTrailEnabled)
        {
            for (var ballIndex = 0; ballIndex < frame.EconomyBallCount; ballIndex++)
            {
                var ball = frame.EconomyBalls[ballIndex];
                if (ball.TrailCount < 2)
                    continue;
                var color = UiColor.Parse(ball.Color, Colors.DodgerBlue);
                var last = ball.TrailCount - 1;
                for (var index = 1; index < ball.TrailCount; index++)
                {
                    if (_visualLod != VisualLodLevel.Full && index % 4 != 0 && index != last)
                        continue;
                    var from = frame.EconomyTrails[ball.TrailStart + index - 1];
                    var to = frame.EconomyTrails[ball.TrailStart + index];
                    var t = (double)index / last;
                    var alpha = (byte)(24 + t * 180);
                    var trailPen = new Pen(GetBrush(Color.FromArgb(alpha, color.R, color.G, color.B)), 2);
                    trailPen.Freeze();
                    dc.DrawLine(trailPen, new Point(from.X, from.Y), new Point(to.X, to.Y));
                }
            }
        }

        var flashSec = Math.Max(1e-6, frame.EconomyTeleportFlashSeconds);
        for (var i = 0; i < frame.EconomyBallCount; i++)
        {
            var ball = frame.EconomyBalls[i];
            if (ball.TeleportFlashT <= 0)
                continue;
            var fade = Math.Clamp(ball.TeleportFlashT / flashSec, 0, 1);
            var flashPen = new Pen(GetBrush(Color.FromArgb((byte)(fade * 220), 255, 140, 0)), 2)
            {
                DashStyle = DashStyles.Dash,
            };
            flashPen.Freeze();
            dc.DrawLine(
                flashPen,
                new Point(ball.TeleportFromX, ball.TeleportFromY),
                new Point(ball.TeleportToX, ball.TeleportToY));
        }

        for (var i = 0; i < frame.EconomyBallCount; i++)
        {
            var ball = frame.EconomyBalls[i];
            var color = UiColor.Parse(ball.Color, Colors.DodgerBlue);
            dc.DrawEllipse(GetBrush(color), null, new Point(ball.X, ball.Y), ball.Size, ball.Size);
            if (_visualLod != VisualLodLevel.Full)
                continue;
            var label = PublicDefaults.FormatMultiplier(ball.Multiplier);
            var fontPx = (int)Math.Round(Math.Clamp(ball.Size * 0.9, 8, 22));
            var (text, outline) = GetLabelTexts(label, fontPx);
            var origin = new Point(ball.X - text.Width / 2, ball.Y - text.Height / 2);
            dc.DrawText(outline, new Point(origin.X + 0.8, origin.Y + 0.8));
            dc.DrawText(text, origin);
        }
    }

    private static SolidColorBrush GetBrush(Color color)
    {
        var key = color.ToString();
        if (BrushCache.TryGetValue(key, out var brush))
            return brush;
        brush = new SolidColorBrush(color);
        brush.Freeze();
        if (BrushCache.Count > 256)
            BrushCache.Clear();
        BrushCache[key] = brush;
        return brush;
    }

    private static (FormattedText Fg, FormattedText Outline) GetLabelTexts(string label, int fontPx)
    {
        var key = (label, fontPx);
        if (LabelCache.TryGetValue(key, out var cached))
            return cached;

        var ft = new FormattedText(
            label,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            fontPx,
            Brushes.White,
            1.25);
        var outline = new FormattedText(
            label,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            fontPx,
            LabelOutlineBrush,
            1.25);
        if (LabelCache.Count > 256)
            LabelCache.Clear();
        LabelCache[key] = (ft, outline);
        return (ft, outline);
    }

    private void DrawHud(DrawingContext dc)
    {
        var frame = _frame;
        var bucket = frame != null && _visualLod == VisualLodLevel.Minimal
            ? frame.Sequence / 6
            : -1;
        if (bucket >= 0 && _hudCacheDrawing != null && _hudCacheBucket == bucket)
        {
            dc.DrawDrawing(_hudCacheDrawing);
            return;
        }
        var tool = _world.Tool.ToString();
        var state = (_frame?.EconomyPlaying ?? _world.IsPlaying) ? "仿真中" : "编辑";
        var coll = (_frame?.EconomyBallCollisionEnabled ?? _world.BallCollisionEnabled) ? "球碰:开" : "球碰:关";
        var ballCount = _frame?.EconomyBallCount ?? _world.Balls.Count;
        var text = $"WBall | {state} | 场景 {_world.WorldWidth:0}×{_world.WorldHeight:0} | 工具:{tool} | {coll} | 球:{ballCount} | 线框:{_world.Wireframes.Count} | 异形:{_world.Solids.Count}" +
                   (_world.Sketch.IsEmpty ? "" : $" | 草图:{_world.Sketch.Points.Count}") +
                   " | 滚轮缩放 中键平移";
        if (!string.Equals(_hudCacheKey, text, StringComparison.Ordinal))
        {
            _hudCacheKey = text;
            _hudCacheText = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                12,
                Brushes.DimGray,
                1.25);
        }
        var formatted = _hudCacheText!;
        if (bucket >= 0)
        {
            var drawing = new DrawingGroup();
            using (var drawingDc = drawing.Open())
            {
                drawingDc.DrawRectangle(HudBackgroundBrush, null,
                    new Rect(0, 0, formatted.Width + 16, formatted.Height + 10));
                drawingDc.DrawText(formatted, new Point(8, 4));
            }
            drawing.Freeze();
            _hudCacheDrawing = drawing;
            _hudCacheBucket = bucket;
            dc.DrawDrawing(drawing);
            return;
        }
        dc.DrawRectangle(HudBackgroundBrush, null,
            new Rect(0, 0, formatted.Width + 16, formatted.Height + 10));
        dc.DrawText(formatted, new Point(8, 4));
    }

    private Point ToWorld(Point screen)
        => new((screen.X - _viewX) / _zoom, (screen.Y - _viewY) / _zoom);

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var before = ToWorld(e.GetPosition(this));
        var factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        _zoom = Math.Clamp(_zoom * factor, 0.3, 3.0);
        var after = ToWorld(e.GetPosition(this));
        _viewX += (after.X - before.X) * _zoom;
        _viewY += (after.Y - before.Y) * _zoom;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        var screen = e.GetPosition(this);
        var world = ToWorld(screen);

        if (e.ChangedButton == MouseButton.Middle)
        {
            _panning = true;
            _panStart = screen;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left)
            return;

        if (_world.Tool == EditorTool.Select)
        {
            // 优先命中当前选中对象的手柄
            if (_world.SelectedId != null)
            {
                var sel = _world.FindObject(_world.SelectedId);
                if (sel != null)
                {
                    var handle = HitHandle(sel, world);
                    if (handle != HandleKind.None)
                    {
                        BeginDrag(sel, handle, world);
                        e.Handled = true;
                        return;
                    }
                }
            }

            // 点选线框
            var hitWire = HitTestWire(world);
            if (hitWire != null)
            {
                _world.SelectedWireId = hitWire.Id;
                _world.SelectedId = null;
                _world.SelectedSolidId = null;
                _world.SelectedBallId = null;
                _world.NotifyChanged();
                e.Handled = true;
                return;
            }

            // 点选异形实体(拖主体 = 整块移动,ED-01;不提供拉边手柄,ED-05)
            var hitSolid = HitTestSolid(world);
            if (hitSolid != null)
            {
                _world.SelectedSolidId = hitSolid.Id;
                _world.SelectedId = null;
                _world.SelectedWireId = null;
                _world.SelectedBallId = null;
                _dragSolidId = hitSolid.Id;
                _solidDragLast = world;
                CaptureMouse();
                _world.NotifyChanged();
                e.Handled = true;
                return;
            }

            var hit = HitTest(world);
            if (hit != null)
            {
                _world.SelectedId = hit.Id;
                _world.SelectedWireId = null;
                _world.SelectedSolidId = null;
                _world.SelectedBallId = null;
                var handle = HitHandle(hit, world);
                BeginDrag(hit, handle == HandleKind.None ? HandleKind.Body : handle, world);
                _world.NotifyChanged();
            }
            else
            {
                var hitBall = HitTestBall(world);
                if (hitBall != null)
                {
                    _world.SelectedBallId = hitBall.Id;
                    _world.SelectedId = null;
                    _world.SelectedWireId = null;
                    _world.SelectedSolidId = null;
                }
                else
                {
                    _world.SelectedId = null;
                    _world.SelectedWireId = null;
                    _world.SelectedSolidId = null;
                    _world.SelectedBallId = null;
                }

                _world.NotifyChanged();
            }

            e.Handled = true;
            return;
        }

        if (_world.Tool == EditorTool.Wire)
        {
            if (_world.IsPlaying)
            {
                _log.Warn("dropzone", "仿真中不可编辑草图");
                e.Handled = true;
                return;
            }

            if (!_world.ContainsPoint(world.X, world.Y))
            {
                _log.Warn("dropzone", "点击在场景外,无法加点");
                e.Handled = true;
                return;
            }

            Run($"wire.point x={Fmt(world.X)} y={Fmt(world.Y)}");
            e.Handled = true;
            return;
        }

        var type = _world.Tool switch
        {
            EditorTool.Block => "block",
            EditorTool.Arrow => "arrow",
            EditorTool.Spawner => "spawner",
            EditorTool.Despawner => "despawner",
            _ => null,
        };
        if (type != null)
        {
            if (!_world.ContainsPoint(world.X, world.Y))
            {
                _log.Warn("dropzone", "点击在场景外实体区,无法放置;请点在内区或 scene.size 扩大场景");
                e.Handled = true;
                return;
            }

            var x = Math.Round(world.X - 20, 1);
            var y = Math.Round(world.Y - 20, 1);
            Run($"scene.add type={type} x={Fmt(x)} y={Fmt(y)} w=40 h=40");
        }

        e.Handled = true;
    }

    private void BeginDrag(SceneObject obj, HandleKind handle, Point world)
    {
        _dragId = obj.Id;
        _activeHandle = handle;
        _startX = obj.X;
        _startY = obj.Y;
        _startW = obj.W;
        _startH = obj.H;
        _startRot = obj.Rotation;
        CaptureMouse();

        if (handle == HandleKind.Rotate)
        {
            _dragKind = DragKind.Rotate;
            _startAngle = Math.Atan2(world.Y - obj.CenterY, world.X - obj.CenterX);
        }
        else if (handle is HandleKind.Body or HandleKind.None)
        {
            _dragKind = DragKind.Move;
            _dragOffset = new Point(world.X - obj.X, world.Y - obj.Y);
        }
        else
        {
            _dragKind = DragKind.Resize;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var screen = e.GetPosition(this);
        if (_panning && e.MiddleButton == MouseButtonState.Pressed)
        {
            _viewX += screen.X - _panStart.X;
            _viewY += screen.Y - _panStart.Y;
            _panStart = screen;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_dragSolidId != null && e.LeftButton == MouseButtonState.Pressed)
        {
            var wp = ToWorld(screen);
            var solid = _world.FindSolid(_dragSolidId);
            if (solid != null)
            {
                solid.MoveBy(wp.X - _solidDragLast.X, wp.Y - _solidDragLast.Y);
                _solidDragLast = wp;
                InvalidateVisual();
            }

            e.Handled = true;
            return;
        }

        if (_dragKind == DragKind.None || _dragId == null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var world = ToWorld(screen);
        var obj = _world.FindObject(_dragId);
        if (obj == null)
            return;

        switch (_dragKind)
        {
            case DragKind.Move:
                obj.X = world.X - _dragOffset.X;
                obj.Y = world.Y - _dragOffset.Y;
                break;
            case DragKind.Rotate:
                {
                    var ang = Math.Atan2(world.Y - obj.CenterY, world.X - obj.CenterX);
                    var deltaDeg = (ang - _startAngle) * 180.0 / Math.PI;
                    // 无吸附(V4Q4)
                    obj.Rotation = _startRot + deltaDeg;
                    obj.SyncArrowDirFromRotation();
                    break;
                }
            case DragKind.Resize:
                ApplyResize(obj, world);
                break;
        }

        InvalidateVisual();
        e.Handled = true;
    }

    private void ApplyResize(SceneObject obj, Point world)
    {
        obj.WorldToLocal(world.X, world.Y, out var lx, out var ly);
        var hx0 = _startW / 2;
        var hy0 = _startH / 2;

        // 固定对边局部坐标,拖动边/角
        double left = -hx0, right = hx0, top = -hy0, bottom = hy0;
        switch (_activeHandle)
        {
            case HandleKind.E:
            case HandleKind.NE:
            case HandleKind.SE:
                right = Math.Max(left + 8, lx);
                break;
            case HandleKind.W:
            case HandleKind.NW:
            case HandleKind.SW:
                left = Math.Min(right - 8, lx);
                break;
        }

        switch (_activeHandle)
        {
            case HandleKind.S:
            case HandleKind.SE:
            case HandleKind.SW:
                bottom = Math.Max(top + 8, ly);
                break;
            case HandleKind.N:
            case HandleKind.NE:
            case HandleKind.NW:
                top = Math.Min(bottom - 8, ly);
                break;
        }

        var newW = Math.Max(8, right - left);
        var newH = Math.Max(8, bottom - top);
        var newCxLocal = (left + right) / 2;
        var newCyLocal = (top + bottom) / 2;
        var rad = _startRot * Math.PI / 180.0;
        var c = Math.Cos(rad);
        var s = Math.Sin(rad);
        var startCx = _startX + _startW / 2;
        var startCy = _startY + _startH / 2;
        var ncx = startCx + newCxLocal * c - newCyLocal * s;
        var ncy = startCy + newCxLocal * s + newCyLocal * c;

        obj.W = newW;
        obj.H = newH;
        obj.X = ncx - newW / 2;
        obj.Y = ncy - newH / 2;
        obj.Rotation = _startRot;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && _panning)
        {
            _panning = false;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left && _dragSolidId != null)
        {
            var solid = _world.FindSolid(_dragSolidId);
            if (solid != null)
            {
                solid.GetAabb(out var minX, out var minY, out _, out _);
                Run($"solid.move id={solid.Id} x={Fmt(minX)} y={Fmt(minY)}");
            }

            _dragSolidId = null;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left && _dragKind != DragKind.None && _dragId != null)
        {
            var obj = _world.FindObject(_dragId);
            if (obj != null)
            {
                switch (_dragKind)
                {
                    case DragKind.Move:
                        Run($"scene.move id={obj.Id} x={Fmt(obj.X)} y={Fmt(obj.Y)}");
                        break;
                    case DragKind.Rotate:
                        Run($"scene.set id={obj.Id} rotation={Fmt(obj.Rotation)}");
                        break;
                    case DragKind.Resize:
                        Run($"scene.set id={obj.Id} x={Fmt(obj.X)} y={Fmt(obj.Y)} w={Fmt(obj.W)} h={Fmt(obj.H)}");
                        break;
                }
            }

            _dragKind = DragKind.None;
            _activeHandle = HandleKind.None;
            _dragId = null;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Back && _world.Tool == EditorTool.Wire)
        {
            Run("wire.undo");
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete)
        {
            if (_world.SelectedWireId != null)
            {
                Run($"wire.remove id={_world.SelectedWireId}");
                e.Handled = true;
                return;
            }

            if (_world.SelectedSolidId != null)
            {
                Run($"solid.remove id={_world.SelectedSolidId}");
                e.Handled = true;
                return;
            }

            if (_world.SelectedBallId != null)
            {
                Run($"ball.despawn id={_world.SelectedBallId}");
                e.Handled = true;
                return;
            }

            if (_world.SelectedId != null)
            {
                Run($"scene.remove id={_world.SelectedId}");
                e.Handled = true;
            }
        }
    }

    private void Run(string command)
    {
        if (_bus == null)
        {
            _log.Warn("dropzone", "指令总线尚未就绪");
            return;
        }

        _ = _bus.ExecuteAsync(command, "UI");
    }

    private MeshSolid? HitTestSolid(Point world)
    {
        for (var i = _world.Solids.Count - 1; i >= 0; i--)
        {
            if (_world.Solids[i].ContainsPoint(world.X, world.Y))
                return _world.Solids[i];
        }

        return null;
    }

    private Ball? HitTestBall(Point world)
    {
        var frame = _frame;
        if (frame != null)
        {
            for (var i = frame.EconomyBallCount - 1; i >= 0; i--)
            {
                var b = frame.EconomyBalls[i];
                var dx = world.X - b.X;
                var dy = world.Y - b.Y;
                if (dx * dx + dy * dy <= b.Size * b.Size)
                {
                    return new Ball
                    {
                        Id = b.Id,
                        X = b.X,
                        Y = b.Y,
                        Size = b.Size,
                        Color = b.Color,
                        Multiplier = b.Multiplier,
                    };
                }
            }
            return null;
        }
        for (var i = _world.Balls.Count - 1; i >= 0; i--)
        {
            var b = _world.Balls[i];
            var dx = world.X - b.X;
            var dy = world.Y - b.Y;
            if (dx * dx + dy * dy <= b.Size * b.Size)
                return b;
        }

        return null;
    }

    private Wireframe? HitTestWire(Point world)
    {
        const double tol = 8;
        for (var i = _world.Wireframes.Count - 1; i >= 0; i--)
        {
            var w = _world.Wireframes[i];
            for (var j = 0; j < w.Points.Count; j++)
            {
                var a = w.Points[j];
                var b = w.Points[(j + 1) % w.Points.Count];
                if (!w.Closed && j == w.Points.Count - 1)
                    break;
                if (DistToSegment(world.X, world.Y, a.X, a.Y, b.X, b.Y) <= tol)
                    return w;
            }
        }

        return null;
    }

    private static double DistToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-12)
            return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
        var t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lenSq, 0, 1);
        var qx = ax + t * dx;
        var qy = ay + t * dy;
        return Math.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
    }

    private SceneObject? HitTest(Point world)
    {
        for (var i = _world.Objects.Count - 1; i >= 0; i--)
        {
            var o = _world.Objects[i];
            if (o.ContainsWorldPoint(world.X, world.Y))
                return o;
            // 旋转手柄也算命中该对象
            if (string.Equals(o.Id, _world.SelectedId, StringComparison.OrdinalIgnoreCase)
                && HitHandle(o, world) != HandleKind.None)
                return o;
        }

        return null;
    }

    private HandleKind HitHandle(SceneObject obj, Point world)
    {
        obj.WorldToLocal(world.X, world.Y, out var lx, out var ly);
        var tol = HandleHit / Math.Max(0.3, _zoom);
        var hx = obj.W / 2;
        var hy = obj.H / 2;

        var rotY = -hy - RotateOffset;
        if (Dist(lx, ly, 0, rotY) <= tol + 2)
            return HandleKind.Rotate;

        if (Dist(lx, ly, -hx, -hy) <= tol) return HandleKind.NW;
        if (Dist(lx, ly, hx, -hy) <= tol) return HandleKind.NE;
        if (Dist(lx, ly, hx, hy) <= tol) return HandleKind.SE;
        if (Dist(lx, ly, -hx, hy) <= tol) return HandleKind.SW;
        if (Dist(lx, ly, 0, -hy) <= tol) return HandleKind.N;
        if (Dist(lx, ly, 0, hy) <= tol) return HandleKind.S;
        if (Dist(lx, ly, hx, 0) <= tol) return HandleKind.E;
        if (Dist(lx, ly, -hx, 0) <= tol) return HandleKind.W;

        if (Math.Abs(lx) <= hx && Math.Abs(ly) <= hy)
            return HandleKind.Body;
        return HandleKind.None;
    }

    private static double Dist(double x1, double y1, double x2, double y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
