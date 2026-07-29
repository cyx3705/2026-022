using System.IO;
using AppShell.Core.Commands;
using WBall.Commands;
using WBall.Data;
using WBall.Editing;
using WBall.Game;
using WBall.Model;

namespace WBall.Verify.Suites;

internal static class EditorCommandSuite
{
    public static async Task RunAsync(VerifyRun run)
    {
        var editorRoot = run.Artifacts.Suite("editor-command-smoke");
        var editorScenes = Path.Combine(editorRoot, "scenes");
        var editorWorld = new SceneWorld();
        editorWorld.Objects.Add(new SceneObject
        {
            Id = "spawn1",
            Type = SceneObjectType.Spawner,
            X = 20,
            Y = 20,
        });
        editorWorld.Balls.Add(new Ball
        {
            Id = "ball1",
            X = 40,
            Y = 40,
            Color = "#3B82F6",
            Multiplier = 1,
            Size = 12,
            Weight = 1,
        });
        editorWorld.Factions.Add(new Faction
        {
            Id = "blue",
            Name = "Blue",
            Color = "#3B82F6",
            InitialBalls = 2,
            InitialMultiplier = 1,
            Score = 4,
        });
        var editorProperties = new ScenePropertyService(editorWorld, editorRoot);
        var editorRegistry = new CommandRegistry();
        WBallCommands.Register(
            editorRegistry,
            editorWorld,
            run.Log,
            editorScenes,
            editorProperties,
            new BallEditorService(editorWorld),
            new FormulaEditorService(editorWorld, editorRoot),
            new FactionEditorService(editorWorld));
        var editorBus = new CommandBus(editorRegistry, run.Log);

        var ballSet = await editorBus.ExecuteAsync(
            "ball.set id=ball1 color=#123456 multiplier=9 size=20 weight=3.5", "verify");
        run.Check("ball.set command", ballSet.Success
            && editorWorld.Balls[0].Color == "#123456"
            && editorWorld.Balls[0].Multiplier == 9
            && editorWorld.Balls[0].Size == 20
            && editorWorld.Balls[0].Weight == 3.5
            && ballSet.Message.Contains("ball1"));
        var ballBeforeInvalid = (
            editorWorld.Balls[0].Color,
            editorWorld.Balls[0].Multiplier,
            editorWorld.Balls[0].Size,
            editorWorld.Balls[0].Weight);
        var invalidBallSet = await editorBus.ExecuteAsync(
            "ball.set id=ball1 color=red multiplier=11 size=22 weight=4", "verify");
        run.Check("ball.set invalid is transactional", !invalidBallSet.Success
            && ballBeforeInvalid == (
                editorWorld.Balls[0].Color,
                editorWorld.Balls[0].Multiplier,
                editorWorld.Balls[0].Size,
                editorWorld.Balls[0].Weight));

        var formulaSet = await editorBus.ExecuteAsync(
            "formula.set sizebase=8 sizescale=2 weightbase=1.5 weightscale=0.5 initial=3 recalc=true", "verify");
        run.Check("formula.set recalc command", formulaSet.Success
            && editorWorld.Defaults.SizeBase == 8
            && editorWorld.Defaults.InitialMultiplier == 3
            && editorWorld.Balls[0].Size == editorWorld.Defaults.SizeFromMultiplier(9)
            && editorWorld.Balls[0].Weight == editorWorld.Defaults.WeightFromMultiplier(9)
            && formulaSet.Message.Contains("已重算 1 个球"));
        var formulaBeforeInvalid = editorWorld.Defaults.SizeBase;
        var invalidFormulaSet = await editorBus.ExecuteAsync("formula.set sizebase=NaN recalc=true", "verify");
        run.Check("formula.set invalid is transactional", !invalidFormulaSet.Success
            && editorWorld.Defaults.SizeBase == formulaBeforeInvalid);

        var factionSet = await editorBus.ExecuteAsync(
            "faction.set id=blue name=Azure color=#234567 balls=2 multiplier=3 score=12", "verify");
        run.Check("faction.set command", factionSet.Success
            && editorWorld.Factions[0].Name == "Azure"
            && editorWorld.Factions[0].Color == "#234567"
            && editorWorld.Factions[0].InitialBalls == 2
            && editorWorld.Factions[0].InitialMultiplier == 3
            && editorWorld.Factions[0].Score == 12);
        var factionBeforeInvalid = (
            editorWorld.Factions[0].Color,
            editorWorld.Factions[0].InitialBalls,
            editorWorld.Factions[0].Score);
        var invalidFactionSet = await editorBus.ExecuteAsync(
            "faction.set id=blue color=#GGGGGG balls=99 score=99", "verify");
        run.Check("faction.set invalid is transactional", !invalidFactionSet.Success
            && factionBeforeInvalid == (
                editorWorld.Factions[0].Color,
                editorWorld.Factions[0].InitialBalls,
                editorWorld.Factions[0].Score));

        var gameStart = await editorBus.ExecuteAsync("game.start", "verify");
        run.Check("game.start command", gameStart.Success
            && editorWorld.IsPlaying
            && editorWorld.Balls.Count == 3
            && gameStart.Message.Contains("吐球 2"));
        var factionList = await editorBus.ExecuteAsync("faction.list", "verify");
        run.Check("faction.list command", factionList.Success
            && factionList.Message.Contains("blue")
            && factionList.Message.Contains("score=12"));
        var resetScore = await editorBus.ExecuteAsync("game.resetscore", "verify");
        run.Check("game.resetscore command", resetScore.Success
            && editorWorld.Factions[0].Score == 0
            && resetScore.Message.Contains("重置"));
    }
}
