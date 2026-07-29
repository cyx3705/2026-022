using WBall.Game;
using WBall.Model;

namespace WBall.Editing;

public sealed record FactionEditRequest(
    string Id,
    string? Name = null,
    string? Color = null,
    int? InitialBalls = null,
    long? InitialMultiplier = null,
    long? Score = null);

public sealed class FactionEditorService(SceneWorld world)
{
    public EditResult Apply(FactionEditRequest request)
    {
        var faction = world.FindFaction(request.Id?.Trim() ?? "");
        if (faction == null)
            return EditResult.Fail("未找到阵营");
        if (request.Name == null
            && request.Color == null
            && request.InitialBalls == null
            && request.InitialMultiplier == null
            && request.Score == null)
        {
            return EditResult.Fail("至少提供一个待修改字段");
        }

        var name = faction.Name;
        if (request.Name != null)
        {
            name = request.Name.Trim();
            if (name.Length == 0)
                return EditResult.Fail("阵营名称不能为空");
        }

        var color = faction.Color;
        if (request.Color != null && !EditValidation.TryNormalizeColor(request.Color, out color))
            return EditResult.Fail("颜色必须是 #RRGGBB");
        if (request.InitialBalls is < 0)
            return EditResult.Fail("初始球数不能为负数");
        if (request.InitialMultiplier is < 1 or > PublicDefaults.MaxMultiplier)
            return EditResult.Fail($"初始倍率范围为 1~{PublicDefaults.MaxMultiplier}");
        if (request.Score is < 0)
            return EditResult.Fail("积分不能为负数");

        faction.Name = name;
        faction.Color = color;
        faction.InitialBalls = request.InitialBalls ?? faction.InitialBalls;
        faction.InitialMultiplier = request.InitialMultiplier ?? faction.InitialMultiplier;
        faction.Score = request.Score ?? faction.Score;
        world.NotifyChanged(markDirty: false);
        return EditResult.Ok(
            $"已更新阵营 {faction.Id} color={faction.Color} balls={faction.InitialBalls} "
            + $"×{faction.InitialMultiplier} score={faction.Score}");
    }
}
