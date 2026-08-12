using System.Globalization;
using AppShell.Core.Commands;
using WBall.Battle;
using WBall.Model;
using WBall.Stage;

namespace WBall.Commands;

/// <summary>
/// v3.1:对战区自定义命令族。窗口只是这些命令的图形外壳 —— 关掉设置窗,
/// 靠控制台手敲这些命令可以完成同样的事(框架不变量:控制台完备性)。
/// </summary>
public static class ArenaConfigCommands
{
    public static void Register(
        CommandRegistry registry,
        BattleRuntime battle,
        SceneWorld battleWorld,
        BattleConfigStore config,
        WeaponCatalog weapons,
        StageState stage)
    {
        RegisterCell(registry, battle, config, weapons);
        RegisterTurretGeometry(registry, battle, config);
        RegisterScale(registry, battle, config, weapons);
        RegisterShell(registry, battle, config, weapons);
        RegisterSmallAndLabel(registry, config);
        RegisterShieldAndRound(registry, battle, config);
        RegisterLimits(registry, battleWorld, config);
        RegisterTurretSetAll(registry, battle, config);
        RegisterConfigAndDefault(registry, battle, config, weapons, stage);
    }

    // ── ① 网格 ──────────────────────────────────────────────

    private static void RegisterCell(
        CommandRegistry registry,
        BattleRuntime battle,
        BattleConfigStore config,
        WeaponCatalog weapons)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "arena.cell",
            Summary = "查询或设置领地格边长(决定格数,即领地模式初始血量)",
            Example = "arena.cell size=8",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "size", Description = "格边长 5~100", Type = ParamType.Double, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var before = ArenaMetrics.Compute(config.Arena, config.Turrets, weapons);
                if (!ctx.Has("size"))
                    return CommandResult.Ok($"格边长 {Num(config.Arena.CellSize)};{Grid(before)}");

                config.Arena.CellSize = BattleConfigStore.ClampField("cellSize", ctx.GetDouble("size"));
                config.Save();
                var after = ArenaMetrics.Compute(config.Arena, config.Turrets, weapons);
                return CommandResult.Ok(
                    $"格边长已设为 {Num(config.Arena.CellSize)}(需 battle.reset 生效)"
                    + $"{Environment.NewLine}改动前:{Grid(before)}"
                    + $"{Environment.NewLine}改动后:{Grid(after)}");
            }),
        });
    }

    // ── ② 炮塔几何 ──────────────────────────────────────────

    private static void RegisterTurretGeometry(
        CommandRegistry registry,
        BattleRuntime battle,
        BattleConfigStore config)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "arena.turret",
            Summary = "查询或设置炮塔半径/离角比例/护罩环倍率",
            Example = "arena.turret radius=26 mx=0.12 my=0.14 ring=1.55",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "radius", Description = "炮塔半径 6~200", Type = ParamType.Double },
                new ParameterSpec { Name = "mx", Description = "离左右边距比例 0.02~0.45", Type = ParamType.Double },
                new ParameterSpec { Name = "my", Description = "离上下边距比例 0.02~0.45", Type = ParamType.Double },
                new ParameterSpec { Name = "ring", Description = "护罩环 ÷ 炮塔半径 1~4", Type = ParamType.Double },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var arena = config.Arena;
                var touched = false;
                if (ctx.Has("radius"))
                {
                    arena.TurretRadius = BattleConfigStore.ClampField("turretRadius", ctx.GetDouble("radius"));
                    touched = true;
                }
                if (ctx.Has("mx"))
                {
                    arena.TurretMarginXRatio = BattleConfigStore.ClampField("turretMarginXRatio", ctx.GetDouble("mx"));
                    touched = true;
                }
                if (ctx.Has("my"))
                {
                    arena.TurretMarginYRatio = BattleConfigStore.ClampField("turretMarginYRatio", ctx.GetDouble("my"));
                    touched = true;
                }
                if (ctx.Has("ring"))
                {
                    arena.ShieldRingScale = BattleConfigStore.ClampField("shieldRingScale", ctx.GetDouble("ring"));
                    touched = true;
                }

                var text = $"炮塔 radius={Num(arena.TurretRadius)} mx={Num(arena.TurretMarginXRatio)} "
                           + $"my={Num(arena.TurretMarginYRatio)} ring={Num(arena.ShieldRingScale)}";
                if (!touched)
                    return CommandResult.Ok(text);

                config.Save();
                return CommandResult.Ok(text + "(半径/离角需 battle.reset 生效;护罩环即时)");
            }),
        });
    }

    // ── ③ 等比缩放 ──────────────────────────────────────────

    private static void RegisterScale(
        CommandRegistry registry,
        BattleRuntime battle,
        BattleConfigStore config,
        WeaponCatalog weapons)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "arena.scale",
            Summary = "等比缩放对战区规模(宽高/炮塔/格边长/弹速同乘 k,格数与血量不变)",
            Example = "arena.scale k=1.5 reset=true",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "k", Description = "缩放系数 0.1~10", Type = ParamType.Double, Required = true, Position = 0 },
                new ParameterSpec { Name = "reset", Description = "是否立即重置战场", Type = ParamType.Bool, Default = "false" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var k = Math.Clamp(ctx.GetDouble("k"), 0.1, 10);
                var arena = config.Arena;
                var beforeCells = ArenaMetrics.Compute(arena, config.Turrets, weapons).TotalCells;

                arena.Width = BattleConfigStore.ClampField("width", arena.Width * k);
                arena.Height = BattleConfigStore.ClampField("height", arena.Height * k);
                arena.TurretRadius = BattleConfigStore.ClampField("turretRadius", arena.TurretRadius * k);
                arena.CellSize = BattleConfigStore.ClampField("cellSize", arena.CellSize * k);
                arena.ProjectileSpeedScale = BattleConfigStore.ClampField(
                    "projectileSpeedScale", arena.ProjectileSpeedScale * k);
                config.Save();

                var after = ArenaMetrics.Compute(arena, config.Turrets, weapons);
                var resetNow = ctx.GetBool("reset", false);
                if (resetNow)
                    battle.Reset(battle.Seed);

                return CommandResult.Ok(
                    $"已按 k={Num(k)} 等比缩放:{Num(arena.Width)}×{Num(arena.Height)} "
                    + $"炮塔 {Num(arena.TurretRadius)} 格边长 {Num(arena.CellSize)} 弹速 ×{Num(arena.ProjectileSpeedScale)}"
                    + $"{Environment.NewLine}格数 {beforeCells} → {after.TotalCells}"
                    + (beforeCells == after.TotalCells ? "(不变,对局结构未改)" : "(有取整偏差)")
                    + (resetNow ? ";战场已重置" : ";尚未重置,battle.reset 后生效"));
            }),
        });
    }

    // ── ④ 大球动量 ──────────────────────────────────────────

    private static void RegisterShell(
        CommandRegistry registry,
        BattleRuntime battle,
        BattleConfigStore config,
        WeaponCatalog weapons)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "arena.shell",
            Summary = "查询或设置大球尺寸/动量映射(支持任意子集)",
            Example = "arena.shell speedJitter=0.3 speedMin=80 weightScale=1",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "sizeFactor", Description = "尺寸基数 ×格边长", Type = ParamType.Double },
                new ParameterSpec { Name = "sizeExp", Description = "尺寸随数值指数 0~1", Type = ParamType.Double },
                new ParameterSpec { Name = "sizeMin", Description = "尺寸下限(格)", Type = ParamType.Double },
                new ParameterSpec { Name = "sizeMax", Description = "尺寸上限(格)", Type = ParamType.Double },
                new ParameterSpec { Name = "speedJitter", Description = "速度抖动 ±比例 0~0.9", Type = ParamType.Double },
                new ParameterSpec { Name = "speedExp", Description = "重弹减速指数 0~1", Type = ParamType.Double },
                new ParameterSpec { Name = "speedMin", Description = "速度下限", Type = ParamType.Double },
                new ParameterSpec { Name = "speedMax", Description = "速度上限", Type = ParamType.Double },
                new ParameterSpec { Name = "weightScale", Description = "质量系数 ×数值", Type = ParamType.Double },
                new ParameterSpec { Name = "speedScale", Description = "弹体速度总缩放", Type = ParamType.Double },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var arena = config.Arena;
                var touched = false;
                touched |= Set(ctx, "sizeFactor", "shellSizeCellFactor", v => arena.ShellSizeCellFactor = v);
                touched |= Set(ctx, "sizeExp", "shellSizeValueExponent", v => arena.ShellSizeValueExponent = v);
                touched |= Set(ctx, "sizeMin", "shellSizeMinCells", v => arena.ShellSizeMinCells = v);
                touched |= Set(ctx, "sizeMax", "shellSizeMaxCells", v => arena.ShellSizeMaxCells = v);
                touched |= Set(ctx, "speedJitter", "shellSpeedJitter", v => arena.ShellSpeedJitter = v);
                touched |= Set(ctx, "speedExp", "shellSpeedValueExponent", v => arena.ShellSpeedValueExponent = v);
                touched |= Set(ctx, "speedMin", "shellSpeedMin", v => arena.ShellSpeedMin = v);
                touched |= Set(ctx, "speedMax", "shellSpeedMax", v => arena.ShellSpeedMax = v);
                touched |= Set(ctx, "weightScale", "shellWeightScale", v => arena.ShellWeightScale = v);
                touched |= Set(ctx, "speedScale", "projectileSpeedScale", v => arena.ProjectileSpeedScale = v);

                if (arena.ShellSizeMinCells > arena.ShellSizeMaxCells)
                    (arena.ShellSizeMinCells, arena.ShellSizeMaxCells) = (arena.ShellSizeMaxCells, arena.ShellSizeMinCells);
                if (arena.ShellSpeedMin > arena.ShellSpeedMax)
                    (arena.ShellSpeedMin, arena.ShellSpeedMax) = (arena.ShellSpeedMax, arena.ShellSpeedMin);

                if (touched)
                    config.Save();

                var metrics = ArenaMetrics.Compute(arena, config.Turrets, weapons);
                return CommandResult.Ok(
                    $"大球 sizeFactor={Num(arena.ShellSizeCellFactor)} sizeExp={Num(arena.ShellSizeValueExponent)} "
                    + $"size夹={Num(arena.ShellSizeMinCells)}~{Num(arena.ShellSizeMaxCells)}格 "
                    + $"speedJitter={Num(arena.ShellSpeedJitter)} speedExp={Num(arena.ShellSpeedValueExponent)} "
                    + $"speed夹={Num(arena.ShellSpeedMin)}~{Num(arena.ShellSpeedMax)} "
                    + $"weightScale={Num(arena.ShellWeightScale)} speedScale={Num(arena.ProjectileSpeedScale)}"
                    + $"{Environment.NewLine}初始大球 size {Num(metrics.ShellSize)}px "
                    + $"speed {Num(metrics.ShellSpeedMin)}~{Num(metrics.ShellSpeedMax)} "
                    + $"weight {Num(metrics.ShellWeight)} 动量 {Num(metrics.MomentumMin)}~{Num(metrics.MomentumMax)}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "arena.preload",
            Summary = "查询或设置开局预载大球队列(发数/数值/武器)",
            Example = "arena.preload count=12 value=1 weapon=直射",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "count", Description = "预载发数 0~512", Type = ParamType.Int },
                new ParameterSpec { Name = "value", Description = "每发数值 1~100000", Type = ParamType.Int },
                new ParameterSpec { Name = "weapon", Description = "武器名(决定基速)" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var arena = config.Arena;
                var touched = false;
                if (ctx.Has("count"))
                {
                    arena.InitialShellCount = (int)BattleConfigStore.ClampField("initialShellCount", ctx.GetInt("count"));
                    touched = true;
                }
                if (ctx.Has("value"))
                {
                    arena.InitialShellValue = (long)BattleConfigStore.ClampField("initialShellValue", ctx.GetInt("value"));
                    touched = true;
                }
                var weaponName = ctx.GetString("weapon");
                if (!string.IsNullOrWhiteSpace(weaponName))
                {
                    if (!weapons.TryResolve(weaponName, out var weapon))
                        return CommandResult.Fail($"未知武器: {weaponName}");
                    arena.InitialShellWeapon = weapon.Name;
                    touched = true;
                }
                if (touched)
                    config.Save();

                var metrics = ArenaMetrics.Compute(arena, config.Turrets, weapons);
                return CommandResult.Ok(
                    $"开局预载 {arena.InitialShellCount} 发,数值 {arena.InitialShellValue},武器 {metrics.ShellWeaponName}"
                    + $"(基速 {Num(metrics.ShellWeaponBaseSpeed)},动量 {Num(metrics.MomentumMin)}~{Num(metrics.MomentumMax)})"
                    + (touched ? ";需 battle.reset 生效" : ""));
            }),
        });
    }

    // ── ⑤ 小球与弹体数字 ────────────────────────────────────

    private static void RegisterSmallAndLabel(CommandRegistry registry, BattleConfigStore config)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "arena.small",
            Summary = "查询或设置小球速度与尺寸系数",
            Example = "arena.small speed=380 size=0.5",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "speed", Description = "出膛速度 20~3000", Type = ParamType.Double },
                new ParameterSpec { Name = "size", Description = "半径 ÷ 格边长 0.1~3", Type = ParamType.Double },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var arena = config.Arena;
                var touched = false;
                touched |= Set(ctx, "speed", "smallBallSpeed", v => arena.SmallBallSpeed = v);
                touched |= Set(ctx, "size", "smallBallSizeCellFactor", v => arena.SmallBallSizeCellFactor = v);
                if (touched)
                    config.Save();
                return CommandResult.Ok(
                    $"小球 speed={Num(arena.SmallBallSpeed)}(×{Num(arena.ProjectileSpeedScale)} 缩放后 "
                    + $"{Num(ArenaFormulas.SmallBallSpeed(arena))}) size={Num(arena.SmallBallSizeCellFactor)}×格边长 = "
                    + $"{Num(ArenaFormulas.SmallBallSize(arena, ArenaFormulas.CellSize(arena)))}px");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "arena.label",
            Summary = "查询或设置弹体积分数字的字号与超出球体部分的暗淡度",
            Example = "arena.label factor=0.8 min=8 max=22 outside=0.28",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "factor", Description = "字号 ÷ 弹体半径", Type = ParamType.Double },
                new ParameterSpec { Name = "min", Description = "最小字号(防止无限小看不见)", Type = ParamType.Double },
                new ParameterSpec { Name = "max", Description = "最大字号", Type = ParamType.Double },
                new ParameterSpec { Name = "outside", Description = "超出球体部分不透明度 0~1(0=完全隐藏)", Type = ParamType.Double },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var arena = config.Arena;
                var touched = false;
                touched |= Set(ctx, "factor", "shellLabelFontFactor", v => arena.ShellLabelFontFactor = v);
                touched |= Set(ctx, "min", "shellLabelFontMin", v => arena.ShellLabelFontMin = v);
                touched |= Set(ctx, "max", "shellLabelFontMax", v => arena.ShellLabelFontMax = v);
                touched |= Set(ctx, "outside", "shellLabelOutsideOpacity", v => arena.ShellLabelOutsideOpacity = v);
                if (arena.ShellLabelFontMin > arena.ShellLabelFontMax)
                    (arena.ShellLabelFontMin, arena.ShellLabelFontMax) = (arena.ShellLabelFontMax, arena.ShellLabelFontMin);
                if (touched)
                    config.Save();
                return CommandResult.Ok(
                    $"弹体数字 factor={Num(arena.ShellLabelFontFactor)} 字号夹={Num(arena.ShellLabelFontMin)}~"
                    + $"{Num(arena.ShellLabelFontMax)} 超出暗淡={Num(arena.ShellLabelOutsideOpacity)}(即时生效)");
            }),
        });
    }

    // ── ⑥ 护盾计价 / 决胜时刻 ───────────────────────────────

    private static void RegisterShieldAndRound(
        CommandRegistry registry,
        BattleRuntime battle,
        BattleConfigStore config)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "arena.shield",
            Summary = "查询或设置护盾计价(一点弹体积分磨掉多少护盾,也是自家小球回充量)",
            Example = "arena.shield cost=50000",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "cost", Description = "计价 1~1e9", Type = ParamType.Double, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var arena = config.Arena;
                if (Set(ctx, "cost", "shieldCostPerValue", v => arena.ShieldCostPerValue = v))
                    config.Save();
                var capacity = arena.ShieldCostPerValue <= 0
                    ? 0
                    : config.Turrets.Min(x => x.InitialShield) / arena.ShieldCostPerValue;
                return CommandResult.Ok(
                    $"护盾计价 {Num(arena.ShieldCostPerValue)}/点;当前初始护盾可挡 {Num(capacity)} 发小球"
                    + $"(等量抵消 {Num(capacity)} 点大球积分)");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "arena.suddendeath",
            Summary = "查询或设置决胜时刻(此后护盾只降不升)",
            Example = "arena.suddendeath at=240",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "at", Description = "秒 0~3600", Type = ParamType.Double, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var arena = config.Arena;
                if (Set(ctx, "at", "suddenDeathAtSeconds", v => arena.SuddenDeathAtSeconds = v))
                    config.Save();
                return CommandResult.Ok(
                    $"决胜时刻 {Num(arena.SuddenDeathAtSeconds)}s"
                    + $"(当前对战 {Num(battle.ElapsedSeconds)}s,{(battle.SuddenDeath ? "已进入" : "未进入")})");
            }),
        });
    }

    // ── ⑦ 全局上限 ──────────────────────────────────────────

    private static void RegisterLimits(
        CommandRegistry registry,
        SceneWorld battleWorld,
        BattleConfigStore config)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "arena.limit",
            Summary = "查询或设置最大弹数与弹丸寿命(寿命仅 direct 模式)",
            Example = "arena.limit max=2000 life=12",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "max", Description = "最大弹数 10~20000", Type = ParamType.Int },
                new ParameterSpec { Name = "life", Description = "弹丸寿命秒 0.5~600", Type = ParamType.Double },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var arena = config.Arena;
                var touched = false;
                if (ctx.Has("max"))
                {
                    arena.MaxProjectiles = (int)BattleConfigStore.ClampField("maxProjectiles", ctx.GetInt("max"));
                    touched = true;
                }
                touched |= Set(ctx, "life", "projectileLifetimeSec", v => arena.ProjectileLifetimeSec = v);
                if (touched)
                    config.Save();
                return CommandResult.Ok(
                    $"最大弹数 {arena.MaxProjectiles} 弹丸寿命 {Num(arena.ProjectileLifetimeSec)}s(direct 模式)");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "arena.collision",
            Summary = "查询或设置战场弹-弹碰撞",
            Example = "arena.collision on=true",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "on", Description = "true/false", Type = ParamType.Bool, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (ctx.Has("on"))
                {
                    config.Arena.BallCollision = ctx.GetBool("on");
                    battleWorld.BallCollisionEnabled = config.Arena.BallCollision;
                    config.Save();
                }
                return CommandResult.Ok(
                    $"战场弹-弹碰撞 {(config.Arena.BallCollision ? "on（吸收关闭）" : "off（吸收启用）")}");
            }),
        });
    }

    // ── ⑧ 炮台批量 ──────────────────────────────────────────

    private static void RegisterTurretSetAll(
        CommandRegistry registry,
        BattleRuntime battle,
        BattleConfigStore config)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "turret.setall",
            Summary = "批量设置全部炮台的持久化数值(写入 turrets.json;缺省项不动)",
            Example = "turret.setall initshield=500000 hp=20000000 rpm=6",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "hp", Description = "生命上限(仅 direct 模式生效)", Type = ParamType.Double },
                new ParameterSpec { Name = "shield", Description = "初始护盾兼容别名(无上限)", Type = ParamType.Double },
                new ParameterSpec { Name = "initshield", Description = "初始护盾", Type = ParamType.Double },
                new ParameterSpec { Name = "size", Description = "投射球半径 2~60", Type = ParamType.Double },
                new ParameterSpec { Name = "count", Description = "单轮弹数 1~200", Type = ParamType.Int },
                new ParameterSpec { Name = "interval", Description = "开火间隔秒 0.05~60", Type = ParamType.Double },
                new ParameterSpec { Name = "rpm", Description = "炮管转速 0.5~60", Type = ParamType.Double },
                new ParameterSpec { Name = "balls", Description = "开局经济球数", Type = ParamType.Int },
                new ParameterSpec { Name = "mult", Description = "开局经济球倍率", Type = ParamType.Int },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (config.Turrets.Count == 0)
                    return CommandResult.Fail("没有炮台可设置");

                var touched = false;
                foreach (var turret in config.Turrets)
                {
                    if (ctx.Has("hp"))
                    {
                        turret.MaxHp = Math.Max(1, ctx.GetDouble("hp"));
                        touched = true;
                    }
                    if (ctx.Has("shield"))
                    {
                        turret.InitialShield = Math.Max(0, ctx.GetDouble("shield"));
                        turret.MaxShield = Math.Max(turret.MaxShield, turret.InitialShield);
                        touched = true;
                    }
                    if (ctx.Has("initshield"))
                    {
                        turret.InitialShield = Math.Max(0, ctx.GetDouble("initshield"));
                        touched = true;
                    }
                    if (ctx.Has("size"))
                    {
                        turret.ProjectileSize = Math.Clamp(ctx.GetDouble("size"), 2, 60);
                        touched = true;
                    }
                    if (ctx.Has("count"))
                    {
                        turret.ProjectileCount = Math.Clamp(ctx.GetInt("count"), 1, 200);
                        touched = true;
                    }
                    if (ctx.Has("interval"))
                    {
                        turret.FireIntervalSec = Math.Clamp(ctx.GetDouble("interval"), 0.05, 60);
                        touched = true;
                    }
                    if (ctx.Has("rpm"))
                    {
                        turret.BarrelRpm = Math.Clamp(ctx.GetDouble("rpm"), 0.5, 60);
                        touched = true;
                    }
                    if (ctx.Has("balls"))
                    {
                        turret.InitialBalls = Math.Clamp(ctx.GetInt("balls"), 0, 64);
                        touched = true;
                    }
                    if (ctx.Has("mult"))
                    {
                        turret.InitialMultiplier = Math.Max(1, ctx.GetInt("mult"));
                        touched = true;
                    }
                    turret.MaxShield = Math.Max(turret.MaxShield, turret.InitialShield);
                }

                if (!touched)
                    return CommandResult.Ok(FormatDefinitions(config));

                config.Save();
                battle.SyncTurretNumbersFromConfig();
                return CommandResult.Ok(
                    $"已批量刷新 {config.Turrets.Count} 座炮台(已写入 turrets.json)"
                    + $"{Environment.NewLine}{FormatDefinitions(config)}"
                    + (battle.TerritoryMode && ctx.Has("hp")
                        ? $"{Environment.NewLine}注意:领地模式血量 = 占格数,hp 仅 direct 模式生效"
                        : ""));
            }),
        });
    }

    // ── ⑨ 全量打印 / 恢复默认 ───────────────────────────────

    private static void RegisterConfigAndDefault(
        CommandRegistry registry,
        BattleRuntime battle,
        BattleConfigStore config,
        WeaponCatalog weapons,
        StageState stage)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "arena.config",
            Summary = "打印对战区全量配置与派生值(格数/初始血量/护盾口径/初始大球动量)",
            Example = "arena.config",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(
                FormatFullConfig(config, weapons, stage))),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "arena.default",
            Summary = "对战区恢复出厂默认(危险;turrets=true 连炮台数值一起回默认)",
            Example = "arena.default reset=true turrets=false",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "reset", Description = "是否立即重置战场", Type = ParamType.Bool, Default = "false" },
                new ParameterSpec { Name = "turrets", Description = "是否同时重置炮台数值(保留 id/名/色/象限)", Type = ParamType.Bool, Default = "false" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                config.ResetArenaDefaults();
                var alsoTurrets = ctx.GetBool("turrets", false);
                if (alsoTurrets)
                    config.ResetTurretNumberDefaults();

                var resetNow = ctx.GetBool("reset", false);
                if (resetNow)
                    battle.Reset(battle.Seed);
                else if (alsoTurrets)
                    battle.SyncTurretNumbersFromConfig();

                return CommandResult.Ok(
                    "对战区已恢复出厂默认"
                    + (alsoTurrets ? "(含炮台数值)" : "")
                    + (resetNow ? ";战场已重置" : ";尚未重置,battle.reset 后完全生效")
                    + $"{Environment.NewLine}{FormatFullConfig(config, weapons, stage)}");
            }),
        });
    }

    // ── 辅助 ────────────────────────────────────────────────

    private static bool Set(CommandContext ctx, string param, string field, Action<double> apply)
    {
        if (!ctx.Has(param))
            return false;
        apply(BattleConfigStore.ClampField(field, ctx.GetDouble(param)));
        return true;
    }

    private static string Grid(ArenaMetrics metrics)
    {
        var cells = string.Join(" ｜ ", metrics.FactionCells.Select(x => $"{x.Name} {x.Cells}"));
        return $"网格 {metrics.Cols}×{metrics.Rows} = {metrics.TotalCells} 格"
               + (metrics.TerritoryMode ? $" → 初始血量 {cells}" : "");
    }

    private static string FormatDefinitions(BattleConfigStore config) =>
        string.Join(Environment.NewLine, config.Turrets.Select(x =>
            $"  {x.Id} {x.Name} q={x.Quadrant} hp={Num(x.MaxHp)} shield={Num(x.InitialShield)}/unlimited "
            + $"size={Num(x.ProjectileSize)} count={x.ProjectileCount} interval={Num(x.FireIntervalSec)} rpm={Num(x.BarrelRpm)}"));

    private static string FormatFullConfig(
        BattleConfigStore config,
        WeaponCatalog weapons,
        StageState stage)
    {
        var arena = config.Arena;
        var metrics = ArenaMetrics.Compute(arena, config.Turrets, weapons);
        var lines = new List<string>
        {
            "── 规模 ──(需重置)",
            $"  width={Num(arena.Width)} height={Num(arena.Height)} turretRadius={Num(arena.TurretRadius)} "
            + $"marginX={Num(arena.TurretMarginXRatio)} marginY={Num(arena.TurretMarginYRatio)}",
            $"  shieldRingScale={Num(arena.ShieldRingScale)}(即时) projectileSpeedScale={Num(arena.ProjectileSpeedScale)}",
            "── 网格与领地 ──",
            $"  cellSize={Num(arena.CellSize)}(需重置) mode={arena.Mode} targeting={arena.Targeting} "
            + $"suddenDeathAtSeconds={Num(arena.SuddenDeathAtSeconds)}(即时)",
            "── 护盾与血量 ──",
            $"  shieldCostPerValue={Num(arena.ShieldCostPerValue)}(即时)",
            FormatDefinitions(config),
            "── 大球动量 ──",
            $"  sizeFactor={Num(arena.ShellSizeCellFactor)} sizeExp={Num(arena.ShellSizeValueExponent)} "
            + $"sizeCells={Num(arena.ShellSizeMinCells)}~{Num(arena.ShellSizeMaxCells)}",
            $"  speedJitter={Num(arena.ShellSpeedJitter)} speedExp={Num(arena.ShellSpeedValueExponent)} "
            + $"speed={Num(arena.ShellSpeedMin)}~{Num(arena.ShellSpeedMax)} weightScale={Num(arena.ShellWeightScale)}",
            $"  preload count={arena.InitialShellCount} value={arena.InitialShellValue} weapon={arena.InitialShellWeapon}",
            "── 小球与弹体数字 ──",
            $"  smallSpeed={Num(arena.SmallBallSpeed)} smallSize={Num(arena.SmallBallSizeCellFactor)}×格边长",
            $"  label factor={Num(arena.ShellLabelFontFactor)} 字号={Num(arena.ShellLabelFontMin)}~{Num(arena.ShellLabelFontMax)} "
            + $"超出暗淡={Num(arena.ShellLabelOutsideOpacity)}",
            "── 全局 ──",
            $"  gravityG={Num(arena.GravityG)} ballCollision={arena.BallCollision} "
            + $"maxProjectiles={arena.MaxProjectiles} projectileLifetimeSec={Num(arena.ProjectileLifetimeSec)}",
            "── 派生值 ──",
            metrics.Format(stage.LogicalWidth, stage.LogicalHeight),
        };
        return string.Join(Environment.NewLine, lines);
    }

    private static string Num(double value) =>
        value.ToString(Math.Abs(value) >= 1000 ? "0.##" : "0.###", CultureInfo.InvariantCulture);
}
