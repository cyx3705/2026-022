using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using WBall.Battle;
using WBall.Model;
using WBall.Presentation;
using WBall.Sim;

namespace WBall.Verify.Suites;

internal static class GameplayRegressionSuite
{
    public static int Run(VerifyRun run)
    {
        var migrationRoot = Path.Combine(run.Root, "interaction-migration");
        var seededConfig = new BattleConfigStore(migrationRoot, run.Log);
        var legacyArena = JsonNode.Parse(File.ReadAllText(seededConfig.ArenaPath))!.AsObject();
        legacyArena.Remove("ballInteractionRulesVersion");
        legacyArena["ballCollision"] = true;
        File.WriteAllText(
            seededConfig.ArenaPath,
            legacyArena.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var migratedConfig = new BattleConfigStore(migrationRoot, run.Log);
        using var migratedDocument = JsonDocument.Parse(File.ReadAllText(migratedConfig.ArenaPath));
        var migrationPersisted = migratedDocument.RootElement.TryGetProperty(
            "ballInteractionRulesVersion", out var rulesVersion)
            && rulesVersion.GetInt32() == 1;
        var migratedToAbsorption = !migratedConfig.Arena.BallCollision;
        migratedConfig.Arena.BallCollision = true;
        migratedConfig.Save();
        var explicitCollisionConfig = new BattleConfigStore(migrationRoot, run.Log);
        run.Check("legacy collision default migrates once and later explicit choice persists",
            migratedToAbsorption
            && migrationPersisted
            && explicitCollisionConfig.Arena.BallCollision,
            $"migrated={migratedToAbsorption} marker={migrationPersisted} "
            + $"explicit={explicitCollisionConfig.Arena.BallCollision}");

        run.Check("current-config start panel does not load demo scenario",
            BattlePanelTemplate.StartCurrentConfigurationCommand == "battle.start seed={seed}"
            && !BattlePanelTemplate.StartCurrentConfigurationCommand.Contains(
                "demo.play", StringComparison.OrdinalIgnoreCase));

        var current = run.NewHarness(new BalanceConfig());
        var marker = current.EconomyWorld.Objects[0];
        marker.X += 17;
        var markerX = marker.X;
        current.BattleConfig.Arena.SmallBallSpeed = 321;
        current.EconomyWorld.Factions[0].InitialBalls = 9;
        current.Director.Start(77, countdownSeconds: 0);
        run.Check("starting current configuration preserves edited scene and settings",
            Math.Abs(marker.X - markerX) < 1e-9
            && Math.Abs(current.BattleConfig.Arena.SmallBallSpeed - 321) < 1e-9
            && current.EconomyWorld.Factions[0].InitialBalls == 9,
            $"markerX={marker.X:0.###} speed={current.BattleConfig.Arena.SmallBallSpeed:0.###} "
            + $"balls={current.EconomyWorld.Factions[0].InitialBalls}");

        var shield = run.NewHarness(new BalanceConfig { ShieldSlotGainPerValue = 1 });
        var faction = shield.EconomyWorld.Factions[0];
        faction.Shield = 0;
        faction.MaxShield = shield.BattleConfig.Arena.ShieldCostPerValue * 20;
        var shieldSink = new SceneObject
        {
            Id = "shield-regression-sink",
            Type = SceneObjectType.Despawner,
            Name = "护盾",
            X = 100,
            Y = 100,
            W = 80,
            H = 40,
        };
        shield.EconomyWorld.Objects.Add(shieldSink);
        var deposited = new Ball
        {
            Id = "shield-regression-ball",
            X = shieldSink.X,
            Y = shieldSink.Y,
            Color = faction.Color,
            Multiplier = 3,
        };
        shield.EconomyWorld.Defaults.ApplyToBall(deposited);
        shield.EconomyWorld.Balls.Add(deposited);
        PhysicsEngine.TeleportBall(shield.EconomyWorld, deposited, shieldSink, null);
        run.Check("shield-slot small-ball points become usable shield points",
            Math.Abs(shield.Battle.ShieldValueOf(faction) - 3) < 1e-9
            && Math.Abs(faction.Shield - shield.BattleConfig.Arena.ShieldCostPerValue * 3) < 1e-6,
            $"displayed={shield.Battle.ShieldValueOf(faction):0.###} raw={faction.Shield:0.###} "
            + $"cost={shield.BattleConfig.Arena.ShieldCostPerValue:0.###}");

        faction.Shield = faction.MaxShield;
        var overflowBall = new Ball
        {
            Id = "shield-unlimited-ball",
            Color = faction.Color,
            Multiplier = 3,
        };
        shield.Bridge.TrySettle(
            "护盾", shield.EconomyWorld, overflowBall, overflowBall.Multiplier, null);
        run.Check("shield-slot growth exceeds legacy maximum",
            faction.Shield > faction.MaxShield
            && Math.Abs(faction.Shield - (faction.MaxShield + shield.BattleConfig.Arena.ShieldCostPerValue * 3)) < 1e-6,
            $"shield={faction.Shield:0.###} legacyMax={faction.MaxShield:0.###}");

        var initialUnlimited = run.NewHarness(new BalanceConfig());
        var initialDefinition = initialUnlimited.BattleConfig.Turrets[0];
        initialDefinition.InitialShield = initialDefinition.MaxShield + 123_456;
        initialUnlimited.Battle.Reset(42);
        run.Check("initial shield exceeds legacy maximum",
            Math.Abs(initialUnlimited.Battle.Turrets[0].Shield - initialDefinition.InitialShield) < 1e-6,
            $"shield={initialUnlimited.Battle.Turrets[0].Shield:0.###} legacyMax={initialDefinition.MaxShield:0.###}");

        var legacyShield = run.NewHarness(new BalanceConfig { ShieldSlotGainPerValue = 50_000 });
        var legacyFaction = legacyShield.EconomyWorld.Factions[0];
        legacyFaction.Shield = 0;
        legacyFaction.MaxShield = 500_000;
        var legacyBall = new Ball
        {
            Id = "legacy-shield-ball",
            Color = legacyFaction.Color,
            Multiplier = 1,
        };
        legacyShield.Bridge.TrySettle(
            "护盾", legacyShield.EconomyWorld, legacyBall, legacyBall.Multiplier, null);
        run.Check("legacy raw shield-slot gain remains compatible",
            Math.Abs(legacyFaction.Shield - 50_000) < 1e-6,
            $"shield={legacyFaction.Shield:0.###}");

        return run.Conclude("GAMEPLAY REGRESSION");
    }
}
