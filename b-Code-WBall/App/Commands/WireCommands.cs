using System.Globalization;
using AppShell.Core.Commands;
using WBall.Geometry;
using WBall.Model;
using WBall.Wire;

namespace WBall.Commands;

/// <summary>线框草图 / 提交线框 / 实体化指令(v1.5)。</summary>
public static class WireCommands
{
    public static void Register(CommandRegistry registry, SceneWorld world)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "wire.point",
            Summary = "在草图上追加顶点",
            Example = "wire.point x=100 y=200",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "x", Description = "X", Type = ParamType.Double, Required = true },
                new ParameterSpec { Name = "y", Description = "Y", Type = ParamType.Double, Required = true },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (world.IsPlaying)
                    return CommandResult.Fail("仿真中不可编辑草图");
                var x = ctx.GetDouble("x");
                var y = ctx.GetDouble("y");
                if (!world.ContainsPoint(x, y))
                    return CommandResult.Fail("超出场景内区,无法加点");

                // 近起点闭合
                if (world.Sketch.Points.Count >= 3)
                {
                    var s = world.Sketch.Points[0];
                    var dx = x - s.X;
                    var dy = y - s.Y;
                    if (Math.Sqrt(dx * dx + dy * dy) <= world.Sketch.CloseRadius)
                        return CloseSketch(world);
                }

                world.Sketch.Points.Add(new WirePoint(x, y));
                world.Tool = EditorTool.Wire;
                world.NotifyChanged();
                return CommandResult.Ok($"草图加点 #{world.Sketch.Points.Count} ({Fmt(x)},{Fmt(y)})");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "wire.undo",
            Summary = "撤销草图上一点",
            Example = "wire.undo",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                if (world.Sketch.Points.Count == 0)
                    return CommandResult.Fail("草图为空");
                world.Sketch.Points.RemoveAt(world.Sketch.Points.Count - 1);
                world.NotifyChanged();
                return CommandResult.Ok($"已撤销,剩余 {world.Sketch.Points.Count} 点");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "wire.clear",
            Summary = "清空当前草图",
            Example = "wire.clear",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                world.Sketch.Clear();
                world.NotifyChanged();
                return CommandResult.Ok("草图已清空");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "wire.close",
            Summary = "闭合草图并提交为线框(进 wireframes)",
            Example = "wire.close",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ => CloseSketch(world)),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "wire.list",
            Summary = "列出已提交线框",
            Example = "wire.list",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var sketch = world.Sketch.IsEmpty
                    ? "草图:(空)"
                    : $"草图: {world.Sketch.Points.Count} 点(未提交)";
                if (world.Wireframes.Count == 0)
                    return CommandResult.Ok(sketch + "\n(无线框)");
                var lines = world.Wireframes.Select(w =>
                    $"{w.Id}\tpoints={w.Points.Count}\tclosed={w.Closed}" +
                    (string.Equals(w.Id, world.SelectedWireId, StringComparison.OrdinalIgnoreCase) ? "\t*" : ""));
                return CommandResult.Ok(sketch + "\n" + string.Join("\n", lines));
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "wire.select",
            Summary = "选中已提交线框",
            Example = "wire.select id=wire1",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "id", Description = "线框 id", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var id = ctx.RequireString("id");
                var w = world.FindWire(id);
                if (w == null)
                    return CommandResult.Fail($"无线框 {id}");
                world.SelectedWireId = w.Id;
                world.SelectedId = null;
                world.NotifyChanged();
                return CommandResult.Ok($"已选中线框 {w.Id}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "wire.remove",
            Summary = "删除已提交线框",
            Example = "wire.remove id=wire1",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "id", Description = "线框 id", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var id = ctx.RequireString("id");
                var w = world.FindWire(id);
                if (w == null)
                    return CommandResult.Fail($"无线框 {id}");
                world.Wireframes.Remove(w);
                if (string.Equals(world.SelectedWireId, id, StringComparison.OrdinalIgnoreCase))
                    world.SelectedWireId = null;
                world.NotifyChanged();
                return CommandResult.Ok($"已删除线框 {id}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "wire.solidify",
            Summary = "将已提交线框实体化为一个异形实体(三角拼接),并删除该线框",
            Example = "wire.solidify id=wire1",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "id", Description = "线框 id;省略则用选中线框" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (world.IsPlaying)
                    return CommandResult.Fail("仿真中不可实体化");

                var id = ctx.GetString("id") ?? world.SelectedWireId;
                if (string.IsNullOrWhiteSpace(id))
                    return CommandResult.Fail("请指定 id= 或先选中线框;草图须先 wire.close");

                var wire = world.FindWire(id!);
                if (wire == null)
                    return CommandResult.Fail($"无线框 {id}");
                if (!wire.Closed)
                    return CommandResult.Fail("线框未闭合,无法实体化");

                try
                {
                    // v1.5.1 MS-01:恰好 1 个异形实体;不再产出多个矩形 block
                    var solid = WireSolidifier.Solidify(wire, world.NextSolidId);
                    world.Solids.Add(solid);

                    world.Wireframes.Remove(wire);
                    world.Sketch.Clear();
                    world.SelectedWireId = null;
                    world.SelectedId = null;
                    world.SelectedSolidId = solid.Id;
                    world.NotifyChanged();
                    return CommandResult.Ok(
                        $"已实体化 {wire.Id} → 异形 {solid.Id} (顶点 {solid.Points.Count}, 三角形 {solid.Triangles.Count})");
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "wire.setsnap",
            Summary = "设置草图近起点闭合半径(非网格吸附)",
            Example = "wire.setsnap r=12",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "r", Description = "近合半径", Type = ParamType.Double, Required = true },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var r = Math.Clamp(ctx.GetDouble("r"), 4, 80);
                world.Sketch.CloseRadius = r;
                world.NotifyChanged(markDirty: false);
                return CommandResult.Ok($"近合半径={Fmt(r)}");
            }),
        });
    }

    private static CommandResult CloseSketch(SceneWorld world)
    {
        if (world.Sketch.Points.Count < 3)
            return CommandResult.Fail("至少 3 点才能闭合提交");

        var pts = world.Sketch.Points.Select(p => (p.X, p.Y)).ToList();
        pts = PolygonMath.MergeClosePoints(pts);
        if (pts.Count < 3)
            return CommandResult.Fail("合并近点后不足 3 点");

        // 允许自交线框存在于表中,但实体化时拒绝(V5Q4)
        var id = world.NextWireId();
        var wire = new Wireframe
        {
            Id = id,
            Closed = true,
            Points = pts.Select(p => new WirePoint(p.X, p.Y)).ToList(),
        };
        world.Wireframes.Add(wire);
        world.Sketch.Clear();
        world.SelectedWireId = id;
        world.SelectedId = null;
        world.NotifyChanged();

        var warn = PolygonMath.IsSelfIntersecting(pts) ? " (警告:自交,实体化将被拒绝)" : "";
        return CommandResult.Ok($"已闭合提交线框 {id}, {pts.Count} 点{warn}");
    }

    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
