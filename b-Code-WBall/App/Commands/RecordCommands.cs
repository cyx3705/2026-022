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

        registry.Register(new CommandDescriptor
        {
            Name = "render.estimate",
            Summary = "估算出片帧数与原始帧吞吐，不改变现场",
            Example = "render.estimate mode=output seconds=180",
            Readonly = true,
            Parameters =
            [
                new ParameterSpec { Name = "mode", Description = "output|simulation|winner", Default = "output" },
                new ParameterSpec { Name = "seconds", Description = "时长", Type = ParamType.Double, Default = "60" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var c = service.Config;
                var seconds = Math.Max(0.01, ctx.GetDouble("seconds", 60));
                var frames = (long)Math.Ceiling(seconds * c.Fps);
                var bytes = (double)frames * c.Width * c.Height * 4;
                return CommandResult.Ok(
                    $"mode={ctx.GetString("mode") ?? "output"} frames≈{frames} raw={FormatBytes(bytes)} "
                    + $"单帧={FormatBytes((double)c.Width * c.Height * 4)} 内存按单帧流式,不随总时长增长");
            }),
        });

        RegisterSimple(registry, "render.pause", "暂停当前出片任务", _ => { service.Pause(); return Status(service); });
        RegisterSimple(registry, "render.resume", "继续当前出片任务", _ => { service.Resume(); return Status(service); });
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
            Summary = alias ? "兼容旧录制配置命令" : "查询或设置出片与时间参数",
            Example = $"{name} w=1280 h=720 fps=30 autoSlow=true minScale=0.25",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "w", Description = "宽", Type = ParamType.Int },
                new ParameterSpec { Name = "h", Description = "高", Type = ParamType.Int },
                new ParameterSpec { Name = "fps", Description = "输出帧率", Type = ParamType.Int },
                new ParameterSpec { Name = "queue", Description = "帧投影队列容量", Type = ParamType.Int },
                new ParameterSpec { Name = "mp4", Description = "输出 MP4", Type = ParamType.Bool },
                new ParameterSpec { Name = "keeppng", Description = "保留 PNG", Type = ParamType.Bool },
                new ParameterSpec { Name = "autoSlow", Description = "出片自动降速", Type = ParamType.Bool },
                new ParameterSpec { Name = "previewAutoSlow", Description = "预览自动降速", Type = ParamType.Bool },
                new ParameterSpec { Name = "startBalls", Description = "开始降速球数", Type = ParamType.Int },
                new ParameterSpec { Name = "fullBalls", Description = "最低倍率球数", Type = ParamType.Int },
                new ParameterSpec { Name = "minScale", Description = "最低倍率", Type = ParamType.Double },
                new ParameterSpec { Name = "manualScale", Description = "手动倍率", Type = ParamType.Double },
                new ParameterSpec { Name = "quantum", Description = "倍率量化", Type = ParamType.Double },
                new ParameterSpec { Name = "hysteresis", Description = "升档迟滞球数", Type = ParamType.Int },
                new ParameterSpec { Name = "maxOutputSeconds", Description = "最长输出秒数", Type = ParamType.Int },
                new ParameterSpec { Name = "usesize", Description = "旧参数,已忽略", Type = ParamType.Bool },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var c = service.Config;
                if (ctx.Has("w")) c.Width = ctx.GetInt("w");
                if (ctx.Has("h")) c.Height = ctx.GetInt("h");
                if (ctx.Has("fps")) c.Fps = ctx.GetInt("fps");
                if (ctx.Has("queue")) c.QueueCapacity = ctx.GetInt("queue");
                if (ctx.Has("mp4")) c.PreferMp4 = ctx.GetBool("mp4");
                if (ctx.Has("keeppng")) c.KeepPng = ctx.GetBool("keeppng");
                if (ctx.Has("autoSlow")) c.RenderAutoSlow = ctx.GetBool("autoSlow");
                if (ctx.Has("previewAutoSlow")) c.PreviewAutoSlow = ctx.GetBool("previewAutoSlow");
                if (ctx.Has("startBalls")) c.SlowStartBalls = ctx.GetInt("startBalls");
                if (ctx.Has("fullBalls")) c.SlowFullBalls = ctx.GetInt("fullBalls");
                if (ctx.Has("minScale")) c.MinSimulationScale = ctx.GetDouble("minScale");
                if (ctx.Has("manualScale")) c.ManualSimulationScale = ctx.GetDouble("manualScale");
                if (ctx.Has("quantum")) c.ScaleQuantization = ctx.GetDouble("quantum");
                if (ctx.Has("hysteresis")) c.HysteresisBalls = ctx.GetInt("hysteresis");
                if (ctx.Has("maxOutputSeconds")) c.MaxOutputSeconds = ctx.GetInt("maxOutputSeconds");
                RenderTimeConfigStore.Validate(c);
                service.SaveConfig();
                applyTimeConfig?.Invoke(c);
                var prefix = alias ? "record.* 已兼容转发;建议改用 render.config\n" : "";
                return CommandResult.Ok(prefix + Config(c));
            }),
        });
    }

    private static void RegisterStart(CommandRegistry registry, string name, RenderJobService service, bool alias)
    {
        registry.Register(new CommandDescriptor
        {
            Name = name,
            Summary = alias ? "兼容旧录制命令,转为独立出片任务" : "启动独立结果导向出片任务",
            Example = $"{name} mode=output seconds=60 scenario=demo4 name=demo",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "mode", Description = "output|simulation|winner", Default = "output" },
                new ParameterSpec { Name = "seconds", Description = "输出或模拟时长", Type = ParamType.Double, Default = "5" },
                new ParameterSpec { Name = "seed", Description = "种子;省略时使用剧本种子", Type = ParamType.Int },
                new ParameterSpec { Name = "scenario", Description = "剧本;省略时冻结当前配置" },
                new ParameterSpec { Name = "name", Description = "任务名", Default = "battle" },
                new ParameterSpec { Name = "maxOutputSeconds", Description = "最长输出时长", Type = ParamType.Int },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    var mode = Enum.TryParse<RenderEndMode>(ctx.GetString("mode") ?? "output", true, out var parsed)
                        ? parsed
                        : throw new FormatException("mode 须为 output|simulation|winner");
                    var scenario = ctx.GetString("scenario");
                    var seed = ctx.Has("seed") ? ctx.GetInt("seed") : service.ResolveSeed(scenario);
                    var status = service.Start(new RenderJobRequest(
                        mode,
                        ctx.GetDouble("seconds", 5),
                        seed,
                        ctx.GetString("name") ?? "battle",
                        ctx.Has("maxOutputSeconds") ? ctx.GetInt("maxOutputSeconds") : null,
                        scenario));
                    return CommandResult.Ok((alias ? "record.* 已兼容转发;建议改用 render.start\n" : "") + Format(status));
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
            Summary = alias ? "兼容旧录制状态命令" : "查询出片任务三套时间与进度",
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

    private static string Status(RenderJobService service) => Format(service.Status);

    public static string Format(RenderJobStatus s) =>
        $"job={s.JobId} stage={s.Stage} frame={s.Frame}/{s.TotalFrames} "
        + $"outputTime={N(s.OutputTime)} simulationTime={N(s.SimulationTime)} wallElapsed={N(s.WallElapsed)} "
        + $"balls={s.BallCount} scale={N(s.SimulationScale)} dir={s.OutputDirectory ?? "-"} "
        + $"genFps={N(s.GeneratedFps)} eta={N(s.EtaSeconds)}s memory={FormatBytes(s.WorkingSetBytes)} "
        + $"queue={s.QueueDepth}/{s.PeakQueueDepth} manifest={s.ManifestPath ?? "-"} "
        + $"png={s.PngDirectory ?? "-"} mp4={s.Mp4Path ?? "-"} hash={s.FinalHash ?? "-"} error={s.Error ?? "-"}";

    private static string Config(RenderTimeConfig c) =>
        $"w={c.Width} h={c.Height} fps={c.Fps} queue={c.QueueCapacity} mp4={B(c.PreferMp4)} keeppng={B(c.KeepPng)} "
        + $"autoSlow={B(c.RenderAutoSlow)} previewAutoSlow={B(c.PreviewAutoSlow)} "
        + $"startBalls={c.SlowStartBalls} fullBalls={c.SlowFullBalls} minScale={N(c.MinSimulationScale)} "
        + $"manualScale={N(c.ManualSimulationScale)} quantum={N(c.ScaleQuantization)} "
        + $"hysteresis={c.HysteresisBalls} maxOutputSeconds={c.MaxOutputSeconds}";

    private static string FormatBytes(double bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / (1024 * 1024 * 1024):0.##} GiB",
        >= 1024 * 1024 => $"{bytes / (1024 * 1024):0.##} MiB",
        _ => $"{bytes / 1024:0.##} KiB",
    };

    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string B(bool value) => value.ToString().ToLowerInvariant();
}
