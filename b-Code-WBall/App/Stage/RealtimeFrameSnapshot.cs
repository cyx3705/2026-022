using WBall.Battle;
using WBall.Game;
using WBall.Model;
using WBall.Recording;

namespace WBall.Stage;

public readonly record struct RealtimeBallFrame(
    string Id,
    double X,
    double Y,
    double Vx,
    double Vy,
    double Size,
    string Color,
    long Multiplier,
    int CapturesLeft,
    int TrailStart,
    int TrailCount,
    double TeleportFlashT,
    double TeleportFromX,
    double TeleportFromY,
    double TeleportToX,
    double TeleportToY);

public readonly record struct RealtimeTrailPoint(double X, double Y);

public readonly record struct RealtimeTurretFrame(
    string Id,
    string Name,
    string Color,
    double X,
    double Y,
    double Radius,
    double BarrelAngleDeg,
    double Hp,
    double Shield,
    bool Alive,
    long Points,
    long SmallAmmo,
    long AmmoTotal,
    double ShieldValue);

public readonly record struct RealtimeFactionFrame(
    string Id,
    string Name,
    string Color,
    bool Alive,
    long Points,
    double Hp,
    long AmmoTotal);

public readonly record struct RealtimeAssistFrame(
    double FromX,
    double FromY,
    double ToX,
    double ToY,
    string Color,
    int Amount,
    double RemainingSeconds);

public readonly record struct RealtimeHitFrame(
    double Time,
    double X,
    double Y,
    double Damage);

/// <summary>UI 线程在固定步读闸口内填充、随后只读消费的双缓冲帧。</summary>
public sealed class RealtimeFrameSnapshot
{
    private RealtimeBallFrame[] _economyBalls = [];
    private RealtimeBallFrame[] _battleBalls = [];
    private RealtimeTrailPoint[] _economyTrails = [];
    private RealtimeTrailPoint[] _battleTrails = [];
    private RealtimeTurretFrame[] _turrets = [];
    private RealtimeFactionFrame[] _factions = [];
    private RealtimeAssistFrame[] _assists = [];
    private RealtimeHitFrame[] _hits = [];
    private int[] _territoryOwners = [];
    private string[] _territoryFactionIds = [];

    public long Sequence { get; private set; }
    public double EconomyWidth { get; private set; }
    public double EconomyHeight { get; private set; }
    public double BattleWidth { get; private set; }
    public double BattleHeight { get; private set; }
    public double ElapsedSeconds { get; private set; }
    public double DirectorElapsedSeconds { get; private set; }
    public DirectorState DirectorState { get; private set; }
    public string? WinnerId { get; private set; }
    public string? WinnerName { get; private set; }
    public string? WinnerColor { get; private set; }
    public VictoryAnimationState? Victory { get; private set; }
    public StageMode StageMode { get; private set; }
    public bool EconomyPlaying { get; private set; }
    public bool EconomyTrailEnabled { get; private set; }
    public bool EconomyBallCollisionEnabled { get; private set; }
    public double EconomyTeleportFlashSeconds { get; private set; }
    public int EconomyBallCount { get; private set; }
    public int BattleBallCount { get; private set; }
    public int EconomyTrailCount { get; private set; }
    public int BattleTrailCount { get; private set; }
    public int TurretCount { get; private set; }
    public int FactionCount { get; private set; }
    public int AssistCount { get; private set; }
    public int HitCount { get; private set; }
    public int TerritoryCols { get; private set; }
    public int TerritoryRows { get; private set; }
    public int TerritoryVersion { get; private set; }
    public int TerritoryOwnerCount { get; private set; }
    public int TerritoryFactionCount { get; private set; }
    public bool TerritoryMode { get; private set; }
    public double TerritoryCellSize { get; private set; }
    public double ShieldRingScale { get; private set; }
    public double ShellLabelFactor { get; private set; }
    public double ShellLabelMin { get; private set; }
    public double ShellLabelMax { get; private set; }
    public double ShellLabelOutsideOpacity { get; private set; }
    public RealtimeBallFrame[] EconomyBalls => _economyBalls;
    public RealtimeBallFrame[] BattleBalls => _battleBalls;
    public RealtimeTrailPoint[] EconomyTrails => _economyTrails;
    public RealtimeTrailPoint[] BattleTrails => _battleTrails;
    public RealtimeTurretFrame[] Turrets => _turrets;
    public RealtimeFactionFrame[] Factions => _factions;
    public RealtimeAssistFrame[] Assists => _assists;
    public RealtimeHitFrame[] Hits => _hits;
    public int[] TerritoryOwners => _territoryOwners;
    public string[] TerritoryFactionIds => _territoryFactionIds;

    public void Capture(
        long sequence,
        StageState stage,
        SceneWorld economyWorld,
        SceneWorld battleWorld,
        BattleRuntime battle,
        BattleDirector director)
    {
        Sequence = sequence;
        EconomyWidth = economyWorld.WorldWidth;
        EconomyHeight = economyWorld.WorldHeight;
        BattleWidth = battleWorld.WorldWidth;
        BattleHeight = battleWorld.WorldHeight;
        ElapsedSeconds = battle.ElapsedSeconds;
        DirectorElapsedSeconds = director.ElapsedSeconds;
        DirectorState = director.State;
        WinnerId = battle.WinnerId is null or "draw" ? null : battle.WinnerId;
        WinnerName = null;
        WinnerColor = null;
        StageMode = stage.Mode;
        EconomyPlaying = economyWorld.IsPlaying;
        EconomyTrailEnabled = economyWorld.TrailEnabled;
        EconomyBallCollisionEnabled = economyWorld.BallCollisionEnabled;
        EconomyTeleportFlashSeconds = economyWorld.TeleportFlashSeconds;

        var includeTrails = economyWorld.Balls.Count + battleWorld.Balls.Count
            < VisualLodController.MinimalThreshold;
        EconomyBallCount = CopyBalls(
            economyWorld.Balls, ref _economyBalls, ref _economyTrails,
            includeTrails, out var economyTrailCount);
        EconomyTrailCount = economyTrailCount;
        BattleBallCount = CopyBalls(
            battleWorld.Balls, ref _battleBalls, ref _battleTrails,
            includeTrails, out var battleTrailCount);
        BattleTrailCount = battleTrailCount;

        var turrets = battle.Turrets;
        Ensure(ref _turrets, turrets.Count);
        TurretCount = turrets.Count;
        for (var i = 0; i < turrets.Count; i++)
        {
            var turret = turrets[i];
            _turrets[i] = new RealtimeTurretFrame(
                turret.Id, turret.Name, turret.Color,
                turret.TurretX, turret.TurretY, turret.TurretRadius,
                turret.BarrelAngleDeg, turret.Hp, turret.Shield, turret.Alive,
                turret.Points, turret.SmallAmmo, battle.AmmoTotalOf(turret),
                battle.ShieldValueOf(turret));
            if (WinnerId != null && turret.Id.Equals(WinnerId, StringComparison.OrdinalIgnoreCase))
            {
                WinnerName = turret.Name;
                WinnerColor = turret.Color;
            }
        }

        var factions = economyWorld.Factions;
        Ensure(ref _factions, factions.Count);
        FactionCount = factions.Count;
        for (var i = 0; i < factions.Count; i++)
        {
            var faction = factions[i];
            _factions[i] = new RealtimeFactionFrame(
                faction.Id, faction.Name, faction.Color, faction.Alive,
                faction.Points, faction.Hp,
                faction.SmallAmmo + faction.QueuedAmmoValue);
        }

        var assists = battle.AssistVisuals;
        Ensure(ref _assists, assists.Count);
        AssistCount = assists.Count;
        for (var i = 0; i < assists.Count; i++)
        {
            var assist = assists[i];
            _assists[i] = new RealtimeAssistFrame(
                assist.FromX, assist.FromY, assist.ToX, assist.ToY,
                assist.Color, assist.Amount, assist.RemainingSeconds);
        }

        var hits = battle.HitMarkers;
        Ensure(ref _hits, hits.Count);
        HitCount = hits.Count;
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            _hits[i] = new RealtimeHitFrame(hit.Time, hit.X, hit.Y, hit.Damage);
        }

        TerritoryCols = battle.TerritoryCols;
        TerritoryRows = battle.TerritoryRows;
        TerritoryVersion = battle.TerritoryVersion;
        TerritoryMode = battle.TerritoryMode;
        TerritoryCellSize = battle.TerritoryCellSize;
        ShieldRingScale = battle.ShieldRingScale;
        var shellLabel = battle.ShellLabelStyle;
        ShellLabelFactor = shellLabel.Factor;
        ShellLabelMin = shellLabel.Min;
        ShellLabelMax = shellLabel.Max;
        ShellLabelOutsideOpacity = shellLabel.OutsideOpacity;
        var owners = battle.TerritoryOwners;
        Ensure(ref _territoryOwners, owners.Length);
        TerritoryOwnerCount = owners.Length;
        owners.CopyTo(_territoryOwners, 0);
        var ids = battle.TerritoryFactionIds;
        Ensure(ref _territoryFactionIds, ids.Count);
        TerritoryFactionCount = ids.Count;
        for (var i = 0; i < ids.Count; i++)
            _territoryFactionIds[i] = ids[i];
    }

    public void SetVictory(VictoryAnimationState? victory) => Victory = victory;

    private static int CopyBalls(
        List<Ball> balls,
        ref RealtimeBallFrame[] target,
        ref RealtimeTrailPoint[] trails,
        bool includeTrails,
        out int trailCount)
    {
        Ensure(ref target, balls.Count);
        var requiredTrails = 0;
        if (includeTrails)
        {
            for (var i = 0; i < balls.Count; i++)
                requiredTrails = checked(requiredTrails + balls[i].Trail.Count);
        }
        Ensure(ref trails, requiredTrails);
        trailCount = 0;
        for (var i = 0; i < balls.Count; i++)
        {
            var ball = balls[i];
            var trailStart = trailCount;
            if (includeTrails)
            {
                foreach (var point in ball.Trail)
                    trails[trailCount++] = new RealtimeTrailPoint(point.X, point.Y);
            }
            target[i] = new RealtimeBallFrame(
                ball.Id, ball.X, ball.Y, ball.Vx, ball.Vy, ball.Size, ball.Color,
                ball.Multiplier, ball.Projectile?.CapturesLeft ?? 0,
                trailStart, includeTrails ? ball.Trail.Count : 0,
                ball.TeleportFlashT,
                ball.TeleportFromX, ball.TeleportFromY,
                ball.TeleportToX, ball.TeleportToY);
        }
        return balls.Count;
    }

    private static void Ensure<T>(ref T[] array, int count)
    {
        if (array.Length >= count)
            return;
        var capacity = Math.Max(count, Math.Max(16, array.Length * 2));
        Array.Resize(ref array, capacity);
    }
}
