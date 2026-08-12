using System.Runtime.CompilerServices;
using WBall.Model;

namespace WBall.Sim;

/// <summary>高球数球碰撞候选索引；返回的候选始终按原 List 下标排序。</summary>
internal sealed class BallCollisionGrid
{
    private const double CellSize = 32;
    private static readonly ConditionalWeakTable<SceneWorld, BallCollisionGrid> Cache = new();
    private readonly Dictionary<long, List<int>> _cells = [];
    private readonly List<List<int>> _bucketPool = [];
    private readonly List<int> _candidates = [];
    private int[] _visit = [];
    private int _stamp;
    private double _maxRadius;

    public static BallCollisionGrid For(SceneWorld world) =>
        Cache.GetValue(world, static _ => new BallCollisionGrid());

    public void Build(List<Ball> balls)
    {
        foreach (var bucket in _bucketPool)
            bucket.Clear();
        _cells.Clear();
        if (_visit.Length < balls.Count)
            _visit = new int[balls.Count];
        _maxRadius = 0;
        var poolIndex = 0;
        for (var index = 0; index < balls.Count; index++)
        {
            var ball = balls[index];
            _maxRadius = Math.Max(_maxRadius, ball.Size);
            var key = Key(Cell(ball.X), Cell(ball.Y));
            if (!_cells.TryGetValue(key, out var bucket))
            {
                if (poolIndex >= _bucketPool.Count)
                    _bucketPool.Add(new List<int>());
                bucket = _bucketPool[poolIndex++];
                _cells[key] = bucket;
            }
            bucket.Add(index);
        }
    }

    public List<int> Query(int ballIndex, Ball ball)
    {
        _candidates.Clear();
        if (++_stamp == int.MaxValue)
        {
            Array.Clear(_visit);
            _stamp = 1;
        }
        var range = Math.Max(1, (int)Math.Ceiling((ball.Size + _maxRadius) / CellSize));
        var centerCol = Cell(ball.X);
        var centerRow = Cell(ball.Y);
        for (var col = centerCol - range; col <= centerCol + range; col++)
        {
            for (var row = centerRow - range; row <= centerRow + range; row++)
            {
                if (!_cells.TryGetValue(Key(col, row), out var bucket))
                    continue;
                foreach (var otherIndex in bucket)
                {
                    if (otherIndex <= ballIndex || _visit[otherIndex] == _stamp)
                        continue;
                    _visit[otherIndex] = _stamp;
                    _candidates.Add(otherIndex);
                }
            }
        }
        _candidates.Sort();
        return _candidates;
    }

    private static int Cell(double value) => (int)Math.Floor(value / CellSize);
    private static long Key(int col, int row) => ((long)col << 32) | (uint)row;
}
