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
internal static class SceneCommands
{
    public static void Register(
        CommandRegistry registry,
        SceneWorld world,
        string scenesDirectory,
        ScenePropertyService sceneProperties)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "scene.tool",
            Summary = "切换落球区编辑工具",
            Example = "scene.tool mode=block",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "mode",
                    Description = "select/block/arrow/spawner/despawner/wire",
                    Required = true,
                    Position = 0,
                    AllowedValues = ["select", "block", "arrow", "spawner", "despawner", "wire"],
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                world.Tool = ctx.RequireString("mode").ToLowerInvariant() switch
                {
                    "select" => EditorTool.Select,
                    "block" => EditorTool.Block,
                    "arrow" => EditorTool.Arrow,
                    "spawner" => EditorTool.Spawner,
                    "despawner" => EditorTool.Despawner,
                    "wire" => EditorTool.Wire,
                    _ => world.Tool,
                };
                world.NotifyChanged();
                return CommandResult.Ok($"工具: {world.Tool}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "scene.size",
            Summary = "查询或设置场景内区逻辑尺寸(界外为实体区)",
            Example = "scene.size w=1000 h=700",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "w",
                    Description = $"宽({SceneWorld.MinWorldSize}~{SceneWorld.MaxWorldSize})",
                    Type = ParamType.Double,
                },
                new ParameterSpec
                {
                    Name = "h",
                    Description = $"高({SceneWorld.MinWorldSize}~{SceneWorld.MaxWorldSize})",
                    Type = ParamType.Double,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var hasW = ctx.GetString("w") != null;
                var hasH = ctx.GetString("h") != null;
                if (!hasW && !hasH)
                {
                    return CommandResult.Ok(
                        $"场景尺寸 {Fmt(world.WorldWidth)}×{Fmt(world.WorldHeight)} " +
                        $"(范围 {SceneWorld.MinWorldSize}~{SceneWorld.MaxWorldSize})");
                }

                try
                {
                    var w = hasW ? ctx.GetDouble("w") : world.WorldWidth;
                    var h = hasH ? ctx.GetDouble("h") : world.WorldHeight;
                    var cull = world.SetWorldSize(w, h);
                    var msg = $"已设置场景尺寸 {Fmt(world.WorldWidth)}×{Fmt(world.WorldHeight)};界外为实体区";
                    if (cull.Any)
                        msg += " | " + cull;
                    return CommandResult.Ok(msg);
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "scene.add",
            Summary = "在落球区添加场景对象",
            Example = "scene.add type=block x=100 y=200 w=80 h=20",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "type", Description = "block/arrow/spawner/despawner", Required = true, AllowedValues = ["block", "arrow", "spawner", "despawner"] },
                new ParameterSpec { Name = "x", Description = "左上 X", Type = ParamType.Double, Required = true },
                new ParameterSpec { Name = "y", Description = "左上 Y", Type = ParamType.Double, Required = true },
                new ParameterSpec { Name = "w", Description = "宽", Type = ParamType.Double, Default = "40" },
                new ParameterSpec { Name = "h", Description = "高", Type = ParamType.Double, Default = "40" },
                new ParameterSpec { Name = "id", Description = "可选 id" },
                new ParameterSpec { Name = "dirx", Description = "箭头方向 X", Type = ParamType.Double, Default = "0" },
                new ParameterSpec { Name = "diry", Description = "箭头方向 Y(+下)", Type = ParamType.Double, Default = "1" },
                new ParameterSpec { Name = "radius", Description = "箭头影响半径", Type = ParamType.Double, Default = "160" },
                new ParameterSpec { Name = "strength", Description = "箭头强度(g)", Type = ParamType.Double, Default = "10" },
                new ParameterSpec { Name = "rotation", Description = "旋转角(度,顺时针,0=轴对齐)", Type = ParamType.Double, Default = "0" },
                new ParameterSpec { Name = "patch", Description = "生成/销毁补丁 JSON" },
                new ParameterSpec { Name = "name", Description = "对象名;销毁器上为函数名(X5/RUN)" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var type = ParseType(ctx.RequireString("type"));
                var id = ctx.GetString("id") ?? world.NextObjectId();
                if (world.FindObject(id) != null)
                    return CommandResult.Fail($"对象 id 已存在: {id}");

                var obj = new SceneObject
                {
                    Id = id,
                    Type = type,
                    X = ctx.GetDouble("x"),
                    Y = ctx.GetDouble("y"),
                    W = Math.Max(8, ctx.GetDouble("w", 40)),
                    H = Math.Max(8, ctx.GetDouble("h", 40)),
                    Rotation = ctx.GetDouble("rotation", 0),
                    DirX = ctx.GetDouble("dirx", 0),
                    DirY = ctx.GetDouble("diry", 1),
                    InfluenceRadius = ctx.GetDouble("radius", 160),
                    StrengthG = ctx.GetDouble("strength", 10),
                    PatchJson = ctx.GetString("patch"),
                    Name = ctx.GetString("name"),
                };
                if (ctx.GetString("rotation") != null)
                    obj.SyncArrowDirFromRotation();
                else if (type == SceneObjectType.Arrow)
                    obj.SyncRotationFromArrowDir();

                // E0:放置点须在场景内区(界外是实体区,不可编辑)
                if (!world.ContainsPoint(obj.X, obj.Y)
                    && !world.ContainsPoint(obj.X + obj.W / 2, obj.Y + obj.H / 2))
                {
                    return CommandResult.Fail(
                        $"超出场景内区 {Fmt(world.WorldWidth)}×{Fmt(world.WorldHeight)};请点在边界内或先 scene.size");
                }

                world.Objects.Add(obj);
                world.SelectedId = id;
                world.NotifyChanged();
                return CommandResult.Ok($"已添加 {type} id={id}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "scene.remove",
            Summary = "删除场景对象(含异形实体)",
            Example = "scene.remove id=obj1",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "id", Description = "对象/异形 id", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var id = ctx.RequireString("id");
                var obj = world.FindObject(id);
                if (obj != null)
                {
                    world.Objects.Remove(obj);
                    if (string.Equals(world.SelectedId, id, StringComparison.OrdinalIgnoreCase))
                        world.SelectedId = null;
                    world.NotifyChanged();
                    return CommandResult.Ok($"已删除 {id}");
                }

                var solid = world.FindSolid(id);
                if (solid != null)
                {
                    world.Solids.Remove(solid);
                    if (string.Equals(world.SelectedSolidId, id, StringComparison.OrdinalIgnoreCase))
                        world.SelectedSolidId = null;
                    world.NotifyChanged();
                    return CommandResult.Ok($"已删除异形 {id}");
                }

                return CommandResult.Fail($"未找到对象 {id}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "scene.move",
            Summary = "移动场景对象",
            Example = "scene.move id=obj1 x=10 y=20",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "id", Description = "对象 id", Required = true },
                new ParameterSpec { Name = "x", Description = "左上 X", Type = ParamType.Double, Required = true },
                new ParameterSpec { Name = "y", Description = "左上 Y", Type = ParamType.Double, Required = true },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var obj = world.FindObject(ctx.RequireString("id"));
                if (obj == null)
                    return CommandResult.Fail("未找到对象");
                obj.X = ctx.GetDouble("x");
                obj.Y = ctx.GetDouble("y");
                world.NotifyChanged();
                return CommandResult.Ok($"已移动 {obj.Id}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "scene.set",
            Summary = "设置场景对象属性(含 rotation)",
            Example = "scene.set id=obj1 rotation=30 w=60",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "id", Description = "对象 id", Required = true },
                new ParameterSpec { Name = "x", Description = "X", Type = ParamType.Double },
                new ParameterSpec { Name = "y", Description = "Y", Type = ParamType.Double },
                new ParameterSpec { Name = "w", Description = "宽", Type = ParamType.Double },
                new ParameterSpec { Name = "h", Description = "高", Type = ParamType.Double },
                new ParameterSpec { Name = "rotation", Description = "旋转角(度)", Type = ParamType.Double },
                new ParameterSpec { Name = "dirx", Description = "方向 X", Type = ParamType.Double },
                new ParameterSpec { Name = "diry", Description = "方向 Y", Type = ParamType.Double },
                new ParameterSpec { Name = "radius", Description = "影响半径", Type = ParamType.Double },
                new ParameterSpec { Name = "strength", Description = "强度 g", Type = ParamType.Double },
                new ParameterSpec { Name = "patch", Description = "补丁 JSON" },
                new ParameterSpec { Name = "name", Description = "对象名;销毁器函数名" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var obj = world.FindObject(ctx.RequireString("id"));
                if (obj == null)
                    return CommandResult.Fail("未找到对象");
                if (ctx.GetString("x") != null) obj.X = ctx.GetDouble("x");
                if (ctx.GetString("y") != null) obj.Y = ctx.GetDouble("y");
                if (ctx.GetString("w") != null) obj.W = Math.Max(8, ctx.GetDouble("w"));
                if (ctx.GetString("h") != null) obj.H = Math.Max(8, ctx.GetDouble("h"));
                var rotSet = ctx.GetString("rotation") != null;
                var dirSet = ctx.GetString("dirx") != null || ctx.GetString("diry") != null;
                if (rotSet) obj.Rotation = ctx.GetDouble("rotation");
                if (ctx.GetString("dirx") != null) obj.DirX = ctx.GetDouble("dirx");
                if (ctx.GetString("diry") != null) obj.DirY = ctx.GetDouble("diry");
                if (ctx.GetString("radius") != null) obj.InfluenceRadius = ctx.GetDouble("radius");
                if (ctx.GetString("strength") != null) obj.StrengthG = ctx.GetDouble("strength");
                if (ctx.GetString("patch") != null) obj.PatchJson = ctx.GetString("patch");
                if (ctx.Has("name"))
                    obj.Name = string.IsNullOrWhiteSpace(ctx.GetString("name")) ? null : ctx.GetString("name")!.Trim();
                if (rotSet)
                    obj.SyncArrowDirFromRotation();
                else if (dirSet)
                    obj.SyncRotationFromArrowDir();
                world.NotifyChanged();
                return CommandResult.Ok($"已更新 {obj.Id}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "despawn.set",
            Summary = "设置销毁器函数名(等价 scene.set name=)",
            Example = "despawn.set id=obj1 name=X5",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "id", Description = "销毁器 id", Required = true },
                new ParameterSpec { Name = "name", Description = "函数名 Xn / RUN", Required = true },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var obj = world.FindObject(ctx.RequireString("id"));
                if (obj == null)
                    return CommandResult.Fail("未找到对象");
                if (obj.Type != SceneObjectType.Despawner)
                    return CommandResult.Fail($"对象 {obj.Id} 不是销毁器");
                obj.Name = ctx.RequireString("name").Trim();
                world.NotifyChanged();
                return CommandResult.Ok($"销毁器 {obj.Id} name={obj.Name}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "scene.list",
            Summary = "列出场景对象(block/arrow/… 与异形 solid 区分)",
            Example = "scene.list",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                if (world.Objects.Count == 0 && world.Solids.Count == 0)
                    return CommandResult.Ok("(空场景)");
                var lines = world.Objects.Select(o =>
                        $"{o.Id}\t{o.Type}\tx={Fmt(o.X)} y={Fmt(o.Y)} w={Fmt(o.W)} h={Fmt(o.H)} rot={Fmt(o.Rotation)}")
                    .Concat(world.Solids.Select(s =>
                    {
                        s.GetAabb(out var minX, out var minY, out var maxX, out var maxY);
                        return $"{s.Id}\tSolid\tpoints={s.Points.Count} tris={s.Triangles.Count}" +
                               $" x={Fmt(minX)} y={Fmt(minY)} w={Fmt(maxX - minX)} h={Fmt(maxY - minY)}";
                    }));
                return CommandResult.Ok(string.Join("\n", lines));
            }),
        });

    }

    private static SceneObjectType ParseType(string s) => s.ToLowerInvariant() switch
    {
        "block" => SceneObjectType.Block,
        "arrow" => SceneObjectType.Arrow,
        "spawner" => SceneObjectType.Spawner,
        "despawner" => SceneObjectType.Despawner,
        _ => throw new InvalidOperationException($"未知 type: {s}"),
    };

    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
