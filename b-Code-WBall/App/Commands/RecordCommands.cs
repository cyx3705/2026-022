using System.Globalization;
using AppShell.Core.Commands;
using WBall.Recording;

namespace WBall.Commands;

public static class RecordCommands
{
    public static void Register(
        CommandRegistry registry,
        RenderJobService service,
        Action<RenderTimeConfig>? applyTimeConfig = null)
    {
        RegisterConfig(registry, "render.config", service, applyTimeConfig, alias: false);
        RegisterStart(registry, "render.start", service, alias: false);
        RegisterStatus(registry, "render.status", service, alias: false);
        RegisterSimple(registry, "render.pause", "暂停当前出片任务", _ => { service.Pause(); return Format(service.Status); });
        RegisterSimple(registry, "render.resume", "继续当前出片任务", _ => { service.Resume(); return Format(service.Status); });
        registry.Register(new CommandDescriptor
        {
            Name = "render.cancel",
            Summary = "取消当前出片任务",
            Example = "render.cancel confirm=true",
            Parameters = [new ParameterSpec { Name = "confirm", Description = "显式确认", Type = ParamType.Bool, Default = "false" }],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (!ctx.GetBool("confirm", false))
                    return CommandResult.Fail("取消出片须 confirm=true");
                service.Cancel();
                return CommandResult.Ok("已请求取消;任务将在当前固定步/帧边界停止");
            }),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "render.list",
            Summary = "列出最近出片任务",
            Example = "render.list limit=20",
            Readonly = true,
            Parameters = [new ParameterSpec { Name = "limit", Description = "条数", Type = ParamType.Int, Default = "20" }],
            Handler = CommandDescriptor.Sync(ctx => CommandResult.Ok(
                string.Join(Environment.NewLine, service.List(ctx.GetInt("limit", 20))))),
        });

        RegisterConfig(registry, "record.config", service, applyTimeConfig, alias: true);
        RegisterStart(registry, "record.start", service, alias: true);
        RegisterStatus(registry, "record.status", service, alias: true);
        RegisterSimple(registry, "record.stop", "兼容旧停止命令", _ =>
        {
            service.Cancel();
            return "record.* 已兼容转发到 render.cancel;已请求取消";
        });
    }

    private static void RegisterConfig(
        CommandRegistry registry,
        string name,
        RenderJobService service,
        Action<RenderTimeConfig>? applyTimeConfig,
        bool alias)
    {
        registry.Register(new CommandDescriptor
        {
            Name = name,
            Summary = alias ? "兼容旧录制配置命令" : "查询或设置 MP4 出片参数",
            Example = $"{name} w=1920 h=1080 fps=30 queue=4 autoSlow=true",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "w", Description = "偶数宽度 320..7680", Type = ParamType.Int },
                new ParameterSpec { Name = "h", Description = "偶数高度 240..4320", Type = ParamType.Int },
                new ParameterSpec { Name = "fps", Description = "输出帧率", Type = ParamType.Int },
                new ParameterSpec { Name = "queue", Description = "帧投影队列容量", Type = ParamType.Int },
                new ParameterSpec { Name = "autoSlow", Description = "出片自动降速", Type = ParamType.Bool },
                new ParameterSpec { Name = "previewAutoSlow", Description = "预览自动降速", Type = ParamType.Bool },
                new ParameterSpec { Name = "startBalls", Description = "开始降速球数", Type = ParamType.Int },
                new ParameterSpec { Name = "fullBalls", Description = "最低倍率球数", Type = ParamType.Int },
                new ParameterSpec { Name = "minScale", Description = "最低倍率", Type = ParamType.Double },
                new ParameterSpec { Name = "manualScale", Description = "手动倍率", Type = ParamType.Double },
                new ParameterSpec { Name = "quantum", Description = "倍率量化", Type = ParamType.Double },
                new ParameterSpec { Name = "hysteresis", Description = "升档迟滞球数", Type = ParamType.Int },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    var next = service.Config.Clone();
                    if (ctx.Has("w")) next.Width = ctx.GetInt("w");
                    if (ctx.Has("h")) next.Height = ctx.GetInt("h");
                    if (ctx.Has("fps")) next.Fps = ctx.GetInt("fps");
                    if (ctx.Has("queue")) next.QueueCapacity = ctx.GetInt("queue");
                    if (ctx.Has("autoSlow")) next.RenderAutoSlow = ctx.GetBool("autoSlow");
                    if (ctx.Has("previewAutoSlow")) next.PreviewAutoSlow = ctx.GetBool("previewAutoSlow");
                    if (ctx.Has("startBalls")) next.SlowStartBalls = ctx.GetInt("startBalls");
                    if (ctx.Has("fullBalls")) next.SlowFullBalls = ctx.GetInt("fullBalls");
                    if (ctx.Has("minScale")) next.MinSimulationScale = ctx.GetDouble("minScale");
                    if (ctx.Has("manualScale")) next.ManualSimulationScale = ctx.GetDouble("manualScale");
                    if (ctx.Has("quantum")) next.ScaleQuantization = ctx.GetDouble("quantum");
                    if (ctx.Has("hysteresis")) next.HysteresisBalls = ctx.GetInt("hysteresis");
                    service.UpdateConfig(next);
                    applyTimeConfig?.Invoke(next);
                    var prefix = alias ? "record.* 已兼容转发;建议改用 render.config\n" : "";
                    return CommandResult.Ok(prefix + Config(next));
                }
                catch (Exception ex) { return CommandResult.Fail(ex.Message); }
            }),
        });
    }

    private static void RegisterStart(CommandRegistry registry, string name, RenderJobService service, bool alias)
    {
        registry.Register(new CommandDescriptor
        {
            Name = name,
            Summary = alias ? "兼容旧录制命令,转为胜者 MP4 出片" : "启动独立 winner-only MP4 出片任务",
            Example = $"{name} seed=42 scenario=demo4 name=demo",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "seed", Description = "种子;省略时使用剧本种子", Type = ParamType.Int },
                new ParameterSpec { Name = "scenario", Description = "剧本;省略时冻结当前配置" },
                new ParameterSpec { Name = "name", Description = "任务名", Default = "battle" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    var scenario = ctx.GetString("scenario");
                    var seed = ctx.Has("seed") ? ctx.GetInt("seed") : service.ResolveSeed(scenario);
                    var status = service.Start(new RenderJobRequest(
                        seed,
                        ctx.GetString("name") ?? "battle",
                        scenario));
                    var prefix = alias ? "record.* 已兼容转发;建议改用 render.start\n" : "";
                    return CommandResult.Ok(prefix + Format(status));
                }
                catch (Exception ex) { return CommandResult.Fail(ex.Message); }
            }),
        });
    }

    private static void RegisterStatus(CommandRegistry registry, string name, RenderJobService service, bool alias)
    {
        registry.Register(new CommandDescriptor
        {
            Name = name,
            Summary = alias ? "兼容旧录制状态命令" : "查询胜者 MP4 出片进度",
            Example = name,
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(
                (alias ? "record.* 已兼容转发;建议改用 render.status\n" : "") + Format(service.Status))),
        });
    }

    private static void RegisterSimple(
        CommandRegistry registry,
        string name,
        string summary,
        Func<CommandContext, string> action)
    {
        registry.Register(new CommandDescriptor
        {
            Name = name,
            Summary = summary,
            Example = name,
            Handler = CommandDescriptor.Sync(ctx => CommandResult.Ok(action(ctx))),
        });
    }

    public static string Format(RenderJobStatus s) =>
        $"job={s.JobId} stage={s.Stage} frame={s.Frame} "
        + $"videoTime={N(s.VideoTime)} simulationTime={N(s.SimulationTime)} wallElapsed={N(s.WallElapsed)} "
        + $"balls={s.BallCount} scale={N(s.SimulationScale)} dir={s.OutputDirectory ?? "-"} "
        + $"genFps={N(s.GeneratedFps)} memory={FormatBytes(s.WorkingSetBytes)} "
        + $"queue={s.QueueDepth}/{s.PeakQueueDepth} manifest={s.ManifestPath ?? "-"} "
        + $"mp4={s.Mp4Path ?? "-"} hash={s.FinalHash ?? "-"} error={s.Error ?? "-"}";

    private static string Config(RenderTimeConfig c) =>
        $"w={c.Width} h={c.Height} fps={c.Fps} queue={c.QueueCapacity} format=mp4 codec=h264 "
        + $"autoSlow={B(c.RenderAutoSlow)} previewAutoSlow={B(c.PreviewAutoSlow)} "
        + $"startBalls={c.SlowStartBalls} fullBalls={c.SlowFullBalls} minScale={N(c.MinSimulationScale)} "
        + $"manualScale={N(c.ManualSimulationScale)} quantum={N(c.ScaleQuantization)} "
        + $"hysteresis={c.HysteresisBalls} end=winner animation=3s";

    private static string FormatBytes(double bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / (1024 * 1024 * 1024):0.##} GiB",
        >= 1024 * 1024 => $"{bytes / (1024 * 1024):0.##} MiB",
        _ => $"{bytes / 1024:0.##} KiB",
    };

    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string B(bool value) => value.ToString().ToLowerInvariant();
}
