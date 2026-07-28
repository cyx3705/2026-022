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
            Parameters = SpecsFor("balance.rate"),
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var c = store.Current;
                var changed = ApplyFields(ctx, "balance.rate", c);
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
            Parameters = SpecsFor("balance.pack"),
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var c = store.Current;
                var changed = ApplyFields(ctx, "balance.pack", c);
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
            // merge 是兼容别名(同时写两个属性),不对应单一字段,单独声明
            Parameters = SpecsFor("balance.duel", ("merge", ParamType.Bool)),
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var c = store.Current;
                var changed = ApplyFields(ctx, "balance.duel", c);
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
            Parameters = SpecsFor("balance.assist"),
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var c = store.Current;
                var changed = ApplyFields(ctx, "balance.assist", c);
                // enabled 同时同步同色融入(v3.3 既有语义:助力总开关兼管 merge)
                if (ctx.Has("enabled"))
                    c.MergeSameOwnerSmall = c.FriendlyAssistEnabled;
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
            Parameters = SpecsFor("balance.shield"),
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var c = store.Current;
                var changed = ApplyFields(ctx, "balance.shield", c);
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
            Parameters = SpecsFor("balance.ember"),
            Handler = CommandDescriptor.Sync(ctx =>
            {
                // 先在副本上改,跨字段约束(min<=max)不成立就整批拒绝,不留半套值
                var c = BalanceConfigStore.Clone(store.Current);
                var changed = ApplyFields(ctx, "balance.ember", c);
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
            Parameters = SpecsFor("balance.economy"),
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var c = store.Current;
                var changed = ApplyFields(ctx, "balance.economy", c);
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
            Parameters = SpecsFor("balance.physics"),
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var c = store.Current;
                var changed = ApplyFields(ctx, "balance.physics", c);
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
            Parameters = SpecsFor("balance.round"),
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var c = store.Current;
                var changed = ApplyFields(ctx, "balance.round", c);
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

    /// <summary>
    /// v3.4 V34-05:参数规格由字段描述符生成(顺序 = 描述符声明顺序)。
    /// extra 用于声明不对应单一字段的兼容别名(如 balance.duel 的 merge)。
    /// </summary>
    private static IReadOnlyList<ParameterSpec> SpecsFor(
        string command,
        params (string Name, ParamType Type)[] extra)
    {
        var specs = BalanceFields.ForCommand(command)
            .Select(field => new ParameterSpec
            {
                Name = field.Parameter,
                Description = $"{field.Label}({field.Scope})",
                Type = ParamTypeOf(field),
            })
            .ToList();
        specs.AddRange(extra.Select(x => new ParameterSpec
        {
            Name = x.Name,
            Description = x.Name,
            Type = x.Type,
        }));
        return specs;
    }

    private static ParamType ParamTypeOf(BalanceFieldDescriptor field) => field.Kind switch
    {
        BalanceFieldKind.Bool => ParamType.Bool,
        BalanceFieldKind.Int => ParamType.Int,
        // long 走 Double 解析(阈值可写 4e4),与 v3.2 的 SetL 语义一致
        _ => ParamType.Double,
    };

    /// <summary>
    /// v3.4 V34-05:把命令上下文里出现的参数写进配置。夹取范围、类型取整全部由描述符负责,
    /// 不再一条字段写一行 SetD/SetI/SetL/SetB 三元组(param/json/setter 三者手工对齐才不出错)。
    /// </summary>
    private static bool ApplyFields(CommandContext ctx, string command, BalanceConfig config)
    {
        var changed = false;
        foreach (var field in BalanceFields.ForCommand(command))
        {
            if (!ctx.Has(field.Parameter))
                continue;
            if (field.IsBoolean)
                field.SetBool(config, ctx.GetBool(field.Parameter));
            else
                field.SetNumber(config, ctx.GetDouble(field.Parameter));
            changed = true;
        }
        return changed;
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
