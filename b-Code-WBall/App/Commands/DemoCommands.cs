using AppShell.Core.Commands;
using AppShell.Core.Logging;
using WBall.Battle;
using WBall.Model;
using WBall.Stage;

namespace WBall.Commands;

public static class DemoCommands
{
    public static void Register(
        CommandRegistry registry,
        StageState stage,
        ScenarioStore scenarios,
        BattleConfigStore battleConfig,
        WeaponCatalog weapons,
        BattleRuntime battle,
        BattleDirector director,
        SceneWorld economyWorld,
        IShellLog log)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "demo.play",
            Summary = "一键加载 Plinko+四方炮台 Demo 并开战",
            Example = "demo.play seed=42",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "seed", Description = "随机种子", Type = ParamType.Int, Default = "42" },
                new ParameterSpec { Name = "scenario", Description = "剧本名", Default = "demo4" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    PlinkoDemoSeeder.EnsureScene(scenarios.ScenesDirectory, log);

                    stage.Configure(
                        split: 0.4,
                        orientation: StageOrientation.Horizontal,
                        hudVisible: true,
                        background: "#0A0C10");
                    stage.SetCompositeVisible(true);

                    var scenarioName = ctx.GetString("scenario") ?? "demo4";
                    var snap = scenarios.Load(scenarioName);
                    scenarios.Apply(snap, battleConfig, weapons, economyWorld);
                    battle.ReloadConfiguration();

                    var seed = ctx.Has("seed") ? ctx.GetInt("seed") : snap.Seed;
                    director.Start(seed);
                    return CommandResult.Ok(
                        $"demo 已开局 scenario={snap.Name} seed={seed} {director.Status()}");
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });
    }
}
