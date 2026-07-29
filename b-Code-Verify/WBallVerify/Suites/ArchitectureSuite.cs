using WBall.Model;

namespace WBall.Verify.Suites;

internal static class ArchitectureSuite
{
    private static readonly HashSet<string> ForbiddenCoreReferences = new(StringComparer.OrdinalIgnoreCase)
    {
        "PresentationCore",
        "PresentationFramework",
        "WindowsBase",
        "OneHistory.AppShell.Core",
        "OneHistory.AppShell.Services",
        "OneHistory.AppShell.Shell",
    };

    public static void Run(VerifyRun run)
    {
        var core = typeof(SceneWorld).Assembly;
        var coreReferences = core.GetReferencedAssemblies().Select(x => x.Name ?? "").ToArray();
        run.Check("architecture core assembly is isolated",
            core.GetName().Name == "WBall.Core"
            && !coreReferences.Any(ForbiddenCoreReferences.Contains),
            $"assembly={core.GetName().Name} refs={string.Join(',', coreReferences)}");

        var application = typeof(SceneStore).Assembly;
        var applicationReferences = application.GetReferencedAssemblies().Select(x => x.Name ?? "").ToArray();
        run.Check("architecture application only depends inward",
            application.GetName().Name == "WBall.Application"
            && applicationReferences.Contains("WBall.Core", StringComparer.OrdinalIgnoreCase)
            && !applicationReferences.Any(ForbiddenCoreReferences.Contains),
            $"assembly={application.GetName().Name} refs={string.Join(',', applicationReferences)}");
    }
}
