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
}
