using System.Windows;
using System.Windows.Media;

namespace WBall.Verify;

/// <summary>v3.4 V34-09:视觉树查找(出片页 suite 用来定位 ScrollViewer 判断横向溢出)。</summary>
internal static class VisualTreeProbe
{
    public static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            var nested = FindVisualChild<T>(child);
            if (nested != null)
                return nested;
        }
        return null;
    }

    public static IReadOnlyList<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        var matches = new List<T>();
        Collect(root, matches);
        return matches;
    }

    private static void Collect<T>(DependencyObject root, List<T> matches) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                matches.Add(match);
            Collect(child, matches);
        }
    }
}
