using AppShell.Core.Commands;
using WBall.Battle;
using WBall.Model;
using WBall.Stage;

namespace WBall.Commands;

public static class StageCommands
{
    public static void Register(
        CommandRegistry registry,
        StageState stage,
        SceneWorld economyWorld,
        BattleRuntime battle)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "stage.show",
            Summary = "显示合成舞台或切回纯落球视图",
            Example = "stage.show on=true",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "on", Description = "true=合成舞台,false=纯落球", Type = ParamType.Bool, Default = "true", Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                stage.SetCompositeVisible(ctx.GetBool("on", true));
                return CommandResult.Ok(stage.ToString());
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "stage.mode",
            Summary = "切换舞台编辑、播放或录制状态",
            Example = "stage.mode play",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "mode",
                    Description = "edit/play/record",
                    Required = true,
                    Position = 0,
                    AllowedValues = ["edit", "play", "record"],
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var mode = ctx.RequireString("mode").ToLowerInvariant() switch
                {
                    "edit" => StageMode.Edit,
                    "play" => StageMode.Play,
                    "record" => StageMode.Record,
                    _ => throw new InvalidOperationException("未知舞台模式"),
                };
                stage.SetMode(mode);
                if (mode == StageMode.Edit)
                {
                    economyWorld.IsPlaying = false;
                    battle.AutomaticFire = false;
                }
                else if (mode == StageMode.Play)
                {
                    economyWorld.IsPlaying = true;
                    battle.AutomaticFire = true;
                }
                // record: 由 record.start 驱动导演,不自动开火空转
                return CommandResult.Ok(stage.ToString());
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "stage.layout",
            Summary = "查询或设置合成舞台布局与逻辑分辨率",
            Example = "stage.layout split=0.5 orientation=horizontal hud=on w=1920 h=1080 bg=#111318",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "split", Description = "经济区占比(0.2~0.8)", Type = ParamType.Double },
                new ParameterSpec { Name = "orientation", Description = "horizontal/vertical", AllowedValues = ["horizontal", "vertical"] },
                new ParameterSpec { Name = "hud", Description = "HUD 开关", Type = ParamType.Bool },
                new ParameterSpec { Name = "w", Description = "逻辑宽(640~7680)", Type = ParamType.Int },
                new ParameterSpec { Name = "h", Description = "逻辑高(360~4320)", Type = ParamType.Int },
                new ParameterSpec { Name = "bg", Description = "背景色 #RRGGBB" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (!ctx.Has("split") && !ctx.Has("orientation") && !ctx.Has("hud")
                    && !ctx.Has("w") && !ctx.Has("h") && !ctx.Has("bg"))
                {
                    return CommandResult.Ok(stage.ToString());
                }

                try
                {
                    StageOrientation? orientation = ctx.GetString("orientation")?.ToLowerInvariant() switch
                    {
                        "horizontal" => StageOrientation.Horizontal,
                        "vertical" => StageOrientation.Vertical,
                        _ => null,
                    };
                    stage.Configure(
                        ctx.Has("split") ? ctx.GetDouble("split") : null,
                        orientation,
                        ctx.Has("hud") ? ctx.GetBool("hud") : null,
                        ctx.GetString("bg"),
                        ctx.Has("w") ? ctx.GetInt("w") : null,
                        ctx.Has("h") ? ctx.GetInt("h") : null);
                    return CommandResult.Ok(stage.ToString());
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "stage.status",
            Summary = "显示合成舞台状态",
            Example = "stage.status",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(stage.ToString())),
        });
    }
}
