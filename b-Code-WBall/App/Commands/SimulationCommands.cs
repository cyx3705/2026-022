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
internal static class SimulationCommands
{
    public static void Register(CommandRegistry registry, SceneWorld world, IShellLog log, ScenePropertyService sceneProperties)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "sim.play",
            Summary = "开始仿真",
            Example = "sim.play",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                world.IsPlaying = true;
                world.NotifyChanged(markDirty: false, visual: true, project: false);
                return CommandResult.Ok("仿真播放");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "sim.pause",
            Summary = "暂停仿真",
            Example = "sim.pause",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                world.IsPlaying = false;
                world.NotifyChanged(markDirty: false, visual: true, project: false);
                return CommandResult.Ok("仿真暂停");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "sim.reset",
            Summary = "重置仿真(清除运行时小球)",
            Example = "sim.reset",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                world.ResetSimulation();
                return CommandResult.Ok("仿真已重置");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "sim.seed",
            Summary = "设置随机种子(影响随机生成器选择)",
            Example = "sim.seed value=42",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "value", Description = "种子", Type = ParamType.Int, Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                world.Seed = ctx.GetInt("value");
                return CommandResult.Ok($"随机种子={world.Seed}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "sim.ballcollision",
            Summary = "球-球碰撞开关(v1.1)",
            Example = "sim.ballcollision on=true",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "on",
                    Description = "true/false",
                    Type = ParamType.Bool,
                    Required = true,
                    Position = 0,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                world.BallCollisionEnabled = ctx.GetBool("on");
                world.NotifyChanged();
                return CommandResult.Ok($"球-球碰撞: {(world.BallCollisionEnabled ? "开" : "关")}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "sim.trail",
            Summary = "小球尾迹总开关(v1.5.2)",
            Example = "sim.trail on=true",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "on",
                    Description = "true/false",
                    Type = ParamType.Bool,
                    Required = true,
                    Position = 0,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                world.TrailEnabled = ctx.GetBool("on");
                if (!world.TrailEnabled)
                    world.ClearBallTrails();
                world.NotifyChanged(markDirty: false);
                return CommandResult.Ok($"尾迹: {(world.TrailEnabled ? "开" : "关")}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "sim.trailset",
            Summary = "设置尾迹长度与传送残影时长(v1.5.2)",
            Example = "sim.trailset length=24 fade=0.35",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "length", Description = "轨迹点数上限", Type = ParamType.Int },
                new ParameterSpec { Name = "fade", Description = "传送残影秒数", Type = ParamType.Double },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (ctx.GetString("length") != null)
                    world.TrailLength = Math.Clamp(ctx.GetInt("length"), 2, 200);
                if (ctx.GetString("fade") != null)
                    world.TeleportFlashSeconds = Math.Clamp(ctx.GetDouble("fade"), 0, 5);
                world.NotifyChanged(markDirty: false);
                return CommandResult.Ok(
                    $"尾迹 length={world.TrailLength} fade={world.TeleportFlashSeconds:0.###}s");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "sim.gravity",
            Summary = "查看/设置全局重力倍数(默认 10g 向下)",
            Example = "sim.gravity g=10",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "g", Description = "重力倍数", Type = ParamType.Double },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (ctx.GetString("g") != null)
                    world.GravityG = Math.Max(0, ctx.GetDouble("g"));
                return CommandResult.Ok($"全局重力={world.GravityG}g 向下(+Y), 1g={SceneWorld.GUnit}px/s²");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "sim.status",
            Summary = "仿真状态摘要",
            Example = "sim.status",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var json = JsonSerializer.Serialize(new
                {
                    playing = world.IsPlaying,
                    gravityG = world.GravityG,
                    ballCollision = world.BallCollisionEnabled,
                    trail = world.TrailEnabled,
                    trailLength = world.TrailLength,
                    teleportFlash = world.TeleportFlashSeconds,
                    worldWidth = world.WorldWidth,
                    worldHeight = world.WorldHeight,
                    objects = world.Objects.Count,
                    solids = world.Solids.Count,
                    balls = world.Balls.Count,
                    tool = world.Tool.ToString(),
                    seed = world.Seed,
                });
                return CommandResult.Ok(json);
            }),
        });
    }

}
