using System.Runtime.CompilerServices;
using WBall.Model;

namespace WBall.Sim;

/// <summary>
/// 场景静态对象的按类型索引。对象属性仍由原实例提供，因此移动和缩放立即可见；
/// 只有对象引用、数量或类型改变时才重建数组，避免每颗球反复执行 LINQ 枚举。
/// </summary>
internal sealed class PhysicsWorldIndex
{
    private static readonly ConditionalWeakTable<SceneWorld, PhysicsWorldIndex> Cache = new();

    private SceneObject[] _objects = [];
    private SceneObjectType[] _types = [];
    private ulong _layoutSignature;
    private readonly StaticObjectGrid _blockGrid = new();
    private readonly StaticObjectGrid _arrowGrid = new();
    private readonly StaticObjectGrid _despawnerGrid = new();

    public SceneObject[] Blocks { get; private set; } = [];
    public SceneObject[] Arrows { get; private set; } = [];
    public SceneObject[] Despawners { get; private set; } = [];
    public SceneObject[] Spawners { get; private set; } = [];

    public List<SceneObject> QueryBlocks(double x, double y, double radius) =>
        _blockGrid.Query(x - radius, y - radius, x + radius, y + radius);

    public List<SceneObject> QueryArrows(double x, double y) =>
        _arrowGrid.Query(x, y, x, y);

    public List<SceneObject> QueryDespawner(double x, double y) =>
        _despawnerGrid.Query(x, y, x, y);

    public static PhysicsWorldIndex For(SceneWorld world)
    {
        var index = Cache.GetValue(world, static _ => new PhysicsWorldIndex());
        index.Refresh(world.Objects);
        return index;
    }

    private void Refresh(List<SceneObject> objects)
    {
        var layoutSignature = LayoutSignature(objects);
        if (Matches(objects) && layoutSignature == _layoutSignature)
            return;

        _layoutSignature = layoutSignature;
        _objects = objects.ToArray();
        _types = new SceneObjectType[_objects.Length];
        var blockCount = 0;
        var arrowCount = 0;
        var despawnerCount = 0;
        var spawnerCount = 0;
        for (var i = 0; i < _objects.Length; i++)
        {
            var type = _objects[i].Type;
            _types[i] = type;
            switch (type)
            {
                case SceneObjectType.Block:
                    blockCount++;
                    break;
                case SceneObjectType.Arrow:
                    arrowCount++;
                    break;
                case SceneObjectType.Despawner:
                    despawnerCount++;
                    break;
                case SceneObjectType.Spawner:
                    spawnerCount++;
                    break;
            }
        }

        Blocks = new SceneObject[blockCount];
        Arrows = new SceneObject[arrowCount];
        Despawners = new SceneObject[despawnerCount];
        Spawners = new SceneObject[spawnerCount];
        blockCount = 0;
        arrowCount = 0;
        despawnerCount = 0;
        spawnerCount = 0;
        for (var i = 0; i < _objects.Length; i++)
        {
            var sceneObject = _objects[i];
            switch (_types[i])
            {
                case SceneObjectType.Block:
                    Blocks[blockCount++] = sceneObject;
                    break;
                case SceneObjectType.Arrow:
                    Arrows[arrowCount++] = sceneObject;
                    break;
                case SceneObjectType.Despawner:
                    Despawners[despawnerCount++] = sceneObject;
                    break;
                case SceneObjectType.Spawner:
                    Spawners[spawnerCount++] = sceneObject;
                    break;
            }
        }

        _blockGrid.Rebuild(Blocks, static sceneObject => BoundsOf(sceneObject));
        _arrowGrid.Rebuild(Arrows, static sceneObject =>
        {
            var radius = Math.Max(1, sceneObject.InfluenceRadius);
            var centerX = sceneObject.X + sceneObject.W / 2;
            var centerY = sceneObject.Y + sceneObject.H / 2;
            return (centerX - radius, centerY - radius, centerX + radius, centerY + radius);
        });
        _despawnerGrid.Rebuild(Despawners, static sceneObject => BoundsOf(sceneObject));
    }

    private bool Matches(List<SceneObject> objects)
    {
        if (objects.Count != _objects.Length)
            return false;
        for (var i = 0; i < objects.Count; i++)
        {
            if (!ReferenceEquals(objects[i], _objects[i]) || objects[i].Type != _types[i])
                return false;
        }
        return true;
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) BoundsOf(SceneObject sceneObject)
    {
        sceneObject.GetAabb(out var minX, out var minY, out var maxX, out var maxY);
        return (minX, minY, maxX, maxY);
    }

    private static ulong LayoutSignature(List<SceneObject> objects)
    {
        var hash = 1469598103934665603UL;
        foreach (var sceneObject in objects)
        {
            Mix(ref hash, (long)sceneObject.Type);
            Mix(ref hash, BitConverter.DoubleToInt64Bits(sceneObject.X));
            Mix(ref hash, BitConverter.DoubleToInt64Bits(sceneObject.Y));
            Mix(ref hash, BitConverter.DoubleToInt64Bits(sceneObject.W));
            Mix(ref hash, BitConverter.DoubleToInt64Bits(sceneObject.H));
            Mix(ref hash, BitConverter.DoubleToInt64Bits(sceneObject.Rotation));
            Mix(ref hash, BitConverter.DoubleToInt64Bits(sceneObject.InfluenceRadius));
        }
        return hash;
    }

    private static void Mix(ref ulong hash, long value)
    {
        hash ^= unchecked((ulong)value);
        hash *= 1099511628211UL;
    }

    private sealed class StaticObjectGrid
    {
        private const double CellSize = 128;
        private readonly Dictionary<long, List<int>> _cells = [];
        private readonly List<int> _candidateIndices = [];
        private readonly List<SceneObject> _candidates = [];
        private SceneObject[] _source = [];
        private int[] _visit = [];
        private int _stamp;

        public void Rebuild(
            SceneObject[] source,
            Func<SceneObject, (double MinX, double MinY, double MaxX, double MaxY)> boundsOf)
        {
            _source = source;
            _cells.Clear();
            _visit = new int[source.Length];
            _stamp = 0;
            for (var index = 0; index < source.Length; index++)
            {
                var bounds = boundsOf(source[index]);
                var minCol = Cell(bounds.MinX);
                var maxCol = Cell(bounds.MaxX);
                var minRow = Cell(bounds.MinY);
                var maxRow = Cell(bounds.MaxY);
                for (var col = minCol; col <= maxCol; col++)
                {
                    for (var row = minRow; row <= maxRow; row++)
                    {
                        var key = Key(col, row);
                        if (!_cells.TryGetValue(key, out var entries))
                            _cells[key] = entries = [];
                        entries.Add(index);
                    }
                }
            }
        }

        public List<SceneObject> Query(double minX, double minY, double maxX, double maxY)
        {
            _candidateIndices.Clear();
            _candidates.Clear();
            if (_source.Length == 0)
                return _candidates;
            if (++_stamp == int.MaxValue)
            {
                Array.Clear(_visit);
                _stamp = 1;
            }

            var minCol = Cell(minX);
            var maxCol = Cell(maxX);
            var minRow = Cell(minY);
            var maxRow = Cell(maxY);
            for (var col = minCol; col <= maxCol; col++)
            {
                for (var row = minRow; row <= maxRow; row++)
                {
                    if (!_cells.TryGetValue(Key(col, row), out var entries))
                        continue;
                    foreach (var index in entries)
                    {
                        if (_visit[index] == _stamp)
                            continue;
                        _visit[index] = _stamp;
                        _candidateIndices.Add(index);
                    }
                }
            }
            _candidateIndices.Sort();
            foreach (var index in _candidateIndices)
                _candidates.Add(_source[index]);
            return _candidates;
        }

        private static int Cell(double value) => (int)Math.Floor(value / CellSize);
        private static long Key(int col, int row) => ((long)col << 32) | (uint)row;
    }
}
