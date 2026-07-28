using System.Globalization;
using System.Reflection;
using System.Text;
using AppShell.Core.Commands;
using WBall.Battle;
using WBall.Model;

namespace WBall.Commands;

/// <summary>v3.2 战斗平衡、无头试跑与数值预设命令。</summary>
public static class BalanceCommands
{
    public static void Register(
        CommandRegistry registry,
        BalanceConfigStore balanceStore,
        BattleConfigStore battleConfig,
        PresetStore presets,
        BalanceSimulator simulator,
        BattleRuntime battle,
        BattleDirector director,
        SceneWorld battleWorld)
    {
        RegisterRate(registry, balanceStore);
        RegisterPack(registry, balanceStore, battle);
        RegisterDuel(registry, balanceStore);
        RegisterAssist(registry, balanceStore, battle);
        RegisterShield(registry, balanceStore);
        RegisterEmber(registry, balanceStore);
        RegisterEconomy(registry, balanceStore);
        RegisterPhysics(registry, balanceStore, battleWorld);
        RegisterRound(registry, balanceStore);
        RegisterConfig(registry, balanceStore, battleConfig, presets, simulator, battle, director, battleWorld);
    }

    private static void RegisterRate(CommandRegistry registry, BalanceConfigStore store)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "balance.rate",
            Summary = "查询或设置大球/小球火力节奏",
            Example = "balance.rate smallBase=6 smallPerAmmo=0.15 smallMax=90 shellFactor=0.25",
            Parameters = Specs(
                ("shellFactor", ParamType.Double), ("shellFloor", ParamType.Double),
                ("smallBase", ParamType.Double), ("smallPerAmmo", ParamType.Double), ("smallMax", ParamType.Double),
                ("frozenFactor", ParamType.Double), ("frozenMax", ParamType.Double),
                ("spread", ParamType.Double), ("frozenSpread", ParamType.Double),
                ("volley", ParamType.Int), ("pending", ParamType.Int),
                ("freezePerValue", ParamType.Double), ("freezeMax", ParamType.Double), ("ammoGuard", ParamType.Int)),
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var c = store.Current;
                var changed = false;
                changed |= SetD(ctx, "shellFactor", "shellIntervalAmmoFactor", x => c.ShellIntervalAmmoFactor = x);
                changed |= SetD(ctx, "shellFloor", "shellIntervalFloorSec", x => c.ShellIntervalFloorSec = x);
                changed |= SetD(ctx, "smallBase", "smallRateBase", x => c.SmallRateBase = x);
                changed |= SetD(ctx, "smallPerAmmo", "smallRatePerAmmo", x => c.SmallRatePerAmmo = x);
                changed |= SetD(ctx, "smallMax", "smallRateMax", x => c.SmallRateMax = x);
                changed |= SetD(ctx, "frozenFactor", "smallRateFrozenFactor", x => c.SmallRateFrozenFactor = x);
                changed |= SetD(ctx, "frozenMax", "smallRateFrozenMax", x => c.SmallRateFrozenMax = x);
                changed |= SetD(ctx, "spread", "smallSpreadDeg", x => c.SmallSpreadDeg = x);
                changed |= SetD(ctx, "frozenSpread", "smallSpreadFrozenDeg", x => c.SmallSpreadFrozenDeg = x);
                changed |= SetI(ctx, "volley", "volleyRingCount", x => c.VolleyRingCount = x);
                changed |= SetI(ctx, "pending", "volleyPendingMax", x => c.VolleyPendingMax = x);
                changed |= SetD(ctx, "freezePerValue", "freezeSecondsPerValue", x => c.FreezeSecondsPerValue = x);
                changed |= SetD(ctx, "freezeMax", "freezeMaxSeconds", x => c.FreezeMaxSeconds = x);
                changed |= SetI(ctx, "ammoGuard", "ammoQueueGuard", x => c.AmmoQueueGuard = x);
                if (changed) store.Save();
                return CommandResult.Ok(
                    $"shellFactor={N(c.ShellIntervalAmmoFactor)} shellFloor={N(c.ShellIntervalFloorSec)} "
                    + $"smallBase={N(c.SmallRateBase)} smallPerAmmo={N(c.SmallRatePerAmmo)} smallMax={N(c.SmallRateMax)} "
                    + $"frozenFactor={N(c.SmallRateFrozenFactor)} frozenMax={N(c.SmallRateFrozenMax)} "
                    + $"spread={N(c.SmallSpreadDeg)} frozenSpread={N(c.SmallSpreadFrozenDeg)} "
                    + $"volley={c.VolleyRingCount} pending={c.VolleyPendingMax} "
                    + $"freezePerValue={N(c.FreezeSecondsPerValue)} freezeMax={N(c.FreezeMaxSeconds)} ammoGuard={c.AmmoQueueGuard}");
            }),
        });
    }

    private static void RegisterPack(CommandRegistry registry, BalanceConfigStore store, BattleRuntime battle)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "balance.pack",
            Summary = "查询或设置小球升格梯度(threshold=0 关闭)",
            Example = "balance.pack threshold=40000 ratio=2 max=64 followSmall=true",
            Parameters = Specs(("threshold", ParamType.Double), ("ratio", ParamType.Int),
                ("max", ParamType.Int), ("followSmall", ParamType.Bool)),
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var c = store.Current;
                var changed = false;
                changed |= SetL(ctx, "threshold", "smallPackThreshold", x => c.SmallPackThreshold = x);
                changed |= SetI(ctx, "ratio", "smallPackRatio", x => c.SmallPackRatio = x);
                changed |= SetI(ctx, "max", "smallPackMax", x => c.SmallPackMax = x);
                changed |= SetB(ctx, "followSmall", x => c.SmallPackSpeedFollowsSmall = x);
                if (changed) store.Save();
                var pools = battle.Turrets.Select(x => $"{x.Id}:{x.SmallAmmo}→{battle.SmallPackValue(x.SmallAmmo)}");
                return CommandResult.Ok(
                    $"threshold={c.SmallPackThreshold} ratio={c.SmallPackRatio} max={c.SmallPackMax} "
                    + $"followSmall={B(c.SmallPackSpeedFollowsSmall)} 当前池/包值=[{string.Join(", ", pools)}]");
            }),
        });
    }

    private static void RegisterDuel(CommandRegistry registry, BalanceConfigStore store)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "balance.duel",
            Summary = "查询或设置光晕对消、研磨和同色融合",
            Example = "balance.duel halo=1.6 grind=2 merge=true",
            Parameters = Specs(("halo", ParamType.Double), ("grind", ParamType.Double), ("merge", ParamType.Bool)),
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var c = store.Current;
                var changed = SetD(ctx, "halo", "haloReachFactor", x => c.HaloReachFactor = x)
                              | SetD(ctx, "grind", "grindRatePerSecond", x => c.GrindRatePerSecond = x);
                if (ctx.Has("merge"))
                {
                    var enabled = ctx.GetBool("merge");
                    c.MergeSameOwnerSmall = enabled;
                    c.FriendlyAssistEnabled = enabled;
                    changed = true;
                }
                if (changed) store.Save();
                return CommandResult.Ok($"halo={N(c.HaloReachFactor)} grind={N(c.GrindRatePerSecond)} merge={B(c.FriendlyAssistEnabled)} (兼容别名)");
            }),
        });
    }

    private static void RegisterAssist(CommandRegistry registry, BalanceConfigStore store, BattleRuntime battle)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "balance.assist",
            Summary = "查询或设置同阵营低速积分传递与升格小球回收",
            Example = "balance.assist enabled=true visual=true smallRate=0.25 shellRate=0.10 reach=1.20 max=100000",
            Parameters = Specs(("enabled", ParamType.Bool), ("smallRate", ParamType.Double),
                ("shellRate", ParamType.Double), ("reach", ParamType.Double), ("max", ParamType.Int),
                ("visual", ParamType.Bool)),
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var c = store.Current;
                var changed = SetB(ctx, "enabled", x =>
                              {
                                  c.FriendlyAssistEnabled = x;
                                  c.MergeSameOwnerSmall = x;
                              })
                              | SetB(ctx, "visual", x => c.FriendlyAssistVisualEnabled = x)
                              | SetD(ctx, "smallRate", "friendlyAbsorbSmallRate", x => c.FriendlyAbsorbSmallRate = x)
                              | SetD(ctx, "shellRate", "friendlyShellTransferRate", x => c.FriendlyShellTransferRate = x)
                              | SetD(ctx, "reach", "friendlyAssistReachFactor", x => c.FriendlyAssistReachFactor = x)
                              | SetI(ctx, "max", "friendlyAssistMaxValue", x => c.FriendlyAssistMaxValue = x);
                if (changed) store.Save();

                var status = battle.FriendlyAssistStatus();
                return CommandResult.Ok(
                    $"enabled={B(c.FriendlyAssistEnabled)} visual={B(c.FriendlyAssistVisualEnabled)} smallRate={N(c.FriendlyAbsorbSmallRate)} "
                    + $"shellRate={N(c.FriendlyShellTransferRate)} reach={N(c.FriendlyAssistReachFactor)} "
                    + $"max={c.FriendlyAssistMaxValue} 60s上限=小球{N(c.FriendlyAbsorbSmallRate * 60)}/大球{N(c.FriendlyShellTransferRate * 60)}\n"
                    + $"在场 small={status.SmallShots} shell={status.Shells} ember={status.Embers} other={status.Others}; "
                    + $"最近1秒 小球转移={status.SmallTransferred} 大球转移={status.ShellTransferred} 回收={status.Reclaimed}");
            }),
        });
    }

    private static void RegisterShield(CommandRegistry registry, BalanceConfigStore store)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "balance.shield",
            Summary = "查询或设置护罩、触杀、回充和自然再生",
            Example = "balance.shield breakthrough=true contact=true refund=true suddenBlock=true slotGain=1 regen=0",
            Parameters = Specs(("breakthrough", ParamType.Bool), ("contact", ParamType.Bool),
                ("refund", ParamType.Bool), ("suddenBlock", ParamType.Bool),
                ("slotGain", ParamType.Double), ("regen", ParamType.Double)),
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var c = store.Current;
                var changed = SetB(ctx, "breakthrough", x => c.ShieldBreakthrough = x)
                              | SetB(ctx, "contact", x => c.ContactKillEnabled = x)
                              | SetB(ctx, "refund", x => c.SelfShieldRefundEnabled = x)
                              | SetB(ctx, "suddenBlock", x => c.SuddenDeathShieldBlock = x)
                              | SetD(ctx, "slotGain", "shieldSlotGainPerValue", x => c.ShieldSlotGainPerValue = x)
                              | SetD(ctx, "regen", "shieldRegenPerSecond", x => c.ShieldRegenPerSecond = x);
                if (changed) store.Save();
                return CommandResult.Ok(
                    $"breakthrough={B(c.ShieldBreakthrough)} contact={B(c.ContactKillEnabled)} "
                    + $"refund={B(c.SelfShieldRefundEnabled)} suddenBlock={B(c.SuddenDeathShieldBlock)} "
                    + $"slotGain={N(c.ShieldSlotGainPerValue)} regen={N(c.ShieldRegenPerSecond)}");
            }),
        });
    }

    private static void RegisterEmber(CommandRegistry registry, BalanceConfigStore store)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "balance.ember",
            Summary = "查询或设置余烬速度和来源",
            Example = "balance.ember speedMin=150 speedMax=400 ammo=true economy=true",
            Parameters = Specs(("speedMin", ParamType.Double), ("speedMax", ParamType.Double),
                ("ammo", ParamType.Bool), ("economy", ParamType.Bool)),
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var c = BalanceConfigStore.Clone(store.Current);
                var changed = SetD(ctx, "speedMin", "emberSpeedMin", x => c.EmberSpeedMin = x)
                              | SetD(ctx, "speedMax", "emberSpeedMax", x => c.EmberSpeedMax = x)
                              | SetB(ctx, "ammo", x => c.EmberFromAmmo = x)
                              | SetB(ctx, "economy", x => c.EmberDrainEconomy = x);
                if (c.EmberSpeedMin > c.EmberSpeedMax)
                    return CommandResult.Fail("speedMin 不得大于 speedMax");
                if (changed) store.Replace(c);
                return CommandResult.Ok($"speed={N(c.EmberSpeedMin)}~{N(c.EmberSpeedMax)} ammo={B(c.EmberFromAmmo)} economy={B(c.EmberDrainEconomy)}");
            }),
        });
    }

    private static void RegisterEconomy(CommandRegistry registry, BalanceConfigStore store)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "balance.economy",
            Summary = "查询或设置经济到火力映射(主要用于 direct 模式)",
            Example = "balance.economy exponent=0.5 sizeBase=8 pierce=0.08",
            Parameters = Specs(("exponent", ParamType.Double), ("sizeBase", ParamType.Double),
                ("burstDamage", ParamType.Double), ("burstSpread", ParamType.Double),
                ("pierce", ParamType.Double), ("gravitySize", ParamType.Double),
                ("gravityDamage", ParamType.Double), ("score", ParamType.Double)),
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var c = store.Current;
                var changed = SetD(ctx, "exponent", "intensityExponent", x => c.IntensityExponent = x)
                              | SetD(ctx, "sizeBase", "sizeGainBase", x => c.SizeGainBase = x)
                              | SetD(ctx, "burstDamage", "burstDamageGain", x => c.BurstDamageGain = x)
                              | SetD(ctx, "burstSpread", "burstSpreadGain", x => c.BurstSpreadGain = x)
                              | SetD(ctx, "pierce", "pierceDamageGain", x => c.PierceDamageGain = x)
                              | SetD(ctx, "gravitySize", "gravitySizeGain", x => c.GravitySizeGain = x)
                              | SetD(ctx, "gravityDamage", "gravityDamageGain", x => c.GravityDamageGain = x)
                              | SetD(ctx, "score", "scoreDamageGain", x => c.ScoreDamageGain = x);
                if (changed) store.Save();
                return CommandResult.Ok(
                    $"exponent={N(c.IntensityExponent)} sizeBase={N(c.SizeGainBase)} "
                    + $"burstDamage={N(c.BurstDamageGain)} burstSpread={N(c.BurstSpreadGain)} "
                    + $"pierce={N(c.PierceDamageGain)} gravitySize={N(c.GravitySizeGain)} "
                    + $"gravityDamage={N(c.GravityDamageGain)} score={N(c.ScoreDamageGain)} (territory 仅部分生效)");
            }),
        });
    }

    private static void RegisterPhysics(CommandRegistry registry, BalanceConfigStore store, SceneWorld world)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "balance.physics",
            Summary = "查询或设置右世界墙面/弹体弹性",
            Example = "balance.physics wall=0.55 ball=0.85",
            Parameters = Specs(("wall", ParamType.Double), ("ball", ParamType.Double)),
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var c = store.Current;
                var changed = SetD(ctx, "wall", "wallRestitution", x => c.WallRestitution = x)
                              | SetD(ctx, "ball", "ballRestitution", x => c.BallRestitution = x);
                if (changed) store.Save();
                world.WallRestitution = c.WallRestitution;
                world.BallRestitution = c.BallRestitution;
                return CommandResult.Ok($"wall={N(c.WallRestitution)} ball={N(c.BallRestitution)} (仅右世界)");
            }),
        });
    }

    private static void RegisterRound(CommandRegistry registry, BalanceConfigStore store)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "balance.round",
            Summary = "查询或设置倒计时、结算展示和硬性时限",
            Example = "balance.round countdown=1 settle=2 limit=0",
            Parameters = Specs(("countdown", ParamType.Double), ("settle", ParamType.Double), ("limit", ParamType.Double)),
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var c = store.Current;
                var changed = SetD(ctx, "countdown", "countdownSeconds", x => c.CountdownSeconds = x)
                              | SetD(ctx, "settle", "settleSeconds", x => c.SettleSeconds = x)
                              | SetD(ctx, "limit", "hardTimeLimitSeconds", x => c.HardTimeLimitSeconds = x);
                if (changed) store.Save();
                return CommandResult.Ok($"countdown={N(c.CountdownSeconds)} settle={N(c.SettleSeconds)} limit={N(c.HardTimeLimitSeconds)}");
            }),
        });
    }

    private static void RegisterConfig(
        CommandRegistry registry,
        BalanceConfigStore store,
        BattleConfigStore battleConfig,
        PresetStore presets,
        BalanceSimulator simulator,
        BattleRuntime battle,
        BattleDirector director,
        SceneWorld battleWorld)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "balance.config",
            Summary = "打印全部战斗平衡字段及生效范围",
            Example = "balance.config",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(FormatConfig(store.Current, battle.TerritoryMode))),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "balance.default",
            Summary = "恢复战斗平衡出厂默认",
            Example = "balance.default reset=true",
            Parameters = Specs(("reset", ParamType.Bool)),
            Handler = CommandDescriptor.Sync(ctx =>
            {
                store.ResetDefaults();
                ApplyPhysics(battleWorld, store.Current);
                if (ctx.GetBool("reset", false)) battle.Reset(battle.Seed);
                return CommandResult.Ok("战斗平衡已恢复出厂默认" + (ctx.GetBool("reset", false) ? ";战场已重置" : ";未重置"));
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "balance.diff",
            Summary = "比较两套平衡配置",
            Example = "balance.diff a=default b=current",
            Readonly = true,
            Parameters = [
                new ParameterSpec { Name = "a", Description = "default|current|预设名", Default = "default" },
                new ParameterSpec { Name = "b", Description = "default|current|预设名", Default = "current" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    var a = ResolveProfile(ctx.GetString("a") ?? "default", store, battleConfig, presets);
                    var b = ResolveProfile(ctx.GetString("b") ?? "current", store, battleConfig, presets);
                    return CommandResult.Ok(FormatDiff(a, b));
                }
                catch (Exception ex) { return CommandResult.Fail(ex.Message); }
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "balance.sim",
            Summary = "在独立实例中按多种子无头试跑",
            Example = "balance.sim seeds=42..49 seconds=180 config=current format=table",
            Readonly = true,
            Parameters = [
                new ParameterSpec { Name = "seeds", Description = "逗号列表或范围", Default = "42,43,44" },
                new ParameterSpec { Name = "seconds", Description = "每局逻辑秒数", Type = ParamType.Double, Default = "180" },
                new ParameterSpec { Name = "config", Description = "current|default|预设名", Default = "current" },
                new ParameterSpec { Name = "format", Description = "table|csv", Default = "table", AllowedValues = ["table", "csv"] },
                new ParameterSpec { Name = "timeoutMs", Description = "整批墙钟超时", Type = ParamType.Int, Default = "60000" },
            ],
            Handler = async ctx =>
            {
                try
                {
                    var seeds = ParseSeeds(ctx.GetString("seeds") ?? "42,43,44");
                    var seconds = Math.Clamp(ctx.GetDouble("seconds", 180), 1, 7200);
                    var profile = ResolveProfile(ctx.GetString("config") ?? "current", store, battleConfig, presets);
                    var timeout = TimeSpan.FromMilliseconds(Math.Clamp(ctx.GetInt("timeoutMs", 60_000), 1000, 600_000));
                    var result = await Task.Run(() => simulator.Run(
                        seeds, seconds, profile.Arena, profile.Balance, timeout, ctx.Progress, ctx.Cancellation))
                        .ConfigureAwait(false);
                    return CommandResult.Ok(result.Format(ctx.GetString("format") ?? "table"));
                }
                catch (OperationCanceledException) { return CommandResult.Fail("试跑已取消"); }
                catch (Exception ex) { return CommandResult.Fail(ex.Message); }
            },
        });

        registry.Register(new CommandDescriptor
        {
            Name = "preset.list",
            Summary = "列出数值预设",
            Example = "preset.list",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(string.Join(Environment.NewLine, presets.List()))),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "preset.save",
            Summary = "把当前 arena+balance 保存为数值预设",
            Example = "preset.save name=rush",
            Parameters = [new ParameterSpec { Name = "name", Description = "预设名", Required = true }],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try { return CommandResult.Ok($"已保存 {presets.Save(ctx.RequireString("name"), battleConfig.Arena, store.Current)}"); }
                catch (Exception ex) { return CommandResult.Fail(ex.Message); }
            }),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "preset.load",
            Summary = "读取数值预设(只覆盖 arena+balance)",
            Example = "preset.load name=rush reset=true",
            Parameters = [
                new ParameterSpec { Name = "name", Description = "预设名", Required = true },
                new ParameterSpec { Name = "reset", Description = "立即重置战场", Type = ParamType.Bool, Default = "false" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    var preset = presets.Load(ctx.RequireString("name"));
                    battleConfig.Replace(battleConfig.Turrets, preset.Arena);
                    store.Replace(preset.Balance);
                    ApplyPhysics(battleWorld, store.Current);
                    if (ctx.GetBool("reset", false)) battle.Reset(battle.Seed);
                    return CommandResult.Ok($"已读取预设 {preset.Name}" + (ctx.GetBool("reset", false) ? ";战场已重置" : ";未重置"));
                }
                catch (Exception ex) { return CommandResult.Fail(ex.Message); }
            }),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "preset.delete",
            Summary = "删除数值预设(须 confirm=true)",
            Example = "preset.delete name=test confirm=true",
            Parameters = [
                new ParameterSpec { Name = "name", Description = "预设名", Required = true },
                new ParameterSpec { Name = "confirm", Description = "显式确认", Type = ParamType.Bool, Default = "false" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (!ctx.GetBool("confirm", false)) return CommandResult.Fail("删除预设须 confirm=true");
                try { presets.Delete(ctx.RequireString("name")); return CommandResult.Ok("预设已删除"); }
                catch (Exception ex) { return CommandResult.Fail(ex.Message); }
            }),
        });
    }

    private static IReadOnlyList<ParameterSpec> Specs(params (string Name, ParamType Type)[] values) =>
        values.Select(x => new ParameterSpec { Name = x.Name, Description = x.Name, Type = x.Type }).ToList();

    private static bool SetD(CommandContext ctx, string param, string field, Action<double> apply)
    {
        if (!ctx.Has(param)) return false;
        apply(BalanceConfigStore.ClampField(field, ctx.GetDouble(param)));
        return true;
    }

    private static bool SetI(CommandContext ctx, string param, string field, Action<int> apply)
    {
        if (!ctx.Has(param)) return false;
        apply((int)Math.Round(BalanceConfigStore.ClampField(field, ctx.GetInt(param))));
        return true;
    }

    private static bool SetL(CommandContext ctx, string param, string field, Action<long> apply)
    {
        if (!ctx.Has(param)) return false;
        apply((long)Math.Round(BalanceConfigStore.ClampField(field, ctx.GetDouble(param))));
        return true;
    }

    private static bool SetB(CommandContext ctx, string param, Action<bool> apply)
    {
        if (!ctx.Has(param)) return false;
        apply(ctx.GetBool(param));
        return true;
    }

    private static void ApplyPhysics(SceneWorld world, BalanceConfig config)
    {
        world.WallRestitution = config.WallRestitution;
        world.BallRestitution = config.BallRestitution;
    }

    private static BattlePreset ResolveProfile(
        string name, BalanceConfigStore balance, BattleConfigStore arena, PresetStore presets)
    {
        if (name.Equals("current", StringComparison.OrdinalIgnoreCase))
            return new BattlePreset { Name = "current", Arena = PresetStore.CloneArena(arena.Arena), Balance = BalanceConfigStore.Clone(balance.Current) };
        if (name.Equals("default", StringComparison.OrdinalIgnoreCase))
            return new BattlePreset { Name = "default", Arena = new ArenaLayoutConfig(), Balance = new BalanceConfig() };
        return presets.Load(name);
    }

    private static string FormatConfig(BalanceConfig c, bool territory)
    {
        var groups = new (string Name, string[] Fields, string Scope)[]
        {
            ("火力节奏", ["ShellIntervalAmmoFactor","ShellIntervalFloorSec","SmallRateBase","SmallRatePerAmmo","SmallRateMax","SmallRateFrozenFactor","SmallRateFrozenMax","SmallSpreadDeg","SmallSpreadFrozenDeg","VolleyRingCount","VolleyPendingMax","FreezeSecondsPerValue","FreezeMaxSeconds","AmmoQueueGuard"], "即时/territory"),
            ("升格梯度", ["SmallPackThreshold","SmallPackRatio","SmallPackMax","SmallPackSpeedFollowsSmall"], "即时/territory"),
            ("对消研磨", ["HaloReachFactor","GrindRatePerSecond"], "即时/territory"),
            ("同阵营助力", ["FriendlyAssistEnabled","FriendlyAssistVisualEnabled","FriendlyAbsorbSmallRate","FriendlyShellTransferRate","FriendlyAssistReachFactor","FriendlyAssistMaxValue"], "即时/territory"),
            ("护罩触杀", ["ShieldBreakthrough","ContactKillEnabled","SelfShieldRefundEnabled","SuddenDeathShieldBlock","ShieldSlotGainPerValue","ShieldRegenPerSecond"], "即时/两者"),
            ("余烬爆发", ["EmberSpeedMin","EmberSpeedMax","EmberFromAmmo","EmberDrainEconomy"], "即时/territory"),
            ("经济映射", ["IntensityExponent","SizeGainBase","BurstDamageGain","BurstSpreadGain","PierceDamageGain","GravitySizeGain","GravityDamageGain","ScoreDamageGain"], territory ? "当前 territory:仅部分生效" : "当前 direct:生效"),
            ("战场物理", ["WallRestitution","BallRestitution"], "即时/仅右世界"),
            ("回合", ["CountdownSeconds","SettleSeconds","HardTimeLimitSeconds"], "倒计时下局/其余即时"),
        };
        var properties = typeof(BalanceConfig).GetProperties().ToDictionary(x => x.Name);
        var sb = new StringBuilder();
        foreach (var group in groups)
        {
            sb.AppendLine($"── {group.Name} ({group.Scope}) ──");
            foreach (var field in group.Fields)
            {
                var value = properties[field].GetValue(c);
                sb.Append(Camel(field)).Append('=').Append(Value(value)).Append(' ');
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string FormatDiff(BattlePreset a, BattlePreset b)
    {
        var lines = new List<string> { $"{a.Name} → {b.Name}" };
        AppendDiff(lines, "arena", a.Arena, b.Arena);
        AppendDiff(lines, "balance", a.Balance, b.Balance);
        return lines.Count == 1 ? lines[0] + Environment.NewLine + "(无差异)" : string.Join(Environment.NewLine, lines);
    }

    private static void AppendDiff(List<string> lines, string prefix, object a, object b)
    {
        foreach (var property in a.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var left = property.GetValue(a);
            var right = property.GetValue(b);
            if (Equals(left, right)) continue;
            lines.Add($"{prefix}.{Camel(property.Name)}: {Value(left)} → {Value(right)}");
        }
    }

    private static IReadOnlyList<int> ParseSeeds(string text)
    {
        var result = new List<int>();
        foreach (var token in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = token.Split("..", StringSplitOptions.TrimEntries);
            if (range.Length == 2 && int.TryParse(range[0], out var start) && int.TryParse(range[1], out var end))
            {
                var step = start <= end ? 1 : -1;
                for (var value = start; ; value += step)
                {
                    result.Add(value);
                    if (value == end || result.Count >= 128) break;
                }
            }
            else if (int.TryParse(token, out var seed)) result.Add(seed);
            else throw new FormatException($"无效种子: {token}");
            if (result.Count >= 128) break;
        }
        if (result.Count == 0) throw new FormatException("seeds 不能为空");
        return result;
    }

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
    private static string N(double value) => value.ToString(Math.Abs(value) >= 1000 ? "0.##" : "0.###", CultureInfo.InvariantCulture);
    private static string B(bool value) => value.ToString().ToLowerInvariant();
    private static string Value(object? value) => value switch
    {
        null => "-",
        bool b => B(b),
        double d => N(d),
        float f => N(f),
        IFormattable x => x.ToString(null, CultureInfo.InvariantCulture) ?? "-",
        _ => value.ToString() ?? "-",
    };
}
