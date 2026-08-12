using System.Collections.Immutable;
using WBall.Model;

namespace WBall.Recording;

public sealed record RenderBallData(
    string Id,
    double X,
    double Y,
    double Size,
    string Color,
    long Multiplier);

public sealed record RenderProjectileData(
    string Id,
    double X,
    double Y,
    double Size,
    string Color,
    string OwnerFactionId,
    ProjectileRole Role,
    int Value,
    bool IsPromotedSmall);

public sealed record RenderTurretData(
    string Id,
    string Name,
    string Color,
    double X,
    double Y,
    double Radius,
    double BarrelAngleDeg,
    double Hp,
    double MaxHp,
    double Shield,
    double MaxShield,
    bool Alive);

public sealed record RenderAssistData(
    double FromX,
    double FromY,
    double ToX,
    double ToY,
    string Color,
    int Amount,
    double RemainingSeconds);

/// <summary>实时舞台与离线组合器共用的固定帧胜利展示状态。</summary>
public sealed record VictoryAnimationState(
    string WinnerId,
    string WinnerName,
    string WinnerColor,
    int FrameIndex,
    int TotalFrames,
    double Progress);

/// <summary>模拟线程交给 STA 合成线程的只读帧投影；不包含 SceneWorld/WPF 对象。</summary>
public sealed record RenderFrameData(
    long FrameIndex,
    double OutputTime,
    double SimulationTime,
    double StepCredit,
    int BallCount,
    double SimulationScale,
    string DirectorState,
    string? WinnerId,
    ImmutableArray<RenderBallData> EconomyBalls,
    ImmutableArray<RenderProjectileData> Projectiles,
    ImmutableArray<RenderTurretData> Turrets,
    ImmutableArray<RenderAssistData> Assists,
    int TerritoryCols,
    int TerritoryRows,
    int TerritoryVersion,
    ImmutableArray<int>? TerritoryOwners,
    ImmutableArray<string> TerritoryFactionIds,
    VictoryAnimationState? Victory = null);

public sealed record RenderStageLayout(
    Stage.StageOrientation Orientation,
    bool CompositeVisible,
    bool HudVisible,
    string Background,
    double Split);

public sealed record RenderStaticData(
    SceneSnapshot EconomyScene,
    double ArenaWidth,
    double ArenaHeight,
    double ShieldRingScale,
    double ShieldCostPerValue,
    double LabelFontFactor,
    double LabelFontMin,
    double LabelFontMax,
    double LabelOutsideOpacity,
    RenderStageLayout Stage);
