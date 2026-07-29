using WBall.Model;

namespace WBall.Editing;

public sealed record BallEditRequest(
    string Id,
    string? Color = null,
    long? Multiplier = null,
    double? Size = null,
    double? Weight = null);

public sealed class BallEditorService(SceneWorld world)
{
    public EditResult Apply(BallEditRequest request)
    {
        var ball = world.FindBall(request.Id?.Trim() ?? "");
        if (ball == null)
            return EditResult.Fail("未找到小球");
        if (request.Color == null && request.Multiplier == null && request.Size == null && request.Weight == null)
            return EditResult.Fail("至少提供一个待修改字段");

        var color = ball.Color;
        if (request.Color != null && !EditValidation.TryNormalizeColor(request.Color, out color))
            return EditResult.Fail("颜色必须是 #RRGGBB");
        if (request.Multiplier is < 1 or > PublicDefaults.MaxMultiplier)
            return EditResult.Fail($"倍率范围为 1~{PublicDefaults.MaxMultiplier}");
        if (request.Size is { } size
            && (!EditValidation.IsFinite(size) || size < PublicDefaults.MinSize || size > PublicDefaults.MaxSize))
            return EditResult.Fail($"Size 范围为 {PublicDefaults.MinSize}~{PublicDefaults.MaxSize}");
        if (request.Weight is { } weight
            && (!EditValidation.IsFinite(weight) || weight < PublicDefaults.MinWeight || weight > PublicDefaults.MaxWeight))
            return EditResult.Fail($"Weight 范围为 {PublicDefaults.MinWeight}~{PublicDefaults.MaxWeight}");

        var multiplier = request.Multiplier ?? ball.Multiplier;
        var finalSize = ball.Size;
        var finalWeight = ball.Weight;
        if (request.Multiplier != null)
        {
            finalSize = world.Defaults.SizeFromMultiplier(multiplier);
            finalWeight = world.Defaults.WeightFromMultiplier(multiplier);
        }

        if (request.Size is { } explicitSize)
            finalSize = PublicDefaults.RoundSize(explicitSize);
        if (request.Weight is { } explicitWeight)
            finalWeight = PublicDefaults.RoundWeight(explicitWeight);

        ball.Color = color;
        ball.Multiplier = multiplier;
        ball.Size = finalSize;
        ball.Weight = finalWeight;
        world.NotifyChanged();
        return EditResult.Ok($"已更新小球 {ball.Id} ×{ball.Multiplier}");
    }
}
