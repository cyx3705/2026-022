using System.IO;
using AppShell.Core.Commands;
using AppShell.Core.Logging;
using WBall.Data;
using WBall.Editing;
using WBall.Model;

namespace WBall.Commands;

/// <summary>Registers the WBall command surface without owning command behavior.</summary>
public static class WBallCommands
{
    public static void Register(
        CommandRegistry registry,
        SceneWorld world,
        IShellLog log,
        string scenesDirectory,
        ScenePropertyService sceneProperties,
        BallEditorService ballEditor,
        FormulaEditorService formulaEditor,
        FactionEditorService factionEditor)
    {
        Directory.CreateDirectory(scenesDirectory);
        SceneCommands.Register(registry, world, scenesDirectory, sceneProperties);
        SceneFileCommands.Register(registry, world, scenesDirectory, sceneProperties);
        BallCommands.Register(registry, world, sceneProperties, log, ballEditor);
        FormulaCommands.Register(registry, world, formulaEditor);
        GameCommands.Register(registry, world, log, factionEditor);
        SimulationCommands.Register(registry, world, log, sceneProperties);
        WireCommands.Register(registry, world);
        SolidCommands.Register(registry, world);
        sceneProperties.RefreshScenes(scenesDirectory, world.LastScenePath);
    }
}
