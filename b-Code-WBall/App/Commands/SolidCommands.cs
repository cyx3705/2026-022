using System.Globalization;
using AppShell.Core.Commands;
using WBall.Model;

namespace WBall.Commands;

/// <summary>异形实体指令(v1.5.1):列出 / 移动 / 删除;P0 不提供拉边(ED-05)。</summary>
public static class SolidCommands
{
    public static void Register(CommandRegistry registry, SceneWorld world)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "solid.list",
            Summary = "列出异形实体(与 block 区分)",
            Example = "solid.list",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                if (world.Solids.Count == 0)
                    return CommandResult.Ok("(无异形实体)");
                var lines = world.Solids.Select(s =>
                {
                    s.GetAabb(out var minX, out var minY, out var maxX, out var maxY);
                    return $"{s.Id}\tsolid\tpoints={s.Points.Count} tris={s.Triangles.Count}" +
                           $" bounds=({Fmt(minX)},{Fmt(minY)})~({Fmt(maxX)},{Fmt(maxY)})" +
                           (string.Equals(s.Id, world.SelectedSolidId, StringComparison.OrdinalIgnoreCase) ? "\t*" : "");
                });
                return CommandResult.Ok(string.Join("\n", lines));
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "solid.move",
            Summary = "整块移动异形实体(x/y 为包围盒左上角)",
            Example = "solid.move id=solid1 x=100 y=200",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "id", Description = "异形 id", Required = true },
                new ParameterSpec { Name = "x", Description = "包围盒左上 X", Type = ParamType.Double, Required = true },
                new ParameterSpec { Name = "y", Description = "包围盒左上 Y", Type = ParamType.Double, Required = true },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var solid = world.FindSolid(ctx.RequireString("id"));
                if (solid == null)
                    return CommandResult.Fail("未找到异形实体");
                solid.GetAabb(out var minX, out var minY, out _, out _);
                solid.MoveBy(ctx.GetDouble("x") - minX, ctx.GetDouble("y") - minY);
                world.NotifyChanged();
                return CommandResult.Ok($"已移动异形 {solid.Id}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "solid.set",
            Summary = "设置异形实体属性(颜色等)",
            Example = "solid.set id=solid1 color=#64748B",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "id", Description = "异形 id", Required = true },
                new ParameterSpec { Name = "color", Description = "填充色 #RRGGBB" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var solid = world.FindSolid(ctx.RequireString("id"));
                if (solid == null)
                    return CommandResult.Fail("未找到异形实体");
                if (ctx.GetString("color") != null)
                    solid.Color = ctx.RequireString("color");
                world.NotifyChanged();
                return CommandResult.Ok($"已更新异形 {solid.Id}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "solid.remove",
            Summary = "删除异形实体",
            Example = "solid.remove id=solid1",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "id", Description = "异形 id", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var id = ctx.RequireString("id");
                var solid = world.FindSolid(id);
                if (solid == null)
                    return CommandResult.Fail($"未找到异形实体 {id}");
                world.Solids.Remove(solid);
                if (string.Equals(world.SelectedSolidId, id, StringComparison.OrdinalIgnoreCase))
                    world.SelectedSolidId = null;
                world.NotifyChanged();
                return CommandResult.Ok($"已删除异形 {id}");
            }),
        });
    }

    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
