using System.Globalization;
using System.IO;
using System.Text.Json;
using AppShell.Core.Commands;
using AppShell.Core.Logging;
using WBall.Data;
using WBall.Editing;
using WBall.Game;
using WBall.Model;
using WBall.Sim;

namespace WBall.Commands;
internal static class GameCommands
{
    public static void Register(
        CommandRegistry registry,
        SceneWorld world,
        IShellLog log,
        FactionEditorService factionEditor)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "game.start",
            Summary = "按阵营 initial_balls / color / initial_multiplier 开局吐球",
            Example = "game.start",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var spawners = world.OfType(SceneObjectType.Spawner).ToList();
                if (spawners.Count == 0)
                    return CommandResult.Fail("场景中无生成器,无法开局");

                var factions = world.Factions
                    .Where(f => !f.Id.Equals(FactionBoard.UnassignedId, StringComparison.OrdinalIgnoreCase))
                    .Where(f => f.Alive)
                    .Where(f => f.InitialBalls > 0)
                    .ToList();
                if (factions.Count == 0)
                    return CommandResult.Fail("无有效阵营(请先配置 factions 的 initial_balls)");

                var spawned = 0;
                foreach (var f in factions)
                {
                    for (var i = 0; i < f.InitialBalls; i++)
                    {
                        var sp = spawners[world.Rng.Next(spawners.Count)];
                        var ball = new Ball
                        {
                            Id = world.NextBallId(),
                            X = sp.X + sp.W / 2,
                            Y = sp.Y + sp.H / 2,
                            Color = f.Color,
                            Multiplier = PublicDefaults.ClampMultiplier(f.InitialMultiplier),
                        };
                        world.Defaults.ApplyToBall(ball);
                        SceneWorld.ApplyPatch(ball, SceneWorld.ParsePatch(sp.PatchJson));
                        world.Balls.Add(ball);
                        spawned++;
                    }
                }

                world.IsPlaying = true;
                world.NotifyChanged();
                log.Info("game", $"game.start 吐球 {spawned}");
                return CommandResult.Ok($"开局完成: 吐球 {spawned}, 仿真已播放");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "game.resetscore",
            Summary = "重置所有阵营积分为 0",
            Example = "game.resetscore",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                foreach (var f in world.Factions)
                    f.Score = 0;
                world.NotifyChanged(markDirty: false);
                return CommandResult.Ok("已重置全部阵营积分");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "faction.set",
            Summary = "事务化设置现有阵营字段",
            Example = "faction.set id=blue name=蓝队 color=#3B82F6 balls=8 multiplier=2 score=0",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "id", Description = "阵营 id", Required = true },
                new ParameterSpec { Name = "name", Description = "名称" },
                new ParameterSpec { Name = "color", Description = "颜色 #RRGGBB" },
                new ParameterSpec { Name = "balls", Description = "初始球数", Type = ParamType.Int },
                new ParameterSpec { Name = "multiplier", Description = "初始倍率" },
                new ParameterSpec { Name = "score", Description = "积分" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                long? multiplier = null;
                if (ctx.GetString("multiplier") is { } rawMultiplier)
                {
                    if (!long.TryParse(rawMultiplier, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        return CommandResult.Fail("初始倍率必须是整数");
                    multiplier = parsed;
                }

                long? score = null;
                if (ctx.GetString("score") is { } rawScore)
                {
                    if (!long.TryParse(rawScore, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        return CommandResult.Fail("积分必须是整数");
                    score = parsed;
                }

                var result = factionEditor.Apply(new FactionEditRequest(
                    ctx.RequireString("id"),
                    ctx.GetString("name"),
                    ctx.GetString("color"),
                    ctx.GetString("balls") != null ? ctx.GetInt("balls") : null,
                    multiplier,
                    score));
                return result.Success ? CommandResult.Ok(result.Message) : CommandResult.Fail(result.Message);
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "faction.list",
            Summary = "列出阵营与积分",
            Example = "faction.list",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                if (world.Factions.Count == 0)
                    return CommandResult.Ok("(无阵营)");
                var lines = world.Factions.Select(f =>
                    $"{f.Id}\t{f.Name}\tcolor={f.Color}\tballs={f.InitialBalls}\t×{f.InitialMultiplier}\tscore={f.Score}");
                return CommandResult.Ok(string.Join("\n", lines));
            }),
        });
    }

}
