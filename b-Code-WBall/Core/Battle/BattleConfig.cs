namespace WBall.Battle;

public interface IWeaponSpeedCatalog
{
    bool TryGetSpeed(string name, out string resolvedName, out double speed);
}

public sealed class TurretDefinition
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Color { get; set; }
    public int Quadrant { get; set; }
    public int InitialBalls { get; set; } = 3;
    public long InitialMultiplier { get; set; } = 1;
    public double MaxHp { get; set; } = 20_000_000;
    public double MaxShield { get; set; } = 5_000_000;
    public double InitialShield { get; set; } = 500_000;
    public double ProjectileSize { get; set; } = 8;
    public int ProjectileCount { get; set; } = 1;
    public double FireIntervalSec { get; set; } = 1.2;
    public double BarrelRpm { get; set; } = 6;
}

public sealed class ArenaLayoutConfig
{
    public string Name { get; set; } = "quad4";
    public double Width { get; set; } = 960;
    public double Height { get; set; } = 900;
    public double GravityG { get; set; }
    public bool BallCollision { get; set; } = true;
    public string Targeting { get; set; } = "spin";
    public double TurretRadius { get; set; } = 26;
    public int MaxProjectiles { get; set; } = 2000;
    public double ProjectileLifetimeSec { get; set; } = 12;
    public string Mode { get; set; } = "territory";
    public double CellSize { get; set; } = 10;
    public double SuddenDeathAtSeconds { get; set; } = 240;
    public double TurretMarginXRatio { get; set; } = 0.12;
    public double TurretMarginYRatio { get; set; } = 0.14;
    public double ShieldRingScale { get; set; } = 1.55;
    public double ShieldCostPerValue { get; set; } = 50_000;
    public double ProjectileSpeedScale { get; set; } = 1;
    public double ShellSizeCellFactor { get; set; } = 0.5;
    public double ShellSizeValueExponent { get; set; } = 0.25;
    public double ShellSizeMinCells { get; set; } = 0.5;
    public double ShellSizeMaxCells { get; set; } = 5;
    public double ShellSpeedJitter { get; set; } = 0.25;
    public double ShellSpeedValueExponent { get; set; } = 0.12;
    public double ShellSpeedMin { get; set; } = 60;
    public double ShellSpeedMax { get; set; } = 700;
    public double ShellWeightScale { get; set; } = 1;
    public int InitialShellCount { get; set; } = 12;
    public long InitialShellValue { get; set; } = 1;
    public string InitialShellWeapon { get; set; } = "直射";
    public double SmallBallSpeed { get; set; } = 380;
    public double SmallBallSizeCellFactor { get; set; } = 0.5;
    public double ShellLabelFontFactor { get; set; } = 0.8;
    public double ShellLabelFontMin { get; set; } = 8;
    public double ShellLabelFontMax { get; set; } = 22;
    public double ShellLabelOutsideOpacity { get; set; } = 0.28;
}
