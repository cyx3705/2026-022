using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AppShell.Core.Commands;
using WBall.Battle;
using WBall.Stage;

namespace WBall.Presentation;

/// <summary>
/// v3.1「对战区」设置窗。纪律:窗口只是命令的图形外壳 —— 一切写入都经 CommandBus 下发
/// arena.* / turret.setall / weapon.set,不直写 BattleConfigStore;读取与预览可直读配置。
/// </summary>
internal sealed class ArenaSettingsView : UserControl, ICommandBusAware
{
    private static readonly Brush HintBrush = new SolidColorBrush(Color.FromRgb(0x7A, 0x86, 0x99));
    private static readonly Brush SectionBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB));

    private readonly BattleConfigStore _config;
    private readonly BalanceConfigStore _balance;
    private readonly BattleRuntime _battle;
    private readonly WeaponCatalog _weapons;
    private readonly StageState _stage;

    private readonly Dictionary<string, TextBox> _fields = new(StringComparer.Ordinal);
    private readonly ComboBox _mode = new();
    private readonly ComboBox _targeting = new();
    private readonly ComboBox _preloadWeapon = new();
    private readonly ComboBox _speedWeapon = new();
    private readonly ComboBox _sizePreset = new();
    private readonly CheckBox _ballCollision = new() { Content = "弹-弹碰撞" };
    private readonly CheckBox _friendlyAssist = new() { Content = "启用低速助力" };
    private readonly CheckBox _assistVisual = new() { Content = "显示助力连线" };
    private readonly TextBlock _assistMetrics = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("Consolas"),
        Foreground = HintBrush,
    };
    private readonly DispatcherTimer _assistTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas") };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = HintBrush };
    private readonly TextBox _scenarioName = new() { Text = "arena_custom", MinWidth = 110 };
    private CommandBus? _bus;
    private bool _loading;

    public ArenaSettingsView(
        BattleConfigStore config,
        BalanceConfigStore balance,
        BattleRuntime battle,
        WeaponCatalog weapons,
        StageState stage)
    {
        _config = config;
        _balance = balance;
        _battle = battle;
        _weapons = weapons;
        _stage = stage;

        var root = new StackPanel { Margin = new Thickness(10, 8, 10, 12) };
        root.Children.Add(Title("对战区设置"));
        root.Children.Add(Hint("窗口 = 命令外壳;等效命令见 arena.config / arena.*(控制台可完成同样的事)"));

        // 规模档预设 + 等比缩放
        _sizePreset.Items.Add("小 720×675");
        _sizePreset.Items.Add("中 960×900(出厂)");
        _sizePreset.Items.Add("大 1440×1350");
        _sizePreset.SelectionChanged += (_, _) => OnSizePresetChanged();
        root.Children.Add(Row("规模档", _sizePreset, Hint("选中只填控件,不自动应用")));
        var scaleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
        scaleRow.Children.Add(new TextBlock { Text = "等比缩放 k", Width = 96, VerticalAlignment = VerticalAlignment.Center });
        scaleRow.Children.Add(Field("scaleK", "1.0", 60));
        scaleRow.Children.Add(Button("缩放并重置", () => RunAsync($"arena.scale k={Read("scaleK", 1)} reset=true")));
        scaleRow.Children.Add(Hint("宽高/炮塔/格边长/弹速同乘 k —— 格数与血量不变"));
        root.Children.Add(scaleRow);

        // ① 规模
        root.Children.Add(Section("① 规模", "需重置"));
        root.Children.Add(Pair("宽 / 高", "w", "h"));
        root.Children.Add(Pair("炮塔半径 / 护罩环", "radius", "ring", "护罩环即时生效"));
        root.Children.Add(Pair("离角 X / Y", "mx", "my", "×战场宽高"));

        // ② 网格与领地
        root.Children.Add(Section("② 网格与领地", "格边长需重置"));
        _mode.Items.Add("territory");
        _mode.Items.Add("direct");
        _targeting.Items.Add("spin");
        _targeting.Items.Add("highestHp");
        _targeting.Items.Add("nearest");
        _targeting.Items.Add("rotate");
        _targeting.Items.Add("lowestHp");
        root.Children.Add(Pair("格边长 / 决胜时刻", "cell", "sudden", "格边长决定格数=领地血量"));
        root.Children.Add(Row("玩法 / 瞄准", _mode, _targeting));

        // ③ 护盾与血量
        root.Children.Add(Section("③ 护盾与血量", "统一四座 · 需重置"));
        root.Children.Add(Row("初始护盾", Field("initshield", "0", 142), Hint("无上限")));
        root.Children.Add(Pair("生命上限 / 护盾计价", "maxhp", "cost", "生命仅 direct;计价即时"));
        root.Children.Add(Hint("差异化设置(不公平局)请走 turret.set id=<炮台> …,本窗只做统一设置"));

        // ④ 大球动量
        root.Children.Add(Section("④ 大球动量", "即时(下一发起)"));
        root.Children.Add(Pair("尺寸基数 / 指数", "sizeFactor", "sizeExp"));
        root.Children.Add(Pair("尺寸下限 / 上限(格)", "sizeMin", "sizeMax"));
        root.Children.Add(Pair("速度抖动 ± / 减速指数", "speedJitter", "speedExp"));
        root.Children.Add(Pair("速度下限 / 上限", "speedMin", "speedMax"));
        root.Children.Add(Pair("质量系数 / 弹速总缩放", "weightScale", "speedScale"));
        root.Children.Add(Pair("预载发数 / 每发数值", "preloadCount", "preloadValue", "需重置"));
        root.Children.Add(Row("预载武器", _preloadWeapon, Hint("决定基速")));
        var speedRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
        speedRow.Children.Add(new TextBlock { Text = "武器基速", Width = 96, VerticalAlignment = VerticalAlignment.Center });
        speedRow.Children.Add(_speedWeapon);
        speedRow.Children.Add(Field("weaponSpeed", "360", 70));
        speedRow.Children.Add(Button("改基速", ApplyWeaponSpeed));
        speedRow.Children.Add(Hint("等效 weapon.set key=speed"));
        root.Children.Add(speedRow);
        _speedWeapon.SelectionChanged += (_, _) => SyncWeaponSpeedField();

        // ⑤ 小球与弹体数字
        root.Children.Add(Section("⑤ 小球与弹体数字", "即时"));
        root.Children.Add(Pair("小球速度 / 尺寸系数", "smallSpeed", "smallSize"));
        root.Children.Add(Pair("数字字号系数 / 最小字号", "labelFactor", "labelMin", "小球缩小后数字不再无限小"));
        root.Children.Add(Pair("最大字号 / 超出暗淡", "labelMax", "labelOutside", "0=超出球体部分完全隐藏"));

        // ⑥ 同阵营助力
        root.Children.Add(Section("⑥ 同阵营助力与回收", "即时 · 低速机制"));
        root.Children.Add(Row("开关", _friendlyAssist, _assistVisual));
        root.Children.Add(Pair("小球吸收兼容值 / 大球助力", "assistSmall", "assistShell", "小球即时等值吸收;大球为点/秒"));
        root.Children.Add(Pair("助力范围 / 单球上限", "assistReach", "assistMax"));
        root.Children.Add(_assistMetrics);

        // ⑦ 全局
        root.Children.Add(Section("⑦ 全局", ""));
        root.Children.Add(Pair("重力 g / 最大弹数", "gravity", "maxProj"));
        root.Children.Add(Row("碰撞（开启后关闭吸收）", _ballCollision));

        // 预览
        root.Children.Add(Section("派生值预览", "随控件即时重算,未应用"));
        _preview.Foreground = new SolidColorBrush(Color.FromRgb(0x2C, 0x33, 0x3E));
        root.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF7)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD5, 0xDA, 0xE2)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 8),
            Child = _preview,
        });

        // 动作条
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        actions.Children.Add(Button("应用", () => ApplyAsync(reset: false)));
        actions.Children.Add(Button("应用并重置战场", () => ApplyAsync(reset: true)));
        actions.Children.Add(Button("刷新回读", Reload));
        actions.Children.Add(Button("恢复出厂默认", () => RunAsync("arena.default turrets=true reset=true"), danger: true));
        root.Children.Add(actions);

        var scenarioRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        scenarioRow.Children.Add(new TextBlock { Text = "剧本", Width = 96, VerticalAlignment = VerticalAlignment.Center });
        scenarioRow.Children.Add(_scenarioName);
        scenarioRow.Children.Add(Button("存为剧本", () => RunAsync($"scenario.save name={_scenarioName.Text.Trim()}")));
        scenarioRow.Children.Add(Button("读取剧本", () => RunAsync($"scenario.load {_scenarioName.Text.Trim()}")));
        root.Children.Add(scenarioRow);
        root.Children.Add(_status);

        // 窄停靠(≈300px)时也不能有控件被裁掉够不到 → 允许横向滚动
        Content = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        Reload();
        _assistTimer.Tick += (_, _) => RefreshAssistMetrics();
        Loaded += (_, _) => _assistTimer.Start();
        Unloaded += (_, _) => _assistTimer.Stop();
    }

    public void AttachBus(CommandBus bus) => _bus = bus;

    // ── 回读 / 预览 ─────────────────────────────────────────

    private void Reload()
    {
        _loading = true;
        var arena = _config.Arena;
        Write("w", arena.Width);
        Write("h", arena.Height);
        Write("radius", arena.TurretRadius);
        Write("ring", arena.ShieldRingScale);
        Write("mx", arena.TurretMarginXRatio);
        Write("my", arena.TurretMarginYRatio);
        Write("cell", arena.CellSize);
        Write("sudden", arena.SuddenDeathAtSeconds);
        Write("initshield", _config.Turrets.Count == 0 ? 0 : _config.Turrets.Min(x => x.InitialShield));
        Write("maxhp", _config.Turrets.Count == 0 ? 0 : _config.Turrets.Min(x => x.MaxHp));
        Write("cost", arena.ShieldCostPerValue);
        Write("sizeFactor", arena.ShellSizeCellFactor);
        Write("sizeExp", arena.ShellSizeValueExponent);
        Write("sizeMin", arena.ShellSizeMinCells);
        Write("sizeMax", arena.ShellSizeMaxCells);
        Write("speedJitter", arena.ShellSpeedJitter);
        Write("speedExp", arena.ShellSpeedValueExponent);
        Write("speedMin", arena.ShellSpeedMin);
        Write("speedMax", arena.ShellSpeedMax);
        Write("weightScale", arena.ShellWeightScale);
        Write("speedScale", arena.ProjectileSpeedScale);
        Write("preloadCount", arena.InitialShellCount);
        Write("preloadValue", arena.InitialShellValue);
        Write("smallSpeed", arena.SmallBallSpeed);
        Write("smallSize", arena.SmallBallSizeCellFactor);
        Write("labelFactor", arena.ShellLabelFontFactor);
        Write("labelMin", arena.ShellLabelFontMin);
        Write("labelMax", arena.ShellLabelFontMax);
        Write("labelOutside", arena.ShellLabelOutsideOpacity);
        Write("gravity", arena.GravityG);
        Write("maxProj", arena.MaxProjectiles);
        Write("scaleK", 1);
        var assist = _balance.Current;
        Write("assistSmall", assist.FriendlyAbsorbSmallRate);
        Write("assistShell", assist.FriendlyShellTransferRate);
        Write("assistReach", assist.FriendlyAssistReachFactor);
        Write("assistMax", assist.FriendlyAssistMaxValue);
        _friendlyAssist.IsChecked = assist.FriendlyAssistEnabled;
        _assistVisual.IsChecked = assist.FriendlyAssistVisualEnabled;

        _mode.SelectedItem = _mode.Items.Cast<string>()
            .FirstOrDefault(x => x.Equals(arena.Mode?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "territory";
        _targeting.SelectedItem = _targeting.Items.Cast<string>()
            .FirstOrDefault(x => x.Equals(arena.Targeting?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "spin";
        _ballCollision.IsChecked = arena.BallCollision;

        var names = _weapons.Weapons.Select(x => x.Name).ToList();
        FillCombo(_preloadWeapon, names, arena.InitialShellWeapon);
        FillCombo(_speedWeapon, names, arena.InitialShellWeapon);
        SyncWeaponSpeedField();
        _sizePreset.SelectedIndex = -1;

        _loading = false;
        RefreshPreview();
        RefreshAssistMetrics();
    }

    private void RefreshPreview()
    {
        if (_loading)
            return;
        try
        {
            var metrics = ArenaMetrics.Compute(PreviewArena(), PreviewTurrets(), _weapons);
            _preview.Text = metrics.Format(_stage.LogicalWidth, _stage.LogicalHeight);
        }
        catch (Exception ex)
        {
            _preview.Text = $"预览失败: {ex.Message}";
        }
    }

    /// <summary>按控件当前值构造一份"假如应用了"的配置(不写盘,只用于派生值预览)。</summary>
    private ArenaLayoutConfig PreviewArena()
    {
        var arena = _config.Arena;
        return new ArenaLayoutConfig
        {
            Name = arena.Name,
            Width = Read("w", arena.Width),
            Height = Read("h", arena.Height),
            GravityG = Read("gravity", arena.GravityG),
            BallCollision = _ballCollision.IsChecked == true,
            Targeting = (_targeting.SelectedItem as string) ?? arena.Targeting,
            TurretRadius = Read("radius", arena.TurretRadius),
            MaxProjectiles = (int)Read("maxProj", arena.MaxProjectiles),
            ProjectileLifetimeSec = arena.ProjectileLifetimeSec,
            Mode = (_mode.SelectedItem as string) ?? arena.Mode,
            CellSize = Read("cell", arena.CellSize),
            SuddenDeathAtSeconds = Read("sudden", arena.SuddenDeathAtSeconds),
            TurretMarginXRatio = Read("mx", arena.TurretMarginXRatio),
            TurretMarginYRatio = Read("my", arena.TurretMarginYRatio),
            ShieldRingScale = Read("ring", arena.ShieldRingScale),
            ShieldCostPerValue = Read("cost", arena.ShieldCostPerValue),
            ProjectileSpeedScale = Read("speedScale", arena.ProjectileSpeedScale),
            ShellSizeCellFactor = Read("sizeFactor", arena.ShellSizeCellFactor),
            ShellSizeValueExponent = Read("sizeExp", arena.ShellSizeValueExponent),
            ShellSizeMinCells = Read("sizeMin", arena.ShellSizeMinCells),
            ShellSizeMaxCells = Read("sizeMax", arena.ShellSizeMaxCells),
            ShellSpeedJitter = Read("speedJitter", arena.ShellSpeedJitter),
            ShellSpeedValueExponent = Read("speedExp", arena.ShellSpeedValueExponent),
            ShellSpeedMin = Read("speedMin", arena.ShellSpeedMin),
            ShellSpeedMax = Read("speedMax", arena.ShellSpeedMax),
            ShellWeightScale = Read("weightScale", arena.ShellWeightScale),
            InitialShellCount = (int)Read("preloadCount", arena.InitialShellCount),
            InitialShellValue = (long)Read("preloadValue", arena.InitialShellValue),
            InitialShellWeapon = (_preloadWeapon.SelectedItem as string) ?? arena.InitialShellWeapon,
            SmallBallSpeed = Read("smallSpeed", arena.SmallBallSpeed),
            SmallBallSizeCellFactor = Read("smallSize", arena.SmallBallSizeCellFactor),
            ShellLabelFontFactor = Read("labelFactor", arena.ShellLabelFontFactor),
            ShellLabelFontMin = Read("labelMin", arena.ShellLabelFontMin),
            ShellLabelFontMax = Read("labelMax", arena.ShellLabelFontMax),
            ShellLabelOutsideOpacity = Read("labelOutside", arena.ShellLabelOutsideOpacity),
        };
    }

    private List<TurretDefinition> PreviewTurrets()
    {
        var initialShield = Read("initshield", 0);
        var maxHp = Read("maxhp", 1);
        return _config.Turrets.Select(x => new TurretDefinition
        {
            Id = x.Id,
            Name = x.Name,
            Color = x.Color,
            Quadrant = x.Quadrant,
            InitialBalls = x.InitialBalls,
            InitialMultiplier = x.InitialMultiplier,
            MaxHp = maxHp,
            MaxShield = Math.Max(x.MaxShield, initialShield),
            InitialShield = Math.Max(0, initialShield),
            ProjectileSize = x.ProjectileSize,
            ProjectileCount = x.ProjectileCount,
            FireIntervalSec = x.FireIntervalSec,
            BarrelRpm = x.BarrelRpm,
        }).ToList();
    }

    // ── 应用(全部经命令下发) ───────────────────────────────

    private async void ApplyAsync(bool reset)
    {
        var arena = _config.Arena;
        var commands = new List<string>();

        if (Changed("w", arena.Width) || Changed("h", arena.Height))
            commands.Add($"arena.size w={Read("w", arena.Width)} h={Read("h", arena.Height)}");
        if (Changed("radius", arena.TurretRadius) || Changed("mx", arena.TurretMarginXRatio)
            || Changed("my", arena.TurretMarginYRatio) || Changed("ring", arena.ShieldRingScale))
        {
            commands.Add($"arena.turret radius={Read("radius", arena.TurretRadius)} mx={Read("mx", arena.TurretMarginXRatio)} "
                         + $"my={Read("my", arena.TurretMarginYRatio)} ring={Read("ring", arena.ShieldRingScale)}");
        }
        if (Changed("cell", arena.CellSize))
            commands.Add($"arena.cell size={Read("cell", arena.CellSize)}");
        if (_mode.SelectedItem is string mode && !mode.Equals(arena.Mode?.Trim(), StringComparison.OrdinalIgnoreCase))
            commands.Add($"arena.mode {mode}");
        if (_targeting.SelectedItem is string targeting
            && !targeting.Equals(arena.Targeting?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            commands.Add($"arena.targeting mode={targeting}");
        }
        if (Changed("sudden", arena.SuddenDeathAtSeconds))
            commands.Add($"arena.suddendeath at={Read("sudden", arena.SuddenDeathAtSeconds)}");
        if (Changed("cost", arena.ShieldCostPerValue))
            commands.Add($"arena.shield cost={Read("cost", arena.ShieldCostPerValue)}");

        var initShield = _config.Turrets.Count == 0 ? 0 : _config.Turrets.Min(x => x.InitialShield);
        var maxHp = _config.Turrets.Count == 0 ? 0 : _config.Turrets.Min(x => x.MaxHp);
        var shieldsDiffer = _config.Turrets.Select(x => x.InitialShield).Distinct().Count() > 1
                            || _config.Turrets.Select(x => x.MaxHp).Distinct().Count() > 1;
        if (shieldsDiffer || Changed("initshield", initShield) || Changed("maxhp", maxHp))
        {
            commands.Add($"turret.setall initshield={Read("initshield", initShield)} "
                         + $"hp={Read("maxhp", maxHp)}");
        }

        var shell = new List<string>();
        AddIfChanged(shell, "sizeFactor", arena.ShellSizeCellFactor, "sizeFactor");
        AddIfChanged(shell, "sizeExp", arena.ShellSizeValueExponent, "sizeExp");
        AddIfChanged(shell, "sizeMin", arena.ShellSizeMinCells, "sizeMin");
        AddIfChanged(shell, "sizeMax", arena.ShellSizeMaxCells, "sizeMax");
        AddIfChanged(shell, "speedJitter", arena.ShellSpeedJitter, "speedJitter");
        AddIfChanged(shell, "speedExp", arena.ShellSpeedValueExponent, "speedExp");
        AddIfChanged(shell, "speedMin", arena.ShellSpeedMin, "speedMin");
        AddIfChanged(shell, "speedMax", arena.ShellSpeedMax, "speedMax");
        AddIfChanged(shell, "weightScale", arena.ShellWeightScale, "weightScale");
        AddIfChanged(shell, "speedScale", arena.ProjectileSpeedScale, "speedScale");
        if (shell.Count > 0)
            commands.Add("arena.shell " + string.Join(" ", shell));

        var preloadWeapon = (_preloadWeapon.SelectedItem as string) ?? arena.InitialShellWeapon;
        if (Changed("preloadCount", arena.InitialShellCount) || Changed("preloadValue", arena.InitialShellValue)
            || !preloadWeapon.Equals(arena.InitialShellWeapon, StringComparison.OrdinalIgnoreCase))
        {
            commands.Add($"arena.preload count={(int)Read("preloadCount", arena.InitialShellCount)} "
                         + $"value={(long)Read("preloadValue", arena.InitialShellValue)} weapon={preloadWeapon}");
        }

        var small = new List<string>();
        AddIfChanged(small, "smallSpeed", arena.SmallBallSpeed, "speed");
        AddIfChanged(small, "smallSize", arena.SmallBallSizeCellFactor, "size");
        if (small.Count > 0)
            commands.Add("arena.small " + string.Join(" ", small));

        var label = new List<string>();
        AddIfChanged(label, "labelFactor", arena.ShellLabelFontFactor, "factor");
        AddIfChanged(label, "labelMin", arena.ShellLabelFontMin, "min");
        AddIfChanged(label, "labelMax", arena.ShellLabelFontMax, "max");
        AddIfChanged(label, "labelOutside", arena.ShellLabelOutsideOpacity, "outside");
        if (label.Count > 0)
            commands.Add("arena.label " + string.Join(" ", label));

        if (Changed("gravity", arena.GravityG))
            commands.Add($"arena.gravity g={Read("gravity", arena.GravityG)}");
        if (Changed("maxProj", arena.MaxProjectiles))
            commands.Add($"arena.limit max={(int)Read("maxProj", arena.MaxProjectiles)}");
        if ((_ballCollision.IsChecked == true) != arena.BallCollision)
            commands.Add($"arena.collision on={(_ballCollision.IsChecked == true).ToString().ToLowerInvariant()}");

        var assist = _balance.Current;
        if ((_friendlyAssist.IsChecked == true) != assist.FriendlyAssistEnabled
            || (_assistVisual.IsChecked == true) != assist.FriendlyAssistVisualEnabled
            || Changed("assistSmall", assist.FriendlyAbsorbSmallRate)
            || Changed("assistShell", assist.FriendlyShellTransferRate)
            || Changed("assistReach", assist.FriendlyAssistReachFactor)
            || Changed("assistMax", assist.FriendlyAssistMaxValue))
        {
            commands.Add(
                $"balance.assist enabled={(_friendlyAssist.IsChecked == true).ToString().ToLowerInvariant()} "
                + $"visual={(_assistVisual.IsChecked == true).ToString().ToLowerInvariant()} "
                + $"smallRate={Read("assistSmall", assist.FriendlyAbsorbSmallRate).ToString("0.####", CultureInfo.InvariantCulture)} "
                + $"shellRate={Read("assistShell", assist.FriendlyShellTransferRate).ToString("0.####", CultureInfo.InvariantCulture)} "
                + $"reach={Read("assistReach", assist.FriendlyAssistReachFactor).ToString("0.####", CultureInfo.InvariantCulture)} "
                + $"max={(int)Read("assistMax", assist.FriendlyAssistMaxValue)}");
        }

        if (commands.Count == 0 && !reset)
        {
            Status("没有改动需要应用");
            return;
        }
        if (reset)
            commands.Add("battle.reset");

        await ExecuteAllAsync(commands);
    }

    private void ApplyWeaponSpeed()
    {
        if (_speedWeapon.SelectedItem is not string name)
            return;
        RunAsync($"weapon.set name={name} key=speed val={Read("weaponSpeed", 360)}");
    }

    private async void RunAsync(string command) => await ExecuteAllAsync([command]);

    private async Task ExecuteAllAsync(IReadOnlyList<string> commands)
    {
        if (_bus == null)
        {
            Status("命令总线未连接");
            return;
        }
        var failures = new List<string>();
        foreach (var command in commands)
        {
            var result = await _bus.ExecuteAsync(command, "对战区设置窗");
            if (!result.Success)
                failures.Add($"{command} → {result.Message}");
        }
        Status(failures.Count == 0
            ? $"已下发 {commands.Count} 条命令:{string.Join(" · ", commands.Select(Head))}"
            : $"部分失败:{string.Join(" ｜ ", failures)}");
        Reload();
    }

    private void OnSizePresetChanged()
    {
        if (_loading || _sizePreset.SelectedIndex < 0)
            return;
        // 出厂中档 960×900/半径 26/格 10 为基准,按档等比填入(不自动应用)
        var k = _sizePreset.SelectedIndex switch { 0 => 0.75, 2 => 1.5, _ => 1.0 };
        Write("w", 960 * k);
        Write("h", 900 * k);
        Write("radius", 26 * k);
        Write("cell", 10 * k);
        Write("speedScale", k);
        RefreshPreview();
        Status($"已填入规模档 ×{k:0.##};点「应用并重置战场」生效(格数与血量不变)");
    }

    private void SyncWeaponSpeedField()
    {
        if (_speedWeapon.SelectedItem is string name && _weapons.TryResolve(name, out var weapon))
            Write("weaponSpeed", weapon.Speed);
    }

    private void RefreshAssistMetrics()
    {
        var status = _battle.FriendlyAssistStatus();
        _assistMetrics.Text = $"在场 small={status.SmallShots} shell={status.Shells} ember={status.Embers} other={status.Others}\n"
                              + $"最近1秒 小球转移={status.SmallTransferred} 大球转移={status.ShellTransferred} 回收={status.Reclaimed}";
    }

    // ── 控件构造小工具 ──────────────────────────────────────

    private static TextBlock Title(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 2),
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        Foreground = HintBrush,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6, 0, 0, 4),
    };

    private static UIElement Section(string text, string when)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            Foreground = SectionBrush,
        });
        if (!string.IsNullOrEmpty(when))
            panel.Children.Add(Hint(when));
        return panel;
    }

    private TextBox Field(string key, string initial, double width)
    {
        var box = new TextBox
        {
            Text = initial,
            Width = width,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        box.TextChanged += (_, _) => RefreshPreview();
        _fields[key] = box;
        return box;
    }

    private UIElement Pair(string label, string keyA, string keyB, string? hint = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Width = 124,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(Field(keyA, "0", 68));
        panel.Children.Add(Field(keyB, "0", 68));
        if (!string.IsNullOrEmpty(hint))
            panel.Children.Add(Hint(hint));
        return panel;
    }

    private static UIElement Row(string label, params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Width = 124,
            VerticalAlignment = VerticalAlignment.Center,
        });
        foreach (var child in children)
        {
            if (child is FrameworkElement element)
                element.Margin = new Thickness(0, 0, 6, 0);
            panel.Children.Add(child);
        }
        return panel;
    }

    private static Button Button(string text, Action onClick, bool danger = false)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 2, 6, 2),
        };
        if (danger)
            button.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x27, 0x27));
        button.Click += (_, _) => onClick();
        return button;
    }

    // ── 值读写 ──────────────────────────────────────────────

    private double Read(string key, double fallback)
    {
        if (!_fields.TryGetValue(key, out var box))
            return fallback;
        return double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    private void Write(string key, double value)
    {
        if (_fields.TryGetValue(key, out var box))
            box.Text = value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private bool Changed(string key, double current) =>
        Math.Abs(Read(key, current) - current) > 1e-9;

    private void AddIfChanged(List<string> parts, string key, double current, string param)
    {
        if (Changed(key, current))
            parts.Add($"{param}={Read(key, current).ToString("0.####", CultureInfo.InvariantCulture)}");
    }

    private static void FillCombo(ComboBox combo, IReadOnlyList<string> items, string? selected)
    {
        combo.Items.Clear();
        foreach (var item in items)
            combo.Items.Add(item);
        combo.SelectedItem = items.FirstOrDefault(x =>
            x.Equals(selected?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? items.FirstOrDefault();
        combo.MinWidth = 92;
    }

    private void Status(string text) => _status.Text = text;

    private static string Head(string command) => command.Split(' ')[0];
}
