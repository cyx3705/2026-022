using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WBall.Battle;
using WBall.Model;

namespace WBall.Recording;

/// <summary>专用 STA 离屏合成器，只消费不可变帧投影。</summary>
internal sealed class StageFrameRenderer
{
    private static readonly Typeface UiTypeface = new("Segoe UI");
    private static readonly Typeface StrongTypeface = new("Segoe UI Semibold");

    private readonly RenderStaticData _static;
    private readonly int _width;
    private readonly int _height;
    private readonly Dictionary<string, SolidColorBrush> _brushes = new(StringComparer.OrdinalIgnoreCase);
    private int[] _territory = [];
    private int _territoryVersion = -1;

    public StageFrameRenderer(RenderStaticData staticData, int width, int height)
    {
        _static = staticData;
        _width = width;
        _height = height;
    }

    public byte[] Render(RenderFrameData frame)
    {
        if (frame.TerritoryOwners != null && frame.TerritoryVersion != _territoryVersion)
        {
            _territory = frame.TerritoryOwners.Value.ToArray();
            _territoryVersion = frame.TerritoryVersion;
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brush(_static.Stage.Background), null, new Rect(0, 0, _width, _height));
            var content = new Rect(0, 0, _width, _height);
            if (!_static.Stage.CompositeVisible)
            {
                DrawEconomy(dc, content, frame);
            }
            else if (_static.Stage.Orientation == Stage.StageOrientation.Horizontal)
            {
                var split = _width * _static.Stage.Split;
                DrawEconomy(dc, new Rect(0, 0, split, _height), frame);
                DrawArena(dc, new Rect(split, 0, _width - split, _height), frame);
            }
            else
            {
                var split = _height * _static.Stage.Split;
                DrawEconomy(dc, new Rect(0, 0, _width, split), frame);
                DrawArena(dc, new Rect(0, split, _width, _height - split), frame);
            }

            if (_static.Stage.HudVisible)
                DrawHud(dc, frame);
            if (frame.Victory is not null)
                DrawVictory(dc, frame, frame.Victory);
        }

        var bitmap = new RenderTargetBitmap(_width, _height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[_width * _height * 4];
        bitmap.CopyPixels(pixels, _width * 4, 0);
        return pixels;
    }

    private void DrawEconomy(DrawingContext dc, Rect host, RenderFrameData frame)
    {
        dc.DrawRectangle(Brush("#2D3136"), null, host);
        var scene = _static.EconomyScene;
        var world = Fit(host, scene.WorldWidth, scene.WorldHeight);
        dc.PushClip(new RectangleGeometry(host));
        dc.DrawRectangle(Brush("#171A1F"), Pen("#48515E", 1), world);

        foreach (var solid in scene.Solids)
        {
            if (solid.Points.Count < 3)
                continue;
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(Map(world, scene.WorldWidth, scene.WorldHeight, solid.Points[0].X, solid.Points[0].Y), true, true);
                context.PolyLineTo(solid.Points.Skip(1)
                    .Select(x => Map(world, scene.WorldWidth, scene.WorldHeight, x.X, x.Y)).ToList(), true, true);
            }
            geometry.Freeze();
            dc.DrawGeometry(Brush(solid.Color), Pen("#D4DAE3", 0.8), geometry);
        }

        foreach (var obj in scene.Objects)
        {
            var rect = MapRect(world, scene.WorldWidth, scene.WorldHeight, obj.X, obj.Y, obj.W, obj.H);
            var type = obj.Type.Trim().ToLowerInvariant();
            var fill = type switch
            {
                "spawner" => "#203C2C",
                "despawner" => "#42262B",
                "arrow" => "#24384A",
                _ => "#3B424C",
            };
            dc.DrawRectangle(Brush(fill), Pen("#7D8999", 0.8), rect);
            if (type == "arrow")
            {
                var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
                var scale = Math.Min(rect.Width, rect.Height) * 0.38;
                dc.DrawLine(Pen("#67B7E8", 2), center,
                    new Point(center.X + obj.DirX * scale, center.Y + obj.DirY * scale));
            }
        }

        foreach (var wire in scene.Wireframes)
        {
            if (wire.Points.Count < 2)
                continue;
            var points = wire.Points.Select(x => Map(world, scene.WorldWidth, scene.WorldHeight, x.X, x.Y)).ToArray();
            for (var index = 1; index < points.Length; index++)
                dc.DrawLine(Pen("#8D99A8", 1), points[index - 1], points[index]);
            if (wire.Closed)
                dc.DrawLine(Pen("#8D99A8", 1), points[^1], points[0]);
        }

        foreach (var ball in frame.EconomyBalls)
        {
            var center = Map(world, scene.WorldWidth, scene.WorldHeight, ball.X, ball.Y);
            var radius = ScaleRadius(world, scene.WorldWidth, scene.WorldHeight, ball.Size);
            dc.DrawEllipse(Brush(ball.Color), Pen("#DCE4EE", 0.6), center, radius, radius);
            if (ball.Multiplier > 1 && radius >= 6)
                DrawCenteredText(dc, PublicDefaults.FormatMultiplier(ball.Multiplier), center, Math.Clamp(radius * 0.8, 7, 18), Brushes.White);
        }
        dc.Pop();
    }

    private void DrawArena(DrawingContext dc, Rect host, RenderFrameData frame)
    {
        dc.DrawRectangle(Brush("#050608"), null, host);
        var world = Fit(host, _static.ArenaWidth, _static.ArenaHeight);
        dc.PushClip(new RectangleGeometry(host));
        dc.DrawRectangle(Brush("#080A0D"), Pen("#3C4654", 1), world);

        if (_territory.Length == frame.TerritoryCols * frame.TerritoryRows
            && frame.TerritoryCols > 0 && frame.TerritoryRows > 0)
        {
            var colors = frame.Turrets.ToDictionary(x => x.Id, x => x.Color, StringComparer.OrdinalIgnoreCase);
            var cellW = world.Width / frame.TerritoryCols;
            var cellH = world.Height / frame.TerritoryRows;
            for (var row = 0; row < frame.TerritoryRows; row++)
                for (var col = 0; col < frame.TerritoryCols; col++)
                {
                    var owner = _territory[row * frame.TerritoryCols + col];
                    if (owner < 0 || owner >= frame.TerritoryFactionIds.Length)
                        continue;
                    var id = frame.TerritoryFactionIds[owner];
                    if (!colors.TryGetValue(id, out var color))
                        continue;
                    dc.DrawRectangle(Brush(color, 0x32), null,
                        new Rect(world.X + col * cellW, world.Y + row * cellH, cellW + 0.35, cellH + 0.35));
                }
        }

        foreach (var projectile in frame.Projectiles)
        {
            var center = Map(world, _static.ArenaWidth, _static.ArenaHeight, projectile.X, projectile.Y);
            var radius = ScaleRadius(world, _static.ArenaWidth, _static.ArenaHeight, projectile.Size);
            dc.DrawEllipse(Brush(projectile.Color, 0x28), null, center, radius * 1.6, radius * 1.6);
            dc.DrawEllipse(Brush(projectile.Color), Pen("#E8EDF5", 0.7), center, radius, radius);
            if (projectile.Value > 0 && radius >= 4)
            {
                var font = Math.Clamp(radius * _static.LabelFontFactor, _static.LabelFontMin, _static.LabelFontMax);
                DrawCenteredText(dc, projectile.Value.ToString(CultureInfo.InvariantCulture), center, font, Brushes.White);
            }
        }


        foreach (var assist in frame.Assists)
        {
            var from = Map(world, _static.ArenaWidth, _static.ArenaHeight, assist.FromX, assist.FromY);
            var to = Map(world, _static.ArenaWidth, _static.ArenaHeight, assist.ToX, assist.ToY);
            var alpha = (byte)Math.Clamp(120 * assist.RemainingSeconds / 0.65, 18, 120);
            dc.DrawLine(Pen(assist.Color, 1.3, alpha), from, to);
            DrawText(dc, $"+{assist.Amount}", new Point(to.X + 4, to.Y - 13), 9, Brush(assist.Color), StrongTypeface);
        }

        var totalShield = frame.Turrets.Sum(turret => Math.Max(0, turret.Shield));
        foreach (var turret in frame.Turrets)
        {
            var center = Map(world, _static.ArenaWidth, _static.ArenaHeight, turret.X, turret.Y);
            var radius = ScaleRadius(world, _static.ArenaWidth, _static.ArenaHeight, turret.Radius);
            var shieldRadius = radius * _static.ShieldRingScale;
            DrawRingGauge(
                dc,
                center,
                shieldRadius,
                Math.Max(2.5, radius * 0.13),
                ArenaFormulas.ShieldShare(turret.Shield, totalShield),
                turret.Color);
            var barrelLength = radius * 1.75;
            var angle = turret.BarrelAngleDeg * Math.PI / 180;
            dc.DrawLine(Pen(turret.Color, Math.Max(2, radius * 0.22)), center,
                new Point(center.X + Math.Cos(angle) * barrelLength, center.Y + Math.Sin(angle) * barrelLength));
            dc.DrawEllipse(Brush(turret.Alive ? turret.Color : "#4B5563"), Pen("#F4F7FA", 1), center, radius, radius);
            DrawCenteredText(
                dc,
                Math.Max(
                        0,
                        Math.Floor(turret.Shield / Math.Max(1e-12, _static.ShieldCostPerValue) + 1e-12))
                    .ToString("0", CultureInfo.InvariantCulture),
                center,
                Math.Max(9, radius * 0.55),
                Brushes.White);
            DrawCenteredText(dc, turret.Name, new Point(center.X, center.Y - radius - 10), 10, Brushes.White);
        }
        dc.Pop();
    }

    private void DrawRingGauge(
        DrawingContext dc,
        Point center,
        double radius,
        double thickness,
        double fraction,
        string color)
    {
        dc.DrawEllipse(null, Pen(color, thickness, 0x2E), center, radius, radius);
        fraction = Math.Clamp(fraction, 0, 1);
        if (fraction <= 0.001)
            return;

        var pen = new Pen(Brush(color, 0xEB), thickness)
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
        using (var context = geometry.Open())
        {
            context.BeginFigure(from, false, false);
            context.ArcTo(
                to,
                new Size(radius, radius),
                0,
                sweep > Math.PI,
                SweepDirection.Clockwise,
                true,
                false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private void DrawHud(DrawingContext dc, RenderFrameData frame)
    {
        var bandHeight = Math.Clamp(_height * 0.075, 34, 58);
        dc.DrawRectangle(Brush("#E60B0E13"), null, new Rect(0, 0, _width, bandHeight));
        var summary = $"OUT {frame.OutputTime:0.00}s   SIM {frame.SimulationTime:0.00}s   "
                      + $"BALLS {frame.BallCount}   SCALE {frame.SimulationScale:0.00}x   {frame.DirectorState.ToUpperInvariant()}";
        DrawText(dc, summary, new Point(14, 9), Math.Clamp(bandHeight * 0.34, 11, 17), Brushes.White, StrongTypeface);
        if (!string.IsNullOrWhiteSpace(frame.WinnerId))
            DrawText(dc, $"WINNER {frame.WinnerId}", new Point(_width - 190, 9), 14, Brushes.Gold, StrongTypeface);

        var x = 14d;
        var y = bandHeight + 7;
        foreach (var turret in frame.Turrets)
        {
            var text = $"{turret.Name}  HP {turret.Hp:0}/{turret.MaxHp:0}  SH {turret.Shield:0}/∞";
            var ft = Text(text, 10, Brush("#DDE5EF"), UiTypeface);
            var width = ft.Width + 18;
            dc.DrawRoundedRectangle(Brush("#C811161E"), Pen(turret.Color, 1.2), new Rect(x, y, width, 23), 3, 3);
            dc.DrawText(ft, new Point(x + 9, y + 5));
            x += width + 6;
            if (x > _width - 160)
                break;
        }
    }

    private void DrawVictory(DrawingContext dc, RenderFrameData frame, VictoryAnimationState victory)
    {
        var seconds = victory.Progress * RenderJobService.VictoryAnimationSeconds;
        var dimProgress = Math.Clamp(seconds / 0.5, 0, 1);
        dc.DrawRectangle(Brush("#000000", (byte)Math.Round(145 * dimProgress)), null, new Rect(0, 0, _width, _height));

        var arenaHost = ArenaHost();
        var arenaWorld = Fit(arenaHost, _static.ArenaWidth, _static.ArenaHeight);
        var highlightProgress = Math.Clamp((seconds - 0.5) / 1.0, 0, 1);
        if (highlightProgress > 0)
        {
            var pulse = 0.72 + 0.28 * Math.Sin(highlightProgress * Math.PI);
            var alpha = (byte)Math.Clamp(110 + 145 * pulse, 0, 255);
            dc.DrawRoundedRectangle(
                null,
                Pen(victory.WinnerColor, Math.Max(3, Math.Min(_width, _height) * 0.006), alpha),
                arenaWorld,
                8,
                8);

            var turret = frame.Turrets.FirstOrDefault(x =>
                x.Id.Equals(victory.WinnerId, StringComparison.OrdinalIgnoreCase));
            if (turret is not null)
            {
                var center = Map(arenaWorld, _static.ArenaWidth, _static.ArenaHeight, turret.X, turret.Y);
                var radius = ScaleRadius(arenaWorld, _static.ArenaWidth, _static.ArenaHeight, turret.Radius);
                var glow = radius * (1.55 + 0.25 * pulse);
                dc.DrawEllipse(Brush(victory.WinnerColor, (byte)(45 + 55 * pulse)), null, center, glow, glow);
                dc.DrawEllipse(null, Pen(victory.WinnerColor, Math.Max(3, radius * 0.16), alpha), center, radius * 1.35, radius * 1.35);
            }
        }

        var titleProgress = Math.Clamp((seconds - 1.0) / 0.45, 0, 1);
        if (titleProgress <= 0)
            return;
        var eased = 1 - Math.Pow(1 - titleProgress, 3);
        var scale = 0.92 + 0.08 * eased;
        var titleSize = Math.Clamp(Math.Min(_width, _height) * 0.092 * scale, 34, 112);
        var subtitleSize = Math.Clamp(titleSize * 0.48, 20, 54);
        var centerPoint = new Point(_width / 2d, _height / 2d);
        var title = Text(victory.WinnerName, titleSize, Brush(victory.WinnerColor), StrongTypeface);
        var subtitle = Text("胜利", subtitleSize, Brushes.White, StrongTypeface);
        var panelWidth = Math.Max(title.Width, subtitle.Width) + titleSize * 1.1;
        var panelHeight = title.Height + subtitle.Height + titleSize * 0.55;
        var panel = new Rect(
            centerPoint.X - panelWidth / 2,
            centerPoint.Y - panelHeight / 2,
            panelWidth,
            panelHeight);
        dc.DrawRoundedRectangle(Brush("#080A0D", 0xDA), Pen(victory.WinnerColor, 2.5), panel, 14, 14);
        dc.DrawText(title, new Point(centerPoint.X - title.Width / 2, panel.Y + titleSize * 0.22));
        dc.DrawText(subtitle, new Point(centerPoint.X - subtitle.Width / 2, panel.Bottom - subtitle.Height - titleSize * 0.18));
    }

    private Rect ArenaHost()
    {
        if (!_static.Stage.CompositeVisible)
            return new Rect(0, 0, _width, _height);
        if (_static.Stage.Orientation == Stage.StageOrientation.Horizontal)
        {
            var split = _width * _static.Stage.Split;
            return new Rect(split, 0, _width - split, _height);
        }
        var verticalSplit = _height * _static.Stage.Split;
        return new Rect(0, verticalSplit, _width, _height - verticalSplit);
    }

    private static Rect Fit(Rect host, double worldWidth, double worldHeight)
    {
        var scale = Math.Min(host.Width / Math.Max(1, worldWidth), host.Height / Math.Max(1, worldHeight));
        var width = worldWidth * scale;
        var height = worldHeight * scale;
        return new Rect(host.X + (host.Width - width) / 2, host.Y + (host.Height - height) / 2, width, height);
    }

    private static Point Map(Rect rect, double worldWidth, double worldHeight, double x, double y) =>
        new(rect.X + x / Math.Max(1, worldWidth) * rect.Width,
            rect.Y + y / Math.Max(1, worldHeight) * rect.Height);

    private static Rect MapRect(Rect rect, double worldWidth, double worldHeight, double x, double y, double w, double h)
    {
        var topLeft = Map(rect, worldWidth, worldHeight, x, y);
        return new Rect(topLeft.X, topLeft.Y,
            w / Math.Max(1, worldWidth) * rect.Width,
            h / Math.Max(1, worldHeight) * rect.Height);
    }

    private static double ScaleRadius(Rect rect, double worldWidth, double worldHeight, double radius) =>
        radius * Math.Min(rect.Width / Math.Max(1, worldWidth), rect.Height / Math.Max(1, worldHeight));

    private SolidColorBrush Brush(string color, byte? alpha = null)
    {
        var key = alpha is null ? color : $"{alpha:X2}:{color}";
        if (_brushes.TryGetValue(key, out var cached))
            return cached;
        var parsed = (Color)ColorConverter.ConvertFromString(NormalizeColor(color));
        if (alpha is not null)
            parsed.A = alpha.Value;
        var brush = new SolidColorBrush(parsed);
        brush.Freeze();
        _brushes[key] = brush;
        return brush;
    }

    private Pen Pen(string color, double width, byte? alpha = null)
    {
        var pen = new Pen(Brush(color, alpha), width);
        pen.Freeze();
        return pen;
    }

    private static string NormalizeColor(string color)
    {
        var value = string.IsNullOrWhiteSpace(color) ? "#FFFFFF" : color.Trim();
        return value.StartsWith('#') ? value : "#" + value;
    }

    private static FormattedText Text(string value, double size, Brush brush, Typeface typeface) => new(
        value,
        CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight,
        typeface,
        size,
        brush,
        1.0);

    private static void DrawText(
        DrawingContext dc,
        string value,
        Point point,
        double size,
        Brush brush,
        Typeface? typeface = null) =>
        dc.DrawText(Text(value, size, brush, typeface ?? UiTypeface), point);

    private static void DrawCenteredText(
        DrawingContext dc,
        string value,
        Point center,
        double size,
        Brush brush)
    {
        var text = Text(value, size, brush, StrongTypeface);
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }
}
