using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AppShell.Core.Commands;
using WBall.Model;
using WBall.Presentation;

namespace WBall.BallUi;

/// <summary>小球对象窗(v1.6.1):上区选中球,下区全局公式。</summary>
public sealed class BallObjectView : UserControl, ICommandBusAware
{
    private readonly SceneWorld _world;
    private CommandBus? _bus;
    private readonly TextBlock _ballStatus;
    private readonly TextBlock _result;
    private readonly TextBox _idBox;
    private readonly TextBox _colorBox;
    private readonly TextBox _multBox;
    private readonly TextBox _sizeBox;
    private readonly TextBox _weightBox;
    private TextBox _initialBox = null!;
    private TextBox _sizeBaseBox = null!;
    private TextBox _sizeScaleBox = null!;
    private TextBox _weightBaseBox = null!;
    private TextBox _weightScaleBox = null!;
    private TextBox _previewMultBox = null!;
    private TextBlock _previewLabel = null!;
    private TextBlock _formulaHint = null!;
    private bool _suppress;

    public BallObjectView(SceneWorld world)
    {
        _world = world;

        var root = new DockPanel { Margin = new Thickness(10) };

        // 下区公式(固定)
        var formula = BuildFormulaPanel();
        DockPanel.SetDock(formula, Dock.Bottom);
        root.Children.Add(formula);

        // 上区选中球
        var ballPanel = new StackPanel { Orientation = Orientation.Vertical };
        ballPanel.Children.Add(new TextBlock
        {
            Text = "小球对象",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        });
        _ballStatus = new TextBlock
        {
            Text = "请在落球区选中小球",
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };
        ballPanel.Children.Add(_ballStatus);
        _result = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray,
        };
        ballPanel.Children.Add(_result);

        _idBox = AddRow(ballPanel, "Id", readOnly: true);
        _colorBox = AddRow(ballPanel, "颜色");
        _multBox = AddRow(ballPanel, "标号");
        _sizeBox = AddRow(ballPanel, "Size(半径)");
        _weightBox = AddRow(ballPanel, "Weight");

        var applyBall = new Button
        {
            Content = "应用球属性",
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(10, 6, 10, 6),
        };
        applyBall.Click += async (_, _) => await ApplyBallAsync();
        ballPanel.Children.Add(applyBall);

        root.Children.Add(new ScrollViewer
        {
            Content = ballPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        Content = root;

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
        Loaded += (_, _) => Refresh();
    }

    private bool _refreshQueued;

    public void AttachBus(CommandBus bus) => _bus = bus;

    private Border BuildFormulaPanel()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(new Separator { Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock
        {
            Text = "公式区（全局）",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        });
        _formulaHint = new TextBlock
        {
            Text = "size=Round→int / weight=Round1；参数可小数",
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(_formulaHint);

        _initialBox = AddRow(panel, "初始标号");
        _sizeBaseBox = AddRow(panel, "SizeBase");
        _sizeScaleBox = AddRow(panel, "SizeScale");
        _weightBaseBox = AddRow(panel, "WeightBase");
        _weightScaleBox = AddRow(panel, "WeightScale");
        foreach (var box in new[] { _initialBox, _sizeBaseBox, _sizeScaleBox, _weightBaseBox, _weightScaleBox })
            box.TextChanged += (_, _) => { if (!_suppress) UpdatePreview(); };

        var previewRow = new DockPanel { Margin = new Thickness(0, 8, 0, 4) };
        previewRow.Children.Add(new TextBlock
        {
            Text = "预览标号",
            Width = 88,
            VerticalAlignment = VerticalAlignment.Center,
        });
        _previewMultBox = new TextBox
        {
            Text = "10",
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _previewMultBox.TextChanged += (_, _) => UpdatePreview();
        previewRow.Children.Add(_previewMultBox);
        panel.Children.Add(previewRow);

        _previewLabel = new TextBlock
        {
            Text = "→ size=? weight=?",
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(_previewLabel);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
        var saveBtn = new Button
        {
            Content = "保存公式",
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(10, 6, 10, 6),
        };
        saveBtn.Click += async (_, _) => await SaveFormulaAsync(recalcAll: false);
        var recalcBtn = new Button
        {
            Content = "重算全部球",
            Padding = new Thickness(10, 6, 10, 6),
        };
        recalcBtn.Click += async (_, _) => await SaveFormulaAsync(recalcAll: true);
        btnRow.Children.Add(saveBtn);
        btnRow.Children.Add(recalcBtn);
        panel.Children.Add(btnRow);

        return new Border
        {
            Child = panel,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 4, 0, 0),
        };
    }

    private void Refresh()
    {
        if (_suppress)
            return;
        _suppress = true;
        try
        {
            LoadFormulaBoxes();
            UpdatePreview();

            if (_world.SelectedBallId == null)
            {
                _ballStatus.Text = "请在落球区选中小球";
                ClearBallBoxes();
                return;
            }

            var ball = _world.FindBall(_world.SelectedBallId);
            if (ball == null)
            {
                _ballStatus.Text = "选中球已不存在";
                ClearBallBoxes();
                return;
            }

            _ballStatus.Text = $"已选中 {ball.Id}";
            _idBox.Text = ball.Id;
            _colorBox.Text = ball.Color;
            _multBox.Text = ball.Multiplier.ToString(CultureInfo.InvariantCulture);
            _sizeBox.Text = ((int)Math.Round(ball.Size)).ToString(CultureInfo.InvariantCulture);
            _weightBox.Text = ball.Weight.ToString("0.#", CultureInfo.InvariantCulture);
        }
        finally
        {
            _suppress = false;
        }
    }

    private void LoadFormulaBoxes()
    {
        var d = _world.Defaults;
        _initialBox.Text = d.InitialMultiplier.ToString(CultureInfo.InvariantCulture);
        _sizeBaseBox.Text = Fmt(d.SizeBase);
        _sizeScaleBox.Text = Fmt(d.SizeScale);
        _weightBaseBox.Text = Fmt(d.WeightBase);
        _weightScaleBox.Text = Fmt(d.WeightScale);
    }

    private void ClearBallBoxes()
    {
        _idBox.Text = "";
        _colorBox.Text = "";
        _multBox.Text = "";
        _sizeBox.Text = "";
        _weightBox.Text = "";
    }

    private void UpdatePreview()
    {
        var d = ReadFormulaFromBoxes() ?? _world.Defaults;
        if (!long.TryParse(_previewMultBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m))
            m = 10;
        m = PublicDefaults.ClampMultiplier(m);
        var size = d.SizeFromMultiplier(m);
        var weight = d.WeightFromMultiplier(m);
        _previewLabel.Text =
            $"→ 标号={PublicDefaults.FormatMultiplier(m)}  size={size}  weight={weight}";
    }

    private PublicDefaults? ReadFormulaFromBoxes()
    {
        if (!TryParseDouble(_sizeBaseBox.Text, out var sb)
            || !TryParseDouble(_sizeScaleBox.Text, out var ss)
            || !TryParseDouble(_weightBaseBox.Text, out var wb)
            || !TryParseDouble(_weightScaleBox.Text, out var ws)
            || !long.TryParse(_initialBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var init))
            return null;

        return new PublicDefaults
        {
            SizeBase = sb,
            SizeScale = ss,
            WeightBase = wb,
            WeightScale = ws,
            InitialMultiplier = PublicDefaults.ClampMultiplier(init),
        };
    }

    private Task SaveFormulaAsync(bool recalcAll)
    {
        var command = "formula.set"
                      + $" sizebase={_sizeBaseBox.Text.Trim()}"
                      + $" sizescale={_sizeScaleBox.Text.Trim()}"
                      + $" weightbase={_weightBaseBox.Text.Trim()}"
                      + $" weightscale={_weightScaleBox.Text.Trim()}"
                      + $" initial={_initialBox.Text.Trim()}"
                      + $" recalc={recalcAll.ToString().ToLowerInvariant()}";
        return RunAsync(command);
    }

    private Task ApplyBallAsync()
    {
        if (_world.SelectedBallId == null)
            return ShowLocalFailureAsync("请先选中小球");
        var command = $"ball.set id={_world.SelectedBallId}"
                      + $" color={_colorBox.Text.Trim()}"
                      + $" multiplier={_multBox.Text.Trim()}"
                      + $" size={_sizeBox.Text.Trim()}"
                      + $" weight={_weightBox.Text.Trim()}";
        return RunAsync(command);
    }

    private async Task RunAsync(string command)
    {
        if (_bus == null)
        {
            await ShowLocalFailureAsync("命令总线尚未连接");
            return;
        }

        var result = await _bus.ExecuteAsync(command, "UI");
        _result.Text = result.Message;
        _result.Foreground = result.Success ? Brushes.SeaGreen : Brushes.Firebrick;
    }

    private Task ShowLocalFailureAsync(string message)
    {
        _result.Text = message;
        _result.Foreground = Brushes.Firebrick;
        return Task.CompletedTask;
    }

    private static TextBox AddRow(Panel parent, string label, bool readOnly = false)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Width = 88,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var box = new TextBox
        {
            IsReadOnly = readOnly,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(box);
        parent.Children.Add(row);
        return box;
    }

    private static bool TryParseDouble(string text, out double value)
        => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string Fmt(double v)
        => v.ToString("0.##", CultureInfo.InvariantCulture);
}
