using AppShell.Core.Commands;
using WBall.Battle;
using WBall.Model;

namespace WBall.Commands;

public static class DirectorCommands
{
    public static void Register(
        CommandRegistry registry,
        BattleDirector director,
        WeaponCatalog weapons,
        ScenarioStore? scenarios = null,
        BattleConfigStore? battleConfig = null,
        BalanceConfigStore? balanceConfig = null,
        BattleRuntime? battle = null,
        SceneWorld? economyWorld = null)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "battle.start",
            Summary = "按确定性种子启动完整对战",
            Example = "battle.start seed=42 scenario=demo4",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "seed", Description = "确定性随机种子", Type = ParamType.Int, Default = "42" },
                new ParameterSpec { Name = "scenario", Description = "可选剧本名(先加载再开战)" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    var seed = ctx.GetInt("seed", 42);
                    var scenarioName = ctx.GetString("scenario");
                    if (!string.IsNullOrWhiteSpace(scenarioName))
                    {
                        if (scenarios == null || battleConfig == null || balanceConfig == null || battle == null)
                            return CommandResult.Fail("剧本服务未装配");
                        var snap = scenarios.Load(scenarioName);
                        scenarios.Apply(snap, battleConfig, balanceConfig, weapons, economyWorld);
                        battle.ReloadConfiguration();
                        seed = ctx.Has("seed") ? seed : snap.Seed;
                    }

                    director.Start(seed);
                    return CommandResult.Ok(director.Status());
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "battle.pause",
            Summary = "暂停导演固定步长推进",
            Example = "battle.pause",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                director.Pause();
                return CommandResult.Ok(director.Status());
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "battle.resume",
            Summary = "继续已暂停的对战",
            Example = "battle.resume",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                director.Resume();
                return CommandResult.Ok(director.Status());
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "battle.reset",
            Summary = "重置对战并返回编辑态",
            Example = "battle.reset",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                director.Reset();
                return CommandResult.Ok(director.Status());
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "battle.status",
            Summary = "显示导演、胜负和弹幕状态",
            Example = "battle.status",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(director.Status())),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "unlock.timeline",
            Summary = "显示攻击类型解封时间线",
            Example = "unlock.timeline",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(director.UnlockStatus())),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "unlock.set",
            Summary = "设置攻击类型解封秒数",
            Example = "unlock.set name=散弹 at=1200",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "武器名或别名", Required = true },
                new ParameterSpec { Name = "at", Description = "解封秒数", Required = true, Type = ParamType.Double },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    var weapon = weapons.Set(
                        ctx.RequireString("name"),
                        "unlock",
                        ctx.RequireString("at"));
                    return CommandResult.Ok($"{weapon.Name} at={weapon.UnlockAtSeconds:0.###}");
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "determinism.check",
            Summary = "同种子运行两遍并比较事件/状态哈希",
            Example = "determinism.check seed=42 steps=600",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "seed", Description = "种子", Type = ParamType.Int, Default = "42" },
                new ParameterSpec { Name = "steps", Description = "固定步数", Type = ParamType.Int, Default = "600" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var seed = ctx.GetInt("seed", 42);
                var steps = Math.Clamp(ctx.GetInt("steps", 600), 1, 100_000);
                director.Start(seed, countdownSeconds: 0);
                director.AdvanceSteps(steps);
                var first = director.DeterministicHash();
                director.Start(seed, countdownSeconds: 0);
                director.AdvanceSteps(steps);
                var second = director.DeterministicHash();
                return first == second
                    ? CommandResult.Ok($"确定性通过 seed={seed} steps={steps} hash={first}")
                    : CommandResult.Fail($"确定性失败 first={first} second={second}");
            }),
        });
    }
}
