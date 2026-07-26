using AppShell.Core.Commands;
using WBall.Battle;
using WBall.Stage;

namespace WBall.Commands;

public static class HudCommands
{
    public static void Register(CommandRegistry registry, StageState stage, StageHudView hud, BattleDirector director)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "hud.show",
            Summary = "显示或隐藏舞台 HUD",
            Example = "hud.show on=true",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "on", Description = "true/false", Type = ParamType.Bool, Default = "true", Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                stage.Configure(hudVisible: ctx.GetBool("on", true));
                return CommandResult.Ok($"hud={stage.HudVisible.ToString().ToLowerInvariant()}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "hud.status",
            Summary = "HUD / 导演摘要",
            Example = "hud.status",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
                CommandResult.Ok(
                    $"hud={stage.HudVisible.ToString().ToLowerInvariant()} watermark={(string.IsNullOrEmpty(hud.Watermark) ? "-" : hud.Watermark)} " +
                    director.Status())),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "hud.watermark",
            Summary = "设置 HUD 水印占位文本(默认空)",
            Example = "hud.watermark text=",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "text", Description = "水印文本", Default = "", Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                hud.Watermark = ctx.GetString("text") ?? "";
                return CommandResult.Ok($"watermark={(string.IsNullOrEmpty(hud.Watermark) ? "(空)" : hud.Watermark)}");
            }),
        });
    }
}
