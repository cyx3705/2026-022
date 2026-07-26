using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AppShell.Core.Commands;
using WBall.Game;
using WBall.Model;
using WBall.Presentation;

namespace WBall.Game;

/// <summary>裁判窗(v1.6):阵营列表/积分 + game.start。</summary>
public sealed class RefereeView : UserControl, ICommandBusAware
{
    private readonly SceneWorld _world;
    private CommandBus? _bus;
    private readonly StackPanel _list;
    private readonly TextBlock _status;

    public RefereeView(SceneWorld world)
    {
        _world = world;
        var root = new StackPanel { Margin = new Thickness(10), Orientation = Orientation.Vertical };

        root.Children.Add(new TextBlock
        {
            Text = "裁判区",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        });

        _status = new TextBlock { Text = "—", Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        root.Children.Add(_status);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var startBtn = new Button { Content = "开局 game.start", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0) };
        startBtn.Click += (_, _) => Run("game.start");
        var resetBtn = new Button { Content = "重置积分", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0) };
        resetBtn.Click += (_, _) => Run("game.resetscore");
        var refreshBtn = new Button { Content = "刷新", Padding = new Thickness(10, 6, 10, 6) };
        refreshBtn.Click += (_, _) => Refresh();
        btnRow.Children.Add(startBtn);
        btnRow.Children.Add(resetBtn);
        btnRow.Children.Add(refreshBtn);
        root.Children.Add(btnRow);

        _list = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(_list);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _world.Changed += () =>
        {
            if (_refreshQueued)
                return;
            _refreshQueued = true;
            Dispatcher.BeginInvoke(() =>
            {
                _refreshQueued = false;
                Refresh();
            }, System.Windows.Threading.DispatcherPriority.Background);
        };
        Refresh();
    }

    private bool _refreshQueued;

    public void AttachBus(CommandBus bus) => _bus = bus;

    private void Refresh()
    {
        _list.Children.Clear();
        if (_world.Factions.Count == 0)
        {
            _status.Text = "无阵营 — 启动时应已种子蓝/红";
            return;
        }

        _status.Text = $"阵营 {_world.Factions.Count} | 总分 {_world.Factions.Sum(f => f.Score)}";
        foreach (var f in _world.Factions.OrderByDescending(x => x.Score).ThenBy(x => x.Id))
        {
            var card = new Border
            {
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 6, 0, 6),
                Margin = new Thickness(0, 0, 0, 4),
            };
            var panel = new StackPanel();
            var title = new DockPanel();
            var swatch = new Border
            {
                Width = 16,
                Height = 16,
                Background = new SolidColorBrush(ParseColor(f.Color)),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(swatch, Dock.Left);
            title.Children.Add(swatch);
            title.Children.Add(new TextBlock
            {
                Text = $"{f.Name} ({f.Id})  score={f.Score}",
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            panel.Children.Add(title);

            var editRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            var ballsBox = MakeBox(f.InitialBalls.ToString(CultureInfo.InvariantCulture), 48);
            var multBox = MakeBox(f.InitialMultiplier.ToString(CultureInfo.InvariantCulture), 56);
            var colorBox = MakeBox(f.Color, 72);
            var apply = new Button { Content = "应用", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(4, 0, 0, 0) };
            var factionId = f.Id;
            apply.Click += (_, _) =>
            {
                var faction = _world.FindFaction(factionId);
                if (faction == null)
                    return;
                if (int.TryParse(ballsBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ib))
                    faction.InitialBalls = Math.Max(0, ib);
                if (long.TryParse(multBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var im))
                    faction.InitialMultiplier = PublicDefaults.ClampMultiplier(im);
                if (!string.IsNullOrWhiteSpace(colorBox.Text))
                    faction.Color = colorBox.Text.Trim();
                _world.NotifyChanged(markDirty: false);
                Refresh();
            };
            editRow.Children.Add(new TextBlock { Text = "球数", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            editRow.Children.Add(ballsBox);
            editRow.Children.Add(new TextBlock { Text = "倍率", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });
            editRow.Children.Add(multBox);
            editRow.Children.Add(new TextBlock { Text = "色", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });
            editRow.Children.Add(colorBox);
            editRow.Children.Add(apply);
            panel.Children.Add(editRow);
            card.Child = panel;
            _list.Children.Add(card);
        }
    }

    private static TextBox MakeBox(string text, double width)
        => new()
        {
            Text = text,
            Width = width,
            Margin = new Thickness(0, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

    private void Run(string cmd)
    {
        if (_bus == null)
            return;
        _ = _bus.ExecuteAsync(cmd, "UI");
    }

    private static Color ParseColor(string hex)
    {
        try
        {
            if (!hex.StartsWith('#'))
                hex = "#" + hex;
            return (Color)ColorConverter.ConvertFromString(hex)!;
        }
        catch
        {
            return Colors.Gray;
        }
    }
}
