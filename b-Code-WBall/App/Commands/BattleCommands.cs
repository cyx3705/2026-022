using System.Globalization;
using AppShell.Core.Commands;
using WBall.Battle;
using WBall.Model;

namespace WBall.Commands;

public static class BattleCommands
{
    public static void Register(
        CommandRegistry registry,
        BattleRuntime battle,
        SceneWorld battleWorld,
        BattleConfigStore config)
    {
        RegisterArena(registry, battle, battleWorld, config);
        RegisterTurret(registry, battle);
        RegisterCombat(registry, battle);
    }

    private static void RegisterArena(
        CommandRegistry registry,
        BattleRuntime battle,
        SceneWorld world,
        BattleConfigStore config)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "arena.size",
            Summary = "查询或设置右世界逻辑尺寸",
            Example = "arena.size w=960 h=900",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "w", Description = "宽", Type = ParamType.Double },
                new ParameterSpec { Name = "h", Description = "高", Type = ParamType.Double },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (!ctx.Has("w") && !ctx.Has("h"))
                    return CommandResult.Ok($"战场 {world.WorldWidth:0}x{world.WorldHeight:0}");
                try
                {
                    config.Arena.Width = ctx.Has("w") ? ctx.GetDouble("w") : world.WorldWidth;
                    config.Arena.Height = ctx.Has("h") ? ctx.GetDouble("h") : world.WorldHeight;
                    config.Save();
                    battle.Reset(battle.Seed);
                    return CommandResult.Ok($"战场已设为 {world.WorldWidth:0}x{world.WorldHeight:0}");
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "arena.gravity",
            Summary = "查询或设置右世界重力",
            Example = "arena.gravity g=0",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "g", Description = "重力倍数", Type = ParamType.Double },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (ctx.Has("g"))
                {
                    config.Arena.GravityG = Math.Clamp(ctx.GetDouble("g"), -50, 50);
                    world.GravityG = config.Arena.GravityG;
                    config.Save();
                }
                return CommandResult.Ok($"战场重力 {world.GravityG:0.###}g");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "arena.layout",
            Summary = "加载战场阵型",
            Example = "arena.layout name=quad4",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "当前支持 quad4", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var name = ctx.RequireString("name");
                if (!name.Equals("quad4", StringComparison.OrdinalIgnoreCase))
                    return CommandResult.Fail("当前内置阵型仅支持 quad4;可直接编辑 arena_layout.json 扩展参数");
                config.Arena.Name = "quad4";
                config.Save();
                battle.Reset(battle.Seed);
                return CommandResult.Ok("已应用四象限阵型 quad4");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "arena.mode",
            Summary = "查询或设置玩法模式(territory=领地战/direct=直击扣血)",
            Example = "arena.mode territory",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "mode", Description = "territory|direct", Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var mode = ctx.GetString("mode");
                if (string.IsNullOrWhiteSpace(mode))
                    return CommandResult.Ok($"玩法模式 {config.Arena.Mode}");
                mode = mode.Trim().ToLowerInvariant();
                if (mode is not ("territory" or "direct"))
                    return CommandResult.Fail("mode 须为 territory|direct");
                config.Arena.Mode = mode;
                config.Save();
                battle.Reset(battle.Seed);
                return CommandResult.Ok($"玩法模式已设为 {mode},战场已重置");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "arena.targeting",
            Summary = "查询或设置瞄准模式(spin=炮管匀速旋转指向)",
            Example = "arena.targeting mode=spin",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "mode",
                    Description = "spin|highestHp|nearest|rotate|lowestHp",
                    Position = 0,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var mode = ctx.GetString("mode");
                if (string.IsNullOrWhiteSpace(mode))
                    return CommandResult.Ok($"瞄准模式 {config.Arena.Targeting}");
                string[] allowed = ["spin", "highesthp", "nearest", "closest", "rotate", "roundrobin", "lowesthp"];
                if (!allowed.Contains(mode.Trim().ToLowerInvariant()))
                    return CommandResult.Fail("mode 须为 spin|highestHp|nearest|rotate|lowestHp");
                config.Arena.Targeting = mode.Trim();
                config.Save();
                return CommandResult.Ok($"瞄准模式已设为 {config.Arena.Targeting}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "arena.status",
            Summary = "显示右世界状态",
            Example = "arena.status",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(
                $"arena={world.WorldWidth:0}x{world.WorldHeight:0} gravity={world.GravityG:0.###} " +
                $"turrets={battle.Turrets.Count} alive={battle.Turrets.Count(x => x.Alive)} " +
                $"projectiles={battle.ProjectileCount}/{config.Arena.MaxProjectiles}")),
        });
    }

    private static void RegisterTurret(CommandRegistry registry, BattleRuntime battle)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "turret.list",
            Summary = "列出炮台生命、护盾和火力参数",
            Example = "turret.list",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(
                string.Join(Environment.NewLine, battle.Turrets.Select(FormatTurret)))),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "turret.set",
            Summary = "修改运行时炮台参数",
            Example = "turret.set id=blue hp=20000000 shield=500000 size=12 count=4 interval=0.8 quadrant=2",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "id", Description = "炮台 id", Required = true },
                new ParameterSpec { Name = "hp", Description = "最大生命并回满", Type = ParamType.Double },
                new ParameterSpec { Name = "shield", Description = "最大护盾并回满", Type = ParamType.Double },
                new ParameterSpec { Name = "size", Description = "投射球半径", Type = ParamType.Double },
                new ParameterSpec { Name = "count", Description = "单轮弹数", Type = ParamType.Int },
                new ParameterSpec { Name = "interval", Description = "开火间隔秒", Type = ParamType.Double },
                new ParameterSpec { Name = "quadrant", Description = "象限 1~4", Type = ParamType.Int },
                new ParameterSpec { Name = "color", Description = "#RRGGBB" },
                new ParameterSpec { Name = "rpm", Description = "炮管转速 0.5~60", Type = ParamType.Double },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    var turret = battle.SetTurret(
                        ctx.RequireString("id"),
                        ctx.Has("hp") ? ctx.GetDouble("hp") : null,
                        ctx.Has("shield") ? ctx.GetDouble("shield") : null,
                        ctx.Has("size") ? ctx.GetDouble("size") : null,
                        ctx.Has("count") ? ctx.GetInt("count") : null,
                        ctx.Has("interval") ? ctx.GetDouble("interval") : null,
                        ctx.Has("quadrant") ? ctx.GetInt("quadrant") : null,
                        ctx.GetString("color"),
                        ctx.Has("rpm") ? ctx.GetDouble("rpm") : null);
                    return CommandResult.Ok(FormatTurret(turret));
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "turret.fire",
            Summary = "手动触发一座炮台开火",
            Example = "turret.fire id=blue",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "id", Description = "炮台 id", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    var count = battle.Fire(ctx.RequireString("id"));
                    return count > 0
                        ? CommandResult.Ok($"已发射 {count} 枚投射球")
                        : CommandResult.Fail("炮台已死亡或没有可用目标");
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "turret.kill",
            Summary = "调试：立即消灭一座炮台",
            Example = "turret.kill id=red",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "id", Description = "炮台 id", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    battle.Kill(ctx.RequireString("id"));
                    return CommandResult.Ok($"已消灭 {ctx.RequireString("id")}");
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });
    }

    private static void RegisterCombat(CommandRegistry registry, BattleRuntime battle)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "combat.hit",
            Summary = "调试：向炮台注入伤害",
            Example = "combat.hit turret=red amount=1000000",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "turret", Description = "炮台 id", Required = true },
                new ParameterSpec { Name = "amount", Description = "伤害", Required = true, Type = ParamType.Double },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    battle.Hit(ctx.RequireString("turret"), ctx.GetDouble("amount"));
                    return CommandResult.Ok(FormatTurret(battle.FindRequired(ctx.RequireString("turret"))));
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "combat.log",
            Summary = "显示近期战斗事件",
            Example = "combat.log limit=40",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "limit", Description = "条数", Type = ParamType.Int, Default = "40" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var text = battle.FormatCombatLog(ctx.GetInt("limit", 40));
                return string.IsNullOrWhiteSpace(text)
                    ? CommandResult.Ok("(暂无战斗事件)")
                    : CommandResult.Ok(text);
            }),
        });
    }

    private static string FormatTurret(WBall.Game.Faction turret) =>
        $"{turret.Id} name={turret.Name} alive={turret.Alive.ToString().ToLowerInvariant()} q={turret.Quadrant} " +
        $"hp={Number(turret.Hp)}/{Number(turret.MaxHp)} shield={Number(turret.Shield)}/{Number(turret.MaxShield)} " +
        $"points={turret.Points} score={turret.Score} size={Number(turret.Firepower.ProjectileSize)} " +
        $"count={turret.Firepower.ProjectileCount} interval={Number(turret.Firepower.FireIntervalSec)} " +
        $"rpm={Number(turret.BarrelRpm)} barrel={Number(turret.BarrelAngleDeg)}°";

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
