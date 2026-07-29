using WBall.Model;

namespace WBall.Editing;

public sealed record FormulaEditRequest(
    double? SizeBase = null,
    double? SizeScale = null,
    double? WeightBase = null,
    double? WeightScale = null,
    long? InitialMultiplier = null,
    bool RecalculateAll = false);

public sealed class FormulaEditorService(SceneWorld world, string dataRoot)
{
    public EditResult Apply(FormulaEditRequest request)
    {
        if (request.SizeBase == null
            && request.SizeScale == null
            && request.WeightBase == null
            && request.WeightScale == null
            && request.InitialMultiplier == null)
        {
            return EditResult.Fail("至少提供一个公式字段");
        }

        foreach (var value in new[] { request.SizeBase, request.SizeScale, request.WeightBase, request.WeightScale })
        {
            if (value is { } number && !EditValidation.IsFinite(number))
                return EditResult.Fail("公式参数必须是有限数");
        }

        if (request.InitialMultiplier is < 1 or > PublicDefaults.MaxMultiplier)
            return EditResult.Fail($"初始倍率范围为 1~{PublicDefaults.MaxMultiplier}");

        var current = world.Defaults;
        var candidate = new PublicDefaults
        {
            SizeBase = request.SizeBase ?? current.SizeBase,
            SizeScale = request.SizeScale ?? current.SizeScale,
            WeightBase = request.WeightBase ?? current.WeightBase,
            WeightScale = request.WeightScale ?? current.WeightScale,
            InitialMultiplier = request.InitialMultiplier ?? current.InitialMultiplier,
        };

        try
        {
            PublicDefaultsStore.Save(dataRoot, candidate);
        }
        catch (Exception ex)
        {
            return EditResult.Fail($"公式保存失败: {ex.Message}");
        }

        world.Defaults = candidate;
        var recalculated = 0;
        if (request.RecalculateAll)
        {
            foreach (var ball in world.Balls)
            {
                candidate.ApplyToBall(ball);
                recalculated++;
            }
        }

        world.NotifyChanged(markDirty: request.RecalculateAll);
        var suffix = request.RecalculateAll ? $"，已重算 {recalculated} 个球" : "";
        return EditResult.Ok(
            $"已更新公式 sizeBase={candidate.SizeBase:0.###} sizeScale={candidate.SizeScale:0.###} "
            + $"weightBase={candidate.WeightBase:0.###} weightScale={candidate.WeightScale:0.###} "
            + $"initial={candidate.InitialMultiplier}{suffix}");
    }
}
