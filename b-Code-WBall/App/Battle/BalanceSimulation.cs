using System.Diagnostics;
using System.Globalization;
using System.IO;
using AppShell.Core.Logging;
using WBall.Model;
using WBall.Stage;

namespace WBall.Battle;

public sealed record BalanceSimulationRow(
    int Seed,
    double Seconds,
    string Winner,
    bool TimedOut,
    IReadOnlyDictionary<string, int> Remaining);

public sealed class BalanceSimulationResult
{
    public required IReadOnlyList<BalanceSimulationRow> Rows { get; init; }
    public bool Interrupted { get; init; }

    public string Format(string format = "table")
    {
        if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
        {
            var lines = new List<string> { "seed,seconds,winner,timeout,remaining" };
            lines.AddRange(Rows.Select(row =>
                $"{row.Seed},{N(row.Seconds)},{row.Winner},{row.TimedOut.ToString().ToLowerInvariant()},\""
                + string.Join(";", row.Remaining.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}")) + "\""));
            if (Interrupted)
                lines.Add("# interrupted=true");
            return string.Join(Environment.NewLine, lines);
        }

        var output = new List<string>();
        output.Add("seed  seconds  winner  timeout  remaining");
        output.AddRange(Rows.Select(row =>
            $"{row.Seed,-5} {N(row.Seconds),7}  {row.Winner,-7} {row.TimedOut.ToString().ToLowerInvariant(),-7}  "
            + string.Join(" ", row.Remaining.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}"))));
        if (Rows.Count == 0)
        {
            output.Add("(无已完成局)");
            if (Interrupted)
                output.Add("汇总 games=0 interrupted=true");
        }
        else
        {
            var seconds = Rows.Select(x => x.Seconds).OrderBy(x => x).ToArray();
            var median = seconds.Length % 2 == 1
                ? seconds[seconds.Length / 2]
                : (seconds[seconds.Length / 2 - 1] + seconds[seconds.Length / 2]) / 2;
            var distribution = string.Join(", ", Rows.GroupBy(x => x.Winner)
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => $"{x.Key}:{x.Count()}"));
            output.Add($"汇总 games={Rows.Count} avg={N(seconds.Average())} median={N(median)} "
                       + $"min={N(seconds.Min())} max={N(seconds.Max())} winners=[{distribution}] "
                       + $"drawRate={N(Rows.Count(x => x.Winner == "draw") * 100.0 / Rows.Count)}% "
                       + $"timeoutRate={N(Rows.Count(x => x.TimedOut) * 100.0 / Rows.Count)}%"
                       + (Interrupted ? " interrupted=true" : ""));
        }
        return string.Join(Environment.NewLine, output);
    }

    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>v3.2 隔离式无头试跑。只读当前场景/武器，世界与配置均为独立实例。</summary>
public sealed class BalanceSimulator
{
    private readonly SceneWorld _sourceEconomyWorld;
    private readonly BattleConfigStore _battleConfig;
    private readonly WeaponCatalog _weapons;
    private readonly IShellLog _log;

    public BalanceSimulator(
        SceneWorld sourceEconomyWorld,
        BattleConfigStore battleConfig,
        WeaponCatalog weapons,
        IShellLog log)
    {
        _sourceEconomyWorld = sourceEconomyWorld;
        _battleConfig = battleConfig;
        _weapons = weapons;
        _log = log;
    }

    public BalanceSimulationResult Run(
        IReadOnlyList<int> seeds,
        double maxSeconds,
        ArenaLayoutConfig arena,
        BalanceConfig balance,
        TimeSpan timeout,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        var rows = new List<BalanceSimulationRow>();
        var stopwatch = Stopwatch.StartNew();
        var interrupted = false;
        foreach (var seed in seeds)
        {
            if (cancellation.IsCancellationRequested || stopwatch.Elapsed >= timeout)
            {
                interrupted = true;
                break;
            }

            BalanceSimulationRow result;
            try
            {
                result = RunOne(seed, maxSeconds, arena, balance, timeout - stopwatch.Elapsed, cancellation);
            }
            catch (OperationCanceledException)
            {
                interrupted = true;
                break;
            }
            rows.Add(result);
            progress?.Report(
                $"seed={seed} seconds={result.Seconds:0.###} winner={result.Winner} timeout={result.TimedOut.ToString().ToLowerInvariant()}");
        }
        return new BalanceSimulationResult { Rows = rows, Interrupted = interrupted };
    }

    private BalanceSimulationRow RunOne(
        int seed,
        double maxSeconds,
        ArenaLayoutConfig arena,
        BalanceConfig balance,
        TimeSpan timeout,
        CancellationToken cancellation)
    {
        var runWatch = Stopwatch.StartNew();
        var runLog = new SimulationLog(_log);
        var battleConfig = BattleConfigStore.CreateMemory(_battleConfig.Turrets, arena, runLog);
        var balanceStore = BalanceConfigStore.CreateMemory(balance, runLog);
        var economy = new SceneWorld { Defaults = _sourceEconomyWorld.Defaults };
        var scenePath = _sourceEconomyWorld.LastScenePath;
        if (!string.IsNullOrWhiteSpace(scenePath) && File.Exists(scenePath))
            SceneStore.Load(economy, scenePath);
        var bridge = new EconomyBridge(_weapons, runLog, balanceStore, battleConfig);
        economy.Settlements = bridge;
        var battleWorld = new SceneWorld
        {
            Defaults = economy.Defaults,
            GravityG = 0,
            BallCollisionEnabled = arena.BallCollision,
            WallRestitution = balance.WallRestitution,
            BallRestitution = balance.BallRestitution,
        };
        var battle = new BattleRuntime(economy, battleWorld, battleConfig, _weapons, runLog, balanceStore);
        var director = new BattleDirector(
            economy, battleWorld, battle, _weapons, bridge, new StageState(), runLog, balanceStore);
        director.Start(seed, countdownSeconds: 0);

        var maxFrames = Math.Max(1, (int)Math.Ceiling(maxSeconds / BattleDirector.FixedStepSeconds));
        var frame = 0;
        while (battle.WinnerId == null && frame < maxFrames)
        {
            if ((frame & 255) == 0)
            {
                cancellation.ThrowIfCancellationRequested();
                if (runWatch.Elapsed >= timeout)
                    break;
            }
            director.AdvanceFixedStep();
            frame++;
        }

        var timedOut = battle.WinnerId == null;
        var winner = battle.WinnerId ?? "timeout";
        var remaining = battle.Turrets.ToDictionary(
            x => x.Id,
            x => (int)Math.Clamp(Math.Round(x.Hp), 0, int.MaxValue),
            StringComparer.OrdinalIgnoreCase);
        return new BalanceSimulationRow(seed, frame * BattleDirector.FixedStepSeconds, winner, timedOut, remaining);
    }

    private sealed class SimulationLog : IShellLog
    {
        private readonly IShellLog _parent;
        public SimulationLog(IShellLog parent) => _parent = parent;
        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
        public void Log(ShellLogLevel level, string category, string message)
        {
            if (level >= ShellLogLevel.Warn)
                _parent.Log(level, "balance.sim", message);
        }
        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }
}
