using System.Text.RegularExpressions;

namespace WBall.Editing;

internal static partial class EditValidation
{
    public static bool TryNormalizeColor(string? value, out string color)
    {
        color = value?.Trim().ToUpperInvariant() ?? "";
        if (!color.StartsWith('#'))
            color = "#" + color;
        return HexColor().IsMatch(color);
    }

    public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    [GeneratedRegex("^#[0-9A-F]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColor();
}
