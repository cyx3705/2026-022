using AppShell.Core.Commands;
using WBall.Battle;
using WBall.Model;

namespace WBall.Commands;

public static class ScenarioCommands
{
    public static void Register(
        CommandRegistry registry,
        ScenarioStore scenarios,
        BattleConfigStore battleConfig,
        WeaponCatalog weapons,
        BattleRuntime battle,
        BattleDirector director,
        SceneWorld economyWorld)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "scenario.list",
            Summary = "列出 workspace/scenarios 剧本",
            Example = "scenario.list",
            Handler = CommandDescriptor.Sync(_ =>
            {
                var names = scenarios.List();
                return names.Count == 0
                    ? CommandResult.Ok("(无剧本)")
                    : CommandResult.Ok(string.Join(Environment.NewLine, names));
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "scenario.save",
            Summary = "保存当前炮台/阵型/武器为剧本",
            Example = "scenario.save name=demo4 seed=42",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "剧本名", Required = true },
                new ParameterSpec { Name = "seed", Description = "默认种子", Type = ParamType.Int },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    var snap = scenarios.Capture(
                        ctx.RequireString("name"),
                        ctx.Has("seed") ? ctx.GetInt("seed") : director.Seed,
                        battleConfig,
                        weapons,
                        economyWorld.LastScenePath);
                    var path = scenarios.Save(snap);
                    return CommandResult.Ok($"已保存 {path}");
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "scenario.load",
            Summary = "加载剧本配置(不自动开战)",
            Example = "scenario.load name=demo4",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "剧本名", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    var snap = scenarios.Load(ctx.RequireString("name"));
                    scenarios.Apply(snap, battleConfig, weapons, economyWorld);
                    battle.ReloadConfiguration();
                    return CommandResult.Ok(
                        $"已加载剧本 {snap.Name} seed={snap.Seed} turrets={snap.Turrets.Count} " +
                        $"scene={snap.EconomyScenePath ?? "-"}");
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });
    }
}
