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
    private readonly StageState _stage;
    private readonly SceneWorld _economyWorld;
    private readonly BattleDirector _director;
    private readonly WeaponCatalog _weapons;

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
        _stage.Changed += InvalidateVisual;
        _economyWorld.Changed += () => Dispatcher.BeginInvoke(InvalidateVisual);
        _director.EventRaised += _ => Dispatcher.BeginInvoke(InvalidateVisual);
        _director.StateChanged += () => Dispatcher.BeginInvoke(InvalidateVisual);
    }

    public string Watermark { get; set; } = "";

    protected override void OnRender(DrawingContext dc)
    {
        if (!_stage.HudVisible)
            return;

        // v2.9 HD-01:紧凑顶带;HD-02:武器条已删,解锁信息并入左上角文字
        const double topBand = 46;
        dc.DrawRectangle(BandBrush, BorderPen, new Rect(0, 0, RenderSize.Width, topBand));

        var shotgun = _weapons.Weapons.FirstOrDefault(w =>
            w.Name.Contains("散弹", StringComparison.OrdinalIgnoreCase)
            || w.Aliases.Any(a => a.Contains("Shotgun", StringComparison.OrdinalIgnoreCase)));
        var unlockAt = shotgun?.UnlockAtSeconds ?? 1200;
        DrawText(
            dc,
            $"本局 {_director.ElapsedSeconds:0}s · 散弹解锁 {unlockAt:0}s",
            12,
            8,
            11,
            MutedBrush);
        DrawText(
            dc,
            $"{_stage.Mode.ToString().ToUpperInvariant()}  {_director.State}",
            12,
            26,
            10,
            LockedBrush);

        var factions = _economyWorld.Factions
            .Where(x => !x.Id.Equals(FactionBoard.UnassignedId, StringComparison.OrdinalIgnoreCase))
            .Take(6)
            .ToList();
        // v2.12.3 NB-05:盒宽按领地占比动态分配,最小宽度保文字
        var left = 220.0;
        var available = Math.Max(180, RenderSize.Width - left - 12);
        var gap = 6.0;
        const double minWidth = 90;
        var totalCells = factions.Sum(f => Math.Max(0.0, f.Hp));
        var flexible = Math.Max(0, available - gap * Math.Max(0, factions.Count - 1) - factions.Count * minWidth);
        var x = left;
        foreach (var faction in factions)
        {
            var share = totalCells <= 0
                ? 1.0 / Math.Max(1, factions.Count)
                : Math.Max(0, faction.Hp) / totalCells;
            var width = minWidth + flexible * share;
            DrawFactionBox(dc, faction, new Rect(x, 7, width, 32));
            x += width + gap;
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
        var ammo = faction.SmallAmmo + faction.Ammo.Sum(shell => shell.Value);
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

    private void DrawText(DrawingContext dc, string text, double x, double y, double size, Brush brush)
    {
        dc.DrawText(MakeText(text, size, brush), new Point(x, y));
    }

    private FormattedText MakeText(string text, double size, Brush brush) =>
        new(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            TitleTypeface,
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

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
