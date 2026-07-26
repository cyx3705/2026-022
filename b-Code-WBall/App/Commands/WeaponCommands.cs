using System.Globalization;
using AppShell.Core.Commands;
using WBall.Battle;

namespace WBall.Commands;

public static class WeaponCommands
{
    public static void Register(CommandRegistry registry, WeaponCatalog catalog)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "weapon.list",
            Summary = "列出攻击类型与销毁器合法结算名",
            Example = "weapon.list",
            Handler = CommandDescriptor.Sync(_ =>
                CommandResult.Ok(string.Join(Environment.NewLine, catalog.Weapons.Select(Format)))),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "weapon.show",
            Summary = "查看一项武器定义",
            Example = "weapon.show name=大球",
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "武器名或别名", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
                catalog.TryResolve(ctx.RequireString("name"), out var weapon)
                    ? CommandResult.Ok(Format(weapon))
                    : CommandResult.Fail($"未知武器: {ctx.RequireString("name")}")),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "weapon.set",
            Summary = "修改并保存武器字段",
            Example = "weapon.set name=散弹 key=unlock val=60",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "武器名或别名", Required = true },
                new ParameterSpec { Name = "key", Description = "enabled/kind/damage/speed/spread/count/scale/unlock/color", Required = true },
                new ParameterSpec { Name = "val", Description = "字段值", Required = true },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    var weapon = catalog.Set(
                        ctx.RequireString("name"),
                        ctx.RequireString("key"),
                        ctx.RequireString("val"));
                    return CommandResult.Ok(Format(weapon));
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "weapon.reload",
            Summary = "从 weapons.json 热重载武器库",
            Example = "weapon.reload",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                catalog.Reload();
                return CommandResult.Ok($"已重载 {catalog.Weapons.Count} 项: {catalog.Path}");
            }),
        });
    }

    private static string Format(WeaponDefinition weapon) =>
        $"{weapon.Name} kind={weapon.Kind.ToString().ToLowerInvariant()} enabled={weapon.Enabled.ToString().ToLowerInvariant()} " +
        $"damage={Number(weapon.DamageCoefficient)} speed={Number(weapon.Speed)} spread={Number(weapon.SpreadDegrees)} " +
        $"count={weapon.BaseCount} scale={Number(weapon.EconomyScale)} unlock={Number(weapon.UnlockAtSeconds)} " +
        $"aliases=[{string.Join(',', weapon.Aliases)}]";

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
