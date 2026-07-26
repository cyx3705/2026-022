using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WBall.Battle;
using WBall.Game;
using WBall.Model;

namespace WBall.Stage;

/// <summary>右世界战场画布：四角炮台 + 弹幕(参考图风格)。</summary>
public sealed class ArenaView : FrameworkElement
{
    private static readonly Brush BackgroundBrush = FrozenBrush("#050608");
    private static readonly Brush DividerBrush = FrozenBrush(Color.FromArgb(40, 80, 90, 110));
    private static readonly Pen DividerPen = FrozenPen(DividerBrush, 1);
    private static readonly Typeface LabelTypeface = new("Segoe UI Semibold");
    private static readonly Dictionary<string, SolidColorBrush> BrushCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly SceneWorld _world;
    private readonly BattleRuntime _battle;
    private WriteableBitmap? _territoryBitmap;
    private int _territoryRenderedVersion = -1;

    public ArenaView(SceneWorld world, BattleRuntime battle)
    {
        _world = world;
        _battle = battle;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        _world.Changed += () => Dispatcher.BeginInvoke(InvalidateVisual);
        _battle.EventRaised += _ => Dispatcher.BeginInvoke(InvalidateVisual);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(RenderSize);
        dc.DrawRectangle(BackgroundBrush, null, bounds);

        // v2.10:世界坐标 → 视口等比缩放,小窗口不再裁剪战场
        var worldW = Math.Max(1, _world.WorldWidth);
        var worldH = Math.Max(1, _world.WorldHeight);
        var scale = Math.Min(bounds.Width / worldW, bounds.Height / worldH);
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
            return;
        dc.PushTransform(new TranslateTransform(
            (bounds.Width - worldW * scale) / 2,
            (bounds.Height - worldH * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));

        if (_battle.TerritoryMode)
            DrawTerritory(dc);

        var cx = worldW / 2;
        var cy = worldH / 2;
        dc.DrawLine(DividerPen, new Point(cx, 0), new Point(cx, worldH));
        dc.DrawLine(DividerPen, new Point(0, cy), new Point(worldW, cy));

        DrawProjectiles(dc);
        DrawTurrets(dc);
        DrawHitMarkers(dc);

        dc.Pop();
        dc.Pop();
    }

    /// <summary>v2.9 TR-01:领地位图 — 每格一像素,近邻放大成像素风;仅在领地版本变化时重写。</summary>
    private void DrawTerritory(DrawingContext dc)
    {
        var cols = _battle.TerritoryCols;
        var rows = _battle.TerritoryRows;
        if (cols <= 0 || rows <= 0)
            return;

        if (_territoryBitmap == null
            || _territoryBitmap.PixelWidth != cols
            || _territoryBitmap.PixelHeight != rows)
        {
            _territoryBitmap = new WriteableBitmap(cols, rows, 96, 96, PixelFormats.Bgra32, null);
            _territoryRenderedVersion = -1;
        }

        if (_territoryRenderedVersion != _battle.TerritoryVersion)
        {
            var owners = _battle.TerritoryOwners;
            var ids = _battle.TerritoryFactionIds;
            var palette = new int[ids.Count];
            for (var i = 0; i < ids.Count; i++)
            {
                var turret = _battle.Turrets.FirstOrDefault(t =>
                    t.Id.Equals(ids[i], StringComparison.OrdinalIgnoreCase));
                var color = SceneWorld.ParseColor(turret?.Color ?? "#334155", Colors.SlateGray);
                palette[i] = (255 << 24)
                    | ((int)(color.R * 0.30) << 16)
                    | ((int)(color.G * 0.30) << 8)
                    | (int)(color.B * 0.30);
            }
            const int neutral = unchecked((int)0xFF05_0608);

            var pixels = new int[owners.Length];
            for (var i = 0; i < owners.Length; i++)
            {
                var owner = owners[i];
                pixels[i] = owner >= 0 && owner < palette.Length ? palette[owner] : neutral;
            }
            _territoryBitmap.WritePixels(new Int32Rect(0, 0, cols, rows), pixels, cols * 4, 0);
            _territoryRenderedVersion = _battle.TerritoryVersion;
        }

        dc.DrawImage(
            _territoryBitmap,
            new Rect(0, 0, cols * _battle.TerritoryCellSize, rows * _battle.TerritoryCellSize));
    }

    private void DrawProjectiles(DrawingContext dc)
    {
        foreach (var ball in _world.Balls)
        {
            var brush = GetBrush(ball.Color);
            var radius = Math.Clamp(ball.Size, 2, 60);
            var color = SceneWorld.ParseColor(ball.Color, Colors.White);

            // 速度反方向渐隐拖尾(纯渲染,无历史缓存)
            var speedSq = ball.Vx * ball.Vx + ball.Vy * ball.Vy;
            if (speedSq > 100)
            {
                var trailPen = new Pen(
                    FrozenBrush(Color.FromArgb(70, color.R, color.G, color.B)),
                    Math.Max(1.5, radius * 0.9))
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                };
                trailPen.Freeze();
                dc.DrawLine(
                    trailPen,
                    new Point(ball.X - ball.Vx * 0.08, ball.Y - ball.Vy * 0.08),
                    new Point(ball.X, ball.Y));
            }

            dc.DrawEllipse(
                FrozenBrush(Color.FromArgb(60, color.R, color.G, color.B)),
                null,
                new Point(ball.X, ball.Y),
                radius * 1.6,
                radius * 1.6);
            dc.DrawEllipse(brush, null, new Point(ball.X, ball.Y), radius, radius);

            // v2.11 BV-01:大球标剩余占领数,随啃格递减
            var captures = ball.Projectile?.CapturesLeft ?? 0;
            if (captures > 1 && radius >= 8)
            {
                var text = new FormattedText(
                    FormatNumber(captures),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    LabelTypeface,
                    Math.Clamp(radius * 0.8, 8, 22),
                    FrozenBrush(Color.FromArgb(235, 10, 12, 16)),
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(text, new Point(ball.X - text.Width / 2, ball.Y - text.Height / 2));
            }
        }
    }

    private void DrawTurrets(DrawingContext dc)
    {
        foreach (var turret in _battle.Turrets)
        {
            if (!turret.Alive)
                continue;

            var color = SceneWorld.ParseColor(turret.Color, Colors.SlateGray);
            var center = new Point(turret.TurretX, turret.TurretY);
            var r = turret.TurretRadius;

            // 多层光晕
            dc.DrawEllipse(FrozenBrush(Color.FromArgb(28, color.R, color.G, color.B)), null, center, r * 2.8, r * 2.8);
            dc.DrawEllipse(FrozenBrush(Color.FromArgb(55, color.R, color.G, color.B)), null, center, r * 1.9, r * 1.9);

            // 粒子喷流:像素方块沿炮管方向向外漂移渐隐(纯渲染,稳定哈希+对战时间推导)
            var angle = turret.BarrelAngleDeg * Math.PI / 180;
            var dirX = Math.Cos(angle);
            var dirY = Math.Sin(angle);
            var elapsed = _battle.ElapsedSeconds;
            for (var i = 0; i < 18; i++)
            {
                var r1 = StableRand(turret.Id, i, 1);
                var r2 = StableRand(turret.Id, i, 2);
                var r3 = StableRand(turret.Id, i, 3);
                var progress = (elapsed * (0.25 + 0.45 * r1) + r2) % 1.0;
                var dist = r * (0.7 + progress * 2.4);
                var lateral = (r3 - 0.5) * r * (0.5 + progress * 1.1);
                var px = center.X + dirX * dist - dirY * lateral;
                var py = center.Y + dirY * dist + dirX * lateral;
                var alpha = (byte)(190 * (1 - progress));
                var pixel = 2.0 + 3.0 * r1;
                dc.DrawRectangle(
                    FrozenBrush(Color.FromArgb(alpha, Lighten(color.R), Lighten(color.G), Lighten(color.B))),
                    null,
                    new Rect(px - pixel / 2, py - pixel / 2, pixel, pixel));
            }

            // 匀速旋转炮管:圆头粗线 + 炮口亮点
            var muzzle = new Point(
                center.X + dirX * r * 1.8,
                center.Y + dirY * r * 1.8);
            var barrelPen = new Pen(
                FrozenBrush(Color.FromArgb(235, Lighten(color.R), Lighten(color.G), Lighten(color.B))),
                Math.Max(4, r * 0.34))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            barrelPen.Freeze();
            dc.DrawLine(barrelPen, center, muzzle);
            dc.DrawEllipse(FrozenBrush(Colors.White), null, muzzle, Math.Max(2.5, r * 0.14), Math.Max(2.5, r * 0.14));

            dc.DrawEllipse(
                FrozenBrush(Color.FromArgb(230, color.R, color.G, color.B)),
                null,
                center,
                r,
                r);

            // v2.12.3 NB-01/02:外圈=护盾,内圈=本方弹药占四方总弹药比例(血量概念已取消)
            var shieldRatio = turret.MaxShield <= 0 ? 0 : Math.Clamp(turret.Shield / turret.MaxShield, 0, 1);
            var totalAmmo = _battle.Turrets.Where(t => t.Alive).Sum(t => _battle.AmmoTotalOf(t));
            var ammoRatio = totalAmmo <= 0 ? 0 : Math.Clamp(_battle.AmmoTotalOf(turret) / (double)totalAmmo, 0, 1);
            DrawRingGauge(
                dc,
                center,
                r * 1.55,
                Math.Max(2.5, r * 0.13),
                shieldRatio,
                Color.FromArgb(235, Lighten(color.R), Lighten(color.G), Lighten(color.B)));
            DrawRingGauge(
                dc,
                center,
                r * 1.22,
                Math.Max(3, r * 0.16),
                ammoRatio,
                Color.FromArgb(255, LightenStrong(color.R), LightenStrong(color.G), LightenStrong(color.B)));

            // v2.10 TU-04:圆心直接标数值(领地格数),不再画名字卡片
            var valueText = new FormattedText(
                FormatNumber((long)turret.Hp),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                Math.Max(9, r * 0.55),
                FrozenBrush(Colors.White),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(
                valueText,
                new Point(center.X - valueText.Width / 2, center.Y - valueText.Height / 2));

            // v2.12.3 NB-04:弹药数量 — 小球池数值 · 大球队列数值和(打出即减)
            var ammoText = new FormattedText(
                $"{FormatNumber(turret.SmallAmmo)}·{FormatNumber(_battle.AmmoTotalOf(turret) - turret.SmallAmmo)}",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                10,
                FrozenBrush(Color.FromArgb(225, Lighten(color.R), Lighten(color.G), Lighten(color.B))),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(
                ammoText,
                new Point(center.X - ammoText.Width / 2, center.Y + r * 1.75 + 4));
        }
    }

    /// <summary>环形仪表:暗轨 + 顶端起顺时针占比弧。</summary>
    private static void DrawRingGauge(
        DrawingContext dc,
        Point center,
        double radius,
        double thickness,
        double fraction,
        Color color)
    {
        var trackPen = new Pen(FrozenBrush(Color.FromArgb(46, color.R, color.G, color.B)), thickness);
        trackPen.Freeze();
        dc.DrawEllipse(null, trackPen, center, radius, radius);

        fraction = Math.Clamp(fraction, 0, 1);
        if (fraction <= 0.001)
            return;

        var pen = new Pen(FrozenBrush(color), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();

        if (fraction >= 0.999)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var start = -Math.PI / 2;
        var sweep = fraction * Math.PI * 2;
        var from = new Point(center.X + radius * Math.Cos(start), center.Y + radius * Math.Sin(start));
        var to = new Point(
            center.X + radius * Math.Cos(start + sweep),
            center.Y + radius * Math.Sin(start + sweep));
        var geometry = new StreamGeometry();
        using (var gctx = geometry.Open())
        {
            gctx.BeginFigure(from, false, false);
            gctx.ArcTo(to, new Size(radius, radius), 0, sweep > Math.PI, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static byte Lighten(byte channel) => (byte)Math.Min(255, channel + 60);

    private static byte LightenStrong(byte channel) => (byte)Math.Min(255, channel + 120);

    /// <summary>稳定伪随机 [0,1):FNV-1a 哈希,跨进程一致,仅供渲染。</summary>
    private static double StableRand(string id, int index, int salt)
    {
        var hash = 2166136261u;
        foreach (var ch in id)
            hash = (hash ^ ch) * 16777619u;
        hash = (hash ^ (uint)index) * 16777619u;
        hash = (hash ^ (uint)salt) * 16777619u;
        return (hash & 0xFFFFFF) / (double)0x1000000;
    }

    private void DrawHitMarkers(DrawingContext dc)
    {
        var elapsed = _battle.ElapsedSeconds;
        foreach (var marker in _battle.HitMarkers)
        {
            var age = elapsed - marker.Time;
            if (age is < 0 or > 1.2)
                continue;
            var fade = 1 - age / 1.2;
            var alpha = (byte)(240 * fade);
            var y = marker.Y - 40 - age * 36;
            DrawLabel(
                dc,
                $"-{FormatNumber((long)marker.Damage)}",
                new Point(marker.X - 16, y),
                13,
                Color.FromArgb(alpha, 255, 235, 160));
        }
    }

    private static string FormatNumber(long value)
    {
        var magnitude = Math.Abs((double)value);
        return magnitude switch
        {
            >= 1_000_000_000_000 => $"{value / 1_000_000_000_000d:0.#}T",
            >= 1_000_000_000 => $"{value / 1_000_000_000d:0.#}G",
            >= 1_000_000 => $"{value / 1_000_000d:0.#}M",
            >= 1_000 => $"{value / 1_000d:0.#}K",
            _ => value.ToString(CultureInfo.InvariantCulture),
        };
    }

    private void DrawLabel(DrawingContext dc, string text, Point origin, double size, Color color)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            size,
            FrozenBrush(color),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, origin);
    }

    private static SolidColorBrush GetBrush(string color)
    {
        if (BrushCache.TryGetValue(color, out var brush))
            return brush;
        brush = FrozenBrush(SceneWorld.ParseColor(color, Colors.White));
        BrushCache[color] = brush;
        return brush;
    }

    private static SolidColorBrush FrozenBrush(string color) =>
        FrozenBrush((Color)ColorConverter.ConvertFromString(color));

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
