using System.IO;
using AppShell.Core.Logging;
using WBall.Battle;
using WBall.Game;
using WBall.Model;
using WBall.Stage;

namespace WBall.Verify;

/// <summary>
/// v3.4 V34-09:一次验证运行的共享上下文。
/// 此前 Program.cs 是 1086 行顶层语句 —— 每个 suite 都靠闭包抓 dataRoot / log / Check / failures,
/// 想单独看一个 suite 必须在千行文件里翻。现在上下文显式传递,suite 可以逐个搬进 Suites/。
/// </summary>
internal sealed class VerifyRun
{
    private readonly List<string> _failures = [];
    private int _checkCount;

    public VerifyRun(string dataRoot, VerifyArtifacts artifacts)
    {
        Root = dataRoot;
        Artifacts = artifacts;
    }

    /// <summary>产物根(= VerifyArtifacts.Root),各 suite 的落盘位由 Artifacts.Suite(name) 取。</summary>
    public string Root { get; }

    public VerifyArtifacts Artifacts { get; }

    public IShellLog Log { get; } = new NullLog();

    public IReadOnlyList<string> Failures => _failures;

    public bool Passed => _failures.Count == 0;

    public int CheckCount => _checkCount;

    public int PassedCount => _checkCount - _failures.Count;

    /// <summary>断言一项并即时打印(输出格式与 v3.3 逐字一致,回归对比靠它)。</summary>
    public void Check(string name, bool passed, string? detail = null)
    {
        _checkCount++;
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}" + (detail == null ? "" : $": {detail}"));
        if (!passed)
            _failures.Add(name + (detail == null ? "" : $": {detail}"));
    }

    /// <summary>suite 结尾统一收口:打印结论并给出进程退出码。</summary>
    public int Conclude(string label)
    {
        Console.WriteLine(Passed ? $"{label} PASS" : $"{label} FAIL ({_failures.Count})");
        return Passed ? 0 : 1;
    }

    public Harness NewHarness(BalanceConfig balance, ArenaLayoutConfig? arena = null) =>
        new(Root, Log, balance, arena);
}

internal sealed class Harness
{
    public Harness(
        string dataRoot,
        IShellLog log,
        BalanceConfig balance,
        ArenaLayoutConfig? arena = null)
    {
        BattleConfig = BattleConfigStore.CreateMemory(
            Demo4Turrets(), arena ?? new ArenaLayoutConfig(), log);
        BalanceStore = BalanceConfigStore.CreateMemory(balance, log);
        Weapons = new WeaponCatalog(dataRoot, log);
        EconomyWorld = new SceneWorld();
        var scenes = Path.Combine(dataRoot, "scenes");
        SceneStore.Load(EconomyWorld, PlinkoDemoSeeder.EnsureScene(scenes, log));
        Bridge = new EconomyBridge(Weapons, log, BalanceStore);
        EconomyWorld.Settlements = Bridge;
        BattleWorld = new SceneWorld { Defaults = EconomyWorld.Defaults, GravityG = 0 };
        Battle = new BattleRuntime(EconomyWorld, BattleWorld, BattleConfig, Weapons, log, BalanceStore);
        Director = new BattleDirector(
            EconomyWorld, BattleWorld, Battle, Weapons, Bridge, new StageState(), log, BalanceStore);
        InitialTerritoryChecksum = Battle.TerritoryChecksum();
    }

    public SceneWorld EconomyWorld { get; }
    public SceneWorld BattleWorld { get; }
    public BattleConfigStore BattleConfig { get; }
    public BalanceConfigStore BalanceStore { get; }
    public WeaponCatalog Weapons { get; }
    public EconomyBridge Bridge { get; }
    public BattleRuntime Battle { get; }
    public BattleDirector Director { get; }
    public int InitialTerritoryChecksum { get; }

    private static IReadOnlyList<TurretDefinition> Demo4Turrets() =>
    [
        new() { Id = "green", Name = "Caleb", Color = "#22C55E", Quadrant = 2, InitialBalls = 4, InitialShield = 2_000_000 },
        new() { Id = "cyan", Name = "Xiaolin", Color = "#06B6D4", Quadrant = 1, InitialBalls = 4, InitialShield = 2_000_000 },
        new() { Id = "orange", Name = "Diu", Color = "#F97316", Quadrant = 3, InitialBalls = 4, InitialShield = 2_000_000 },
        new() { Id = "magenta", Name = "Wemmbu", Color = "#EC4899", Quadrant = 4, InitialBalls = 4, InitialShield = 2_000_000 },
    ];
}

internal sealed class NullLog : IShellLog
{
    public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
    public void Log(ShellLogLevel level, string category, string message) { }
    public IReadOnlyList<ShellLogEntry> Snapshot() => [];
}

internal sealed class CallbackProgress(Action<string> callback) : IProgress<string>
{
    public void Report(string value) => callback(value);
}
