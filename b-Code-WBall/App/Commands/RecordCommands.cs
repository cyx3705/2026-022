using AppShell.Core.Commands;
using WBall.Recording;
using WBall.Stage;

namespace WBall.Commands;

public static class RecordCommands
{
    public static void Register(CommandRegistry registry, StageRecorder recorder, StageState stage)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "record.config",
            Summary = "查询或设置录制参数",
            Example = "record.config w=1280 h=720 fps=30 mp4=true keeppng=true usesize=true",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "w", Description = "宽(usesize=false 时生效)", Type = ParamType.Int },
                new ParameterSpec { Name = "h", Description = "高(usesize=false 时生效)", Type = ParamType.Int },
                new ParameterSpec { Name = "fps", Description = "帧率", Type = ParamType.Int },
                new ParameterSpec { Name = "mp4", Description = "优先输出 MP4", Type = ParamType.Bool },
                new ParameterSpec { Name = "keeppng", Description = "保留 PNG 帧", Type = ParamType.Bool },
                new ParameterSpec { Name = "usesize", Description = "跟 StageView 实际尺寸", Type = ParamType.Bool },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var c = recorder.Config;
                if (ctx.Has("w")) c.Width = Math.Clamp(ctx.GetInt("w"), 320, 7680);
                if (ctx.Has("h")) c.Height = Math.Clamp(ctx.GetInt("h"), 240, 4320);
                if (ctx.Has("fps")) c.Fps = Math.Clamp(ctx.GetInt("fps"), 1, 120);
                if (ctx.Has("mp4")) c.PreferMp4 = ctx.GetBool("mp4");
                if (ctx.Has("keeppng")) c.KeepPng = ctx.GetBool("keeppng");
                if (ctx.Has("usesize")) c.UseStageViewSize = ctx.GetBool("usesize");
                return CommandResult.Ok(
                    $"w={c.Width} h={c.Height} fps={c.Fps} mp4={c.PreferMp4.ToString().ToLowerInvariant()} " +
                    $"keeppng={c.KeepPng.ToString().ToLowerInvariant()} usesize={c.UseStageViewSize.ToString().ToLowerInvariant()}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "record.start",
            Summary = "离线录制舞台(PNG 兜底,尽量 MF MP4)",
            Example = "record.start seconds=5 seed=42 name=demo",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "seconds", Description = "时长秒", Type = ParamType.Double, Default = "5" },
                new ParameterSpec { Name = "seed", Description = "录制用种子", Type = ParamType.Int },
                new ParameterSpec { Name = "name", Description = "输出名前缀" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    stage.SetMode(StageMode.Record);
                    var result = recorder.Record(
                        ctx.GetDouble("seconds", 5),
                        ctx.Has("seed") ? ctx.GetInt("seed") : null,
                        ctx.GetString("name"));
                    return CommandResult.Ok(result);
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "record.stop",
            Summary = "停止录制(同步录制模式下开局即跑完,主要用于状态复位)",
            Example = "record.stop",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                stage.SetMode(StageMode.Edit);
                return CommandResult.Ok(recorder.StatusText);
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "record.status",
            Summary = "录制状态",
            Example = "record.status",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(
                $"recording={recorder.IsRecording.ToString().ToLowerInvariant()} status={recorder.StatusText} " +
                $"dir={recorder.LastOutputDirectory ?? "-"} mp4={recorder.LastMp4Path ?? "-"}")),
        });
    }
}
