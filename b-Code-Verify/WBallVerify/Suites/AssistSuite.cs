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
    public static Harness NewHarness(
        VerifyRun run,
        BalanceConfig balance,
        int seed = 42,
        bool protectFriendlyValue = true)
    {
        var harness = run.NewHarness(balance, new ArenaLayoutConfig { BallCollision = false });
        harness.Battle.Reset(seed);
        if (protectFriendlyValue)
            Array.Fill(harness.Battle.TerritoryOwners, 0);
        return harness;
    }

    public static Harness NewCollisionHarness(VerifyRun run, BalanceConfig balance, int seed = 42)
    {
        var harness = run.NewHarness(balance, new ArenaLayoutConfig { BallCollision = true });
        harness.Battle.Reset(seed);
        Array.Fill(harness.Battle.TerritoryOwners, 0);
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
    public static long ProjectileValue(Harness harness) => harness.BattleWorld.Balls
        .Where(x => x.Projectile != null)
        .Sum(x => (long)x.Projectile!.CapturesLeft + x.Projectile.FriendlyPendingSmallValue);

    public static int RunFriendlyAbsorbSmoke(VerifyRun run)
    {
        const int frames = 120;
        const int smallsPerFrame = 8;
        var balance = new BalanceConfig { FriendlyAbsorbSmallRate = 2 };
        var pressure = NewHarness(run, balance);
        var receiver = AddBall(pressure, "absorb-gate-receiver", ProjectileRole.Shell, 100);
        receiver.X = pressure.BattleWorld.WorldWidth * 0.5;
        receiver.Y = pressure.BattleWorld.WorldHeight * 0.5;
        receiver.Vx = 0;
        receiver.Vy = 0;
        var initialTotal = ProjectileValue(pressure);
        var peakLiveSmall = 0;
        var injected = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            for (var i = 0; i < smallsPerFrame; i++)
            {
                var small = AddBall(
                    pressure, $"pressure-{frame:D3}-{i:D2}", ProjectileRole.SmallShot, 1);
                PlaceInsideHalo(receiver, small, balance.FriendlyAssistReachFactor, i, smallsPerFrame);
                injected++;
            }
            pressure.Battle.Step(1.0 / 60);
            peakLiveSmall = Math.Max(peakLiveSmall, pressure.BattleWorld.Balls.Count(x =>
                x.Projectile != null && x.Projectile.Role == ProjectileRole.SmallShot));
        }

        var target = receiver.Projectile!;
        run.Check("friendly absorb gate reclaims sustained small-ball pressure",
            peakLiveSmall == 0,
            $"injected={injected} peakLiveSmall={peakLiveSmall}");
        run.Check("friendly absorb gate adds every reclaimed point immediately",
            target.CapturesLeft == 100 + injected,
            $"receiver={target.CapturesLeft} expected={100 + injected}");
        run.Check("friendly absorb gate leaves no delayed value or live small balls",
            target.FriendlyPendingSmallValue == 0
            && pressure.BattleWorld.Balls.Count(x => x.Projectile?.Role == ProjectileRole.SmallShot) == 0,
            $"pending={target.FriendlyPendingSmallValue} liveSmall="
            + pressure.BattleWorld.Balls.Count(x => x.Projectile?.Role == ProjectileRole.SmallShot));
        run.Check("friendly absorb gate conserves reclaimed value",
            ProjectileValue(pressure) == initialTotal + injected,
            $"expected={initialTotal + injected} actual={ProjectileValue(pressure)}");

        var disabled = NewCollisionHarness(run, new BalanceConfig());
        var disabledReceiver = AddBall(disabled, "absorb-disabled-receiver", ProjectileRole.Shell, 100);
        disabledReceiver.X = disabled.BattleWorld.WorldWidth * 0.5;
        disabledReceiver.Y = disabled.BattleWorld.WorldHeight * 0.5;
        for (var i = 0; i < 8; i++)
        {
            var small = AddBall(disabled, $"disabled-{i:D2}", ProjectileRole.SmallShot, 1);
            PlaceInsideHalo(disabledReceiver, small, disabled.BalanceStore.Current.FriendlyAssistReachFactor, i, 8);
        }
        disabled.Battle.Step(1.0 / 60);
        run.Check("friendly absorb gate is disabled while ball collision is enabled",
            disabledReceiver.Projectile!.CapturesLeft == 100
            && disabledReceiver.Projectile.FriendlyPendingSmallValue == 0
            && disabled.BattleWorld.Balls.Count(x => x.Projectile?.Role == ProjectileRole.SmallShot) == 8,
            $"receiver={disabledReceiver.Projectile!.CapturesLeft} pending="
            + $"{disabledReceiver.Projectile.FriendlyPendingSmallValue} liveSmall="
            + disabled.BattleWorld.Balls.Count(x => x.Projectile?.Role == ProjectileRole.SmallShot));

        var collisionShells = NewCollisionHarness(run, new BalanceConfig { FriendlyShellTransferRate = 10 });
        var collisionLarge = AddBall(collisionShells, "collision-large", ProjectileRole.Shell, 10);
        var collisionDonor = AddBall(collisionShells, "collision-donor", ProjectileRole.Shell, 6);
        collisionShells.Battle.Step(1.0 / 60);
        run.Check("friendly shell absorption is disabled while ball collision is enabled",
            collisionLarge.Projectile!.CapturesLeft == 10
            && collisionDonor.Projectile!.CapturesLeft == 6,
            $"receiver={collisionLarge.Projectile!.CapturesLeft} donor={collisionDonor.Projectile!.CapturesLeft}");

        var smallOnly = NewHarness(run, new BalanceConfig());
        var smallOnlyFirst = AddBall(smallOnly, "small-only-a", ProjectileRole.SmallShot, 2);
        var smallOnlySecond = AddBall(smallOnly, "small-only-b", ProjectileRole.SmallShot, 4);
        smallOnly.Battle.Step(1.0 / 60);
        run.Check("small balls never absorb other small balls",
            smallOnly.BattleWorld.Balls.Contains(smallOnlyFirst)
            && smallOnly.BattleWorld.Balls.Contains(smallOnlySecond)
            && smallOnlyFirst.Projectile!.CapturesLeft == 2
            && smallOnlySecond.Projectile!.CapturesLeft == 4,
            $"first={smallOnlyFirst.Projectile!.CapturesLeft} second={smallOnlySecond.Projectile!.CapturesLeft}");

        var shells = NewHarness(run, new BalanceConfig { FriendlyShellTransferRate = 0.5 });
        var large = AddBall(shells, "absorb-gate-large", ProjectileRole.Shell, 10);
        var smallShell = AddBall(shells, "absorb-gate-small-shell", ProjectileRole.Shell, 6);
        large.X = shells.BattleWorld.WorldWidth * 0.5;
        large.Y = shells.BattleWorld.WorldHeight * 0.5;
        PlaceSweptContact(large, smallShell, shells.BalanceStore.Current.FriendlyAssistReachFactor);
        shells.Battle.Step(1.0 / 60);
        run.Check("friendly absorb gate keeps large-ball transfer rate limited",
            large.Projectile!.CapturesLeft == 11 && smallShell.Projectile!.CapturesLeft == 5,
            $"receiver={large.Projectile!.CapturesLeft} donor={smallShell.Projectile!.CapturesLeft}");

        return run.Conclude("FRIENDLY ABSORB SMOKE");
    }

    public static void VerifyV352Fixes(VerifyRun run)
    {
        var movingSmall = NewHarness(run, new BalanceConfig());
        var receiver = AddBall(movingSmall, "v352-small-receiver", ProjectileRole.Shell, 10);
        var small = AddBall(movingSmall, "v352-moving-small", ProjectileRole.SmallShot, 1);
        receiver.X = movingSmall.BattleWorld.WorldWidth * 0.5;
        receiver.Y = movingSmall.BattleWorld.WorldHeight * 0.5;
        PlaceSweptContact(receiver, small, movingSmall.BalanceStore.Current.FriendlyAssistReachFactor);
        var smallTotal = ProjectileValue(movingSmall);
        movingSmall.Battle.Step(1.0 / 60);
        run.Check("v3.5.2 moving friendly small is absorbed with ball collision disabled",
            !movingSmall.BattleWorld.BallCollisionEnabled
            && !movingSmall.BattleWorld.Balls.Contains(small)
            && receiver.Projectile!.CapturesLeft == 11,
            $"collision={movingSmall.BattleWorld.BallCollisionEnabled} smallAlive={movingSmall.BattleWorld.Balls.Contains(small)} "
            + $"receiver={receiver.Projectile!.CapturesLeft}");
        run.Check("v3.5.2 moving small absorption conserves value",
            ProjectileValue(movingSmall) == smallTotal,
            $"before={smallTotal} after={ProjectileValue(movingSmall)}");

        var movingShell = NewHarness(run, new BalanceConfig());
        var largeShell = AddBall(movingShell, "v352-large-shell", ProjectileRole.Shell, 10);
        var donorShell = AddBall(movingShell, "v352-donor-shell", ProjectileRole.Shell, 6);
        largeShell.X = movingShell.BattleWorld.WorldWidth * 0.5;
        largeShell.Y = movingShell.BattleWorld.WorldHeight * 0.5;
        PlaceSweptContact(largeShell, donorShell, movingShell.BalanceStore.Current.FriendlyAssistReachFactor);
        var shellTotal = ProjectileValue(movingShell);
        movingShell.Battle.Step(1.0 / 60);
        run.Check("v3.5.2 larger friendly shell absorbs from moving smaller shell",
            largeShell.Projectile!.CapturesLeft == 11
            && donorShell.Projectile!.CapturesLeft == 5,
            $"receiver={largeShell.Projectile.CapturesLeft} donor={donorShell.Projectile!.CapturesLeft}");
        run.Check("v3.5.2 shell transfer conserves value",
            ProjectileValue(movingShell) == shellTotal,
            $"before={shellTotal} after={ProjectileValue(movingShell)}");

        var reclaimedShell = NewHarness(run, new BalanceConfig());
        var reclaimReceiver = AddBall(reclaimedShell, "v352-reclaim-receiver", ProjectileRole.Shell, 10);
        var onePointShell = AddBall(reclaimedShell, "v352-one-point-shell", ProjectileRole.Shell, 1);
        reclaimReceiver.X = reclaimedShell.BattleWorld.WorldWidth * 0.5;
        reclaimReceiver.Y = reclaimedShell.BattleWorld.WorldHeight * 0.5;
        PlaceSweptContact(reclaimReceiver, onePointShell, reclaimedShell.BalanceStore.Current.FriendlyAssistReachFactor);
        reclaimedShell.Battle.Step(1.0 / 60);
        run.Check("v3.5.2 fully transferred friendly shell is removed",
            !reclaimedShell.BattleWorld.Balls.Contains(onePointShell)
            && reclaimReceiver.Projectile!.CapturesLeft == 11,
            $"donorAlive={reclaimedShell.BattleWorld.Balls.Contains(onePointShell)} "
            + $"receiver={reclaimReceiver.Projectile!.CapturesLeft}");

        var equalReceiverA = RunEqualShellContact(run, seed: 42);
        var equalReceiverB = RunEqualShellContact(run, seed: 42);
        run.Check("v3.5.2 equal friendly shells choose one deterministic receiver",
            equalReceiverA.ReceiverId == equalReceiverB.ReceiverId
            && equalReceiverA.Values.Order().SequenceEqual(new[] { 9, 11 })
            && equalReceiverA.Total == 20
            && equalReceiverB.Total == 20,
            $"first={equalReceiverA.ReceiverId}:{string.Join('/', equalReceiverA.Values)} "
            + $"second={equalReceiverB.ReceiverId}:{string.Join('/', equalReceiverB.Values)}");

        var capped = NewHarness(run, new BalanceConfig { FriendlyShellTransferRate = 10 });
        var cappedReceiver = AddBall(capped, "v352-cap-receiver", ProjectileRole.Shell, 10);
        var cappedDonor = AddBall(capped, "v352-cap-donor", ProjectileRole.Shell, 6);
        cappedReceiver.X = capped.BattleWorld.WorldWidth * 0.25;
        cappedReceiver.Y = capped.BattleWorld.WorldHeight * 0.5;
        cappedDonor.X = capped.BattleWorld.WorldWidth * 0.75;
        cappedDonor.Y = cappedReceiver.Y;
        Advance(capped, 2);
        PlaceSweptContact(cappedReceiver, cappedDonor, capped.BalanceStore.Current.FriendlyAssistReachFactor);
        capped.Battle.Step(1.0 / 60);
        run.Check("v3.5.2 precharged shell budget cannot burst above one point",
            cappedReceiver.Projectile!.CapturesLeft == 11
            && cappedDonor.Projectile!.CapturesLeft == 5,
            $"receiver={cappedReceiver.Projectile!.CapturesLeft} donor={cappedDonor.Projectile!.CapturesLeft}");

        var cooldown = NewHarness(run, new BalanceConfig());
        var cooldownReceiver = AddBall(cooldown, "v352-cooldown-receiver", ProjectileRole.Shell, 10);
        var firstSmall = AddBall(cooldown, "v352-cooldown-small-a", ProjectileRole.SmallShot, 1);
        var secondSmall = AddBall(cooldown, "v352-cooldown-small-b", ProjectileRole.SmallShot, 1);
        cooldown.Battle.Step(1.0 / 60);
        var afterFirstContact = cooldownReceiver.Projectile!.CapturesLeft;
        cooldown.Battle.Step(1.0 / 60);
        run.Check("v3.5.2 every small-ball point is added immediately",
            afterFirstContact == 12
            && cooldownReceiver.Projectile.CapturesLeft == 12
            && cooldownReceiver.Projectile.FriendlyPendingSmallValue == 0
            && cooldown.BattleWorld.Balls.Count(x => ReferenceEquals(x, firstSmall) || ReferenceEquals(x, secondSmall)) == 0,
            $"afterFirst={afterFirstContact} afterSecond={cooldownReceiver.Projectile.CapturesLeft} "
            + $"pending={cooldownReceiver.Projectile.FriendlyPendingSmallValue} "
            + $"smallAlive={cooldown.BattleWorld.Balls.Count(x => ReferenceEquals(x, firstSmall) || ReferenceEquals(x, secondSmall))}");

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
        run.Check("v3.5.2 shell bounces after breaking shield",
            target.Alive
            && target.Shield == 0
            && shield.BattleWorld.Balls.Contains(shell)
            && shell.Projectile!.CapturesLeft == 1
            && shell.Vx < 0,
            $"alive={target.Alive} shield={target.Shield:0.###} shellAlive={shield.BattleWorld.Balls.Contains(shell)} "
            + $"value={shell.Projectile!.CapturesLeft} vx={shell.Vx:0.###}");

        var shieldUnits = NewHarness(run, new BalanceConfig());
        var shieldTarget = shieldUnits.Battle.Turrets[1];
        var shieldCost = shieldUnits.BattleConfig.Arena.ShieldCostPerValue;
        shieldTarget.Shield = shieldCost * 3;
        var promotedSmall = AddBall(shieldUnits, "v352-shield-small", ProjectileRole.SmallShot, 5);
        promotedSmall.X = shieldTarget.TurretX;
        promotedSmall.Y = shieldTarget.TurretY;
        shieldUnits.Battle.Step(1.0 / 60);
        run.Check("v3.5.2 shield and promoted small cancel by equal value",
            shieldTarget.Shield == 0
            && shieldUnits.BattleWorld.Balls.Contains(promotedSmall)
            && promotedSmall.Projectile!.CapturesLeft == 2,
            $"shield={shieldTarget.Shield:0.###} smallAlive={shieldUnits.BattleWorld.Balls.Contains(promotedSmall)} "
            + $"smallValue={promotedSmall.Projectile!.CapturesLeft}");

        var shieldPoint = NewHarness(run, new BalanceConfig());
        var pointTarget = shieldPoint.Battle.Turrets[1];
        pointTarget.Shield = shieldPoint.BattleConfig.Arena.ShieldCostPerValue * 5;
        var pointSmall = AddBall(shieldPoint, "v352-shield-point", ProjectileRole.SmallShot, 1);
        pointSmall.X = pointTarget.TurretX;
        pointSmall.Y = pointTarget.TurretY;
        shieldPoint.Battle.Step(1.0 / 60);
        run.Check("v3.5.2 one-point small removes one displayed shield point",
            !shieldPoint.BattleWorld.Balls.Contains(pointSmall)
            && Math.Abs(shieldPoint.Battle.ShieldValueOf(pointTarget) - 4) < 1e-9,
            $"smallAlive={shieldPoint.BattleWorld.Balls.Contains(pointSmall)} "
            + $"shieldValue={shieldPoint.Battle.ShieldValueOf(pointTarget):0.###}");

        var serialized = JsonSerializer.Serialize(new BalanceConfig { ShieldBreakthrough = true });
        run.Check("v3.5.2 breakthrough compatibility flag is not persisted",
            !serialized.Contains("ShieldBreakthrough", StringComparison.OrdinalIgnoreCase));
    }

    private static void PlaceSweptContact(Ball receiver, Ball donor, double reachFactor)
    {
        var reach = (receiver.Size + donor.Size) * reachFactor;
        donor.X = receiver.X + reach + 2;
        donor.Y = receiver.Y;
        donor.Vx = -(2 * reach + 4) * 60;
        donor.Vy = 0;
        receiver.Vx = 0;
        receiver.Vy = 0;
    }

    private static void PlaceInsideHalo(
        Ball receiver,
        Ball donor,
        double reachFactor,
        int index,
        int count)
    {
        var physicalReach = receiver.Size + donor.Size;
        var haloReach = physicalReach * reachFactor;
        var radius = physicalReach + (haloReach - physicalReach) * 0.5;
        var angle = Math.PI * 2 * index / count;
        donor.X = receiver.X + Math.Cos(angle) * radius;
        donor.Y = receiver.Y + Math.Sin(angle) * radius;
        donor.Vx = 0;
        donor.Vy = 0;
    }

    private static (string ReceiverId, int[] Values, long Total) RunEqualShellContact(VerifyRun run, int seed)
    {
        var harness = NewHarness(run, new BalanceConfig(), seed);
        var left = AddBall(harness, "v352-equal-a", ProjectileRole.Shell, 10);
        var right = AddBall(harness, "v352-equal-b", ProjectileRole.Shell, 10);
        left.X = harness.BattleWorld.WorldWidth * 0.5;
        left.Y = harness.BattleWorld.WorldHeight * 0.5;
        PlaceSweptContact(left, right, harness.BalanceStore.Current.FriendlyAssistReachFactor);
        harness.Battle.Step(1.0 / 60);
        var receiver = left.Projectile!.CapturesLeft > right.Projectile!.CapturesLeft ? left : right;
        var values = new[] { left.Projectile!.CapturesLeft, right.Projectile!.CapturesLeft };
        return (receiver.Id, values, ProjectileValue(harness));
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
