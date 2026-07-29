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
internal static class SceneFileCommands
{
    public static void Register(
        CommandRegistry registry,
        SceneWorld world,
        string scenesDirectory,
        ScenePropertyService sceneProperties)
    {
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

    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
