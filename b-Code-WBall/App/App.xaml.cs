using System.Windows;
using AppShell.Core.Commands;
using AppShell.Core.Logging;
using AppShell.Services;
using AppShell.Shell;
using WBall.Battle;
using WBall.Commands;
using WBall.Data;
using WBall.Game;
using WBall.Model;
using WBall.Presentation;
using WBall.Recording;
using WBall.Stage;

namespace AppShell.App;

/// <summary>WBall 装配点(自由区)。v2.0:对战舞台/经济桥/录制。</summary>
public partial class App : Application
{
    private ShellLog? _log;
    private RenderJobService? _renderJobs;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = new AppPaths("WBall");
        var log = new ShellLog(paths);
        var settings = new SettingsService(paths);
        _log = log;

        DispatcherUnhandledException += (_, args) =>
        {
            log.Log(ShellLogLevel.Fatal, "app", $"未处理异常: {args.Exception}");
            MessageBox.Show($"发生未处理异常,已记录日志:\n{args.Exception.Message}",
                "WBall", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            log.Log(ShellLogLevel.Fatal, "app", $"未处理异常(非 UI 线程): {args.ExceptionObject}");

        var workspaceRoot = settings.Get("workspace.root") ?? paths.WorkspaceDir;
        var workspace = new WorkspaceService(workspaceRoot);
        var scenesDir = System.IO.Path.Combine(workspace.Root, "scenes");
        System.IO.Directory.CreateDirectory(scenesDir);

        MigrateLegacyPanels(paths, log);
        MigrateStageWindowLayout(paths, log);
        SeedBattlePanel(paths, log);
        SeedEditorPanel(paths, log);

        var world = new SceneWorld();
        world.Defaults = PublicDefaultsStore.Load(paths.Root);
        SeedDefaultFactionsIfEmpty(world);

        // v2.6 BT-01:冷启动默认装填 Plinko 经济场景,左侧即为落球盘
        try
        {
            SceneStore.Load(world, PlinkoDemoSeeder.EnsureScene(scenesDir, log));
        }
        catch (Exception ex)
        {
            log.Warn("scene", $"默认 Plinko 场景加载失败,回退空画布: {ex.Message}");
        }

        var weapons = new WeaponCatalog(paths.Root, log);
        var balanceConfig = new BalanceConfigStore(paths.Root, log);
        var economyBridge = new EconomyBridge(weapons, log, balanceConfig);
        world.Settlements = economyBridge;
        var battleConfig = new BattleConfigStore(paths.Root, log);
        var scenarios = new ScenarioStore(workspace.Root, log);
        var presets = new PresetStore(paths.Root, log);
        var simulator = new BalanceSimulator(world, battleConfig, weapons, log);
        var renderTime = new RenderTimeConfigStore(paths.Root, log);

        // v3.4:取消数据库后,这里只剩"自定义性质登记 + 场景文件目录",无需 Dispose
        var sceneProperties = new ScenePropertyService(world, paths.Root);

        var stageState = new StageState();
        var renderJobs = new RenderJobService(
            world, battleConfig, balanceConfig, weapons, stageState, scenarios, renderTime,
            paths.Root, workspace.Root, log);
        _renderJobs = renderJobs;
        var workspaceViews = new WBallWorkspaceViews(
            world, log, paths.Root, battleConfig, balanceConfig, presets, weapons, economyBridge,
            renderTime, stageState, renderJobs);

        var config = new ShellConfig
        {
            AppName = "WBall",
            AppVersion = "3.3.0",
            Workspace = workspace,
            // v3.4:取消数据库 —— 不再提供 DataService,于是 db.* 不注册、表窗口不存在。
            // MCP 网关依赖 DataService(没有它框架会自动降级并告警),这里显式关掉,免得每次启动刷警告。
            // 注意:模块托管(EnableModules)与数据库无关,保持框架默认(开),行为与 v3.3 一致。
            EnableMcp = false,
            // v3.4 V34-06:0.7.2 起没有 MainContent —— 舞台改为 id=stage 的工具窗口(见 ToolWindows)
            OnResourceOpen = path => TryOpenSceneFile(path, world, log, sceneProperties, scenesDir),
        };

        config.ToolWindows.AddRange(workspaceViews.ToolWindows);

        config.ConfigureCommands = registry =>
        {
            WBallCommands.Register(registry, world, log, scenesDir, sceneProperties, paths.Root);
            StageCommands.Register(registry, workspaceViews.Stage, world, workspaceViews.Battle);
            WeaponCommands.Register(registry, weapons);
            BattleCommands.Register(registry, workspaceViews.Battle, workspaceViews.BattleWorld, battleConfig);
            // v3.1:对战区自定义命令族(设置窗只是它们的图形外壳)
            ArenaConfigCommands.Register(
                registry,
                workspaceViews.Battle,
                workspaceViews.BattleWorld,
                battleConfig,
                weapons,
                workspaceViews.Stage);
            BalanceCommands.Register(
                registry,
                balanceConfig,
                battleConfig,
                presets,
                simulator,
                workspaceViews.Battle,
                workspaceViews.Director,
                workspaceViews.BattleWorld);
            DirectorCommands.Register(
                registry,
                workspaceViews.Director,
                weapons,
                scenarios,
                battleConfig,
                balanceConfig,
                workspaceViews.Battle,
                world);
            HudCommands.Register(registry, workspaceViews.Stage, workspaceViews.StageView.Hud, workspaceViews.Director);
            ScenarioCommands.Register(
                registry,
                scenarios,
                battleConfig,
                balanceConfig,
                weapons,
                workspaceViews.Battle,
                workspaceViews.Director,
                world);
            DemoCommands.Register(
                registry,
                workspaceViews.Stage,
                scenarios,
                battleConfig,
                balanceConfig,
                weapons,
                workspaceViews.Battle,
                workspaceViews.Director,
                world,
                log);
            RecordCommands.Register(registry, renderJobs, workspaceViews.StageView.ApplyTimeConfig);
        };

        var window = new ShellWindow(config, new FileLayoutStore(paths), log, settings, paths.Root);
        workspaceViews.AttachCommands(window.Commands);
        MainWindow = window;
        window.Show();

        log.Info("app", $"WBall {config.AppVersion} 启动,数据目录: {paths.Root}, 场景目录: {scenesDir}");

        var startupCommands = new List<string>();
        for (var i = 0; i < e.Args.Length - 1; i++)
        {
            if (e.Args[i] == "--exec")
                startupCommands.Add(e.Args[++i]);
        }

        if (startupCommands.Count > 0)
            _ = RunStartupCommandsAsync(window, startupCommands);
    }

    /// <summary>启动时若无阵营则种子蓝/红两队。</summary>
    public static void SeedDefaultFactionsIfEmpty(SceneWorld world)
    {
        if (world.Factions.Count > 0)
            return;
        world.Factions.Add(new Faction
        {
            Id = "blue",
            Name = "蓝队",
            Color = "#3B82F6",
            InitialBalls = 3,
            InitialMultiplier = 1,
            Score = 0,
        });
        world.Factions.Add(new Faction
        {
            Id = "red",
            Name = "红队",
            Color = "#EF4444",
            InitialBalls = 3,
            InitialMultiplier = 1,
            Score = 0,
        });
    }

    private static bool TryOpenSceneFile(
        string absolutePath,
        SceneWorld world,
        ShellLog log,
        ScenePropertyService sceneProperties,
        string scenesDirectory)
    {
        if (!absolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            var text = System.IO.File.ReadAllText(absolutePath);
            if (!text.Contains("\"objects\"", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("\"Objects\"", StringComparison.Ordinal))
                return false;

            SceneStore.Load(world, absolutePath);
            var diffs = SceneStore.DiffAgainstFile(world, absolutePath);
            sceneProperties.RefreshScenes(scenesDirectory, world.LastScenePath);
            if (diffs.Count > 0)
            {
                log.Warn("scene", $"加载后核对不一致: {string.Join("; ", diffs)}");
                return true;
            }

            log.Info("scene", $"资源窗口打开场景并核对一致: {absolutePath}");
            return true;
        }
        catch (Exception ex)
        {
            log.Warn("scene", $"打开场景失败: {ex.Message}");
            return false;
        }
    }

    private static async Task RunStartupCommandsAsync(ShellWindow window, List<string> commands)
    {
        foreach (var command in commands)
            await window.Commands.ExecuteAsync(command, "脚本:startup");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _renderJobs?.Dispose();
        _log?.Dispose();
        base.OnExit(e);
    }

    /// <summary>v2.8:一次性迁移 — 移除旧面板并重置停靠布局套用新默认。</summary>
    /// <summary>
    /// v3.4 V34-06:一次性布局迁移。0.5.0 时代舞台是 ShellConfig.MainContent(布局里是文档节点),
    /// 0.7.2 起它是 id=stage 的工具窗口。旧布局没有 stage 节点,直接恢复会让新窗口落进退化尺寸
    /// (实测只拿到 3% 高度);因此升级首启时丢弃旧停靠布局,套用新默认(舞台占中央列 74%)。
    /// 只删布局文件,面板/场景/配置/剧本等用户数据一律不动 —— 与 v2.8 的迁移做法一致。
    /// </summary>
    private static void MigrateStageWindowLayout(AppPaths paths, ShellLog log)
    {
        try
        {
            var layout = System.IO.Path.Combine(paths.LayoutDir, "current.layout.xml");
            if (!System.IO.File.Exists(layout))
                return;
            var xml = System.IO.File.ReadAllText(layout);
            if (xml.Contains("ContentId=\"stage\"", StringComparison.Ordinal))
                return;

            System.IO.File.Delete(layout);
            log.Info("app", "v3.4 舞台改为工具窗口:已重置停靠布局并套用新默认(仅布局,用户数据未动)");
        }
        catch (Exception ex)
        {
            log.Warn("app", $"舞台窗口布局迁移失败(将沿用现有布局): {ex.Message}");
        }
    }

    private static void MigrateLegacyPanels(AppPaths paths, ShellLog log)
    {
        try
        {
            var panelsDir = System.IO.Path.Combine(paths.Root, "panels");
            System.IO.Directory.CreateDirectory(panelsDir);
            string[] legacy = ["dropzone.json", "scene.json", "wireframe.json", "formula.json", "motor.json"];
            var removedAny = false;
            foreach (var name in legacy)
            {
                var file = System.IO.Path.Combine(panelsDir, name);
                if (!System.IO.File.Exists(file))
                    continue;
                System.IO.File.Delete(file);
                removedAny = true;
                log.Info("app", $"v2.8 已移除旧面板 panels/{name}");
            }

            if (removedAny)
            {
                var layout = System.IO.Path.Combine(paths.LayoutDir, "current.layout.xml");
                if (System.IO.File.Exists(layout))
                {
                    System.IO.File.Delete(layout);
                    log.Info("app", "v2.8 窗口整合:已重置停靠布局,采用新默认(对战台+编辑工具)");
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn("app", $"旧面板迁移失败: {ex.Message}");
        }
    }

    /// <summary>v2.8:「对战台」面板 — 开战/暂停/重开/录制主工作流。</summary>
    private static void SeedBattlePanel(AppPaths paths, ShellLog log)
    {
        try
        {
            var panelsDir = System.IO.Path.Combine(paths.Root, "panels");
            System.IO.Directory.CreateDirectory(panelsDir);
            var file = System.IO.Path.Combine(panelsDir, "battle.json");
            System.IO.File.WriteAllText(file,
                """
                {
                  "id": "battle",
                  "title": "对战台",
                  "visible": true,
                  "side": "right",
                  "ratio": 0.22,
                  "controls": [
                    { "type": "label", "id": "bhint", "label": "对战", "default": "一键开战,自动分胜负" },
                    { "type": "number", "id": "seed", "label": "种子", "min": 0, "max": 999999, "step": 1, "default": "42" },
                    { "type": "button", "label": "一键开战", "command": "demo.play seed={seed}" },
                    { "type": "button", "label": "暂停", "command": "battle.pause" },
                    { "type": "button", "label": "继续", "command": "battle.resume" },
                    { "type": "button", "label": "重开(回编辑)", "command": "battle.reset", "style": "danger" },
                    { "type": "button", "label": "对战状态", "command": "battle.status" },
                    { "type": "label", "id": "rlbl", "label": "出片", "default": "独立计算画面帧,不录屏" },
                    { "type": "number", "id": "recsec", "label": "时长(s)", "min": 5, "max": 600, "step": 5, "default": "60" },
                    { "type": "button", "label": "开始出片", "command": "render.start mode=output seconds={recsec}" },
                    { "type": "button", "label": "出片与时间", "command": "win.show name=render" },
                    { "type": "button", "label": "出片状态", "command": "render.status" },
                    { "type": "label", "id": "mlbl", "label": "更多", "default": "低频操作走控制台" },
                    { "type": "button", "label": "炮台一览", "command": "turret.list" },
                    { "type": "button", "label": "对战区设置", "command": "win.show name=arenaset" },
                    { "type": "button", "label": "战斗平衡", "command": "win.show name=balance" },
                    { "type": "button", "label": "对战区一览", "command": "arena.config" },
                    { "type": "button", "label": "编辑工具", "command": "panel.show id=editor" },
                    { "type": "button", "label": "调试窗", "command": "win.show name=objdebug" }
                  ]
                }
                """);
            log.Info("app", "已写入面板 panels/battle.json");
        }
        catch (Exception ex)
        {
            log.Error("app", $"对战台面板初始化失败: {ex.Message}");
        }
    }

    /// <summary>v2.8:「编辑工具」面板 — 落球工具+场景+线框三合一。</summary>
    private static void SeedEditorPanel(AppPaths paths, ShellLog log)
    {
        try
        {
            var panelsDir = System.IO.Path.Combine(paths.Root, "panels");
            System.IO.Directory.CreateDirectory(panelsDir);
            var file = System.IO.Path.Combine(panelsDir, "editor.json");
            System.IO.File.WriteAllText(file,
                """
                {
                  "id": "editor",
                  "title": "编辑工具",
                  "visible": true,
                  "side": "right",
                  "ratio": 0.22,
                  "controls": [
                    { "type": "label", "id": "toolhint", "label": "放置", "default": "点击画布放置" },
                    { "type": "button", "label": "选择", "command": "scene.tool mode=select" },
                    { "type": "button", "label": "方块", "command": "scene.tool mode=block" },
                    { "type": "button", "label": "重力箭头", "command": "scene.tool mode=arrow" },
                    { "type": "button", "label": "生成器", "command": "scene.tool mode=spawner" },
                    { "type": "button", "label": "销毁器", "command": "scene.tool mode=despawner" },
                    { "type": "label", "id": "wlbl", "label": "线框", "default": "草图,闭合,实体化" },
                    { "type": "button", "label": "线框绘制", "command": "scene.tool mode=wire" },
                    { "type": "button", "label": "闭合提交", "command": "wire.close" },
                    { "type": "button", "label": "撤销一点", "command": "wire.undo" },
                    { "type": "button", "label": "清空草图", "command": "wire.clear" },
                    { "type": "button", "label": "实体化为异形", "command": "wire.solidify" },
                    { "type": "label", "id": "simlbl", "label": "仿真", "default": "纯落球调试" },
                    { "type": "button", "label": "播放", "command": "sim.play" },
                    { "type": "button", "label": "暂停", "command": "sim.pause" },
                    { "type": "button", "label": "重置", "command": "sim.reset", "style": "danger" },
                    { "type": "button", "label": "吐球", "command": "ball.spawn" },
                    { "type": "number", "id": "gravity", "label": "重力(g)", "min": 0, "max": 50, "step": 1, "default": "10" },
                    { "type": "button", "label": "应用重力", "command": "sim.gravity g={gravity}" },
                    { "type": "check", "id": "ballcol", "label": "球-球碰撞", "default": "true" },
                    { "type": "button", "label": "应用碰撞", "command": "sim.ballcollision on={ballcol}" },
                    { "type": "check", "id": "trail", "label": "尾迹", "default": "false" },
                    { "type": "button", "label": "应用尾迹", "command": "sim.trail on={trail}" },
                    { "type": "label", "id": "sclbl", "label": "场景", "default": "文件=权威入口" },
                    { "type": "text", "id": "scenefile", "label": "场景名", "default": "plinko_demo" },
                    { "type": "button", "label": "保存场景", "command": "scene.save file={scenefile}" },
                    { "type": "button", "label": "加载场景", "command": "scene.load file={scenefile}" },
                    { "type": "number", "id": "worldW", "label": "宽", "min": 200, "max": 4000, "step": 50, "default": "720" },
                    { "type": "number", "id": "worldH", "label": "高", "min": 200, "max": 4000, "step": 50, "default": "900" },
                    { "type": "button", "label": "应用尺寸", "command": "scene.size w={worldW} h={worldH}" },
                    { "type": "button", "label": "新建场景", "command": "scene.new", "style": "danger" },
                    { "type": "label", "id": "dbglbl", "label": "调试窗", "default": "按需唤出" },
                    { "type": "button", "label": "对象调试", "command": "win.show name=objdebug" },
                    { "type": "button", "label": "小球窗", "command": "win.show name=ballpanel" },
                    { "type": "button", "label": "裁判区", "command": "win.show name=referee" }
                  ]
                }
                """);
            log.Info("app", "已写入面板 panels/editor.json");
        }
        catch (Exception ex)
        {
            log.Error("app", $"编辑工具面板初始化失败: {ex.Message}");
        }
    }
}
