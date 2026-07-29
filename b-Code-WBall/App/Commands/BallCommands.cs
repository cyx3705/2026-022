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
internal static class BallCommands
{
    public static void Register(
        CommandRegistry registry,
        SceneWorld world,
        ScenePropertyService sceneProperties,
        IShellLog log,
        BallEditorService ballEditor)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "ball.spawn",
            Summary = "生成小球(位置仅在内存)",
            Example = "ball.spawn x=100 y=50 color=#3B82F6 size=12 weight=1",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "x", Description = "中心 X", Type = ParamType.Double },
                new ParameterSpec { Name = "y", Description = "中心 Y", Type = ParamType.Double },
                new ParameterSpec { Name = "spawner", Description = "生成器 id(优先于 x/y)" },
                new ParameterSpec { Name = "id", Description = "可选 id" },
                new ParameterSpec { Name = "color", Description = "颜色", Default = "#3B82F6" },
                new ParameterSpec { Name = "weight", Description = "重量(缺省按公式)", Type = ParamType.Double },
                new ParameterSpec { Name = "size", Description = "半径(缺省按公式)", Type = ParamType.Double },
                new ParameterSpec { Name = "multiplier", Description = "倍率(缺省 InitialMultiplier)" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                double x = 40, y = 40;
                var spawnerId = ctx.GetString("spawner");
                if (!string.IsNullOrEmpty(spawnerId))
                {
                    var sp = world.FindObject(spawnerId);
                    if (sp == null || sp.Type != SceneObjectType.Spawner)
                        return CommandResult.Fail($"生成器不存在: {spawnerId}");
                    x = sp.X + sp.W / 2;
                    y = sp.Y + sp.H / 2;
                }
                else if (ctx.GetString("x") != null && ctx.GetString("y") != null)
                {
                    x = ctx.GetDouble("x");
                    y = ctx.GetDouble("y");
                }
                else
                {
                    var first = world.OfType(SceneObjectType.Spawner).FirstOrDefault();
                    if (first != null)
                    {
                        x = first.X + first.W / 2;
                        y = first.Y + first.H / 2;
                    }
                }

                var id = ctx.GetString("id") ?? world.NextBallId();
                if (world.FindBall(id) != null)
                    return CommandResult.Fail($"小球 id 已存在: {id}");

                long mult = world.Defaults.InitialMultiplier;
                if (ctx.GetString("multiplier") != null
                    && long.TryParse(ctx.GetString("multiplier"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mParse))
                    mult = PublicDefaults.ClampMultiplier(mParse);

                var ball = new Ball
                {
                    Id = id,
                    X = x,
                    Y = y,
                    Color = ctx.GetString("color") ?? "#3B82F6",
                    Multiplier = PublicDefaults.ClampMultiplier(mult),
                };
                world.Defaults.ApplyToBall(ball);
                if (ctx.GetString("weight") != null)
                    ball.Weight = PublicDefaults.RoundWeight(ctx.GetDouble("weight"));
                if (ctx.GetString("size") != null)
                    ball.Size = PublicDefaults.RoundSize(ctx.GetDouble("size"));

                if (!string.IsNullOrEmpty(spawnerId))
                {
                    var sp = world.FindObject(spawnerId);
                    SceneWorld.ApplyPatch(ball, SceneWorld.ParsePatch(sp?.PatchJson));
                }

                world.Balls.Add(ball);
                world.NotifyChanged();
                return CommandResult.Ok($"已生成小球 {id} ×{ball.Multiplier} @({Fmt(x)},{Fmt(y)})");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "ball.recalc",
            Summary = "按当前公式重算全部球的 size/weight(取整)",
            Example = "ball.recalc",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                foreach (var ball in world.Balls)
                    world.Defaults.ApplyToBall(ball);
                world.NotifyChanged();
                return CommandResult.Ok($"已重算 {world.Balls.Count} 个球");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "ball.despawn",
            Summary = "销毁/传送小球(有生成器则同 id 传送,无则移除)",
            Example = "ball.despawn id=ball1",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "id", Description = "小球 id", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var ball = world.FindBall(ctx.RequireString("id"));
                if (ball == null)
                    return CommandResult.Fail("未找到小球");

                var sink = world.OfType(SceneObjectType.Despawner).FirstOrDefault();
                if (sink == null)
                {
                    var spawners = world.OfType(SceneObjectType.Spawner).ToList();
                    if (spawners.Count == 0)
                    {
                        if (string.Equals(world.SelectedBallId, ball.Id, StringComparison.OrdinalIgnoreCase))
                            world.SelectedBallId = null;
                        world.Balls.Remove(ball);
                        world.NotifyChanged();
                        return CommandResult.Ok($"小球 {ball.Id} 已移除(无销毁器/生成器)");
                    }

                    // 无销毁器时仅传送到生成器
                    var sp0 = spawners[world.Rng.Next(spawners.Count)];
                    SceneWorld.ApplyPatch(ball, SceneWorld.ParsePatch(sp0.PatchJson));
                    ball.X = sp0.X + sp0.W / 2;
                    ball.Y = sp0.Y + sp0.H / 2;
                    ball.Vx = ball.Vy = 0;
                    world.NotifyChanged();
                    return CommandResult.Ok($"小球 {ball.Id} 已传送至生成器 {sp0.Id}");
                }

                PhysicsEngine.TeleportBall(world, ball, sink, msg => log.Info("ball", msg));
                // TeleportBall may have removed the ball
                if (world.FindBall(ball.Id) == null)
                    return CommandResult.Ok($"小球 {ball.Id} 已移除(无生成器)");
                return CommandResult.Ok($"小球 {ball.Id} 已传送 ×{ball.Multiplier}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "ball.set",
            Summary = "设置小球性质(不写位置到库;位置仅内存)",
            Example = "ball.set id=ball1 color=#FF0000 multiplier=5",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "id", Description = "小球 id", Required = true },
                new ParameterSpec { Name = "color", Description = "颜色" },
                new ParameterSpec { Name = "weight", Description = "重量", Type = ParamType.Double },
                new ParameterSpec { Name = "size", Description = "半径", Type = ParamType.Double },
                new ParameterSpec { Name = "multiplier", Description = "倍率(变时按公式重算 size/weight)" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                long? multiplier = null;
                if (ctx.GetString("multiplier") is { } rawMultiplier)
                {
                    if (!long.TryParse(rawMultiplier, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        return CommandResult.Fail("倍率必须是整数");
                    multiplier = parsed;
                }

                var result = ballEditor.Apply(new BallEditRequest(
                    ctx.RequireString("id"),
                    ctx.GetString("color"),
                    multiplier,
                    ctx.GetString("size") != null ? ctx.GetDouble("size") : null,
                    ctx.GetString("weight") != null ? ctx.GetDouble("weight") : null));
                return result.Success ? CommandResult.Ok(result.Message) : CommandResult.Fail(result.Message);
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "ball.list",
            Summary = "列出小球(含内存位置,仅控制台)",
            Example = "ball.list",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                if (world.Balls.Count == 0)
                    return CommandResult.Ok("(无小球)");
                var lines = world.Balls.Select(b =>
                    $"{b.Id}\tcolor={b.Color}\t×{b.Multiplier}\tweight={Fmt(b.Weight)}\tsize={Fmt(b.Size)}\t@({Fmt(b.X)},{Fmt(b.Y)})");
                return CommandResult.Ok(string.Join("\n", lines));
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "ball.addprop",
            Summary = "为小球登记自定义性质(不可为 color/weight/size/x/y)",
            Example = "ball.addprop name=team",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "性质名", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    var name = sceneProperties.AddProperty(ctx.RequireString("name"));
                    return CommandResult.Ok($"已登记自定义性质 {name}");
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "ball.removeprop",
            Summary = "删除小球自定义性质(拒绝删除内建性质)",
            Example = "ball.removeprop name=team",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "性质名", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    sceneProperties.RemoveProperty(ctx.RequireString("name"));
                    return CommandResult.Ok("已删除自定义性质");
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "ball.setprop",
            Summary = "设置小球自定义性质值(直接写 SceneWorld)",
            Example = "ball.setprop id=ball1 name=team value=red",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "id", Description = "小球 id", Required = true },
                new ParameterSpec { Name = "name", Description = "列名", Required = true },
                new ParameterSpec { Name = "value", Description = "值", Required = true },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var ball = world.FindBall(ctx.RequireString("id"));
                if (ball == null)
                    return CommandResult.Fail("未找到小球");
                var name = ctx.RequireString("name");
                if (name.Equals("color", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("weight", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("size", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("multiplier", StringComparison.OrdinalIgnoreCase))
                    return CommandResult.Fail("内建性质请用 ball.set");
                if (!sceneProperties.CustomProperties.Contains(name, StringComparer.OrdinalIgnoreCase))
                    return CommandResult.Fail($"自定义性质不存在,请先 ball.addprop name={name}");
                ball.Props[name] = ctx.RequireString("value");
                world.NotifyChanged();
                return CommandResult.Ok($"已设置 {ball.Id}.{name}");
            }),
        });
    }

    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
