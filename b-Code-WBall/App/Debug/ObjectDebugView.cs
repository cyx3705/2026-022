using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AppShell.Core.Commands;
using WBall.Model;
using WBall.Presentation;

namespace WBall.Debug;

/// <summary>对象调试窗(v1.6):与投影表字段一致;销毁器 name / 球 multiplier。</summary>
public sealed class ObjectDebugView : UserControl, ICommandBusAware
{
    private static readonly string[] Palette =
    [
        "#64748B", "#3B82F6", "#EF4444", "#22C55E",
        "#F59E0B", "#A855F7", "#06B6D4", "#EC4899",
        "#FFFFFF", "#111827", "#F97316", "#84CC16",
    ];

    private readonly SceneWorld _world;
    private CommandBus? _bus;
    private readonly TextBlock _status;
    private readonly TextBlock _result;
    private readonly TextBox _idBox;
    private readonly TextBox _typeBox;
    private readonly TextBox _xBox;
    private readonly TextBox _yBox;
    private readonly TextBox _wBox;
    private readonly TextBox _hBox;
    private readonly TextBox _rotBox;
    private readonly TextBox _nameBox;
    private readonly TextBox _multBox;
    private readonly TextBox _colorBox;
    private readonly Rectangle _preview;
    private readonly Button _applyBtn;
    private bool _suppress;

    public ObjectDebugView(SceneWorld world)
    {
        _world = world;
        var root = new StackPanel { Margin = new Thickness(10), Orientation = Orientation.Vertical };

        root.Children.Add(new TextBlock
        {
            Text = "对象调试",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        });

        _status = new TextBlock { Text = "请在落球区选中对象或球", Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        root.Children.Add(_status);
        _result = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray,
        };
        root.Children.Add(_result);

        _idBox = AddRow(root, "Id", readOnly: true);
        _typeBox = AddRow(root, "类型", readOnly: true);
        _xBox = AddRow(root, "X");
        _yBox = AddRow(root, "Y");
        _wBox = AddRow(root, "W");
        _hBox = AddRow(root, "H");
        _rotBox = AddRow(root, "旋转°");
        _nameBox = AddRow(root, "Name");
        _multBox = AddRow(root, "倍率");

        root.Children.Add(new TextBlock { Text = "颜色", Margin = new Thickness(0, 10, 0, 4) });
        var colorRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        _preview = new Rectangle
        {
            Width = 36,
            Height = 28,
            RadiusX = 4,
            RadiusY = 4,
            Stroke = Brushes.Gray,
            StrokeThickness = 1,
            Margin = new Thickness(0, 0, 8, 0),
        };
        DockPanel.SetDock(_preview, Dock.Left);
        colorRow.Children.Add(_preview);
        _colorBox = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };
        _colorBox.TextChanged += (_, _) => UpdatePreviewFromBox();
        colorRow.Children.Add(_colorBox);
        root.Children.Add(colorRow);

        var wrap = new WrapPanel();
        foreach (var hex in Palette)
        {
            var swatch = new Border
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(2),
                Background = new SolidColorBrush(Parse(hex)),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = hex,
                ToolTip = hex,
            };
            swatch.MouseLeftButtonDown += async (_, _) =>
            {
                _colorBox.Text = (string)swatch.Tag;
                await ApplyColorImmediateAsync();
            };
            wrap.Children.Add(swatch);
        }

        root.Children.Add(wrap);

        _applyBtn = new Button
        {
            Content = "应用几何/颜色/属性",
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(10, 6, 10, 6),
        };
        _applyBtn.Click += async (_, _) => await ApplyAllAsync();
        root.Children.Add(_applyBtn);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _world.Changed += () => Dispatcher.BeginInvoke(RefreshFromSelection);
        RefreshFromSelection();
    }

    public void AttachBus(CommandBus bus) => _bus = bus;

    private static TextBox AddRow(Panel root, string label, bool readOnly = false)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var lbl = new TextBlock
        {
            Text = label,
            Width = 48,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(lbl, Dock.Left);
        row.Children.Add(lbl);
        var box = new TextBox { IsReadOnly = readOnly };
        if (readOnly)
            box.Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));
        row.Children.Add(box);
        root.Children.Add(row);
        return box;
    }

    private void RefreshFromSelection()
    {
        _suppress = true;
        try
        {
            if (_world.SelectedBallId != null)
            {
                var ball = _world.FindBall(_world.SelectedBallId);
                if (ball != null)
                {
                    Fill("ball", ball.Id, ball.X, ball.Y, ball.Size * 2, ball.Size * 2, 0, ball.Color, name: null, mult: ball.Multiplier);
                    SetGeomEnabled(false, false, false, false, false);
                    _nameBox.IsEnabled = false;
                    _multBox.IsEnabled = true;
                    _status.Text = "小球(位置只读);可改颜色与倍率";
                    return;
                }
            }

            if (_world.SelectedSolidId != null)
            {
                var s = _world.FindSolid(_world.SelectedSolidId);
                if (s != null)
                {
                    s.GetAabb(out var minX, out var minY, out var maxX, out var maxY);
                    Fill("solid", s.Id, minX, minY, maxX - minX, maxY - minY, 0, s.Color, name: null, mult: null);
                    SetGeomEnabled(x: true, y: true, w: false, h: false, rot: false);
                    _nameBox.IsEnabled = false;
                    _multBox.IsEnabled = false;
                    return;
                }
            }

            if (_world.SelectedId != null)
            {
                var o = _world.FindObject(_world.SelectedId);
                if (o != null)
                {
                    Fill(o.Type.ToString().ToLowerInvariant(), o.Id, o.X, o.Y, o.W, o.H, o.Rotation,
                        MeshSolid.DefaultColor, name: o.Name, mult: null);
                    SetGeomEnabled(true, true, true, true, true);
                    _nameBox.IsEnabled = o.Type == SceneObjectType.Despawner || o.Type == SceneObjectType.Spawner;
                    _multBox.IsEnabled = false;
                    if (o.Type == SceneObjectType.Despawner)
                        _status.Text = "销毁器: Name 即函数(X5/RUN)";
                    return;
                }
            }

            _status.Text = "请在落球区选中对象、异形或球";
            ClearFields();
        }
        finally
        {
            _suppress = false;
            UpdatePreviewFromBox();
        }
    }

    private void Fill(string type, string id, double x, double y, double w, double h, double rot, string color,
        string? name, long? mult)
    {
        _status.Text = $"编辑中: {type} / {id}";
        _idBox.Text = id;
        _typeBox.Text = type;
        _xBox.Text = Fmt(x);
        _yBox.Text = Fmt(y);
        _wBox.Text = Fmt(w);
        _hBox.Text = Fmt(h);
        _rotBox.Text = Fmt(rot);
        _nameBox.Text = name ?? "";
        _multBox.Text = mult?.ToString(CultureInfo.InvariantCulture) ?? "";
        _colorBox.Text = color;
    }

    private void ClearFields()
    {
        _idBox.Text = _typeBox.Text = _xBox.Text = _yBox.Text = _wBox.Text = _hBox.Text = _rotBox.Text = "";
        _nameBox.Text = _multBox.Text = "";
        _colorBox.Text = MeshSolid.DefaultColor;
        _nameBox.IsEnabled = false;
        _multBox.IsEnabled = false;
    }

    private void SetGeomEnabled(bool x, bool y, bool w, bool h, bool rot)
    {
        _xBox.IsEnabled = x;
        _yBox.IsEnabled = y;
        _wBox.IsEnabled = w;
        _hBox.IsEnabled = h;
        _rotBox.IsEnabled = rot;
    }

    private void UpdatePreviewFromBox()
    {
        if (_suppress)
            return;
        try
        {
            _preview.Fill = new SolidColorBrush(Parse(_colorBox.Text.Trim()));
        }
        catch
        {
            _preview.Fill = Brushes.Transparent;
        }
    }

    private async Task ApplyColorImmediateAsync()
    {
        UpdatePreviewFromBox();
        var type = _typeBox.Text;
        var id = _idBox.Text;
        var color = _colorBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(color))
            return;
        if (type == "ball")
            await RunAsync($"ball.set id={id} color={color}");
        else if (type == "solid")
            await RunAsync($"solid.set id={id} color={color}");
    }

    private async Task ApplyAllAsync()
    {
        var type = _typeBox.Text;
        var id = _idBox.Text;
        if (string.IsNullOrWhiteSpace(id))
        {
            ShowLocalFailure("请先选中对象或球");
            return;
        }

        if (type == "ball")
        {
            var command = $"ball.set id={id} color={_colorBox.Text.Trim()}";
            if (_multBox.IsEnabled)
                command += $" multiplier={_multBox.Text.Trim()}";
            await RunAsync(command);
            return;
        }

        if (type == "solid")
        {
            if (!double.TryParse(_xBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !double.TryParse(_yBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                ShowLocalFailure("X/Y 必须是有效数字");
                return;
            }

            if (!IsHexColor(_colorBox.Text))
            {
                ShowLocalFailure("颜色必须是 #RRGGBB");
                return;
            }

            if (await RunAsync($"solid.move id={id} x={Fmt(x)} y={Fmt(y)}"))
                await RunAsync($"solid.set id={id} color={_colorBox.Text.Trim()}");
            return;
        }

        // scene object
        var cmd = new List<string> { $"scene.set id={id}" };
        if (!TryAddNumber(cmd, "x", _xBox)
            || !TryAddNumber(cmd, "y", _yBox)
            || !TryAddNumber(cmd, "w", _wBox)
            || !TryAddNumber(cmd, "h", _hBox)
            || !TryAddNumber(cmd, "rotation", _rotBox))
        {
            ShowLocalFailure("几何字段必须是有效数字");
            return;
        }

        if (_nameBox.IsEnabled)
            cmd.Add($"name={CommandParser.QuoteArg(_nameBox.Text.Trim())}");
        if (cmd.Count > 1)
            await RunAsync(string.Join(" ", cmd));
    }

    private async Task<bool> RunAsync(string command)
    {
        if (_bus == null)
        {
            ShowLocalFailure("命令总线尚未连接");
            return false;
        }

        var result = await _bus.ExecuteAsync(command, "UI");
        _result.Text = result.Message;
        _result.Foreground = result.Success ? Brushes.SeaGreen : Brushes.Firebrick;
        return result.Success;
    }

    private static bool TryAddNumber(List<string> command, string name, TextBox box)
    {
        if (!box.IsEnabled)
            return true;
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return false;
        command.Add($"{name}={Fmt(value)}");
        return true;
    }

    private void ShowLocalFailure(string message)
    {
        _result.Text = message;
        _result.Foreground = Brushes.Firebrick;
    }

    private static bool IsHexColor(string value)
        => System.Text.RegularExpressions.Regex.IsMatch(
            value.Trim(), "^#[0-9A-Fa-f]{6}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static Color Parse(string hex)
    {
        hex = hex.Trim();
        if (!hex.StartsWith('#'))
            hex = "#" + hex;
        return (Color)ColorConverter.ConvertFromString(hex)!;
    }
}
