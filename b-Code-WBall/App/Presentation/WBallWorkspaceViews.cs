using AppShell.Core.Commands;
using AppShell.Core.Docking;
using AppShell.Core.Logging;
using WBall.BallUi;
using WBall.Battle;
using WBall.Debug;
using WBall.DropZone;
using WBall.Game;
using WBall.Model;
using WBall.Stage;
using WBall.Recording;

namespace WBall.Presentation;

/// <summary>
/// WBall 页面工作区。集中管理业务视图的创建、停靠注册和命令总线连接。
/// </summary>
internal sealed class WBallWorkspaceViews
{
    private readonly DropZoneView _dropZone;
    private readonly ObjectDebugView _objectDebug;
    private readonly BallObjectView _ball;
    private readonly RefereeView _referee;
    private readonly ArenaSettingsView _arenaSettings;
    private readonly BalanceSettingsView _balanceSettings;
    private readonly RenderSettingsView _renderSettings;
    private readonly StageView _stageView;
    private readonly IReadOnlyList<ICommandBusAware> _commandViews;

    public WBallWorkspaceViews(
        SceneWorld world,
        IShellLog log,
        string dataRoot,
        BattleConfigStore battleConfig,
        BalanceConfigStore balanceConfig,
        PresetStore presets,
        WeaponCatalog weapons,
        EconomyBridge economyBridge,
        RenderTimeConfigStore renderTime,
        StageState stageState,
        RenderJobService renderJobs)
    {
        _dropZone = new DropZoneView(world, log);
        _dropZone.AutoStepEnabled = false;
        _objectDebug = new ObjectDebugView(world);
        _ball = new BallObjectView(world, dataRoot);
        _referee = new RefereeView(world);

        Stage = stageState;
        BattleWorld = new SceneWorld
        {
            Defaults = world.Defaults,
            GravityG = 0,
            BallCollisionEnabled = true,
            Seed = world.Seed,
            WallRestitution = balanceConfig.Current.WallRestitution,
            BallRestitution = balanceConfig.Current.BallRestitution,
        };
        Battle = new BattleRuntime(world, BattleWorld, battleConfig, weapons, log, balanceConfig);
        Director = new BattleDirector(world, BattleWorld, Battle, weapons, economyBridge, Stage, log, balanceConfig);
        var arenaView = new ArenaView(BattleWorld, Battle);
        _stageView = new StageView(Stage, world, BattleWorld, Director, _dropZone, arenaView, weapons, renderTime.Current);
        SyncAutoStep();
        Stage.Changed += SyncAutoStep;
        Director.StateChanged += SyncAutoStep;

        // v3.1:对战区设置窗(命令的图形外壳)
        _arenaSettings = new ArenaSettingsView(battleConfig, balanceConfig, Battle, weapons, Stage);
        _balanceSettings = new BalanceSettingsView(balanceConfig, battleConfig, presets);
        _renderSettings = new RenderSettingsView(renderJobs);

        _commandViews = [_dropZone, _objectDebug, _ball, _referee, _arenaSettings, _balanceSettings, _renderSettings];
        ToolWindows = CreateToolWindows();
    }

    public StageState Stage { get; }

    public SceneWorld BattleWorld { get; }

    public BattleRuntime Battle { get; }

    public BattleDirector Director { get; }

    public DropZoneView EconomyView => _dropZone;

    public StageView StageView => _stageView;

    public IReadOnlyList<ToolWindowDescriptor> ToolWindows { get; }

    public void AttachCommands(CommandBus bus)
    {
        foreach (var view in _commandViews)
            view.AttachBus(bus);
    }

    private void SyncAutoStep()
    {
        var directorActive = Director.State is DirectorState.Countdown
            or DirectorState.Running
            or DirectorState.Settling;
        var recording = Stage.Mode == StageMode.Record;
        _dropZone.AutoStepEnabled = !directorActive && !recording;
    }

    private IReadOnlyList<ToolWindowDescriptor> CreateToolWindows() =>
    [
        // v3.4 V34-06:AppShell 0.7.2 删除了 ShellConfig.MainContent(中央区只剩无标签空背景),
        // 对战舞台改为普通工具窗口。DockSide.Top 的窗格由 DockingHost 插进**中央列**,
        // 因此舞台仍占据窗口中部;底部的表窗口/控制台同在中央列下方,布局观感与 0.5.0 时代一致。
        new()
        {
            Id = "stage",
            Title = "对战舞台",
            DefaultSide = DockSide.Top,
            DefaultRatio = 0.74,
            ContentFactory = () => _stageView,
        },
        new()
        {
            Id = "resource",
            Title = "资源窗口",
            DefaultSide = DockSide.Left,
            DefaultRatio = 0.16,
        },
        new()
        {
            Id = "table",
            Title = "表窗口",
            DefaultSide = DockSide.Bottom,
            DefaultRatio = 0.28,
        },
        new()
        {
            Id = "console",
            Title = "控制台",
            DefaultSide = DockSide.Tab,
            DefaultTabTarget = "table",
            DefaultRatio = 0.28,
        },
        // v2.8:调试三窗默认隐藏并合为一组标签,按需 win.show 唤出
        new()
        {
            Id = "objdebug",
            Title = "对象调试",
            DefaultSide = DockSide.Right,
            DefaultRatio = 0.22,
            DefaultVisible = false,
            ContentFactory = () => _objectDebug,
        },
        new()
        {
            Id = "ballpanel",
            Title = "小球",
            DefaultSide = DockSide.Tab,
            DefaultTabTarget = "objdebug",
            DefaultRatio = 0.24,
            DefaultVisible = false,
            ContentFactory = () => _ball,
        },
        new()
        {
            Id = "referee",
            Title = "裁判区",
            DefaultSide = DockSide.Tab,
            DefaultTabTarget = "objdebug",
            DefaultRatio = 0.22,
            DefaultVisible = false,
            ContentFactory = () => _referee,
        },
        // v3.1 AW-01:「对战区」设置窗 — 默认隐藏,win.show name=arenaset 或「对战台→对战区设置」唤出。
        // 并入既有调试标签组(objdebug)而非新开右侧窗格:新窗格拿不到稳定比例会塌缩;
        // 面板 id(battle/editor)由面板系统后置创建,注册期不能作为 tab 目标。
        new()
        {
            Id = "arenaset",
            Title = "对战区",
            DefaultSide = DockSide.Tab,
            DefaultTabTarget = "objdebug",
            DefaultRatio = 0.26,
            DefaultVisible = false,
            ContentFactory = () => _arenaSettings,
        },
        new()
        {
            Id = "balance",
            Title = "战斗平衡",
            DefaultSide = DockSide.Tab,
            DefaultTabTarget = "arenaset",
            DefaultRatio = 0.28,
            DefaultVisible = false,
            ContentFactory = () => _balanceSettings,
        },
        new()
        {
            Id = "render",
            Title = "出片与时间",
            DefaultSide = DockSide.Tab,
            DefaultTabTarget = "balance",
            DefaultRatio = 0.30,
            DefaultVisible = false,
            ContentFactory = () => _renderSettings,
        },
    ];
}
