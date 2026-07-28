using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AppShell.Core.Commands;
using WBall.Battle;

namespace WBall.Presentation;

/// <summary>v3.2 战斗平衡命令的图形外壳。</summary>
internal sealed class BalanceSettingsView : UserControl, ICommandBusAware
{
    /// <summary>
    /// v3.4 V34-05:控件由 <see cref="BalanceFields"/> 生成。
    /// 此前这里有一份 50 行私有 Field 表,与范围表 / Clone / 命令 switch 各自维护 ——
    /// 加一个字段要在四处同步登记,漏一处就是"UI 有、命令没有"或"存了读不回来"。
    /// </summary>
    private static IReadOnlyList<(string Group, string Command, IReadOnlyList<BalanceFieldDescriptor> Fields)> Groups
        => BalanceFields.Groups;

    private readonly BalanceConfigStore _balance;
    private readonly BattleConfigStore _arena;
    private readonly PresetStore _presets;
    private readonly Dictionary<string, Control> _controls = new(StringComparer.Ordinal);
    private readonly ComboBox _presetName = new() { Width = 150, IsEditable = true };
    private readonly TextBox _seeds = new() { Width = 150, Text = "42..49" };
    private readonly TextBox _seconds = new() { Width = 70, Text = "180" };
    private readonly ComboBox _simConfig = new() { Width = 110, IsEditable = true };
    private readonly TextBox _result = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        MinHeight = 170,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12,
    };
    private readonly ProgressBar _progress = new() { Height = 3, Visibility = Visibility.Collapsed };
    private readonly TextBlock _mode = new() { Foreground = Brushes.DarkGoldenrod, Margin = new Thickness(0, 0, 0, 8) };
    private CommandBus? _bus;
    private CancellationTokenSource? _simulationCancellation;


    public BalanceSettingsView(BalanceConfigStore balance, BattleConfigStore arena, PresetStore presets)
    {
        _balance = balance;
        _arena = arena;
        _presets = presets;
        Background = Brushes.White;

        var root = new DockPanel { Margin = new Thickness(12) };
        var actions = BuildActions();
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);

        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = "战斗平衡",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        });
        content.Children.Add(_mode);
        foreach (var group in Groups)
            content.Children.Add(BuildGroup(group));
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = content,
        });
        Content = root;
        Loaded += (_, _) => Refresh();
    }

    public void AttachBus(CommandBus bus)
    {
        _bus = bus;
        Refresh();
    }

    private FrameworkElement BuildGroup(
        (string Group, string Command, IReadOnlyList<BalanceFieldDescriptor> Fields) group)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var header = new DockPanel { Margin = new Thickness(0, 6, 0, 4) };
        var apply = new Button { Content = "应用", MinWidth = 64, Height = 26 };
        apply.Click += async (_, _) => await ExecuteGroup(group);
        DockPanel.SetDock(apply, Dock.Right);
        header.Children.Add(apply);
        header.Children.Add(new TextBlock
        {
            Text = group.Group,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(header);

        foreach (var field in group.Fields)
        {
            var row = new Grid { MinHeight = 30 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            var label = new TextBlock { Text = field.Label, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(label);
            Control input;
            if (field.IsBoolean)
                input = new CheckBox { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            else
                input = new TextBox { Width = 100, Height = 24, VerticalContentAlignment = VerticalAlignment.Center };
            Grid.SetColumn(input, 1);
            row.Children.Add(input);
            var scope = new TextBlock
            {
                Text = field.Scope,
                Foreground = Brushes.DimGray,
                FontSize = 11,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(scope, 2);
            row.Children.Add(scope);
            _controls[field.Property] = input;
            panel.Children.Add(row);
        }
        panel.Children.Add(new Border { Height = 1, Background = Brushes.Gainsboro, Margin = new Thickness(0, 4, 0, 0) });
        return panel;
    }

    private FrameworkElement BuildActions()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(_progress);

        var primary = new WrapPanel { Margin = new Thickness(0, 8, 0, 6) };
        primary.Children.Add(Button("应用全部", async () => await ApplyAll(false)));
        primary.Children.Add(Button("应用并重置", async () => await ApplyAll(true)));
        primary.Children.Add(Button("恢复出厂", async () =>
        {
            await Execute("balance.default reset=false");
            Refresh();
        }));
        panel.Children.Add(primary);

        var preset = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        preset.Children.Add(new TextBlock { Text = "预设", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        preset.Children.Add(_presetName);
        preset.Children.Add(Button("保存", async () =>
        {
            await Execute($"preset.save name={Quote(PresetText())}");
            RefreshPresets();
        }));
        preset.Children.Add(Button("读取", async () =>
        {
            await Execute($"preset.load name={Quote(PresetText())} reset=false");
            Refresh();
        }));
        panel.Children.Add(preset);

        var simulation = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        simulation.Children.Add(new TextBlock { Text = "种子", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        simulation.Children.Add(_seeds);
        simulation.Children.Add(new TextBlock { Text = "秒", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) });
        simulation.Children.Add(_seconds);
        _simConfig.Items.Add("current");
        _simConfig.Items.Add("default");
        _simConfig.SelectedIndex = 0;
        simulation.Children.Add(_simConfig);
        simulation.Children.Add(Button("开始试跑", RunSimulation));
        simulation.Children.Add(Button("默认 vs 当前", RunComparison));
        simulation.Children.Add(Button("取消", () =>
        {
            _simulationCancellation?.Cancel();
            return Task.CompletedTask;
        }));
        panel.Children.Add(simulation);
        panel.Children.Add(_result);
        return panel;
    }

    private Button Button(string text, Func<Task> action)
    {
        var button = new Button { Content = text, MinWidth = 68, Height = 28, Margin = new Thickness(0, 0, 6, 0) };
        button.Click += async (_, _) => await action();
        return button;
    }

    private async Task ExecuteGroup(
        (string Group, string Command, IReadOnlyList<BalanceFieldDescriptor> Fields) group)
    {
        await Execute(BuildCommand(group));
        Refresh();
    }

    private async Task ApplyAll(bool reset)
    {
        foreach (var group in Groups)
            await Execute(BuildCommand(group));
        if (reset)
            await Execute("battle.reset");
        Refresh();
    }

    private string BuildCommand(
        (string Group, string Command, IReadOnlyList<BalanceFieldDescriptor> Fields) group)
    {
        var builder = new StringBuilder(group.Command);
        foreach (var field in group.Fields)
        {
            builder.Append(' ').Append(field.Parameter).Append('=');
            var control = _controls[field.Property];
            builder.Append(control switch
            {
                CheckBox check => (check.IsChecked == true).ToString().ToLowerInvariant(),
                TextBox text => string.IsNullOrWhiteSpace(text.Text) ? "0" : text.Text.Trim(),
                _ => "0",
            });
        }
        return builder.ToString();
    }

    private async Task RunSimulation()
    {
        _simulationCancellation?.Cancel();
        _simulationCancellation = new CancellationTokenSource();
        _progress.Visibility = Visibility.Visible;
        _progress.IsIndeterminate = true;
        try
        {
            var config = _simConfig.Text.Trim();
            var result = await Execute(
                $"balance.sim seeds={_seeds.Text.Trim()} seconds={_seconds.Text.Trim()} config={Quote(config)} format=table",
                _simulationCancellation.Token);
            _result.Text = result?.Message ?? "";
        }
        finally
        {
            _progress.IsIndeterminate = false;
            _progress.Visibility = Visibility.Collapsed;
        }
    }

    private async Task RunComparison()
    {
        _simulationCancellation?.Cancel();
        _simulationCancellation = new CancellationTokenSource();
        _progress.Visibility = Visibility.Visible;
        _progress.IsIndeterminate = true;
        try
        {
            var common = $"seeds={_seeds.Text.Trim()} seconds={_seconds.Text.Trim()} format=table";
            var baseline = await Execute($"balance.sim {common} config=default", _simulationCancellation.Token);
            var current = await Execute($"balance.sim {common} config=current", _simulationCancellation.Token);
            _result.Text = "DEFAULT\r\n" + (baseline?.Message ?? "") + "\r\n\r\nCURRENT\r\n" + (current?.Message ?? "");
        }
        finally
        {
            _progress.IsIndeterminate = false;
            _progress.Visibility = Visibility.Collapsed;
        }
    }

    private async Task<CommandResult?> Execute(string command, CancellationToken cancellation = default)
    {
        if (_bus == null)
            return null;
        return await _bus.ExecuteAsync(command, "UI", cancellation);
    }

    private void Refresh()
    {
        var config = _balance.Current;
        var properties = typeof(BalanceConfig).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .ToDictionary(x => x.Name, StringComparer.Ordinal);
        foreach (var (name, control) in _controls)
        {
            var value = properties[name].GetValue(config);
            switch (control)
            {
                case CheckBox check:
                    check.IsChecked = value is true;
                    break;
                case TextBox text:
                    text.Text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                    break;
            }
        }
        var territory = !string.Equals(_arena.Arena.Mode, "direct", StringComparison.OrdinalIgnoreCase);
        _mode.Text = territory
            ? "当前模式：territory；经济到火力组仅部分生效"
            : "当前模式：direct；经济到火力组全部生效";
        RefreshPresets();
    }

    private void RefreshPresets()
    {
        var selected = PresetText();
        _presetName.Items.Clear();
        _simConfig.Items.Clear();
        _simConfig.Items.Add("current");
        _simConfig.Items.Add("default");
        foreach (var name in _presets.List())
        {
            _presetName.Items.Add(name);
            _simConfig.Items.Add(name);
        }
        if (!string.IsNullOrWhiteSpace(selected))
            _presetName.Text = selected;
        else if (_presetName.Items.Count > 0)
            _presetName.SelectedIndex = 0;
        if (string.IsNullOrWhiteSpace(_simConfig.Text))
            _simConfig.SelectedIndex = 0;
    }

    private string PresetText() => string.IsNullOrWhiteSpace(_presetName.Text) ? "custom" : _presetName.Text.Trim();

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
