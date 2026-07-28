using System.Globalization;
using System.IO;
using System.Text.Json;
using AppShell.Core.Commands;
using AppShell.Core.Logging;
using WBall.Data;
using WBall.Game;
using WBall.Model;
using WBall.Sim;

namespace WBall.Commands;

/// <summary>WBall 指令: scene.* / ball.* / sim.* / formula.* / game.*(v3.4 取消数据库后 proj.* 一并移除)。</summary>
public static class WBallCommands
{
    public static void Register(
        CommandRegistry registry,
        SceneWorld world,
        IShellLog log,
        string scenesDirectory,
        ScenePropertyService sceneProperties,
        string? dataRoot = null)
    {
        Directory.CreateDirectory(scenesDirectory);
        RegisterScene(registry, world, scenesDirectory, sceneProperties);
        RegisterBall(registry, world, sceneProperties, log);
        RegisterSim(registry, world, log, sceneProperties);
        RegisterFormula(registry, world, dataRoot);
        RegisterGame(registry, world, log);
        WireCommands.Register(registry, world);
        SolidCommands.Register(registry, world);
        sceneProperties.RefreshScenes(scenesDirectory, world.LastScenePath);
    }

    private static void RegisterScene(
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

        registry.Register(new CommandDescriptor
        {
            Name = "scene.refresh",
            Summary = "刷新 scenes 管理表(扫描 workspace/scenes)",
            Example = "scene.refresh",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var n = sceneProperties.RefreshScenes(scenesDirectory, world.LastScenePath);
                return CommandResult.Ok($"已刷新 scenes 表: {n} 行");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "scene.rename",
            Summary = "重命名 scenes 目录中的场景文件并刷新表",
            Example = "scene.rename file=demo.json name=level1.json",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "file", Description = "原文件名", Required = true },
                new ParameterSpec { Name = "name", Description = "新文件名", Required = true },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    sceneProperties.RefreshScenes(scenesDirectory, world.LastScenePath);
                    sceneProperties.RenameSceneFile(ctx.RequireString("file"), ctx.RequireString("name"));
                    var n = sceneProperties.RefreshScenes(scenesDirectory, world.LastScenePath);
                    return CommandResult.Ok($"已重命名 → {ctx.RequireString("name")} (scenes={n})");
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "scene.clear",
            Summary = "清空全部场景对象",
            Example = "scene.clear",
            RequiresUiThread = true,
            ConfirmPrompt = _ => "确认清空落球区全部场景对象?",
            Handler = CommandDescriptor.Sync(_ =>
            {
                world.ClearObjects();
                return CommandResult.Ok("场景对象已清空");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "scene.new",
            Summary = "新建空场景(清除对象与小球,恢复默认仿真设置)",
            Example = "scene.new",
            RequiresUiThread = true,
            ConfirmPrompt = _ => "确认新建空场景?当前未保存内容将丢失",
            Handler = CommandDescriptor.Sync(_ =>
            {
                SceneStore.NewScene(world);
                // 阵营非场景权威;清空后重新种子蓝/红便于裁判区继续用
                if (world.Factions.Count == 0)
                {
                    world.Factions.Add(new Faction
                    {
                        Id = "blue",
                        Name = "蓝队",
                        Color = "#3B82F6",
                        InitialBalls = 3,
                        InitialMultiplier = 1,
                    });
                    world.Factions.Add(new Faction
                    {
                        Id = "red",
                        Name = "红队",
                        Color = "#EF4444",
                        InitialBalls = 3,
                        InitialMultiplier = 1,
                    });
                    world.NotifyChanged(markDirty: false);
                }

                sceneProperties.RefreshScenes(scenesDirectory, world.LastScenePath);
                return CommandResult.Ok("已新建空场景(已断开与原文件关联)");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "scene.save",
            Summary = "将 SceneWorld 保存为场景 JSON(文件←运行时;不经数据库)",
            Example = "scene.save file=demo.json",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "file",
                    Description = "文件名或相对 scenes/ 的路径;省略则写回最近路径",
                    Position = 0,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    var fileArg = ctx.GetString("file");
                    string path;
                    if (!string.IsNullOrWhiteSpace(fileArg))
                        path = ResolveScenePath(scenesDirectory, fileArg!, createDir: true);
                    else if (!string.IsNullOrWhiteSpace(world.LastScenePath))
                        path = world.LastScenePath!;
                    else
                        return CommandResult.Fail("请指定 file=,或先 load 过场景");

                    SceneStore.Save(world, path);
                    sceneProperties.RefreshScenes(scenesDirectory, world.LastScenePath);
                    return CommandResult.Ok($"已保存场景 → {path} (对象 {world.Objects.Count}, 球 {world.Balls.Count})");
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "scene.load",
            Summary = "从场景 JSON 加载到 SceneWorld(文件→运行时;不经数据库)",
            Example = "scene.load file=demo.json",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "file",
                    Description = "文件名或路径",
                    Required = true,
                    Position = 0,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    var path = ResolveScenePath(scenesDirectory, ctx.RequireString("file"), createDir: false);
                    SceneStore.Load(world, path);
                    var verify = SceneStore.DiffAgainstFile(world, path);
                    if (verify.Count > 0)
                        return CommandResult.Fail($"加载后核对失败(不应发生):\n" + string.Join("\n", verify));
                    sceneProperties.RefreshScenes(scenesDirectory, world.LastScenePath);
                    return CommandResult.Ok(
                        $"已加载并核对一致 ← {path} (对象 {world.Objects.Count}, 球 {world.Balls.Count}, 重力 {world.GravityG}g, 球碰 {(world.BallCollisionEnabled ? "开" : "关")})");
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "scene.files",
            Summary = "列出 workspace/scenes 下的场景文件",
            Example = "scene.files",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                Directory.CreateDirectory(scenesDirectory);
                var files = Directory.EnumerateFiles(scenesDirectory, "*.json")
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .Select(f => Path.GetFileName(f))
                    .ToList();
                if (files.Count == 0)
                    return CommandResult.Ok($"(无场景文件) 目录: {scenesDirectory}");
                return CommandResult.Ok(string.Join("\n", files) + $"\n目录: {scenesDirectory}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "scene.current",
            Summary = "显示当前场景路径与是否有未保存改动(验收 12)",
            Example = "scene.current",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var path = world.LastScenePath ?? "(尚未 save/load)";
                var dirty = world.SceneDirty ? "有未保存改动" : "与文件一致/无关联文件";
                return CommandResult.Ok($"path={path}\ndirty={world.SceneDirty} ({dirty})\n权威=文件→SceneWorld(不经数据库)");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "scene.verify",
            Summary = "核对运行时 SceneWorld 是否与场景文件一致(文件→SceneWorld)",
            Example = "scene.verify file=demo.json",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "file",
                    Description = "场景文件;省略则用最近 save/load 路径",
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    var fileArg = ctx.GetString("file");
                    string path;
                    if (!string.IsNullOrWhiteSpace(fileArg))
                        path = ResolveScenePath(scenesDirectory, fileArg!, createDir: false);
                    else if (!string.IsNullOrWhiteSpace(world.LastScenePath))
                        path = world.LastScenePath!;
                    else
                        return CommandResult.Fail("无文件可核对:请指定 file= 或先 scene.save/load");

                    var diffs = SceneStore.DiffAgainstFile(world, path);
                    if (diffs.Count == 0)
                        return CommandResult.Ok($"✓ 运行时与文件一致 ← {path} (对象 {world.Objects.Count}, 球 {world.Balls.Count})");
                    return CommandResult.Fail($"运行时与文件不一致 ({diffs.Count} 处):\n" + string.Join("\n", diffs));
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });
    }

    /// <summary>相对路径落在 scenesDirectory;绝对路径须为 .json。</summary>
    private static string ResolveScenePath(string scenesDirectory, string file, bool createDir)
    {
        var trimmed = file.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed))
            throw new InvalidOperationException("file 不能为空");

        string full;
        if (Path.IsPathRooted(trimmed))
        {
            full = Path.GetFullPath(trimmed);
        }
        else
        {
            if (!trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                trimmed += ".json";
            var root = Path.GetFullPath(scenesDirectory);
            full = Path.GetFullPath(Path.Combine(root, trimmed));
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("场景路径越出 scenes 目录");
        }

        if (!full.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("场景文件须为 .json");

        if (createDir)
        {
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }

        return full;
    }

    private static void RegisterBall(CommandRegistry registry, SceneWorld world, ScenePropertyService sceneProperties, IShellLog log)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "ball.spawn",
            Summary = "生成小球(位置仅在内存)",
            Example = "ball.spawn x=100 y=50 color=#3B82F6 size=12 weight=1",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "x", Description = "中心 X", Type = ParamType.Double },
                new ParameterSpec { Name = "y", Description = "中心 Y", Type = ParamType.Double },
                new ParameterSpec { Name = "spawner", Description = "生成器 id(优先于 x/y)" },
                new ParameterSpec { Name = "id", Description = "可选 id" },
                new ParameterSpec { Name = "color", Description = "颜色", Default = "#3B82F6" },
                new ParameterSpec { Name = "weight", Description = "重量(缺省按公式)", Type = ParamType.Double },
                new ParameterSpec { Name = "size", Description = "半径(缺省按公式)", Type = ParamType.Double },
                new ParameterSpec { Name = "multiplier", Description = "倍率(缺省 InitialMultiplier)" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                double x = 40, y = 40;
                var spawnerId = ctx.GetString("spawner");
                if (!string.IsNullOrEmpty(spawnerId))
                {
                    var sp = world.FindObject(spawnerId);
                    if (sp == null || sp.Type != SceneObjectType.Spawner)
                        return CommandResult.Fail($"生成器不存在: {spawnerId}");
                    x = sp.X + sp.W / 2;
                    y = sp.Y + sp.H / 2;
                }
                else if (ctx.GetString("x") != null && ctx.GetString("y") != null)
                {
                    x = ctx.GetDouble("x");
                    y = ctx.GetDouble("y");
                }
                else
                {
                    var first = world.OfType(SceneObjectType.Spawner).FirstOrDefault();
                    if (first != null)
                    {
                        x = first.X + first.W / 2;
                        y = first.Y + first.H / 2;
                    }
                }

                var id = ctx.GetString("id") ?? world.NextBallId();
                if (world.FindBall(id) != null)
                    return CommandResult.Fail($"小球 id 已存在: {id}");

                long mult = world.Defaults.InitialMultiplier;
                if (ctx.GetString("multiplier") != null
                    && long.TryParse(ctx.GetString("multiplier"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mParse))
                    mult = PublicDefaults.ClampMultiplier(mParse);

                var ball = new Ball
                {
                    Id = id,
                    X = x,
                    Y = y,
                    Color = ctx.GetString("color") ?? "#3B82F6",
                    Multiplier = PublicDefaults.ClampMultiplier(mult),
                };
                world.Defaults.ApplyToBall(ball);
                if (ctx.GetString("weight") != null)
                    ball.Weight = PublicDefaults.RoundWeight(ctx.GetDouble("weight"));
                if (ctx.GetString("size") != null)
                    ball.Size = PublicDefaults.RoundSize(ctx.GetDouble("size"));

                if (!string.IsNullOrEmpty(spawnerId))
                {
                    var sp = world.FindObject(spawnerId);
                    SceneWorld.ApplyPatch(ball, SceneWorld.ParsePatch(sp?.PatchJson));
                }

                world.Balls.Add(ball);
                world.NotifyChanged();
                return CommandResult.Ok($"已生成小球 {id} ×{ball.Multiplier} @({Fmt(x)},{Fmt(y)})");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "ball.recalc",
            Summary = "按当前公式重算全部球的 size/weight(取整)",
            Example = "ball.recalc",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                foreach (var ball in world.Balls)
                    world.Defaults.ApplyToBall(ball);
                world.NotifyChanged();
                return CommandResult.Ok($"已重算 {world.Balls.Count} 个球");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "ball.despawn",
            Summary = "销毁/传送小球(有生成器则同 id 传送,无则移除)",
            Example = "ball.despawn id=ball1",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "id", Description = "小球 id", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var ball = world.FindBall(ctx.RequireString("id"));
                if (ball == null)
                    return CommandResult.Fail("未找到小球");

                var sink = world.OfType(SceneObjectType.Despawner).FirstOrDefault();
                if (sink == null)
                {
                    var spawners = world.OfType(SceneObjectType.Spawner).ToList();
                    if (spawners.Count == 0)
                    {
                        if (string.Equals(world.SelectedBallId, ball.Id, StringComparison.OrdinalIgnoreCase))
                            world.SelectedBallId = null;
                        world.Balls.Remove(ball);
                        world.NotifyChanged();
                        return CommandResult.Ok($"小球 {ball.Id} 已移除(无销毁器/生成器)");
                    }

                    // 无销毁器时仅传送到生成器
                    var sp0 = spawners[world.Rng.Next(spawners.Count)];
                    SceneWorld.ApplyPatch(ball, SceneWorld.ParsePatch(sp0.PatchJson));
                    ball.X = sp0.X + sp0.W / 2;
                    ball.Y = sp0.Y + sp0.H / 2;
                    ball.Vx = ball.Vy = 0;
                    world.NotifyChanged();
                    return CommandResult.Ok($"小球 {ball.Id} 已传送至生成器 {sp0.Id}");
                }

                PhysicsEngine.TeleportBall(world, ball, sink, msg => log.Info("ball", msg));
                // TeleportBall may have removed the ball
                if (world.FindBall(ball.Id) == null)
                    return CommandResult.Ok($"小球 {ball.Id} 已移除(无生成器)");
                return CommandResult.Ok($"小球 {ball.Id} 已传送 ×{ball.Multiplier}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "ball.set",
            Summary = "设置小球性质(不写位置到库;位置仅内存)",
            Example = "ball.set id=ball1 color=#FF0000 multiplier=5",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "id", Description = "小球 id", Required = true },
                new ParameterSpec { Name = "color", Description = "颜色" },
                new ParameterSpec { Name = "weight", Description = "重量", Type = ParamType.Double },
                new ParameterSpec { Name = "size", Description = "半径", Type = ParamType.Double },
                new ParameterSpec { Name = "multiplier", Description = "倍率(变时按公式重算 size/weight)" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var ball = world.FindBall(ctx.RequireString("id"));
                if (ball == null)
                    return CommandResult.Fail("未找到小球");
                if (ctx.GetString("color") != null)
                    ball.Color = ctx.GetString("color")!;
                var multSet = false;
                if (ctx.GetString("multiplier") != null
                    && long.TryParse(ctx.GetString("multiplier"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m))
                {
                    ball.Multiplier = PublicDefaults.ClampMultiplier(m);
                    multSet = true;
                }

                if (multSet)
                    world.Defaults.ApplyToBall(ball);
                if (ctx.GetString("weight") != null)
                    ball.Weight = PublicDefaults.RoundWeight(ctx.GetDouble("weight"));
                if (ctx.GetString("size") != null)
                    ball.Size = PublicDefaults.RoundSize(ctx.GetDouble("size"));
                world.NotifyChanged();
                return CommandResult.Ok($"已更新小球 {ball.Id} ×{ball.Multiplier}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "ball.list",
            Summary = "列出小球(含内存位置,仅控制台)",
            Example = "ball.list",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                if (world.Balls.Count == 0)
                    return CommandResult.Ok("(无小球)");
                var lines = world.Balls.Select(b =>
                    $"{b.Id}\tcolor={b.Color}\t×{b.Multiplier}\tweight={Fmt(b.Weight)}\tsize={Fmt(b.Size)}\t@({Fmt(b.X)},{Fmt(b.Y)})");
                return CommandResult.Ok(string.Join("\n", lines));
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "ball.addprop",
            Summary = "为小球登记自定义性质(不可为 color/weight/size/x/y)",
            Example = "ball.addprop name=team",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "性质名", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    var name = sceneProperties.AddProperty(ctx.RequireString("name"));
                    return CommandResult.Ok($"已登记自定义性质 {name}");
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "ball.removeprop",
            Summary = "删除小球自定义性质(拒绝删除内建性质)",
            Example = "ball.removeprop name=team",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "性质名", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    sceneProperties.RemoveProperty(ctx.RequireString("name"));
                    return CommandResult.Ok("已删除自定义性质");
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "ball.setprop",
            Summary = "设置小球自定义性质值(直接写 SceneWorld)",
            Example = "ball.setprop id=ball1 name=team value=red",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "id", Description = "小球 id", Required = true },
                new ParameterSpec { Name = "name", Description = "列名", Required = true },
                new ParameterSpec { Name = "value", Description = "值", Required = true },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var ball = world.FindBall(ctx.RequireString("id"));
                if (ball == null)
                    return CommandResult.Fail("未找到小球");
                var name = ctx.RequireString("name");
                if (name.Equals("color", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("weight", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("size", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("multiplier", StringComparison.OrdinalIgnoreCase))
                    return CommandResult.Fail("内建性质请用 ball.set");
                if (!sceneProperties.CustomProperties.Contains(name, StringComparer.OrdinalIgnoreCase))
                    return CommandResult.Fail($"自定义性质不存在,请先 ball.addprop name={name}");
                ball.Props[name] = ctx.RequireString("value");
                world.NotifyChanged();
                return CommandResult.Ok($"已设置 {ball.Id}.{name}");
            }),
        });
    }

    private static void RegisterFormula(CommandRegistry registry, SceneWorld world, string? dataRoot)
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
                    "(编辑入口: win.show name=ballpanel; 参数支持小数)");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "formula.set",
            Summary = "设置小球公式参数并持久化(不自动重算场上球)",
            Example = "formula.set sizebase=6 sizescale=6 weightbase=1 weightscale=1 initial=1",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "sizebase", Description = "SizeBase", Type = ParamType.Double },
                new ParameterSpec { Name = "sizescale", Description = "SizeScale", Type = ParamType.Double },
                new ParameterSpec { Name = "weightbase", Description = "WeightBase", Type = ParamType.Double },
                new ParameterSpec { Name = "weightscale", Description = "WeightScale", Type = ParamType.Double },
                new ParameterSpec { Name = "initial", Description = "InitialMultiplier" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var d = world.Defaults;
                if (ctx.GetString("sizebase") != null) d.SizeBase = ctx.GetDouble("sizebase");
                if (ctx.GetString("sizescale") != null) d.SizeScale = ctx.GetDouble("sizescale");
                if (ctx.GetString("weightbase") != null) d.WeightBase = ctx.GetDouble("weightbase");
                if (ctx.GetString("weightscale") != null) d.WeightScale = ctx.GetDouble("weightscale");
                if (ctx.GetString("initial") != null
                    && long.TryParse(ctx.GetString("initial"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var init))
                    d.InitialMultiplier = PublicDefaults.ClampMultiplier(init);

                if (!string.IsNullOrWhiteSpace(dataRoot))
                    PublicDefaultsStore.Save(dataRoot, d);

                world.NotifyChanged(markDirty: false);
                return CommandResult.Ok(
                    $"已更新公式 sizeBase={Fmt(d.SizeBase)} sizeScale={Fmt(d.SizeScale)} " +
                    $"weightBase={Fmt(d.WeightBase)} weightScale={Fmt(d.WeightScale)} initial={d.InitialMultiplier}");
            }),
        });
    }

    private static void RegisterGame(CommandRegistry registry, SceneWorld world, IShellLog log)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "game.start",
            Summary = "按阵营 initial_balls / color / initial_multiplier 开局吐球",
            Example = "game.start",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var spawners = world.OfType(SceneObjectType.Spawner).ToList();
                if (spawners.Count == 0)
                    return CommandResult.Fail("场景中无生成器,无法开局");

                var factions = world.Factions
                    .Where(f => !f.Id.Equals(FactionBoard.UnassignedId, StringComparison.OrdinalIgnoreCase))
                    .Where(f => f.Alive)
                    .Where(f => f.InitialBalls > 0)
                    .ToList();
                if (factions.Count == 0)
                    return CommandResult.Fail("无有效阵营(请先配置 factions 的 initial_balls)");

                var spawned = 0;
                foreach (var f in factions)
                {
                    for (var i = 0; i < f.InitialBalls; i++)
                    {
                        var sp = spawners[world.Rng.Next(spawners.Count)];
                        var ball = new Ball
                        {
                            Id = world.NextBallId(),
                            X = sp.X + sp.W / 2,
                            Y = sp.Y + sp.H / 2,
                            Color = f.Color,
                            Multiplier = PublicDefaults.ClampMultiplier(f.InitialMultiplier),
                        };
                        world.Defaults.ApplyToBall(ball);
                        SceneWorld.ApplyPatch(ball, SceneWorld.ParsePatch(sp.PatchJson));
                        world.Balls.Add(ball);
                        spawned++;
                    }
                }

                world.IsPlaying = true;
                world.NotifyChanged();
                log.Info("game", $"game.start 吐球 {spawned}");
                return CommandResult.Ok($"开局完成: 吐球 {spawned}, 仿真已播放");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "game.resetscore",
            Summary = "重置所有阵营积分为 0",
            Example = "game.resetscore",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                foreach (var f in world.Factions)
                    f.Score = 0;
                world.NotifyChanged(markDirty: false);
                return CommandResult.Ok("已重置全部阵营积分");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "faction.list",
            Summary = "列出阵营与积分",
            Example = "faction.list",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                if (world.Factions.Count == 0)
                    return CommandResult.Ok("(无阵营)");
                var lines = world.Factions.Select(f =>
                    $"{f.Id}\t{f.Name}\tcolor={f.Color}\tballs={f.InitialBalls}\t×{f.InitialMultiplier}\tscore={f.Score}");
                return CommandResult.Ok(string.Join("\n", lines));
            }),
        });
    }

    private static void RegisterSim(CommandRegistry registry, SceneWorld world, IShellLog log, ScenePropertyService sceneProperties)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "sim.play",
            Summary = "开始仿真",
            Example = "sim.play",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                world.IsPlaying = true;
                world.NotifyChanged(markDirty: false, visual: true, project: false);
                return CommandResult.Ok("仿真播放");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "sim.pause",
            Summary = "暂停仿真",
            Example = "sim.pause",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                world.IsPlaying = false;
                world.NotifyChanged(markDirty: false, visual: true, project: false);
                return CommandResult.Ok("仿真暂停");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "sim.reset",
            Summary = "重置仿真(清除运行时小球)",
            Example = "sim.reset",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                world.ResetSimulation();
                return CommandResult.Ok("仿真已重置");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "sim.seed",
            Summary = "设置随机种子(影响随机生成器选择)",
            Example = "sim.seed value=42",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "value", Description = "种子", Type = ParamType.Int, Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                world.Seed = ctx.GetInt("value");
                return CommandResult.Ok($"随机种子={world.Seed}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "sim.ballcollision",
            Summary = "球-球碰撞开关(v1.1)",
            Example = "sim.ballcollision on=true",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "on",
                    Description = "true/false",
                    Type = ParamType.Bool,
                    Required = true,
                    Position = 0,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                world.BallCollisionEnabled = ctx.GetBool("on");
                world.NotifyChanged();
                return CommandResult.Ok($"球-球碰撞: {(world.BallCollisionEnabled ? "开" : "关")}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "sim.trail",
            Summary = "小球尾迹总开关(v1.5.2)",
            Example = "sim.trail on=true",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "on",
                    Description = "true/false",
                    Type = ParamType.Bool,
                    Required = true,
                    Position = 0,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                world.TrailEnabled = ctx.GetBool("on");
                if (!world.TrailEnabled)
                    world.ClearBallTrails();
                world.NotifyChanged(markDirty: false);
                return CommandResult.Ok($"尾迹: {(world.TrailEnabled ? "开" : "关")}");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "sim.trailset",
            Summary = "设置尾迹长度与传送残影时长(v1.5.2)",
            Example = "sim.trailset length=24 fade=0.35",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "length", Description = "轨迹点数上限", Type = ParamType.Int },
                new ParameterSpec { Name = "fade", Description = "传送残影秒数", Type = ParamType.Double },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (ctx.GetString("length") != null)
                    world.TrailLength = Math.Clamp(ctx.GetInt("length"), 2, 200);
                if (ctx.GetString("fade") != null)
                    world.TeleportFlashSeconds = Math.Clamp(ctx.GetDouble("fade"), 0, 5);
                world.NotifyChanged(markDirty: false);
                return CommandResult.Ok(
                    $"尾迹 length={world.TrailLength} fade={world.TeleportFlashSeconds:0.###}s");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "sim.gravity",
            Summary = "查看/设置全局重力倍数(默认 10g 向下)",
            Example = "sim.gravity g=10",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "g", Description = "重力倍数", Type = ParamType.Double },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (ctx.GetString("g") != null)
                    world.GravityG = Math.Max(0, ctx.GetDouble("g"));
                return CommandResult.Ok($"全局重力={world.GravityG}g 向下(+Y), 1g={SceneWorld.GUnit}px/s²");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "sim.status",
            Summary = "仿真状态摘要",
            Example = "sim.status",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var json = JsonSerializer.Serialize(new
                {
                    playing = world.IsPlaying,
                    gravityG = world.GravityG,
                    ballCollision = world.BallCollisionEnabled,
                    trail = world.TrailEnabled,
                    trailLength = world.TrailLength,
                    teleportFlash = world.TeleportFlashSeconds,
                    worldWidth = world.WorldWidth,
                    worldHeight = world.WorldHeight,
                    objects = world.Objects.Count,
                    solids = world.Solids.Count,
                    balls = world.Balls.Count,
                    tool = world.Tool.ToString(),
                    seed = world.Seed,
                });
                return CommandResult.Ok(json);
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
