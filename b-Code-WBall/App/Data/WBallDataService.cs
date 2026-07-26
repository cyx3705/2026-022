using AppShell.Core.Data;
using AppShell.Services;
using WBall.Model;

namespace WBall.Data;

/// <summary>
/// 包装 SqliteDataService:对 balls / scene_objects 的写操作先打进 SceneWorld。
/// 其余表原样转发(自由区,不改 AppShell.*)。
/// v1.6.1:表只镜像结果,不再合成公共行。
/// </summary>
public sealed class WBallDataService : IDataService, IDisposable
{
    private readonly SqliteDataService _inner;
    private readonly PropertyProjection _projection;

    public WBallDataService(SqliteDataService inner, PropertyProjection projection)
    {
        _inner = inner;
        _projection = projection;
    }

    public PropertyProjection Projection => _projection;

    public event Action<string, string?>? DataChanged
    {
        add => _inner.DataChanged += value;
        remove => _inner.DataChanged -= value;
    }

    public IReadOnlyList<string> ListConnections() => _inner.ListConnections();
    public IReadOnlyList<string> ListTables(string? connection = null) => _inner.ListTables(connection);
    public IReadOnlyList<ColumnInfo> GetSchema(string table, string? connection = null)
        => _inner.GetSchema(table, connection);

    public QueryResult Query(
        string table, string? where = null, string? order = null,
        int limit = 500, int page = 1, string? connection = null)
        => _inner.Query(table, where, order, limit, page, connection);

    public long ExportCsv(string table, string filePath, string? where = null, string? connection = null)
        => _inner.ExportCsv(table, filePath, where, connection);

    public (QueryResult? Result, int Affected) ExecuteSql(string sql, string? connection = null)
    {
        // 阻止绕过投影直接改 balls 位置列等:对投影表危险 SQL 做简单拒绝
        var trimmed = sql.TrimStart();
        if (LooksLikeProjectionWrite(trimmed))
        {
            throw new InvalidOperationException(
                "请勿用 db.sql 直接改写投影表;请使用 ball.set / scene.set / 表窗口编辑(会同步 SceneWorld)");
        }

        return _inner.ExecuteSql(sql, connection);
    }

    public int Insert(string table, string set, string? connection = null)
    {
        if (_projection.IsManaged(table))
            return _projection.ApplyInsert(table, set);
        return _inner.Insert(table, set, connection);
    }

    public int Update(string table, string set, string? where, string? connection = null)
    {
        if (_projection.IsManaged(table))
            return _projection.ApplyUpdate(table, set, where);
        return _inner.Update(table, set, where, connection);
    }

    public int Delete(string table, string? where, string? connection = null)
    {
        if (_projection.IsManaged(table))
            return _projection.ApplyDelete(table, where);
        return _inner.Delete(table, where, connection);
    }

    public void Dispose() => _projection.Dispose();

    private static bool LooksLikeProjectionWrite(string sql)
    {
        if (sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase))
            return false;
        return RegexContainsTable(sql, "balls")
               || RegexContainsTable(sql, "scene_objects")
               || RegexContainsTable(sql, "scenes")
               || RegexContainsTable(sql, "wireframes")
               || RegexContainsTable(sql, "solids")
               || RegexContainsTable(sql, "factions");
    }

    private static bool RegexContainsTable(string sql, string table)
        => System.Text.RegularExpressions.Regex.IsMatch(
            sql,
            $@"\b{table}\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
