using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AppShell.Core.Data;
using AppShell.Services;
using WBall.Game;
using WBall.Model;

namespace WBall.Data;

/// <summary>
/// 性质投影表:SQLite 只镜像性质;权威在 SceneWorld。
/// balls 无 x/y/vx/vy;表改经本类打进 SceneWorld 再刷投影。
/// scenes 为文件管理表(非运行时权威);refresh 扫描磁盘。
/// v1.6.2:播放中不自动写库;冷路径事务化全量同步。
/// </summary>
public sealed class PropertyProjection : IDisposable
{
    public const string BallsTable = "balls";
    public const string ObjectsTable = "scene_objects";
    public const string ScenesTable = "scenes";
    public const string WireframesTable = "wireframes";
    public const string SolidsTable = "solids";
    public const string FactionsTable = "factions";

    private static readonly HashSet<string> RequiredBallColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "alive", "color", "weight", "size", "multiplier",
    };

    private static readonly HashSet<string> ForbiddenBallColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "x", "y", "vx", "vy",
    };

    private readonly SqliteDataService _db;
    private readonly SceneWorld _world;
    private readonly object _gate = new();
    private readonly List<string> _customBallColumns = new();
    private readonly System.Windows.Threading.DispatcherTimer _debounce;
    private bool _syncing;
    private bool _disposed;
    private bool _projectionPending;
    private string? _scenesDirectory;
    private string? _dataRoot;

    public SceneWorld World => _world;

    /// <summary>是否有未刷入 SQLite 的投影脏数据。</summary>
    public bool IsProjectionPending => _projectionPending;

    public PropertyProjection(SqliteDataService db, SceneWorld world, string? dataRoot = null)
    {
        _db = db;
        _world = world;
        _dataRoot = dataRoot;
        EnsureSchema();
        _world.ProjectionDirty += OnProjectionDirty;
        _debounce = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(600),
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            SyncFromWorld(force: true);
        };
        SyncFromWorld(force: true);
    }

    public IReadOnlyList<string> CustomBallColumns
    {
        get
        {
            lock (_gate)
            {
                return _customBallColumns.ToList();
            }
        }
    }

    public bool IsManaged(string table)
        => table.Equals(BallsTable, StringComparison.OrdinalIgnoreCase)
           || table.Equals(ObjectsTable, StringComparison.OrdinalIgnoreCase)
           || table.Equals(ScenesTable, StringComparison.OrdinalIgnoreCase)
           || table.Equals(WireframesTable, StringComparison.OrdinalIgnoreCase)
           || table.Equals(SolidsTable, StringComparison.OrdinalIgnoreCase)
           || table.Equals(FactionsTable, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _world.ProjectionDirty -= OnProjectionDirty;
        _debounce.Stop();
    }

    private void OnProjectionDirty()
    {
        if (_syncing)
            return;
        _projectionPending = true;
        // 播放中绝不写库,暂停后再刷
        if (_world.IsPlaying)
            return;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>暂停仿真或手动同步时调用:强制把内存刷进投影表。</summary>
    public void SyncFromWorld(bool force = false)
    {
        if (_syncing)
            return;
        if (!force && _world.IsPlaying)
        {
            _projectionPending = true;
            return;
        }

        _syncing = true;
        try
        {
            EnsureSchema();
            var customs = CustomBallColumns;
            var sql = new StringBuilder(Math.Max(1024, 128 * (_world.Balls.Count + _world.Objects.Count + 8)));
            sql.AppendLine("BEGIN;");
            sql.AppendLine("DELETE FROM balls;");
            foreach (var ball in _world.Balls)
            {
                var cols = new List<string> { "id", "alive", "color", "weight", "size", "multiplier" };
                var vals = new List<string>
                {
                    SqlString(ball.Id),
                    "1",
                    SqlString(ball.Color),
                    ball.Weight.ToString(CultureInfo.InvariantCulture),
                    ball.Size.ToString(CultureInfo.InvariantCulture),
                    ball.Multiplier.ToString(CultureInfo.InvariantCulture),
                };
                foreach (var c in customs)
                {
                    cols.Add(c);
                    ball.Props.TryGetValue(c, out var pv);
                    vals.Add(SqlString(pv ?? ""));
                }

                sql.Append("INSERT INTO balls (")
                    .Append(string.Join(",", cols))
                    .Append(") VALUES (")
                    .Append(string.Join(",", vals))
                    .AppendLine(");");
            }

            sql.AppendLine("DELETE FROM scene_objects;");
            foreach (var o in _world.Objects)
            {
                sql.AppendLine(
                    $"""
                    INSERT INTO scene_objects
                    (id,type,x,y,w,h,rotation,dir_x,dir_y,influence_radius,strength_g,patch_json,name)
                    VALUES (
                      {SqlString(o.Id)},
                      {SqlString(o.Type.ToString().ToLowerInvariant())},
                      {F(o.X)},{F(o.Y)},{F(o.W)},{F(o.H)},{F(o.Rotation)},
                      {F(o.DirX)},{F(o.DirY)},{F(o.InfluenceRadius)},{F(o.StrengthG)},
                      {SqlString(o.PatchJson)},
                      {SqlString(o.Name)}
                    );
                    """);
            }

            sql.AppendLine("DELETE FROM wireframes;");
            foreach (var w in _world.Wireframes)
            {
                var ptsJson = "[" + string.Join(",",
                    w.Points.Select(p =>
                        $"{{\"x\":{F(p.X)},\"y\":{F(p.Y)}}}")) + "]";
                sql.AppendLine(
                    $"""
                    INSERT INTO wireframes (id,closed,point_count,points_json)
                    VALUES (
                      {SqlString(w.Id)},
                      {(w.Closed ? 1 : 0)},
                      {w.Points.Count},
                      {SqlString(ptsJson)}
                    );
                    """);
            }

            sql.AppendLine("DELETE FROM solids;");
            foreach (var s in _world.Solids)
            {
                var ptsJson = "[" + string.Join(",",
                    s.Points.Select(p =>
                        $"{{\"x\":{F(p.X)},\"y\":{F(p.Y)}}}")) + "]";
                sql.AppendLine(
                    $"""
                    INSERT INTO solids (id,point_count,tri_count,points_json,color)
                    VALUES (
                      {SqlString(s.Id)},
                      {s.Points.Count},
                      {s.Triangles.Count},
                      {SqlString(ptsJson)},
                      {SqlString(s.Color)}
                    );
                    """);
            }

            sql.AppendLine("DELETE FROM factions;");
            foreach (var f in _world.Factions)
            {
                sql.AppendLine(
                    $"""
                    INSERT INTO factions (id,name,color,initial_balls,initial_multiplier,score)
                    VALUES (
                      {SqlString(f.Id)},
                      {SqlString(f.Name)},
                      {SqlString(f.Color)},
                      {f.InitialBalls},
                      {f.InitialMultiplier},
                      {f.Score}
                    );
                    """);
            }

            sql.AppendLine("COMMIT;");
            _db.ExecuteSql(sql.ToString());

            if (!string.IsNullOrWhiteSpace(_scenesDirectory))
                UpdateScenesCurrentFlags(_world.LastScenePath);

            _projectionPending = false;
        }
        catch
        {
            try { _db.ExecuteSql("ROLLBACK;"); } catch { /* ignore */ }
            throw;
        }
        finally
        {
            _syncing = false;
        }
    }

    public void EnsureSchema()
    {
        _db.ExecuteSql(
            """
            CREATE TABLE IF NOT EXISTS balls (
                id TEXT PRIMARY KEY NOT NULL,
                alive INTEGER NOT NULL DEFAULT 1,
                color TEXT NOT NULL DEFAULT '#3B82F6',
                weight REAL NOT NULL DEFAULT 1,
                size REAL NOT NULL DEFAULT 12,
                multiplier INTEGER NOT NULL DEFAULT 1
            );
            """);
        EnsureColumn(BallsTable, "multiplier", "INTEGER NOT NULL DEFAULT 1");

        _db.ExecuteSql(
            """
            CREATE TABLE IF NOT EXISTS scene_objects (
                id TEXT PRIMARY KEY NOT NULL,
                type TEXT NOT NULL,
                x REAL NOT NULL DEFAULT 0,
                y REAL NOT NULL DEFAULT 0,
                w REAL NOT NULL DEFAULT 40,
                h REAL NOT NULL DEFAULT 40,
                rotation REAL NOT NULL DEFAULT 0,
                dir_x REAL NOT NULL DEFAULT 0,
                dir_y REAL NOT NULL DEFAULT 1,
                influence_radius REAL NOT NULL DEFAULT 160,
                strength_g REAL NOT NULL DEFAULT 10,
                patch_json TEXT,
                name TEXT
            );
            """);
        EnsureColumn(ObjectsTable, "rotation", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(ObjectsTable, "name", "TEXT");

        _db.ExecuteSql(
            """
            CREATE TABLE IF NOT EXISTS scenes (
                name TEXT PRIMARY KEY NOT NULL,
                path TEXT NOT NULL,
                width REAL NOT NULL DEFAULT 800,
                height REAL NOT NULL DEFAULT 600,
                object_count INTEGER NOT NULL DEFAULT 0,
                ball_count INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT,
                is_current INTEGER NOT NULL DEFAULT 0
            );
            """);

        _db.ExecuteSql(
            """
            CREATE TABLE IF NOT EXISTS wireframes (
                id TEXT PRIMARY KEY NOT NULL,
                closed INTEGER NOT NULL DEFAULT 1,
                point_count INTEGER NOT NULL DEFAULT 0,
                points_json TEXT
            );
            """);

        // 异形实体摘要(BR-04):细顶点以快照+画布为准
        _db.ExecuteSql(
            """
            CREATE TABLE IF NOT EXISTS solids (
                id TEXT PRIMARY KEY NOT NULL,
                point_count INTEGER NOT NULL DEFAULT 0,
                tri_count INTEGER NOT NULL DEFAULT 0,
                points_json TEXT,
                color TEXT NOT NULL DEFAULT '#64748B'
            );
            """);
        EnsureColumn(SolidsTable, "color", "TEXT NOT NULL DEFAULT '#64748B'");

        _db.ExecuteSql(
            """
            CREATE TABLE IF NOT EXISTS factions (
                id TEXT PRIMARY KEY NOT NULL,
                name TEXT NOT NULL DEFAULT '',
                color TEXT NOT NULL DEFAULT '#3B82F6',
                initial_balls INTEGER NOT NULL DEFAULT 3,
                initial_multiplier INTEGER NOT NULL DEFAULT 1,
                score INTEGER NOT NULL DEFAULT 0
            );
            """);

        lock (_gate)
        {
            _customBallColumns.Clear();
            foreach (var col in _db.GetSchema(BallsTable))
            {
                if (RequiredBallColumns.Contains(col.Name) || ForbiddenBallColumns.Contains(col.Name))
                    continue;
                _customBallColumns.Add(col.Name);
            }
        }

        DropForbiddenBallColumns();
    }

    private void EnsureColumn(string table, string name, string decl)
    {
        var exists = _db.GetSchema(table)
            .Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (!exists)
            _db.ExecuteSql($"ALTER TABLE {table} ADD COLUMN {QuoteIdent(name)} {decl}");
    }

    private void DropForbiddenBallColumns()
    {
        foreach (var col in _db.GetSchema(BallsTable))
        {
            if (!ForbiddenBallColumns.Contains(col.Name))
                continue;
            try
            {
                _db.ExecuteSql($"ALTER TABLE balls DROP COLUMN {QuoteIdent(col.Name)}");
            }
            catch
            {
                // 旧 SQLite 不支持 DROP COLUMN 时忽略;Sync 也不会写入这些列
            }
        }
    }

    /// <summary>扫描 scenes 目录并重写 scenes 表。</summary>
    public int RefreshScenes(string scenesDirectory, string? currentScenePath = null)
    {
        _scenesDirectory = scenesDirectory;
        Directory.CreateDirectory(scenesDirectory);
        EnsureSchema();
        _db.ExecuteSql("DELETE FROM scenes");

        var currentFull = string.IsNullOrWhiteSpace(currentScenePath)
            ? null
            : Path.GetFullPath(currentScenePath);
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(scenesDirectory, "*.json")
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var full = Path.GetFullPath(file);
            var name = Path.GetFileName(full);
            var rel = Path.GetRelativePath(scenesDirectory, full).Replace('\\', '/');
            SceneStore.TryPeekMeta(full, out var w, out var h, out var oc, out var bc);
            var updated = File.GetLastWriteTime(full).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var isCurrent = currentFull != null
                            && full.Equals(currentFull, StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;
            _db.ExecuteSql(
                $"""
                INSERT INTO scenes (name,path,width,height,object_count,ball_count,updated_at,is_current)
                VALUES (
                  {SqlString(name)},
                  {SqlString(rel)},
                  {F(w)},{F(h)},
                  {oc},{bc},
                  {SqlString(updated)},
                  {isCurrent}
                )
                """);
            count++;
        }

        return count;
    }

    private void UpdateScenesCurrentFlags(string? currentPath)
    {
        try
        {
            _db.ExecuteSql("UPDATE scenes SET is_current=0");
            if (string.IsNullOrWhiteSpace(currentPath) || string.IsNullOrWhiteSpace(_scenesDirectory))
                return;
            var name = Path.GetFileName(currentPath);
            _db.ExecuteSql($"UPDATE scenes SET is_current=1 WHERE name={SqlString(name)}");
        }
        catch
        {
            // 表可能尚未 refresh
        }
    }

    public string AddBallColumn(string name, string sqlType = "TEXT")
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new InvalidOperationException("列名须为字母/数字/下划线,且不以数字开头");
        if (RequiredBallColumns.Contains(name) || ForbiddenBallColumns.Contains(name))
            throw new InvalidOperationException($"不能添加保留列: {name}");
        if (CustomBallColumns.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"列已存在: {name}");

        var type = sqlType.Trim().ToUpperInvariant() switch
        {
            "INT" or "INTEGER" => "INTEGER",
            "REAL" or "FLOAT" or "DOUBLE" => "REAL",
            _ => "TEXT",
        };
        _db.ExecuteSql($"ALTER TABLE balls ADD COLUMN {QuoteIdent(name)} {type}");
        lock (_gate)
        {
            _customBallColumns.Add(name);
        }

        SyncFromWorld(force: true);
        return name;
    }

    public void RemoveBallColumn(string name)
    {
        name = name.Trim();
        if (RequiredBallColumns.Contains(name))
            throw new InvalidOperationException($"不能删除必修列: {name}");
        if (ForbiddenBallColumns.Contains(name))
            throw new InvalidOperationException($"禁止列 {name} 本就不该存在");
        if (!CustomBallColumns.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"列不存在: {name}");

        _db.ExecuteSql($"ALTER TABLE balls DROP COLUMN {QuoteIdent(name)}");
        lock (_gate)
        {
            _customBallColumns.RemoveAll(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var ball in _world.Balls)
            ball.Props.Remove(name);

        SyncFromWorld(force: true);
    }

    /// <summary>表窗口 / db.update → SceneWorld → 再刷投影。</summary>
    public int ApplyUpdate(string table, string set, string? where)
    {
        var assignments = ParseAssignments(set);
        if (assignments.Count == 0)
            throw new InvalidOperationException("set= 不能为空");

        if (table.Equals(BallsTable, StringComparison.OrdinalIgnoreCase))
            return UpdateBalls(assignments, where);
        if (table.Equals(ObjectsTable, StringComparison.OrdinalIgnoreCase))
            return UpdateObjects(assignments, where);
        if (table.Equals(ScenesTable, StringComparison.OrdinalIgnoreCase))
            return UpdateScenes(assignments, where);
        if (table.Equals(WireframesTable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "wireframes 表请勿直接改点列;请用画布/wire.* 指令,实体化请 wire.solidify");
        if (table.Equals(SolidsTable, StringComparison.OrdinalIgnoreCase))
            return UpdateSolids(assignments, where);
        if (table.Equals(FactionsTable, StringComparison.OrdinalIgnoreCase))
            return UpdateFactions(assignments, where);
        throw new InvalidOperationException($"非投影表: {table}");
    }

    /// <summary>solids 投影仅允许改 color;几何请用 solid.move / 画布。</summary>
    private int UpdateSolids(Dictionary<string, string> assignments, string? where)
    {
        foreach (var key in assignments.Keys)
        {
            if (!key.Equals("color", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "solids 表仅允许更新 color;几何请用 solid.move / solid.remove 或画布编辑");
        }

        if (!assignments.TryGetValue("color", out var color) || string.IsNullOrWhiteSpace(color))
            throw new InvalidOperationException("solids 更新须含 color=");

        var ids = ResolveSolidIds(where);
        if (ids.Count == 0)
            return 0;

        foreach (var id in ids)
        {
            var solid = _world.FindSolid(id);
            if (solid == null)
                continue;
            solid.Color = color.Trim();
        }

        _world.NotifyChanged(project: false);
        SyncFromWorld(force: true);
        return ids.Count;
    }

    public int ApplyDelete(string table, string? where)
    {
        if (table.Equals(BallsTable, StringComparison.OrdinalIgnoreCase))
        {
            var ids = ResolveBallIds(where);
            foreach (var id in ids)
            {
                var ball = _world.FindBall(id);
                if (ball != null)
                    _world.Balls.Remove(ball);
            }

            _world.NotifyChanged(project: false);
            SyncFromWorld(force: true);
            return ids.Count;
        }

        if (table.Equals(ObjectsTable, StringComparison.OrdinalIgnoreCase))
        {
            var ids = ResolveObjectIds(where);
            foreach (var id in ids)
            {
                var obj = _world.FindObject(id);
                if (obj != null)
                    _world.Objects.Remove(obj);
            }

            _world.NotifyChanged(project: false);
            SyncFromWorld(force: true);
            return ids.Count;
        }

        if (table.Equals(ScenesTable, StringComparison.OrdinalIgnoreCase))
            return DeleteScenes(where);

        if (table.Equals(WireframesTable, StringComparison.OrdinalIgnoreCase))
        {
            var ids = ResolveWireIds(where);
            foreach (var id in ids)
            {
                var w = _world.FindWire(id);
                if (w != null)
                    _world.Wireframes.Remove(w);
            }

            if (_world.SelectedWireId != null
                && ids.Any(id => string.Equals(id, _world.SelectedWireId, StringComparison.OrdinalIgnoreCase)))
                _world.SelectedWireId = null;
            _world.NotifyChanged(project: false);
            SyncFromWorld(force: true);
            return ids.Count;
        }

        if (table.Equals(SolidsTable, StringComparison.OrdinalIgnoreCase))
        {
            var ids = ResolveSolidIds(where);
            foreach (var id in ids)
            {
                var s = _world.FindSolid(id);
                if (s != null)
                    _world.Solids.Remove(s);
            }

            if (_world.SelectedSolidId != null
                && ids.Any(id => string.Equals(id, _world.SelectedSolidId, StringComparison.OrdinalIgnoreCase)))
                _world.SelectedSolidId = null;
            _world.NotifyChanged(project: false);
            SyncFromWorld(force: true);
            return ids.Count;
        }

        if (table.Equals(FactionsTable, StringComparison.OrdinalIgnoreCase))
        {
            var ids = ResolveFactionIds(where);
            foreach (var id in ids)
            {
                var f = _world.FindFaction(id);
                if (f != null)
                    _world.Factions.Remove(f);
            }

            _world.NotifyChanged(project: false);
            SyncFromWorld(force: true);
            return ids.Count;
        }

        throw new InvalidOperationException($"非投影表: {table}");
    }

    public int ApplyInsert(string table, string set)
    {
        if (table.Equals(ScenesTable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请勿 db.insert scenes;请用 scene.save 创建场景文件后 scene.refresh");
        if (table.Equals(WireframesTable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请勿 db.insert wireframes;请用线框工具绘制并 wire.close");
        if (table.Equals(SolidsTable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请勿 db.insert solids;异形实体请由 wire.solidify 产生");
        if (table.Equals(FactionsTable, StringComparison.OrdinalIgnoreCase))
            return InsertFaction(set);
        throw new InvalidOperationException(
            $"请勿直接 db.insert 投影表 {table};请使用 ball.spawn / scene.add,以便写入 SceneWorld");
    }

    private int InsertFaction(string set)
    {
        var assignments = ParseAssignments(set);
        if (!assignments.TryGetValue("id", out var id) || string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("factions 插入须含 id=");
        id = id.Trim();
        if (_world.FindFaction(id) != null)
            throw new InvalidOperationException($"阵营 id 已存在: {id}");

        var f = new Faction
        {
            Id = id,
            Name = assignments.TryGetValue("name", out var n) ? n : id,
            Color = assignments.TryGetValue("color", out var c) && !string.IsNullOrWhiteSpace(c)
                ? c.Trim()
                : "#3B82F6",
            InitialBalls = assignments.TryGetValue("initial_balls", out var ib)
                           && int.TryParse(ib, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ibn)
                ? Math.Max(0, ibn)
                : 3,
            InitialMultiplier = assignments.TryGetValue("initial_multiplier", out var im)
                                && long.TryParse(im, NumberStyles.Integer, CultureInfo.InvariantCulture, out var imn)
                ? PublicDefaults.ClampMultiplier(imn)
                : 1,
            Score = assignments.TryGetValue("score", out var sc)
                    && long.TryParse(sc, NumberStyles.Integer, CultureInfo.InvariantCulture, out var scn)
                ? Math.Max(0, scn)
                : 0,
        };
        _world.Factions.Add(f);
        _world.NotifyChanged(project: false);
        SyncFromWorld(force: true);
        return 1;
    }

    private int UpdateScenes(Dictionary<string, string> assignments, string? where)
    {
        if (string.IsNullOrWhiteSpace(_scenesDirectory))
            throw new InvalidOperationException("scenes 目录未知;请先执行 scene.refresh");

        var names = ResolveSceneNames(where);
        if (names.Count == 0)
            return 0;

        foreach (var key in assignments.Keys)
        {
            if (key.Equals("width", StringComparison.OrdinalIgnoreCase)
                || key.Equals("height", StringComparison.OrdinalIgnoreCase)
                || key.Equals("object_count", StringComparison.OrdinalIgnoreCase)
                || key.Equals("ball_count", StringComparison.OrdinalIgnoreCase)
                || key.Equals("updated_at", StringComparison.OrdinalIgnoreCase)
                || key.Equals("is_current", StringComparison.OrdinalIgnoreCase)
                || key.Equals("path", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"scenes.{key} 只读(来自文件元数据);改名请 set name=,或用 scene.rename");
            }
        }

        if (!assignments.TryGetValue("name", out var newNameRaw))
            throw new InvalidOperationException("scenes 表目前仅支持改 name(重命名文件)");

        var newName = newNameRaw.Trim();
        if (!newName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            newName += ".json";
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("非法文件名");

        var renamed = 0;
        foreach (var oldName in names)
        {
            if (oldName.Equals(newName, StringComparison.OrdinalIgnoreCase))
                continue;
            RenameSceneFile(oldName, newName);
            renamed++;
        }

        RefreshScenes(_scenesDirectory!, _world.LastScenePath);
        return renamed;
    }

    private int DeleteScenes(string? where)
    {
        if (string.IsNullOrWhiteSpace(_scenesDirectory))
            throw new InvalidOperationException("scenes 目录未知;请先执行 scene.refresh");

        var names = ResolveSceneNames(where);
        foreach (var name in names)
        {
            var path = Path.Combine(_scenesDirectory!, name);
            if (File.Exists(path))
                File.Delete(path);
            if (!string.IsNullOrWhiteSpace(_world.LastScenePath)
                && Path.GetFileName(_world.LastScenePath).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                _world.LastScenePath = null;
            }
        }

        RefreshScenes(_scenesDirectory!, _world.LastScenePath);
        return names.Count;
    }

    public void RenameSceneFile(string oldFileName, string newFileName)
    {
        if (string.IsNullOrWhiteSpace(_scenesDirectory))
            throw new InvalidOperationException("scenes 目录未知;请先执行 scene.refresh");

        var oldName = oldFileName.Trim();
        var newName = newFileName.Trim();
        if (!oldName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            oldName += ".json";
        if (!newName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            newName += ".json";

        var src = Path.GetFullPath(Path.Combine(_scenesDirectory!, oldName));
        var dst = Path.GetFullPath(Path.Combine(_scenesDirectory!, newName));
        var root = Path.GetFullPath(_scenesDirectory!);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        if (!src.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !dst.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("场景路径越出 scenes 目录");
        if (!File.Exists(src))
            throw new InvalidOperationException($"场景不存在: {oldName}");
        if (File.Exists(dst))
            throw new InvalidOperationException($"目标已存在: {newName}");

        File.Move(src, dst);
        if (!string.IsNullOrWhiteSpace(_world.LastScenePath)
            && Path.GetFullPath(_world.LastScenePath).Equals(src, StringComparison.OrdinalIgnoreCase))
            _world.LastScenePath = dst;
    }

    private List<string> ResolveSceneNames(string? where)
    {
        if (string.IsNullOrWhiteSpace(where))
        {
            var all = _db.Query(ScenesTable, limit: 10_000, page: 1);
            var idx = all.Columns.ToList().FindIndex(c => c.Equals("name", StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
                return [];
            return all.Rows
                .Select(r => Convert.ToString(r[idx], CultureInfo.InvariantCulture) ?? "")
                .Where(s => s.Length > 0)
                .ToList();
        }

        var nameMatch = Regex.Match(where, @"\bname\s*=\s*'([^']+)'", RegexOptions.IgnoreCase)
                        is { Success: true } m1
            ? m1
            : Regex.Match(where, @"\bname\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase) is { Success: true } m2
                ? m2
                : Regex.Match(where, @"\bname\s*=\s*([^\s]+)", RegexOptions.IgnoreCase);
        if (nameMatch.Success)
            return [nameMatch.Groups[1].Value.Trim()];

        var result = _db.Query(ScenesTable, where: where, limit: 10_000, page: 1);
        var idIdx = result.Columns.ToList().FindIndex(c => c.Equals("name", StringComparison.OrdinalIgnoreCase));
        if (idIdx < 0)
            return [];
        return result.Rows
            .Select(r => Convert.ToString(r[idIdx], CultureInfo.InvariantCulture) ?? "")
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private int UpdateBalls(Dictionary<string, string> assignments, string? where)
    {
        foreach (var key in assignments.Keys)
        {
            if (ForbiddenBallColumns.Contains(key))
                throw new InvalidOperationException($"禁止写入位置列 {key}(位置仅在 SceneWorld 内存)");
            if (key.Equals("id", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("禁止通过表改修改 id");
        }

        var ids = ResolveBallIds(where);
        if (ids.Count == 0)
            return 0;

        var updated = 0;
        foreach (var id in ids)
        {
            var ball = _world.FindBall(id);
            if (ball == null)
                continue;
            ApplyBallAssignments(ball, assignments);
            updated++;
        }

        _world.NotifyChanged(project: false);
        SyncFromWorld(force: true);
        return updated;
    }

    private int UpdateFactions(Dictionary<string, string> assignments, string? where)
    {
        if (assignments.ContainsKey("id"))
            throw new InvalidOperationException("禁止通过表改修改 factions.id");

        var ids = ResolveFactionIds(where);
        if (ids.Count == 0)
            return 0;

        foreach (var id in ids)
        {
            var f = _world.FindFaction(id);
            if (f == null)
                continue;
            ApplyFactionAssignments(f, assignments);
        }

        _world.NotifyChanged(project: false);
        SyncFromWorld(force: true);
        return ids.Count;
    }

    private static void ApplyFactionAssignments(Faction f, Dictionary<string, string> assignments)
    {
        foreach (var (col, raw) in assignments)
        {
            if (col.Equals("name", StringComparison.OrdinalIgnoreCase))
                f.Name = raw;
            else if (col.Equals("color", StringComparison.OrdinalIgnoreCase))
                f.Color = raw.Trim();
            else if (col.Equals("initial_balls", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ib))
                f.InitialBalls = Math.Max(0, ib);
            else if (col.Equals("initial_multiplier", StringComparison.OrdinalIgnoreCase)
                     && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var im))
                f.InitialMultiplier = PublicDefaults.ClampMultiplier(im);
            else if (col.Equals("score", StringComparison.OrdinalIgnoreCase)
                     && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sc))
                f.Score = Math.Max(0, sc);
        }
    }

    private int UpdateObjects(Dictionary<string, string> assignments, string? where)
    {
        var ids = ResolveObjectIds(where);
        if (ids.Count == 0)
            return 0;

        foreach (var id in ids)
        {
            var obj = _world.FindObject(id);
            if (obj == null)
                continue;
            ApplyObjectAssignments(obj, assignments);
        }

        _world.NotifyChanged(project: false);
        SyncFromWorld(force: true);
        return ids.Count;
    }

    private void ApplyBallAssignments(Ball ball, Dictionary<string, string> assignments)
    {
        var multChanged = false;
        foreach (var (col, raw) in assignments)
        {
            if (col.Equals("color", StringComparison.OrdinalIgnoreCase))
                ball.Color = raw;
            else if (col.Equals("weight", StringComparison.OrdinalIgnoreCase)
                     && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var w))
                ball.Weight = PublicDefaults.RoundWeight(w);
            else if (col.Equals("size", StringComparison.OrdinalIgnoreCase)
                     && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                ball.Size = PublicDefaults.RoundSize(s);
            else if (col.Equals("multiplier", StringComparison.OrdinalIgnoreCase)
                     && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m))
            {
                ball.Multiplier = PublicDefaults.ClampMultiplier(m);
                multChanged = true;
            }
            else if (col.Equals("alive", StringComparison.OrdinalIgnoreCase))
            {
                // 忽略或仅作显示
            }
            else
                ball.Props[col] = raw;
        }

        // 倍率变化时按公式覆盖 size/weight(V6Q11);同次显式 size/weight 优先保留
        if (multChanged
            && !assignments.Keys.Any(k => k.Equals("size", StringComparison.OrdinalIgnoreCase))
            && !assignments.Keys.Any(k => k.Equals("weight", StringComparison.OrdinalIgnoreCase)))
            _world.Defaults.ApplyToBall(ball);
    }

    private static void ApplyObjectAssignments(SceneObject obj, Dictionary<string, string> assignments)
    {
        var dirChanged = false;
        var rotChanged = false;
        foreach (var (col, raw) in assignments)
        {
            if (col.Equals("type", StringComparison.OrdinalIgnoreCase))
            {
                obj.Type = raw.ToLowerInvariant() switch
                {
                    "block" => SceneObjectType.Block,
                    "arrow" => SceneObjectType.Arrow,
                    "spawner" => SceneObjectType.Spawner,
                    "despawner" => SceneObjectType.Despawner,
                    _ => obj.Type,
                };
            }
            else if (col.Equals("x", StringComparison.OrdinalIgnoreCase)
                     && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
                obj.X = x;
            else if (col.Equals("y", StringComparison.OrdinalIgnoreCase)
                     && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                obj.Y = y;
            else if (col.Equals("w", StringComparison.OrdinalIgnoreCase)
                     && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var w))
                obj.W = Math.Max(8, w);
            else if (col.Equals("h", StringComparison.OrdinalIgnoreCase)
                     && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
                obj.H = Math.Max(8, h);
            else if (col.Equals("rotation", StringComparison.OrdinalIgnoreCase)
                     && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var rot))
            {
                obj.Rotation = rot;
                rotChanged = true;
            }
            else if ((col.Equals("dir_x", StringComparison.OrdinalIgnoreCase) || col.Equals("dirx", StringComparison.OrdinalIgnoreCase))
                     && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var dx))
            {
                obj.DirX = dx;
                dirChanged = true;
            }
            else if ((col.Equals("dir_y", StringComparison.OrdinalIgnoreCase) || col.Equals("diry", StringComparison.OrdinalIgnoreCase))
                     && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var dy))
            {
                obj.DirY = dy;
                dirChanged = true;
            }
            else if ((col.Equals("influence_radius", StringComparison.OrdinalIgnoreCase) || col.Equals("radius", StringComparison.OrdinalIgnoreCase))
                     && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var r))
                obj.InfluenceRadius = r;
            else if ((col.Equals("strength_g", StringComparison.OrdinalIgnoreCase) || col.Equals("strength", StringComparison.OrdinalIgnoreCase))
                     && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var sg))
                obj.StrengthG = sg;
            else if (col.Equals("patch_json", StringComparison.OrdinalIgnoreCase) || col.Equals("patch", StringComparison.OrdinalIgnoreCase))
                obj.PatchJson = raw;
            else if (col.Equals("name", StringComparison.OrdinalIgnoreCase))
                obj.Name = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        }

        if (rotChanged)
            obj.SyncArrowDirFromRotation();
        else if (dirChanged)
            obj.SyncRotationFromArrowDir();
    }

    private List<string> ResolveBallIds(string? where)
    {
        if (string.IsNullOrWhiteSpace(where))
            return _world.Balls.Select(b => b.Id).ToList();

        var idMatch = Regex.Match(where, @"\bid\s*=\s*'([^']+)'", RegexOptions.IgnoreCase)
                      is { Success: true } m1
            ? m1
            : Regex.Match(where, @"\bid\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase) is { Success: true } m2
                ? m2
                : Regex.Match(where, @"\bid\s*=\s*([^\s]+)", RegexOptions.IgnoreCase);

        if (idMatch.Success)
            return [idMatch.Groups[1].Value.Trim()];

        var result = _db.Query(BallsTable, where: where, limit: 10_000, page: 1);
        var idIdx = result.Columns.ToList().FindIndex(c => c.Equals("id", StringComparison.OrdinalIgnoreCase));
        if (idIdx < 0)
            return [];
        return result.Rows
            .Select(r => Convert.ToString(r[idIdx], CultureInfo.InvariantCulture) ?? "")
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> ResolveFactionIds(string? where)
    {
        if (string.IsNullOrWhiteSpace(where))
            return _world.Factions.Select(f => f.Id).ToList();

        var idMatch = Regex.Match(where, @"\bid\s*=\s*'([^']+)'", RegexOptions.IgnoreCase)
                      is { Success: true } m1
            ? m1
            : Regex.Match(where, @"\bid\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase) is { Success: true } m2
                ? m2
                : Regex.Match(where, @"\bid\s*=\s*([^\s]+)", RegexOptions.IgnoreCase);
        if (idMatch.Success)
            return [idMatch.Groups[1].Value.Trim()];

        var result = _db.Query(FactionsTable, where: where, limit: 10_000, page: 1);
        var idIdx = result.Columns.ToList().FindIndex(c => c.Equals("id", StringComparison.OrdinalIgnoreCase));
        if (idIdx < 0)
            return [];
        return result.Rows
            .Select(r => Convert.ToString(r[idIdx], CultureInfo.InvariantCulture) ?? "")
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> ResolveObjectIds(string? where)
    {
        if (string.IsNullOrWhiteSpace(where))
            return _world.Objects.Select(o => o.Id).ToList();

        var idMatch = Regex.Match(where, @"\bid\s*=\s*'([^']+)'", RegexOptions.IgnoreCase)
                      is { Success: true } m1
            ? m1
            : Regex.Match(where, @"\bid\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase) is { Success: true } m2
                ? m2
                : Regex.Match(where, @"\bid\s*=\s*([^\s]+)", RegexOptions.IgnoreCase);

        if (idMatch.Success)
            return [idMatch.Groups[1].Value.Trim()];

        var result = _db.Query(ObjectsTable, where: where, limit: 10_000, page: 1);
        var idIdx = result.Columns.ToList().FindIndex(c => c.Equals("id", StringComparison.OrdinalIgnoreCase));
        if (idIdx < 0)
            return [];
        return result.Rows
            .Select(r => Convert.ToString(r[idIdx], CultureInfo.InvariantCulture) ?? "")
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> ResolveWireIds(string? where)
    {
        if (string.IsNullOrWhiteSpace(where))
            return _world.Wireframes.Select(w => w.Id).ToList();

        var idMatch = Regex.Match(where, @"\bid\s*=\s*'([^']+)'", RegexOptions.IgnoreCase)
                      is { Success: true } m1
            ? m1
            : Regex.Match(where, @"\bid\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase) is { Success: true } m2
                ? m2
                : Regex.Match(where, @"\bid\s*=\s*([^\s]+)", RegexOptions.IgnoreCase);
        if (idMatch.Success)
            return [idMatch.Groups[1].Value.Trim()];

        var result = _db.Query(WireframesTable, where: where, limit: 10_000, page: 1);
        var idIdx = result.Columns.ToList().FindIndex(c => c.Equals("id", StringComparison.OrdinalIgnoreCase));
        if (idIdx < 0)
            return [];
        return result.Rows
            .Select(r => Convert.ToString(r[idIdx], CultureInfo.InvariantCulture) ?? "")
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> ResolveSolidIds(string? where)
    {
        if (string.IsNullOrWhiteSpace(where))
            return _world.Solids.Select(s => s.Id).ToList();

        var idMatch = Regex.Match(where, @"\bid\s*=\s*'([^']+)'", RegexOptions.IgnoreCase)
                      is { Success: true } m1
            ? m1
            : Regex.Match(where, @"\bid\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase) is { Success: true } m2
                ? m2
                : Regex.Match(where, @"\bid\s*=\s*([^\s]+)", RegexOptions.IgnoreCase);
        if (idMatch.Success)
            return [idMatch.Groups[1].Value.Trim()];

        var result = _db.Query(SolidsTable, where: where, limit: 10_000, page: 1);
        var idIdx = result.Columns.ToList().FindIndex(c => c.Equals("id", StringComparison.OrdinalIgnoreCase));
        if (idIdx < 0)
            return [];
        return result.Rows
            .Select(r => Convert.ToString(r[idIdx], CultureInfo.InvariantCulture) ?? "")
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>解析 set="color='#f00', weight=1.5"。</summary>
    public static Dictionary<string, string> ParseAssignments(string set)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in SplitCsvRespectingQuotes(set))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0)
                continue;
            var col = part[..idx].Trim().Trim('"', '\'');
            var val = Unquote(part[(idx + 1)..].Trim());
            if (col.Length > 0)
                map[col] = val;
        }

        return map;
    }

    private static IEnumerable<string> SplitCsvRespectingQuotes(string input)
    {
        var sb = new StringBuilder();
        var inSingle = false;
        var inDouble = false;
        foreach (var ch in input)
        {
            if (ch == '\'' && !inDouble)
                inSingle = !inSingle;
            else if (ch == '"' && !inSingle)
                inDouble = !inDouble;

            if (ch == ',' && !inSingle && !inDouble)
            {
                yield return sb.ToString();
                sb.Clear();
                continue;
            }

            sb.Append(ch);
        }

        if (sb.Length > 0)
            yield return sb.ToString();
    }

    private static string Unquote(string v)
    {
        v = v.Trim();
        if (v.Length >= 2 && ((v[0] == '\'' && v[^1] == '\'') || (v[0] == '"' && v[^1] == '"')))
            return v[1..^1].Replace("''", "'", StringComparison.Ordinal);
        return v;
    }

    private static string SqlString(string? s)
    {
        if (s == null)
            return "NULL";
        return "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static string F(double v) => v.ToString(CultureInfo.InvariantCulture);
    private static string QuoteIdent(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
}
