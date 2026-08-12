using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WBall.Model;
using WBall.Presentation;

namespace WBall.Stage;

/// <summary>
/// 最小 LOD 的逐球位图层。仍逐个投影全部球，只把大量 WPF DrawEllipse 调用
/// 收敛成一次像素上传和一次 DrawImage。
/// </summary>
internal sealed class BallBitmapLayer
{
    private readonly Dictionary<string, int> _colors = new(StringComparer.OrdinalIgnoreCase);
    private WriteableBitmap? _bitmap;
    private int[] _pixels = [];
    private int _width;
    private int _height;
    private double _scaleX = 1;
    private double _scaleY = 1;

    public void Draw(DrawingContext dc, List<Ball> balls, double worldWidth, double worldHeight)
    {
        var width = Math.Max(1, (int)Math.Ceiling(worldWidth));
        var height = Math.Max(1, (int)Math.Ceiling(worldHeight));
        EnsureBitmap(width, height);
        _scaleX = width / Math.Max(1, worldWidth);
        _scaleY = height / Math.Max(1, worldHeight);
        Array.Clear(_pixels);
        foreach (var ball in balls)
            Rasterize(ball);
        _bitmap!.WritePixels(new Int32Rect(0, 0, _width, _height), _pixels, _width * 4, 0);
        dc.DrawImage(_bitmap, new Rect(0, 0, worldWidth, worldHeight));
    }

    public void Draw(
        DrawingContext dc,
        RealtimeBallFrame[] balls,
        int count,
        double worldWidth,
        double worldHeight,
        double rasterScale = 0.5)
    {
        rasterScale = Math.Clamp(rasterScale, 0.25, 1);
        var width = Math.Max(1, (int)Math.Ceiling(worldWidth * rasterScale));
        var height = Math.Max(1, (int)Math.Ceiling(worldHeight * rasterScale));
        EnsureBitmap(width, height);
        _scaleX = width / Math.Max(1, worldWidth);
        _scaleY = height / Math.Max(1, worldHeight);
        Array.Clear(_pixels);
        for (var i = 0; i < count; i++)
            Rasterize(balls[i]);
        _bitmap!.WritePixels(new Int32Rect(0, 0, _width, _height), _pixels, _width * 4, 0);
        dc.DrawImage(_bitmap, new Rect(0, 0, worldWidth, worldHeight));
    }

    private void EnsureBitmap(int width, int height)
    {
        if (_bitmap != null && width == _width && height == _height)
            return;
        _width = width;
        _height = height;
        _pixels = new int[checked(width * height)];
        _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
    }

    private void Rasterize(Ball ball)
        => Rasterize(ball.X, ball.Y, ball.Size, ball.Color);

    private void Rasterize(RealtimeBallFrame ball)
        => Rasterize(ball.X, ball.Y, ball.Size, ball.Color);

    private void Rasterize(double positionX, double positionY, double size, string color)
    {
        var centerX = (int)Math.Round(positionX * _scaleX);
        var centerY = (int)Math.Round(positionY * _scaleY);
        var radius = Math.Max(1, (int)Math.Ceiling(size * Math.Min(_scaleX, _scaleY)));
        var minX = Math.Max(0, centerX - radius);
        var maxX = Math.Min(_width - 1, centerX + radius);
        var minY = Math.Max(0, centerY - radius);
        var maxY = Math.Min(_height - 1, centerY + radius);
        if (minX > maxX || minY > maxY)
            return;
        var radiusSquared = radius * radius;
        var packed = PackedColor(color);
        for (var y = minY; y <= maxY; y++)
        {
            var dy = y - centerY;
            var row = y * _width;
            for (var x = minX; x <= maxX; x++)
            {
                var dx = x - centerX;
                if (dx * dx + dy * dy <= radiusSquared)
                    _pixels[row + x] = packed;
            }
        }
    }

    private int PackedColor(string colorText)
    {
        if (_colors.TryGetValue(colorText, out var packed))
            return packed;
        var color = UiColor.Parse(colorText, Colors.White);
        packed = unchecked((int)(0xFF000000u | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B));
        if (_colors.Count >= 256)
            _colors.Clear();
        _colors[colorText] = packed;
        return packed;
    }
}
