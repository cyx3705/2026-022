using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WBall.Battle;
using WBall.Game;
using WBall.Model;

namespace WBall.Stage;

/// <summary>合成舞台 HUD：计分盒、计时、解封、炮台卡片、攻击类型条。</summary>
public sealed class StageHudView : FrameworkElement
{
    private static readonly Brush BandBrush = FrozenBrush(Color.FromArgb(230, 11, 14, 19));
    private static readonly Brush CardBrush = FrozenBrush(Color.FromArgb(200, 17, 22, 30));
    private static readonly Brush MutedBrush = FrozenBrush(Color.FromRgb(0xA9, 0xB4, 0xC3));
    private static readonly Brush LockedBrush = FrozenBrush(Color.FromRgb(0x64, 0x74, 0x8B));
    private static readonly Pen BorderPen = FrozenPen(FrozenBrush(Color.FromRgb(0x3A, 0x43, 0x50)), 1);
    private static readonly Typeface TitleTypeface = new("Segoe UI Semibold");
    [ThreadStatic]
    private static Dictionary<(string Text, int Size10, string Brush, int Dpi100), FormattedText>? _textCache;
    [ThreadStatic]
    private static Dictionary<string, SolidColorBrush>? _colorBrushCache;
    [ThreadStatic]
    private static Dictionary<(string Brush, int Thickness10), Pen>? _penCache;
    private readonly StageState _stage;
    private readonly SceneWorld _economyWorld;
    private readonly BattleDirector _director;
    private readonly WeaponCatalog _weapons;
    private readonly FrameInvalidationGate _invalidation;
    private SemaphoreSlim? _runtimeGate;
    private RealtimeFrameSnapshot? _frame;
    private DrawingGroup? _cachedFrameDrawing;
    private long _cachedFrameBucket = -1;
    private double _cachedFrameWidth;
    private double _cachedFrameHeight;
    private string? _cachedWatermark;

    public StageHudView(
        StageState stage,
        SceneWorld economyWorld,
        BattleDirector director,
        WeaponCatalog weapons)
    {
        _stage = stage;
        _economyWorld = economyWorld;
        _director = director;
        _weapons = weapons;
        IsHitTestVisible = false;
        _invalidation = new FrameInvalidationGate(this);
        _stage.Changed += _invalidation.Request;
        _economyWorld.Changed += () =>
        {
            if (_frame == null)
                _invalidation.Request();
        };
        _director.EventRaised += _ =>
        {
            if (_frame == null)
                _invalidation.Request();
        };
        _director.StateChanged += () =>
        {
            if (_frame == null)
                _invalidation.Request();
        };
    }

    public string Watermark { get; set; } = "";
    public long RenderAllocatedBytes { get; private set; }
    public long RenderCount { get; private set; }

    public void AttachRuntimeGate(SemaphoreSlim gate) => _runtimeGate = gate;

    public void SetRealtimeFrame(RealtimeFrameSnapshot frame)
    {
        _frame = frame;
        InvalidateVisual();
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
        if (!_stage.HudVisible)
            return;
        var frame = _frame;
        if (frame != null)
        {
            var totalBalls = frame.EconomyBallCount + frame.BattleBallCount;
            var divisor = totalBalls >= VisualLodController.MinimalThreshold
                ? 6L
                : totalBalls >= VisualLodController.SimplifiedThreshold ? 3L : 1L;
            var bucket = frame.Sequence / divisor;
            if (_cachedFrameDrawing == null
                || _cachedFrameBucket != bucket
                || Math.Abs(_cachedFrameWidth - RenderSize.Width) > 1e-6
                || Math.Abs(_cachedFrameHeight - RenderSize.Height) > 1e-6
                || !string.Equals(_cachedWatermark, Watermark, StringComparison.Ordinal))
            {
                var drawing = new DrawingGroup();
                using (var drawingDc = drawing.Open())
                    DrawCore(drawingDc, frame);
                drawing.Freeze();
                _cachedFrameDrawing = drawing;
                _cachedFrameBucket = bucket;
                _cachedFrameWidth = RenderSize.Width;
                _cachedFrameHeight = RenderSize.Height;
                _cachedWatermark = Watermark;
            }
            dc.DrawDrawing(_cachedFrameDrawing);
            return;
        }
        DrawCore(dc, null);
    }

    private void DrawCore(DrawingContext dc, RealtimeFrameSnapshot? frame)
    {

        // v2.9 HD-01:紧凑顶带;HD-02:武器条已删,解锁信息并入左上角文字
        const double topBand = 46;
        dc.DrawRectangle(BandBrush, BorderPen, new Rect(0, 0, RenderSize.Width, topBand));

        var shotgun = _weapons.Weapons.FirstOrDefault(w =>
            w.Name.Contains("散弹", StringComparison.OrdinalIgnoreCase)
            || w.Aliases.Any(a => a.Contains("Shotgun", StringComparison.OrdinalIgnoreCase)));
        var unlockAt = shotgun?.UnlockAtSeconds ?? 1200;
        DrawText(
            dc,
            $"本局 {frame?.DirectorElapsedSeconds ?? _director.ElapsedSeconds:0}s · 散弹解锁 {unlockAt:0}s",
            12,
            8,
            11,
            MutedBrush);
        DrawText(
            dc,
            $"{(frame?.StageMode ?? _stage.Mode).ToString().ToUpperInvariant()}  {frame?.DirectorState ?? _director.State}",
            12,
            26,
            10,
            LockedBrush);

        var factionCount = frame == null
            ? Math.Min(6, _economyWorld.Factions.Count(x =>
                !x.Id.Equals(FactionBoard.UnassignedId, StringComparison.OrdinalIgnoreCase)))
            : CountVisibleFactions(frame);
        // v2.12.3 NB-05:盒宽按领地占比动态分配,最小宽度保文字
        var left = 220.0;
        var available = Math.Max(180, RenderSize.Width - left - 12);
        var gap = 6.0;
        const double minWidth = 90;
        var totalCells = frame == null
            ? _economyWorld.Factions.Where(x =>
                    !x.Id.Equals(FactionBoard.UnassignedId, StringComparison.OrdinalIgnoreCase))
                .Take(6).Sum(f => Math.Max(0.0, f.Hp))
            : SumFactionHp(frame);
        var flexible = Math.Max(0, available - gap * Math.Max(0, factionCount - 1) - factionCount * minWidth);
        var x = left;
        if (frame == null)
        {
            foreach (var faction in _economyWorld.Factions
                         .Where(item => !item.Id.Equals(FactionBoard.UnassignedId, StringComparison.OrdinalIgnoreCase))
                         .Take(6))
            {
                var share = totalCells <= 0 ? 1.0 / Math.Max(1, factionCount) : Math.Max(0, faction.Hp) / totalCells;
                var width = minWidth + flexible * share;
                DrawFactionBox(dc, faction, new Rect(x, 7, width, 32));
                x += width + gap;
            }
        }
        else
        {
            for (var index = 0; index < frame.FactionCount; index++)
            {
                var faction = frame.Factions[index];
                if (faction.Id.Equals(FactionBoard.UnassignedId, StringComparison.OrdinalIgnoreCase))
                    continue;
                var share = totalCells <= 0 ? 1.0 / Math.Max(1, factionCount) : Math.Max(0, faction.Hp) / totalCells;
                var width = minWidth + flexible * share;
                DrawFactionBox(dc, faction, new Rect(x, 7, width, 32));
                x += width + gap;
                if (--factionCount == 0)
                    break;
            }
        }

        // 炮台卡片改由 Arena 绘制,HUD 不再重复大卡片
        if (!string.IsNullOrWhiteSpace(Watermark))
        {
            DrawText(
                dc,
                Watermark,
                Math.Max(12, RenderSize.Width - 220),
                Math.Max(12, RenderSize.Height - 28),
                12,
                MutedBrush);
        }
    }

    private void DrawFactionBox(DrawingContext dc, Faction faction, Rect rect)
    {
        // v2.9:单行紧凑条 — 色条 + 名 + PTS + 领地(HP)
        var color = ParseColor(faction.Color);
        var fill = FrozenBrush(Color.FromArgb(faction.Alive ? (byte)56 : (byte)28, color.R, color.G, color.B));
        var line = FrozenPen(FrozenBrush(color), 1.5);
        dc.DrawRoundedRectangle(fill, line, rect, 3, 3);
        dc.DrawRectangle(FrozenBrush(color), null, new Rect(rect.X, rect.Y, 4, rect.Height));
        DrawText(dc, faction.Name, rect.X + 10, rect.Y + 2, 11, Brushes.White);
        var ammo = faction.SmallAmmo + faction.QueuedAmmoValue;
        DrawText(
            dc,
            faction.Alive
                ? $"PTS {FormatNumber(faction.Points)} · 领 {FormatNumber((long)faction.Hp)} · 弹 {FormatNumber(ammo)}"
                : "DEAD",
            rect.X + 10,
            rect.Y + 17,
            10,
            MutedBrush);
    }

    private void DrawFactionBox(DrawingContext dc, RealtimeFactionFrame faction, Rect rect)
    {
        var color = ParseColor(faction.Color);
        var fill = FrozenBrush(Color.FromArgb(faction.Alive ? (byte)56 : (byte)28, color.R, color.G, color.B));
        var line = FrozenPen(FrozenBrush(color), 1.5);
        dc.DrawRoundedRectangle(fill, line, rect, 3, 3);
        dc.DrawRectangle(FrozenBrush(color), null, new Rect(rect.X, rect.Y, 4, rect.Height));
        DrawText(dc, faction.Name, rect.X + 10, rect.Y + 2, 11, Brushes.White);
        DrawText(
            dc,
            faction.Alive
                ? $"PTS {FormatNumber(faction.Points)} · 领 {FormatNumber((long)faction.Hp)} · 弹 {FormatNumber(faction.AmmoTotal)}"
                : "DEAD",
            rect.X + 10, rect.Y + 17, 10, MutedBrush);
    }

    private static int CountVisibleFactions(RealtimeFrameSnapshot frame)
    {
        var count = 0;
        for (var i = 0; i < frame.FactionCount && count < 6; i++)
        {
            if (!frame.Factions[i].Id.Equals(FactionBoard.UnassignedId, StringComparison.OrdinalIgnoreCase))
                count++;
        }
        return count;
    }

    private static double SumFactionHp(RealtimeFrameSnapshot frame)
    {
        var total = 0d;
        var count = 0;
        for (var i = 0; i < frame.FactionCount && count < 6; i++)
        {
            var faction = frame.Factions[i];
            if (faction.Id.Equals(FactionBoard.UnassignedId, StringComparison.OrdinalIgnoreCase))
                continue;
            total += Math.Max(0, faction.Hp);
            count++;
        }
        return total;
    }

    private void DrawText(DrawingContext dc, string text, double x, double y, double size, Brush brush)
    {
        dc.DrawText(MakeText(text, size, brush), new Point(x, y));
    }

    private FormattedText MakeText(string text, double size, Brush brush)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var cache = _textCache ??= new();
        var key = (text, (int)Math.Round(size * 10), brush.ToString(), (int)Math.Round(dpi * 100));
        if (cache.TryGetValue(key, out var formatted))
            return formatted;
        formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            TitleTypeface,
            size,
            brush,
            dpi);
        if (cache.Count >= 1024)
            cache.Clear();
        cache[key] = formatted;
        return formatted;
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

    private static Color ParseColor(string? color)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(FactionBoard.NormalizeColor(color));
        }
        catch
        {
            return Color.FromRgb(0x94, 0xA3, 0xB8);
        }
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var key = color.ToString();
        var cache = _colorBrushCache ??= new();
        if (cache.TryGetValue(key, out var cached))
            return cached;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        if (cache.Count >= 256)
            cache.Clear();
        cache[key] = brush;
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var cache = _penCache ??= new();
        var key = (brush.ToString(), (int)Math.Round(thickness * 10));
        if (cache.TryGetValue(key, out var cached))
            return cached;
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        if (cache.Count >= 256)
            cache.Clear();
        cache[key] = pen;
        return pen;
    }
}
