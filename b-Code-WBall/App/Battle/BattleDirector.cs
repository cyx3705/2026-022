using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AppShell.Core.Logging;
using WBall.Game;
using WBall.Model;
using WBall.Sim;
using WBall.Stage;

namespace WBall.Battle;

public enum DirectorState
{
    Idle,
    Countdown,
    Running,
    Settling,
    Ended,
}

/// <summary>左右世界的唯一时钟、随机源与回合状态机。</summary>
public sealed class BattleDirector
{
    public const double FixedStepSeconds = 1.0 / 60.0;

    private readonly SceneWorld _economyWorld;
    private readonly SceneWorld _battleWorld;
    private readonly BattleRuntime _battle;
    private readonly WeaponCatalog _weapons;
    private readonly EconomyBridge _economyBridge;
    private readonly StageState _stage;
    private readonly IShellLog _log;
    private double _countdownRemaining;
    private double _settlingRemaining;

    public BattleDirector(
        SceneWorld economyWorld,
        SceneWorld battleWorld,
        BattleRuntime battle,
        WeaponCatalog weapons,
        EconomyBridge economyBridge,
        StageState stage,
        IShellLog log)
    {
        _economyWorld = economyWorld;
        _battleWorld = battleWorld;
        _battle = battle;
        _weapons = weapons;
        _economyBridge = economyBridge;
        _stage = stage;
        _log = log;
        _economyBridge.Availability = IsUnlocked;
        _economyBridge.ShieldEconomyBlocked = () => _battle.SuddenDeath;
        _battle.SetUnlockPredicate(IsUnlocked);
        _battle.EventRaised += OnBattleEvent;
    }

    public event Action<BattleEvent>? EventRaised;
    public event Action? StateChanged;

    public DirectorState State { get; private set; } = DirectorState.Idle;
    public bool IsPaused { get; private set; }
    public int Seed { get; private set; } = 42;
    public double ElapsedSeconds { get; private set; }
    public long Frame { get; private set; }
    public IReadOnlyList<BattleEvent> Events => _events;
    private readonly List<BattleEvent> _events = [];

    public void Start(int seed, double countdownSeconds = 1)
    {
        Seed = seed;
        ElapsedSeconds = 0;
        Frame = 0;
        IsPaused = false;
        _events.Clear();
        _battle.Reset(seed);

        var sharedRandom = new Random(seed);
        _economyWorld.UseRandom(sharedRandom);
        _battleWorld.UseRandom(sharedRandom);
        _economyWorld.ResetSimulation();
        _economyWorld.UseRandom(sharedRandom);
        SpawnEconomyBalls();
        _economyWorld.IsPlaying = false;

        _battle.AutomaticFire = true;
        _countdownRemaining = Math.Max(0, countdownSeconds);
        State = _countdownRemaining > 0 ? DirectorState.Countdown : DirectorState.Running;
        _stage.SetMode(StageMode.Play);
        Raise("start", $"对战开始 seed={seed}");
        StateChanged?.Invoke();
    }

    public void Pause()
    {
        if (State is DirectorState.Countdown or DirectorState.Running or DirectorState.Settling)
            IsPaused = true;
    }

    public void Resume()
    {
        if (State is DirectorState.Countdown or DirectorState.Running or DirectorState.Settling)
            IsPaused = false;
    }

    public void Reset()
    {
        _economyWorld.ResetSimulation();
        _battle.Reset(Seed);
        _battle.AutomaticFire = false;
        ElapsedSeconds = 0;
        Frame = 0;
        IsPaused = false;
        State = DirectorState.Idle;
        _events.Clear();
        _stage.SetMode(StageMode.Edit);
        StateChanged?.Invoke();
    }

    public void AdvanceFixedStep()
    {
        if (IsPaused || State is DirectorState.Idle or DirectorState.Ended)
            return;

        Frame++;
        if (State == DirectorState.Countdown)
        {
            _countdownRemaining -= FixedStepSeconds;
            if (_countdownRemaining <= 0)
            {
                State = DirectorState.Running;
                Raise("running", "倒计时结束,进入对战");
                StateChanged?.Invoke();
            }
            return;
        }

        if (State == DirectorState.Settling)
        {
            _settlingRemaining -= FixedStepSeconds;
            if (_settlingRemaining <= 0)
            {
                State = DirectorState.Ended;
                _stage.SetMode(StageMode.Edit);
                Raise("ended", _battle.WinnerId == "draw" ? "本局平局" : $"本局胜者 {_battle.WinnerId}");
                StateChanged?.Invoke();
            }
            return;
        }

        ElapsedSeconds += FixedStepSeconds;
        PhysicsEngine.Step(_economyWorld, FixedStepSeconds, message => _log.Warn("economy", message));
        _battle.Step(FixedStepSeconds);
        _economyWorld.NotifyChanged(markDirty: false, visual: true, project: false);

        if (_battle.WinnerId != null)
        {
            State = DirectorState.Settling;
            _settlingRemaining = 2;
            _battle.AutomaticFire = false;
            StateChanged?.Invoke();
        }
    }

    public void AdvanceSteps(int steps)
    {
        for (var index = 0; index < Math.Max(0, steps); index++)
            AdvanceFixedStep();
    }

    public bool IsUnlocked(WeaponDefinition weapon) =>
        weapon.Enabled && ElapsedSeconds + 1e-9 >= weapon.UnlockAtSeconds;

    public string UnlockStatus() => string.Join(
        Environment.NewLine,
        _weapons.Weapons.Select(x =>
            $"{x.Name} at={x.UnlockAtSeconds:0.###} unlocked={IsUnlocked(x).ToString().ToLowerInvariant()}"));

    public string Status() =>
        $"state={State.ToString().ToLowerInvariant()} paused={IsPaused.ToString().ToLowerInvariant()} " +
        $"seed={Seed} frame={Frame} elapsed={ElapsedSeconds:0.###} " +
        $"alive={_battle.Turrets.Count(x => x.Alive)} projectiles={_battle.ProjectileCount} winner={_battle.WinnerId ?? "-"}";

    public string DeterministicHash()
    {
        var builder = new StringBuilder();
        builder.Append(Seed).Append('|').Append(Frame).Append('|').Append(State).Append('|')
            .Append(_battle.TerritoryChecksum()).Append('|');
        foreach (var turret in _battle.Turrets.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            builder.Append(turret.Id).Append(':')
                .Append(Round(turret.Hp)).Append(':').Append(Round(turret.Shield)).Append(':')
                .Append(turret.Points).Append(':').Append(turret.Alive).Append(':')
                .Append(Round(turret.BarrelAngleDeg)).Append(':')
                .Append(turret.SmallAmmo).Append(':').Append(turret.SmallMode).Append(':')
                .Append(Round(turret.BarrelFreezeRemaining)).Append(':')
                .Append(turret.VolleyPending).Append(':').Append(turret.Ammo.Count).Append('|');
        }
        foreach (var ball in _battleWorld.Balls
                     .OrderBy(x => x.Projectile?.OwnerFactionId, StringComparer.Ordinal)
                     .ThenBy(x => x.X)
                     .ThenBy(x => x.Y))
        {
            builder.Append(ball.Projectile?.OwnerFactionId).Append(':').Append(Round(ball.X)).Append(':').Append(Round(ball.Y))
                .Append(':').Append(Round(ball.Vx)).Append(':').Append(Round(ball.Vy)).Append('|');
        }
        foreach (var ball in _economyWorld.Balls
                     .OrderBy(x => x.Color, StringComparer.Ordinal)
                     .ThenBy(x => x.X)
                     .ThenBy(x => x.Y))
        {
            builder.Append(ball.Color).Append(':').Append(ball.Multiplier).Append(':')
                .Append(Round(ball.X)).Append(':').Append(Round(ball.Y)).Append('|');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private void SpawnEconomyBalls()
    {
        var spawners = _economyWorld.OfType(SceneObjectType.Spawner).ToList();
        if (spawners.Count == 0)
        {
            _log.Warn("battle", "经济世界没有生成器;右世界仍会运行,但不会产生新经济结算");
            return;
        }

        foreach (var faction in _battle.Turrets.Where(x => x.Alive && x.InitialBalls > 0))
        {
            for (var index = 0; index < faction.InitialBalls; index++)
            {
                var spawner = spawners[_economyWorld.Rng.Next(spawners.Count)];
                // v2.10 DR-01:开局同样走 360° 抛球
                var throwAngle = _economyWorld.Rng.NextDouble() * Math.PI * 2;
                var throwSpeed = 150 + _economyWorld.Rng.NextDouble() * 170;
                var ball = new Ball
                {
                    Id = _economyWorld.NextBallId(),
                    X = spawner.CenterX,
                    Y = spawner.CenterY,
                    Vx = Math.Cos(throwAngle) * throwSpeed,
                    Vy = Math.Sin(throwAngle) * throwSpeed,
                    Color = faction.Color,
                    Multiplier = PublicDefaults.ClampMultiplier(faction.InitialMultiplier),
                };
                _economyWorld.Defaults.ApplyToBall(ball);
                SceneWorld.ApplyPatch(ball, SceneWorld.ParsePatch(spawner.PatchJson));
                _economyWorld.Balls.Add(ball);
            }
        }
        _economyWorld.NotifyChanged(markDirty: false);
    }

    private void OnBattleEvent(BattleEvent battleEvent)
    {
        var normalized = battleEvent with { Time = ElapsedSeconds };
        _events.Add(normalized);
        EventRaised?.Invoke(normalized);
    }

    private void Raise(string kind, string message, string? factionId = null)
    {
        var battleEvent = new BattleEvent(ElapsedSeconds, kind, message, factionId);
        _events.Add(battleEvent);
        EventRaised?.Invoke(battleEvent);
        _log.Info("director", $"[{ElapsedSeconds:0.000}] {message}");
    }

    private static string Round(double value) =>
        Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);
}
