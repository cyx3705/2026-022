using WBall.Recording;

namespace WBall.Verify.Suites;

/// <summary>
/// v3.4 V34-09:时间轴 suite（v3.2.1 出片时钟）。
/// 每个输出帧推进多少个 1/60 模拟步、自动降速档位、手动倍率 —— 与画面无关,纯时钟算术,
/// 所以它是拆分的第一个叶子:不需要 dataRoot、不落盘、不启窗。
/// </summary>
internal static class TimelineSuite
{
    public static void Run(VerifyRun run)
    {
        foreach (var fps in new[] { 24, 25, 30, 50, 60 })
        {
            var timeline = new TimelineClock(new RenderTimeConfig { PreviewAutoSlow = false }, autoSlow: false);
            var advanced = 0;
            for (var frame = 0; frame < fps * 10; frame++)
                advanced += timeline.AdvanceOutputFrame(fps, 0);
            run.Check($"timeline exact {fps}fps", Math.Abs(advanced / 60.0 - 10) <= 1.0 / 60,
                $"steps={advanced} simulation={advanced / 60.0:0.######}");
        }

        var slowTimeline = new TimelineClock(new RenderTimeConfig(), autoSlow: true);
        run.Check("timeline auto scale starts at 1x", slowTimeline.ResolveScale(2_000) == 1);
        run.Check("timeline auto scale reaches minimum", slowTimeline.ResolveScale(10_000) == 0.25);
        slowTimeline.Reset();
        var slowSteps = 0;
        for (var frame = 0; frame < 60; frame++)
            slowSteps += slowTimeline.AdvanceOutputFrame(60, 10_000);
        run.Check("timeline 10k balls advances 0.25x", slowSteps == 15, $"steps={slowSteps}");

        var manualTimeline = new TimelineClock(new RenderTimeConfig { ManualSimulationScale = 1.5 }, autoSlow: false);
        run.Check("timeline manual scale ignores ball count", manualTimeline.ResolveScale(100_000) == 1.5);
    }
}
