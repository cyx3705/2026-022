using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using AppShell.Core.Commands;
using WBall.Battle;
using WBall.Commands;
using WBall.DropZone;
using WBall.Stage;

namespace WBall.Verify.Suites;

internal sealed record VisibleRuntimeMetric(
    double WarmupSeconds,
    double MeasurementSeconds,
    double AverageFramesPerSecond,
    double P95FrameIntervalMilliseconds,
    double MaximumFrameIntervalMilliseconds,
    int UiStallsOver200Milliseconds,
    double CommandP95Milliseconds,
    int CommandSamples,
    long PeakWorkingSetBytes,
    double WorkingSetSlopeMiBPerMinute,
    long LohGrowthBytes,
    long UiThreadAllocatedBytes,
    long EconomyRenderAllocatedBytes,
    long ArenaRenderAllocatedBytes,
    long HudRenderAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long FirstSequence,
    long LastSequence,
    int PresentedFrames);

internal static class VisibleRuntimeProbe
{
    public static VisibleRuntimeMetric Run(
        VerifyRun run,
        bool smoke,
        double? warmupSeconds = null,
        double? measurementSeconds = null)
    {
        VisibleRuntimeMetric? result = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = RunOnDispatcher(run, smoke, warmupSeconds, measurementSeconds);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "WBall.VisibleRuntimeProbe",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
            throw new InvalidOperationException("Visible WPF runtime probe failed", failure);
        return result ?? throw new InvalidOperationException("Visible WPF runtime probe produced no result");
    }

    private static VisibleRuntimeMetric RunOnDispatcher(
        VerifyRun run,
        bool smoke,
        double? warmupSeconds,
        double? measurementSeconds)
    {
        var warmup = TimeSpan.FromSeconds(warmupSeconds ?? (smoke ? 1 : 30));
        var duration = TimeSpan.FromSeconds(measurementSeconds ?? (smoke ? 3 : 600));
        var harness = run.NewHarness(new BalanceConfig { FriendlyAssistVisualEnabled = false },
            new ArenaLayoutConfig { BallCollision = false });
        harness.Stage.Configure(logicalWidth: 1920, logicalHeight: 1080);
        harness.Director.Start(seed: 42, countdownSeconds: 0);
        RuntimePerformanceSuite.SeedEconomyForProbe(harness.EconomyWorld, 10_000);
        RuntimePerformanceSuite.SeedBattleForProbe(harness, 10_000, projectile: true);

        var economy = new DropZoneView(harness.EconomyWorld, run.Log) { AutoStepEnabled = false };
        var arena = new ArenaView(harness.BattleWorld, harness.Battle);
        var stage = new StageView(
            harness.Stage, harness.EconomyWorld, harness.BattleWorld, harness.Battle,
            harness.Director, economy, arena, harness.Weapons,
            new WBall.Recording.RenderTimeConfig { PreviewAutoSlow = true });

        var registry = new CommandRegistry();
        DirectorCommands.Register(registry, harness.Director, harness.Weapons);
        stage.Coordinator.WrapNewCommands(registry, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var bus = new CommandBus(registry, run.Log);

        var window = new Window
        {
            Title = "WBall V3.7 runtime performance probe",
            Content = stage,
            Width = 1920,
            Height = 1080,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
        };

        var totalWatch = Stopwatch.StartNew();
        var measurementWatch = new Stopwatch();
        var intervals = new List<double>(20_000);
        var commandSamples = new List<double>(32);
        var memorySamples = new List<(double Seconds, long Bytes)>(32);
        var process = Process.GetCurrentProcess();
        var frame = new DispatcherFrame();
        long lastPresentation = 0;
        long firstPresentation = 0;
        long firstSequence = 0;
        long lastSequence = 0;
        var presentedFrames = 0;
        var measuring = false;
        var commandBusy = false;
        var startGen2 = 0;
        var startGen0 = 0;
        var startGen1 = 0;
        var startLoh = 0L;
        var startUiAllocated = 0L;
        var startEconomyAllocated = 0L;
        var startArenaAllocated = 0L;
        var startHudAllocated = 0L;
        var nextMemorySample = 0d;

        stage.FramePresented += sequence =>
        {
            if (!measuring)
                return;
            var now = Stopwatch.GetTimestamp();
            if (lastPresentation != 0)
                intervals.Add(Stopwatch.GetElapsedTime(lastPresentation, now).TotalMilliseconds);
            else
            {
                firstSequence = sequence;
                firstPresentation = now;
            }
            lastPresentation = now;
            lastSequence = sequence;
            presentedFrames++;
        };

        var commandTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(smoke ? 0.7 : 30),
        };
        commandTimer.Tick += async (_, _) =>
        {
            if (!measuring || commandBusy)
                return;
            commandBusy = true;
            try
            {
                await MeasureCommand(bus, "battle.pause", commandSamples);
                await MeasureCommand(bus, "battle.resume", commandSamples);
            }
            finally
            {
                commandBusy = false;
            }
        };

        var monitor = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        monitor.Tick += (_, _) =>
        {
            if (!measuring && totalWatch.Elapsed >= warmup)
            {
                measuring = true;
                measurementWatch.Start();
                startGen2 = GC.CollectionCount(2);
                startGen0 = GC.CollectionCount(0);
                startGen1 = GC.CollectionCount(1);
                startLoh = LohSize();
                startUiAllocated = GC.GetAllocatedBytesForCurrentThread();
                startEconomyAllocated = economy.RenderAllocatedBytes;
                startArenaAllocated = arena.RenderAllocatedBytes;
                startHudAllocated = stage.Hud.RenderAllocatedBytes;
                SampleMemory(process, memorySamples, 0);
                nextMemorySample = smoke ? 0.5 : 30;
                commandTimer.Start();
            }

            if (!measuring)
                return;
            var seconds = measurementWatch.Elapsed.TotalSeconds;
            if (seconds >= nextMemorySample)
            {
                SampleMemory(process, memorySamples, seconds);
                nextMemorySample += smoke ? 0.5 : 30;
            }
            if (measurementWatch.Elapsed < duration)
                return;

            measurementWatch.Stop();
            commandTimer.Stop();
            monitor.Stop();
            SampleMemory(process, memorySamples, measurementWatch.Elapsed.TotalSeconds);
            frame.Continue = false;
        };

        window.Show();
        monitor.Start();
        Dispatcher.PushFrame(frame);
        window.Close();
        stage.Coordinator.Dispose();

        intervals.Sort();
        commandSamples.Sort();
        var measuredSeconds = Math.Max(1e-9, measurementWatch.Elapsed.TotalSeconds);
        var presentationSeconds = presentedFrames > 1
            ? Stopwatch.GetElapsedTime(firstPresentation, lastPresentation).TotalSeconds
            : measuredSeconds;
        var presentationFps = presentedFrames > 1
            ? (presentedFrames - 1) / Math.Max(1e-9, presentationSeconds)
            : presentedFrames / measuredSeconds;
        return new VisibleRuntimeMetric(
            warmup.TotalSeconds,
            measuredSeconds,
            presentationFps,
            Percentile(intervals, 0.95),
            intervals.Count == 0 ? 0 : intervals[^1],
            intervals.Count(value => value > 200),
            Percentile(commandSamples, 0.95),
            commandSamples.Count,
            memorySamples.Count == 0 ? 0 : memorySamples.Max(item => item.Bytes),
            WorkingSetSlope(memorySamples),
            LohSize() - startLoh,
            GC.GetAllocatedBytesForCurrentThread() - startUiAllocated,
            economy.RenderAllocatedBytes - startEconomyAllocated,
            arena.RenderAllocatedBytes - startArenaAllocated,
            stage.Hud.RenderAllocatedBytes - startHudAllocated,
            GC.CollectionCount(0) - startGen0,
            GC.CollectionCount(1) - startGen1,
            GC.CollectionCount(2) - startGen2,
            firstSequence,
            lastSequence,
            presentedFrames);
    }

    private static async Task MeasureCommand(CommandBus bus, string command, List<double> samples)
    {
        var started = Stopwatch.GetTimestamp();
        var result = await bus.ExecuteAsync(command, "runtime-performance");
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (!result.Success)
            throw new InvalidOperationException($"Performance command failed: {command}: {result.Message}");
        samples.Add(elapsed);
    }

    private static void SampleMemory(Process process, List<(double Seconds, long Bytes)> samples, double seconds)
    {
        process.Refresh();
        samples.Add((seconds, process.WorkingSet64));
    }

    private static long LohSize()
    {
        var generations = GC.GetGCMemoryInfo().GenerationInfo;
        return generations.Length > 3 ? generations[3].SizeAfterBytes : 0;
    }

    private static double WorkingSetSlope(List<(double Seconds, long Bytes)> samples)
    {
        if (samples.Count < 2)
            return 0;
        var meanX = samples.Average(item => item.Seconds);
        var meanY = samples.Average(item => (double)item.Bytes);
        var numerator = 0d;
        var denominator = 0d;
        foreach (var sample in samples)
        {
            var dx = sample.Seconds - meanX;
            numerator += dx * (sample.Bytes - meanY);
            denominator += dx * dx;
        }
        if (denominator <= 1e-12)
            return 0;
        var bytesPerSecond = numerator / denominator;
        return bytesPerSecond * 60 / 1024 / 1024;
    }

    private static double Percentile(List<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
            return 0;
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}
