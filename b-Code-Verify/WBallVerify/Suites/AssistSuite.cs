using System.Diagnostics;
using System.Text.Json;
using WBall.Battle;
using WBall.Model;

namespace WBall.Verify.Suites;

/// <summary>
/// v3.4 V34-09:同阵营助力 suite。
/// 既含 <c>--assist-performance</c> 的开销上限断言,也提供 compat suite 复用的助力测试脚手架
/// （造弹、推进、取积分总量）—— 这些辅助函数原先散在 Program.cs 底部。
/// </summary>
internal static class AssistSuite
{
    /// <summary>--assist-performance:10k 球下开启助力的额外开销必须有界。</summary>
    public static int RunPerformance(VerifyRun run)
    {
        // 首次调用只为热身 JIT,不计时
        _ = MeasureSteps(run, enabled: false, ballCount: 1_000, measuredSteps: 1);
        var disabledMs = MeasureSteps(run, enabled: false, ballCount: 10_000, measuredSteps: 3);
        var enabledMs = MeasureSteps(run, enabled: true, ballCount: 10_000, measuredSteps: 3);
        var ratio = enabledMs / Math.Max(0.001, disabledMs);
        run.Check("v3.3 10k assist overhead remains bounded",
            ratio <= 3.0,
            $"disabled={disabledMs:0.###}ms enabled={enabledMs:0.###}ms ratio={ratio:0.###}x");
        return run.Conclude("ASSIST PERFORMANCE");
    }

    /// <summary>助力测试专用世界:关掉球-球碰撞,免得物理把摆好的弹推散。</summary>
    public static Harness NewHarness(VerifyRun run, BalanceConfig balance, int seed = 42)
    {
        var harness = run.NewHarness(balance, new ArenaLayoutConfig { BallCollision = false });
        harness.Battle.Reset(seed);
        return harness;
    }

    public static Ball AddBall(
        Harness harness,
        string id,
        ProjectileRole role,
        int value,
        int ownerIndex = 0)
    {
        var owner = harness.Battle.Turrets[ownerIndex];
        var shell = role == ProjectileRole.Shell;
        var ball = new Ball
        {
            Id = id,
            X = harness.BattleWorld.WorldWidth * 0.25,
            Y = harness.BattleWorld.WorldHeight * 0.25,
            Color = owner.Color,
            Size = shell ? harness.Battle.ShellSizeFor(value) : 5,
            Weight = shell ? harness.Battle.ShellWeightFor(value) : 1,
            Projectile = new ProjectileState
            {
                OwnerFactionId = owner.Id,
                WeaponName = shell ? "大球" : "小球",
                Damage = value,
                CapturesLeft = value,
                Role = role,
                IsPromotedSmall = role == ProjectileRole.SmallShot && value > 1,
            },
        };
        harness.BattleWorld.Balls.Add(ball);
        return ball;
    }

    public static void Advance(Harness harness, double seconds)
    {
        for (var i = 0; i < (int)Math.Ceiling(seconds * 60); i++)
            harness.Battle.Step(1.0 / 60);
    }

    /// <summary>在场弹体的积分总量(守恒断言的口径)。</summary>
    public static int ProjectileValue(Harness harness) => harness.BattleWorld.Balls
        .Where(x => x.Projectile != null)
        .Sum(x => x.Projectile!.CapturesLeft);

    public static void VerifyV351Fixes(VerifyRun run)
    {
        var intermittent = NewHarness(run, new BalanceConfig());
        var receiver = AddBall(intermittent, "v351-receiver", ProjectileRole.Shell, 10);
        var small = AddBall(intermittent, "v351-small", ProjectileRole.SmallShot, 1);
        intermittent.Battle.Step(1.0 / 60);
        intermittent.BattleWorld.Balls.Remove(small);
        Advance(intermittent, 4.1);
        small.X = receiver.X;
        small.Y = receiver.Y;
        intermittent.BattleWorld.Balls.Add(small);
        intermittent.Battle.Step(1.0 / 60);
        run.Check("v3.5.1 intermittent friendly contact absorbs small shot",
            !intermittent.BattleWorld.Balls.Contains(small)
            && receiver.Projectile!.CapturesLeft == 11,
            $"smallAlive={intermittent.BattleWorld.Balls.Contains(small)} receiver={receiver.Projectile!.CapturesLeft}");

        var shield = NewHarness(run, new BalanceConfig());
        var target = shield.Battle.Turrets[1];
        target.Shield = target.MaxShield;
        var capacity = (int)(target.Shield / shield.BattleConfig.Arena.ShieldCostPerValue);
        var shell = AddBall(shield, "v351-shield-shell", ProjectileRole.Shell, capacity + 1);
        var shieldRadius = target.TurretRadius * shield.Battle.ShieldRingScale;
        shell.X = target.TurretX - shieldRadius - shell.Size + 0.5;
        shell.Y = target.TurretY;
        shell.Vx = 30;
        shell.Vy = 0;
        shield.Battle.Step(1.0 / 60);
        run.Check("v3.5.1 shell bounces after breaking shield",
            target.Alive
            && target.Shield == 0
            && shield.BattleWorld.Balls.Contains(shell)
            && shell.Projectile!.CapturesLeft == 1
            && shell.Vx < 0,
            $"alive={target.Alive} shield={target.Shield:0.###} shellAlive={shield.BattleWorld.Balls.Contains(shell)} "
            + $"value={shell.Projectile!.CapturesLeft} vx={shell.Vx:0.###}");

        var serialized = JsonSerializer.Serialize(new BalanceConfig { ShieldBreakthrough = true });
        run.Check("v3.5.1 breakthrough compatibility flag is not persisted",
            !serialized.Contains("ShieldBreakthrough", StringComparison.OrdinalIgnoreCase));
    }

    private static double MeasureSteps(VerifyRun run, bool enabled, int ballCount, int measuredSteps)
    {
        var harness = NewHarness(run, new BalanceConfig
        {
            FriendlyAssistEnabled = enabled,
            MergeSameOwnerSmall = false,
            FriendlyAbsorbSmallRate = 0,
            FriendlyShellTransferRate = 0,
            FriendlyAssistVisualEnabled = false,
        });
        var owner = harness.Battle.Turrets[0];
        for (var i = 0; i < ballCount; i++)
        {
            harness.BattleWorld.Balls.Add(new Ball
            {
                Id = $"perf-{i:D5}",
                X = (i % 100 + 0.5) * harness.BattleWorld.WorldWidth / 100,
                Y = (i / 100 + 0.5) * harness.BattleWorld.WorldHeight / Math.Ceiling(ballCount / 100.0),
                Color = owner.Color,
                Size = 1,
                Weight = 1,
                Projectile = new ProjectileState
                {
                    OwnerFactionId = owner.Id,
                    WeaponName = "小球",
                    CapturesLeft = 1,
                    Role = ProjectileRole.SmallShot,
                },
            });
        }

        harness.Battle.Step(1.0 / 60); // 先走一帧,把首帧建表成本排除在计时之外
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < measuredSteps; i++)
            harness.Battle.Step(1.0 / 60);
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds / measuredSteps;
    }
}
