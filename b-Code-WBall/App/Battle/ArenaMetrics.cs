using System.Globalization;
using System.Text;

namespace WBall.Battle;

/// <summary>
/// v3.1:对战区映射公式的唯一真相。运行时(BattleRuntime)、命令(arena.config)与设置窗
/// 共读同一组静态函数,杜绝"三处各算一套"。
/// </summary>
public static class ArenaFormulas
{
    public static double CellSize(ArenaLayoutConfig arena) => Math.Clamp(arena.CellSize, 5, 100);

    public static double ShellSize(ArenaLayoutConfig arena, double cellSize, double value)
    {
        var raw = cellSize * arena.ShellSizeCellFactor
                  * Math.Pow(Math.Max(1, value), arena.ShellSizeValueExponent);
        return Math.Clamp(raw, cellSize * arena.ShellSizeMinCells, cellSize * arena.ShellSizeMaxCells);
    }

    public static double ShellWeight(ArenaLayoutConfig arena, double value) =>
        Math.Max(1, value * arena.ShellWeightScale);

    /// <summary>jitter01 ∈ [0,1):0 = 最慢边界,1 = 最快边界。</summary>
    public static double ShellSpeed(ArenaLayoutConfig arena, double weaponSpeed, double value, double jitter01)
    {
        var jitter = (1 - arena.ShellSpeedJitter) + jitter01 * (2 * arena.ShellSpeedJitter);
        var speed = Math.Clamp(
            Math.Clamp(weaponSpeed, 80, 1200) * jitter
                / Math.Pow(Math.Max(1, value), arena.ShellSpeedValueExponent),
            arena.ShellSpeedMin,
            arena.ShellSpeedMax);
        return speed * arena.ProjectileSpeedScale;
    }

    public static double SmallBallSize(ArenaLayoutConfig arena, double cellSize) =>
        cellSize * arena.SmallBallSizeCellFactor;

    public static double SmallBallSpeed(ArenaLayoutConfig arena) =>
        arena.SmallBallSpeed * arena.ProjectileSpeedScale;
}

/// <summary>v3.1 AW-05 / AK-04:对战区派生值 — 改一个数,这里立刻能看到它到底改了什么。</summary>
public sealed class ArenaMetrics
{
    public double CellSize { get; private init; }
    public int Cols { get; private init; }
    public int Rows { get; private init; }
    public int TotalCells { get; private init; }

    /// <summary>各炮台开局占有格数(领地模式下 = 初始血量)。</summary>
    public IReadOnlyList<(string Id, string Name, int Cells)> FactionCells { get; private init; } = [];

    public double InitialShieldMin { get; private init; }
    public double InitialShieldMax { get; private init; }
    public double ShieldCostPerValue { get; private init; }

    /// <summary>初始护盾能挡多少发小球(等于能抵消多少点大球积分)。</summary>
    public double ShieldSmallBallCapacity { get; private init; }

    public string ShellWeaponName { get; private init; } = "";
    public double ShellWeaponBaseSpeed { get; private init; }
    public long InitialShellValue { get; private init; }
    public int InitialShellCount { get; private init; }
    public double ShellSize { get; private init; }
    public double ShellSpeedMin { get; private init; }
    public double ShellSpeedMax { get; private init; }
    public double ShellWeight { get; private init; }
    public double MomentumMin => ShellWeight * ShellSpeedMin;
    public double MomentumMax => ShellWeight * ShellSpeedMax;

    public double SmallBallSize { get; private init; }
    public double SmallBallSpeed { get; private init; }

    public double Width { get; private init; }
    public double Height { get; private init; }
    public double Aspect => Height <= 0 ? 0 : Width / Height;
    public bool TerritoryMode { get; private init; }

    public static ArenaMetrics Compute(
        ArenaLayoutConfig arena,
        IReadOnlyList<TurretDefinition> turrets,
        WeaponCatalog? weapons)
    {
        var cell = ArenaFormulas.CellSize(arena);
        var cols = Math.Max(1, (int)Math.Ceiling(arena.Width / cell));
        var rows = Math.Max(1, (int)Math.Ceiling(arena.Height / cell));

        // 与 BattleRuntime.InitTerritory 同一象限判据:x<cx=左,y<cy=上
        var cx = arena.Width / 2;
        var cy = arena.Height / 2;
        var colsLeft = 0;
        for (var col = 0; col < cols; col++)
        {
            if ((col + 0.5) * cell < cx)
                colsLeft++;
        }
        var rowsTop = 0;
        for (var row = 0; row < rows; row++)
        {
            if ((row + 0.5) * cell < cy)
                rowsTop++;
        }
        var colsRight = cols - colsLeft;
        var rowsBottom = rows - rowsTop;

        var perQuadrant = new Dictionary<int, int>
        {
            [1] = colsRight * rowsTop,
            [2] = colsLeft * rowsTop,
            [3] = colsLeft * rowsBottom,
            [4] = colsRight * rowsBottom,
        };
        var claimed = new HashSet<int>();
        var factionCells = new List<(string, string, int)>();
        foreach (var turret in turrets)
        {
            var quadrant = Math.Clamp(turret.Quadrant, 1, 4);
            // 同象限多座时,只有第一座拿到该象限(与 InitTerritory 的 first-match 一致)
            var cells = claimed.Add(quadrant) ? perQuadrant[quadrant] : 0;
            factionCells.Add((turret.Id, turret.Name, cells));
        }

        var weaponName = string.IsNullOrWhiteSpace(arena.InitialShellWeapon)
            ? "直射"
            : arena.InitialShellWeapon.Trim();
        var baseSpeed = 360d; // WeaponDefinition.Speed 默认
        if (weapons != null && weapons.TryResolve(weaponName, out var weapon))
        {
            baseSpeed = weapon.Speed;
            weaponName = weapon.Name;
        }

        var value = Math.Max(1, arena.InitialShellValue);
        return new ArenaMetrics
        {
            CellSize = cell,
            Cols = cols,
            Rows = rows,
            TotalCells = cols * rows,
            FactionCells = factionCells,
            InitialShieldMin = turrets.Count == 0 ? 0 : turrets.Min(x => x.InitialShield),
            InitialShieldMax = turrets.Count == 0 ? 0 : turrets.Max(x => x.InitialShield),
            ShieldCostPerValue = arena.ShieldCostPerValue,
            ShieldSmallBallCapacity = arena.ShieldCostPerValue <= 0
                ? 0
                : (turrets.Count == 0 ? 0 : turrets.Min(x => x.InitialShield)) / arena.ShieldCostPerValue,
            ShellWeaponName = weaponName,
            ShellWeaponBaseSpeed = baseSpeed,
            InitialShellValue = value,
            InitialShellCount = arena.InitialShellCount,
            ShellSize = ArenaFormulas.ShellSize(arena, cell, value),
            ShellSpeedMin = ArenaFormulas.ShellSpeed(arena, baseSpeed, value, 0),
            ShellSpeedMax = ArenaFormulas.ShellSpeed(arena, baseSpeed, value, 1),
            ShellWeight = ArenaFormulas.ShellWeight(arena, value),
            SmallBallSize = ArenaFormulas.SmallBallSize(arena, cell),
            SmallBallSpeed = ArenaFormulas.SmallBallSpeed(arena),
            Width = arena.Width,
            Height = arena.Height,
            TerritoryMode = !string.Equals(arena.Mode?.Trim(), "direct", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>派生值多行摘要;logicalWidth/Height 传出片逻辑分辨率以对比长宽比。</summary>
    public string Format(int logicalWidth = 0, int logicalHeight = 0)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"网格 {Cols}×{Rows} = {TotalCells} 格(格边长 {N(CellSize)})");
        sb.AppendLine(TerritoryMode
            ? "  开局占格(=领地模式初始血量): " + string.Join(
                " ｜ ", FactionCells.Select(x => $"{x.Name} {x.Cells}"))
            : "  direct 模式:初始血量取各炮台 maxHp,格数仅供渲染");
        sb.AppendLine(InitialShieldMin == InitialShieldMax
            ? $"初始护盾 {N(InitialShieldMin)} = 可挡 {N(ShieldSmallBallCapacity)} 发小球"
              + $"(等量抵消 {N(ShieldSmallBallCapacity)} 点大球积分,计价 {N(ShieldCostPerValue)}/点)"
            : $"初始护盾 {N(InitialShieldMin)}~{N(InitialShieldMax)}(不统一)"
              + $",计价 {N(ShieldCostPerValue)}/点");
        sb.AppendLine($"初始大球 ×{InitialShellCount} 发(数值 {InitialShellValue},武器 {ShellWeaponName} 基速 {N(ShellWeaponBaseSpeed)})");
        sb.AppendLine($"  size {N(ShellSize)}px  speed {N(ShellSpeedMin)}~{N(ShellSpeedMax)}  weight {N(ShellWeight)}"
                      + $"  动量 {N(MomentumMin)}~{N(MomentumMax)}");
        sb.AppendLine($"小球 size {N(SmallBallSize)}px  speed {N(SmallBallSpeed)}");
        var aspect = $"战场 {N(Width)}×{N(Height)} = {Aspect:0.###}:1";
        if (logicalWidth > 0 && logicalHeight > 0)
        {
            var stageAspect = (double)logicalWidth / Math.Max(1, logicalHeight);
            var same = Math.Abs(stageAspect - Aspect) < 0.005;
            aspect += $" ｜ 出片 {logicalWidth}×{logicalHeight} = {stageAspect:0.###}:1"
                      + (same ? "(一致)" : "(不一致,战场会信箱式留边)");
        }
        sb.Append(aspect);
        return sb.ToString();
    }

    private static string N(double value) =>
        value.ToString(Math.Abs(value) >= 1000 ? "0.##" : "0.###", CultureInfo.InvariantCulture);
}
