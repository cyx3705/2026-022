using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AppShell.Core.Commands;
using WBall.Commands;
using WBall.Recording;

namespace WBall.Presentation;

public sealed class RenderSettingsView : UserControl, ICommandBusAware
{
    private readonly RenderJobService _service;
    private readonly Dictionary<string, TextBox> _fields = new(StringComparer.Ordinal);
    private readonly ComboBox _scenario = new() { MinWidth = 160 };
    private readonly ComboBox _resolution = new() { MinWidth = 130 };
    private readonly CheckBox _renderAuto = new() { Content = "出片自动降速" };
    private readonly CheckBox _previewAuto = new() { Content = "预览自动降速" };
    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.WrapWithOverflow,
        FontFamily = new FontFamily("Consolas"),
        Foreground = new SolidColorBrush(Color.FromRgb(0x3F, 0x4A, 0x5A)),
    };
    private CommandBus? _bus;
    private Button? _startButton;
    private Button? _pauseButton;
    private Button? _resumeButton;
    private Button? _cancelButton;
    private Button? _applyButton;

    public RenderSettingsView(RenderJobService service)
    {
        _service = service;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF9, 0xFC));
        foreach (var preset in new[] { "1280x720", "1920x1080", "2560x1440", "3840x2160", "自定义" })
            _resolution.Items.Add(preset);
        _resolution.SelectedIndex = 0;
        _resolution.SelectionChanged += (_, _) => ApplyResolutionPreset();
        _scenario.Items.Add("当前配置");
        foreach (var scenario in _service.Scenarios)
            _scenario.Items.Add(scenario);
        _scenario.SelectedIndex = 0;
        _scenario.SelectionChanged += (_, _) =>
        {
            if (_scenario.SelectedIndex > 0)
                Write("seed", _service.ResolveSeed(_scenario.SelectedItem?.ToString()));
        };

        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(Title("出片与时间"));
        root.Children.Add(Section("任务"));
        root.Children.Add(Row("输入来源", _scenario));
        root.Children.Add(Row("种子", Field("seed", "42", 76)));
        root.Children.Add(Row("名称", Field("name", "battle", 160)));
        root.Children.Add(Row("结束条件", new TextBlock
        {
            Text = "其他阵营的炮台与全部可战积分被消灭",
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        }));

        root.Children.Add(Section("画面"));
        root.Children.Add(Row("分辨率预设", _resolution));
        root.Children.Add(Row("宽 / 高", Field("w", "1280", 76), Field("h", "720", 76)));
        root.Children.Add(Row("FPS", Field("fps", "30", 76)));
        root.Children.Add(Row("帧队列容量", Field("queue", "4", 76)));
        root.Children.Add(Row("输出", new TextBlock { Text = "H.264 MP4（固定）" }));

        root.Children.Add(Section("时间"));
        root.Children.Add(Row("自动倍率", _renderAuto, _previewAuto));
        root.Children.Add(Row("开始 / 最低球数", Field("start", "2000", 76), Field("full", "10000", 76)));
        root.Children.Add(Row("最低 / 手动倍率", Field("minScale", "0.25", 76), Field("manual", "1", 76)));
        root.Children.Add(Row("量化 / 迟滞球数", Field("quantum", "0.05", 76), Field("hysteresis", "200", 76)));

        var configActions = new WrapPanel { Margin = new Thickness(0, 6, 0, 8) };
        _applyButton = Button("应用参数", ApplyConfig);
        configActions.Children.Add(_applyButton);
        configActions.Children.Add(Button("刷新回读", () => { RefreshConfig(); return Task.CompletedTask; }));
        root.Children.Add(configActions);

        root.Children.Add(Section("任务操作"));
        var actions = new WrapPanel { Margin = new Thickness(0, 2, 0, 8) };
        _startButton = Button("开始出片", Start);
        _pauseButton = Button("暂停", () => Execute("render.pause"));
        _resumeButton = Button("继续", () => Execute("render.resume"));
        _cancelButton = Button("取消", () => Execute("render.cancel confirm=true"));
        actions.Children.Add(_startButton);
        actions.Children.Add(_pauseButton);
        actions.Children.Add(_resumeButton);
        actions.Children.Add(_cancelButton);
        actions.Children.Add(Button("打开结果目录", OpenDirectory));
        root.Children.Add(actions);

        root.Children.Add(Section("进度"));
        root.Children.Add(_status);
        Content = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        RefreshConfig();
        RefreshStatus();
        _service.StatusChanged += OnStatusChanged;
    }

    public void AttachBus(CommandBus bus) => _bus = bus;

    private async Task ApplyConfig()
    {
        var command = $"render.config w={Text("w")} h={Text("h")} fps={Text("fps")} queue={Text("queue")} "
                      + $"autoSlow={Bool(_renderAuto)} "
                      + $"previewAutoSlow={Bool(_previewAuto)} startBalls={Text("start")} fullBalls={Text("full")} "
                      + $"minScale={Text("minScale")} manualScale={Text("manual")} quantum={Text("quantum")} "
                      + $"hysteresis={Text("hysteresis")}";
        await Execute(command);
        RefreshConfig();
    }

    private async Task Start()
    {
        await ApplyConfig();
        var scenario = _scenario.SelectedIndex > 0
            ? $" scenario={Quote(_scenario.SelectedItem?.ToString() ?? "")}" : "";
        await Execute($"render.start seed={Text("seed")} name={Quote(Text("name"))}{scenario}");
    }

    private async Task Execute(string command)
    {
        if (_bus == null)
        {
            _status.Text = "命令总线未连接";
            return;
        }
        var result = await _bus.ExecuteAsync(command, "出片与时间");
        if (!result.Success)
            _status.Text = result.Message;
        else
            RefreshStatus();
    }

    private Task OpenDirectory()
    {
        var directory = _service.Status.OutputDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !System.IO.Directory.Exists(directory))
        {
            _status.Text = "当前没有可打开的结果目录";
            return Task.CompletedTask;
        }
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { directory }, UseShellExecute = true });
        return Task.CompletedTask;
    }

    private void OnStatusChanged()
    {
        if (Dispatcher.CheckAccess())
            RefreshStatus();
        else
            Dispatcher.BeginInvoke(RefreshStatus);
    }

    private void RefreshStatus()
    {
        var status = _service.Status;
        _status.Text = RecordCommands.Format(status);
        var active = status.Active;
        foreach (var field in _fields.Values)
            field.IsEnabled = !active;
        _scenario.IsEnabled = !active;
        _resolution.IsEnabled = !active;
        _renderAuto.IsEnabled = !active;
        _previewAuto.IsEnabled = !active;
        if (_applyButton != null) _applyButton.IsEnabled = !active;
        if (_startButton != null) _startButton.IsEnabled = !active;
        if (_pauseButton != null) _pauseButton.IsEnabled = active && status.Stage != "paused";
        if (_resumeButton != null) _resumeButton.IsEnabled = status.Stage == "paused";
        if (_cancelButton != null) _cancelButton.IsEnabled = active;
    }

    private void RefreshConfig()
    {
        var c = _service.Config;
        Write("w", c.Width); Write("h", c.Height); Write("fps", c.Fps); Write("queue", c.QueueCapacity);
        Write("start", c.SlowStartBalls); Write("full", c.SlowFullBalls);
        Write("minScale", c.MinSimulationScale); Write("manual", c.ManualSimulationScale);
        Write("quantum", c.ScaleQuantization); Write("hysteresis", c.HysteresisBalls);
        var preset = $"{c.Width}x{c.Height}";
        _resolution.SelectedItem = _resolution.Items.Contains(preset) ? preset : "自定义";
        _renderAuto.IsChecked = c.RenderAutoSlow;
        _previewAuto.IsChecked = c.PreviewAutoSlow;
    }

    private void ApplyResolutionPreset()
    {
        if (_resolution.SelectedItem is not string preset || preset == "自定义" || !_fields.ContainsKey("w"))
            return;
        var dimensions = preset.Split('x');
        _fields["w"].Text = dimensions[0];
        _fields["h"].Text = dimensions[1];
    }

    private TextBox Field(string key, string value, double width)
    {
        var field = new TextBox
        {
            Text = value,
            Width = width,
            Height = 24,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _fields[key] = field;
        return field;
    }

    private static TextBlock Title(string text) => new()
    {
        Text = text,
        FontSize = 18,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 6),
    };

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB)),
        Margin = new Thickness(0, 8, 0, 4),
    };

    private static WrapPanel Row(string label, params UIElement[] controls)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(new TextBlock { Text = label, Width = 120, VerticalAlignment = VerticalAlignment.Center });
        foreach (var control in controls)
            row.Children.Add(control);
        return row;
    }

    private static Button Button(string label, Func<Task> action)
    {
        var button = new Button
        {
            Content = label,
            Height = 28,
            MinWidth = 68,
            Margin = new Thickness(0, 0, 6, 4),
            Padding = new Thickness(8, 2, 8, 2),
        };
        button.Click += async (_, _) => await action();
        return button;
    }

    private string Text(string key) => _fields[key].Text.Trim();
    private void Write(string key, double value) => _fields[key].Text = value.ToString("0.####", CultureInfo.InvariantCulture);
    private static string Bool(CheckBox value) => (value.IsChecked == true).ToString().ToLowerInvariant();
    private static string Quote(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
