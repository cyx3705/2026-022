using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace WBall.Stage;

/// <summary>消费与离线组合器相同 VictoryAnimationState 的实时舞台覆盖层。</summary>
internal sealed class StageVictoryOverlay : FrameworkElement
{
    private static readonly Typeface Typeface = new("Segoe UI Semibold");
    private RealtimeFrameSnapshot? _frame;

    public StageVictoryOverlay() => IsHitTestVisible = false;

    public void SetRealtimeFrame(RealtimeFrameSnapshot frame)
    {
        _frame = frame;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var victory = _frame?.Victory;
        if (victory == null)
            return;
        var seconds = victory.Progress * Recording.RenderJobService.VictoryAnimationSeconds;
        var dim = (byte)Math.Round(145 * Math.Clamp(seconds / 0.5, 0, 1));
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(dim, 0, 0, 0)), null, new Rect(RenderSize));
        if (seconds < 1)
            return;

        var progress = Math.Clamp((seconds - 1) / 0.45, 0, 1);
        var scale = 0.92 + 0.08 * (1 - Math.Pow(1 - progress, 3));
        var titleSize = Math.Clamp(Math.Min(RenderSize.Width, RenderSize.Height) * 0.092 * scale, 34, 112);
        var titleBrush = Parse(victory.WinnerColor);
        var title = Text(victory.WinnerName, titleSize, titleBrush);
        var subtitle = Text("胜利", Math.Clamp(titleSize * 0.48, 20, 54), Brushes.White);
        var center = new Point(RenderSize.Width / 2, RenderSize.Height / 2);
        var width = Math.Max(title.Width, subtitle.Width) + titleSize * 1.1;
        var height = title.Height + subtitle.Height + titleSize * 0.55;
        var panel = new Rect(center.X - width / 2, center.Y - height / 2, width, height);
        dc.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(218, 8, 10, 13)),
            new Pen(titleBrush, 2.5), panel, 14, 14);
        dc.DrawText(title, new Point(center.X - title.Width / 2, panel.Y + titleSize * 0.22));
        dc.DrawText(subtitle, new Point(center.X - subtitle.Width / 2, panel.Bottom - subtitle.Height - titleSize * 0.18));
    }

    private FormattedText Text(string value, double size, Brush brush) => new(
        value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        Typeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static SolidColorBrush Parse(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
