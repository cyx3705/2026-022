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
    private readonly SceneDebugView _sceneDebug;
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
        var objectDebug = new ObjectDebugView(world);
        var ball = new BallObjectView(world);
        var referee = new RefereeView(world);
        _sceneDebug = new SceneDebugView(world, objectDebug, ball, referee);

        Stage = stageState;
        BattleWorld = new SceneWorld
        {
            Defaults = world.Defaults,
            GravityG = 0,
            BallCollisionEnabled = battleConfig.Arena.BallCollision,
            Seed = world.Seed,
            WallRestitution = balanceConfig.Current.WallRestitution,
            BallRestitution = balanceConfig.Current.BallRestitution,
        };
        Battle = new BattleRuntime(world, BattleWorld, battleConfig, weapons, log, balanceConfig);
        Director = new BattleDirector(world, BattleWorld, Battle, weapons, economyBridge, Stage, log, balanceConfig);
        var arenaView = new ArenaView(BattleWorld, Battle);
        _stageView = new StageView(Stage, world, BattleWorld, Battle, Director, _dropZone, arenaView, weapons, renderTime.Current);
        SyncAutoStep();
        Stage.Changed += SyncAutoStep;
        Director.StateChanged += SyncAutoStep;

        // v3.1:对战区设置窗(命令的图形外壳)
        _arenaSettings = new ArenaSettingsView(battleConfig, balanceConfig, Battle, weapons, Stage);
        _balanceSettings = new BalanceSettingsView(balanceConfig, battleConfig, presets);
        _renderSettings = new RenderSettingsView(renderJobs);

        _commandViews = [_dropZone, _sceneDebug, _arenaSettings, _balanceSettings, _renderSettings];
        ToolWindows = CreateToolWindows();
    }

    public StageState Stage { get; }

    public SceneWorld BattleWorld { get; }

    public BattleRuntime Battle { get; }

    public BattleDirector Director { get; }

    public DropZoneView EconomyView => _dropZone;

    public StageView StageView => _stageView;

    public RealtimeSimulationCoordinator Coordinator => _stageView.Coordinator;

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
        // AppShell 3.0.0 以 DockSide.Center 明确冻结中央标签区契约。
        new()
        {
            Id = "stage",
            Title = "对战舞台",
            DefaultSide = DockSide.Center,
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
            Id = "console",
            Title = "控制台",
            DefaultSide = DockSide.Bottom,
            DefaultRatio = 0.28,
        },
        // v3.5:对象、小球与裁判合并为默认可见的场景调试工作台。
        new()
        {
            Id = "scenedebug",
            Title = "场景调试",
            DefaultSide = DockSide.Right,
            DefaultRatio = 0.28,
            DefaultVisible = true,
            ContentFactory = () => _sceneDebug,
        },
        // v3.1 AW-01:「对战区」设置窗默认隐藏，并入场景调试标签组。
        new()
        {
            Id = "arenaset",
            Title = "对战区",
            DefaultSide = DockSide.Tab,
            DefaultTabTarget = "scenedebug",
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
