using WBall.Model;

namespace WBall.Battle;

public interface ISettlementService
{
    bool IsKnown(string name);

    bool TrySettle(
        string name,
        SceneWorld economyWorld,
        Ball ball,
        long value,
        Action<string>? warn);
}
