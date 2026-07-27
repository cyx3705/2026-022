namespace WBall.Recording;

/// <summary>固定 60 Hz 模拟的统一时间信用；输入只含确定性球数与配置。</summary>
public sealed class TimelineClock
{
    public const int SimulationHz = 60;

    private readonly RenderTimeConfig _config;
    private double _stepCredit;
    private bool _scaleInitialized;

    public TimelineClock(RenderTimeConfig config, bool autoSlow)
    {
        _config = RenderTimeConfigStore.Clone(config);
        AutoSlow = autoSlow;
        CurrentScale = _config.ManualSimulationScale;
    }

    public bool AutoSlow { get; set; }
    public long OutputFrames { get; private set; }
    public long SimulationSteps { get; private set; }
    public double CurrentScale { get; private set; }
    public double OutputTime(int fps) => OutputFrames / (double)Math.Max(1, fps);
    public double SimulationTime => SimulationSteps / (double)SimulationHz;
    public double StepCredit => _stepCredit;

    public int AdvanceOutputFrame(int fps, int ballCount)
    {
        fps = Math.Clamp(fps, 1, 120);
        CurrentScale = ResolveScale(ballCount);
        _stepCredit += SimulationHz * CurrentScale / fps;
        var steps = ConsumeSteps();
        OutputFrames++;
        return steps;
    }

    public int AdvanceWallTime(double seconds, int ballCount)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
            return 0;
        CurrentScale = ResolveScale(ballCount);
        _stepCredit += SimulationHz * CurrentScale * Math.Min(seconds, 0.1);
        return ConsumeSteps();
    }

    public void Reset()
    {
        _stepCredit = 0;
        OutputFrames = 0;
        SimulationSteps = 0;
        CurrentScale = _config.ManualSimulationScale;
        _scaleInitialized = false;
    }

    public double ResolveScale(int ballCount)
    {
        if (!AutoSlow)
            return _config.ManualSimulationScale;

        ballCount = Math.Max(0, ballCount);
        var candidate = QuantizedScale(ballCount);
        if (!_scaleInitialized)
        {
            _scaleInitialized = true;
            return candidate;
        }

        if (candidate < CurrentScale)
            return candidate;
        if (candidate > CurrentScale
            && QuantizedScale(ballCount + _config.HysteresisBalls) > CurrentScale)
            return candidate;
        return CurrentScale;
    }

    private double QuantizedScale(int ballCount)
    {
        double scale;
        if (ballCount <= _config.SlowStartBalls)
            scale = 1;
        else if (ballCount >= _config.SlowFullBalls)
            scale = _config.MinSimulationScale;
        else
        {
            var t = (ballCount - _config.SlowStartBalls)
                    / (double)(_config.SlowFullBalls - _config.SlowStartBalls);
            scale = 1 - t * (1 - _config.MinSimulationScale);
        }
        var quantum = _config.ScaleQuantization;
        scale = Math.Round(scale / quantum, MidpointRounding.AwayFromZero) * quantum;
        return Math.Clamp(scale, _config.MinSimulationScale, 1);
    }

    private int ConsumeSteps()
    {
        var steps = (int)Math.Floor(_stepCredit + 1e-12);
        _stepCredit -= steps;
        SimulationSteps += steps;
        return steps;
    }
}
