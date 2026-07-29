using System.Globalization;
using System.IO;
using System.Text.Json;
using AppShell.Core.Commands;
using AppShell.Core.Logging;
using WBall.Data;
using WBall.Editing;
using WBall.Game;
using WBall.Model;
using WBall.Sim;

namespace WBall.Commands;
internal static class FormulaCommands
{
    public static void Register(
        CommandRegistry registry,
        SceneWorld world,
        FormulaEditorService formulaEditor)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "formula.show",
            Summary = "显示小球公式参数(与小球窗同一配置)",
            Example = "formula.show",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var d = world.Defaults;
                return CommandResult.Ok(
                    $"size = Round({Fmt(d.SizeBase)} + {Fmt(d.SizeScale)} × √mult) → int\n" +
                    $"weight = Round1({Fmt(d.WeightBase)} + {Fmt(d.WeightScale)} × √mult)\n" +
                    $"initialMultiplier = {d.InitialMultiplier}\n" +
                    "(编辑入口: win.show name=scenedebug; 参数支持小数)");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "formula.set",
            Summary = "设置小球公式参数并持久化,可选重算场上球",
            Example = "formula.set sizebase=6 sizescale=6 weightbase=1 weightscale=1 initial=1 recalc=false",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "sizebase", Description = "SizeBase", Type = ParamType.Double },
                new ParameterSpec { Name = "sizescale", Description = "SizeScale", Type = ParamType.Double },
                new ParameterSpec { Name = "weightbase", Description = "WeightBase", Type = ParamType.Double },
                new ParameterSpec { Name = "weightscale", Description = "WeightScale", Type = ParamType.Double },
                new ParameterSpec { Name = "initial", Description = "InitialMultiplier" },
                new ParameterSpec { Name = "recalc", Description = "是否重算全部现存球", Type = ParamType.Bool, Default = "false" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                long? initial = null;
                if (ctx.GetString("initial") is { } rawInitial)
                {
                    if (!long.TryParse(rawInitial, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        return CommandResult.Fail("初始倍率必须是整数");
                    initial = parsed;
                }

                var result = formulaEditor.Apply(new FormulaEditRequest(
                    ctx.GetString("sizebase") != null ? ctx.GetDouble("sizebase") : null,
                    ctx.GetString("sizescale") != null ? ctx.GetDouble("sizescale") : null,
                    ctx.GetString("weightbase") != null ? ctx.GetDouble("weightbase") : null,
                    ctx.GetString("weightscale") != null ? ctx.GetDouble("weightscale") : null,
                    initial,
                    ctx.GetBool("recalc", false)));
                return result.Success ? CommandResult.Ok(result.Message) : CommandResult.Fail(result.Message);
            }),
        });
    }

    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
