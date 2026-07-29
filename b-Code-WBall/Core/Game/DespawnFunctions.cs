using System.Text.RegularExpressions;
using WBall.Model;

namespace WBall.Game;

/// <summary>销毁器 name 函数解析与执行: Xn / RUN / 配置化攻击结算。</summary>
public static class DespawnFunctions
{
    private static readonly Regex XnPattern = new(@"^X(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public enum Kind
    {
        None,
        Multiply,
        Run,
        Settlement,
        Unknown,
    }

    public readonly struct ParseResult
    {
        public Kind Kind { get; init; }
        public int Factor { get; init; }
        public string? Name { get; init; }
    }

    public static ParseResult Parse(string? name, Func<string, bool>? isSettlement = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new ParseResult { Kind = Kind.None };

        var n = name.Trim();
        if (n.Equals("RUN", StringComparison.OrdinalIgnoreCase))
            return new ParseResult { Kind = Kind.Run };

        var m = XnPattern.Match(n);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var factor) && factor >= 1)
            return new ParseResult { Kind = Kind.Multiply, Factor = factor };

        if (isSettlement?.Invoke(n) == true)
            return new ParseResult { Kind = Kind.Settlement, Name = n };

        return new ParseResult { Kind = Kind.Unknown, Name = n };
    }

    /// <summary>应用函数到球;返回是否应计入投影刷新。</summary>
    public static bool Apply(
        ParseResult fn,
        SceneWorld world,
        Ball ball,
        PublicDefaults defaults,
        Action<string>? warn)
    {
        switch (fn.Kind)
        {
            case Kind.None:
            case Kind.Unknown:
                if (fn.Kind == Kind.Unknown)
                    warn?.Invoke($"未知销毁函数 {fn.Name},仅传送: {ball.Id}");
                return false;

            case Kind.Multiply:
                {
                    var next = ball.Multiplier * (long)fn.Factor;
                    ball.Multiplier = PublicDefaults.ClampMultiplier(next);
                    defaults.ApplyToBall(ball);
                    return true;
                }

            case Kind.Run:
                {
                    var scored = ball.Multiplier;
                    var settled = world.Settlements?.TrySettle("RUN", world, ball, scored, warn) == true;
                    if (!settled)
                        FactionBoard.AddScore(world, ball.Color, scored, warn);
                    ball.Multiplier = PublicDefaults.ClampMultiplier(defaults.InitialMultiplier);
                    defaults.ApplyToBall(ball);
                    // 颜色保持(V6Q13)
                    return true;
                }

            case Kind.Settlement:
                {
                    var value = ball.Multiplier;
                    world.Settlements?.TrySettle(fn.Name!, world, ball, value, warn);
                    ball.Multiplier = PublicDefaults.ClampMultiplier(defaults.InitialMultiplier);
                    defaults.ApplyToBall(ball);
                    return true;
                }

            default:
                return false;
        }
    }
}
