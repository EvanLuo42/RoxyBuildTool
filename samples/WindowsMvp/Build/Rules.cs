using RoxyBuildTool;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Configuration;
using RoxyBuildTool.Model;

namespace WindowsMvp.Build;

[BuildFragment("Game.Flavor")]
public enum GameFlavor
{
    Client,
    DedicatedServer,
    Editor,
}

public sealed class WindowsMvpRules;

public abstract class WindowsTarget : BuildTarget
{
    [Configure(Priority = -100)]
    protected static void ConfigureWindows(TargetRules rules)
    {
        rules.Matrix
            .Axis(Platforms.Windows)
            .Axis(Architectures.X64)
            .Axis(BuildProfiles.All.ToArray())
            .Axis(Toolchains.Msvc)
            .Axis(LinkModels.Modular);
    }
}

public sealed class GameTarget : WindowsTarget
{
    [Configure]
    private static void ConfigureTarget(TargetRules rules)
    {
        rules.RootModules.Add<GameExecutableModule>();
        rules.Matrix.Axis(GameFlavor.Client, GameFlavor.DedicatedServer);
    }
}

public sealed class EditorTarget : WindowsTarget
{
    [Configure]
    private static void ConfigureTarget(TargetRules rules)
    {
        rules.RootModules.Add<EditorExecutableModule>();
        rules.Matrix
            .Axis(GameFlavor.Editor)
            .Exclude(configuration => configuration.Is(GameFlavor.Editor) && configuration.Is(BuildProfiles.Shipping),
                "Editor is never shipped.");
    }
}

public sealed class GameWorkspace : BuildWorkspace
{
    [Configure]
    private static void ConfigureWorkspace(WorkspaceRules rules)
    {
        rules.Targets.Add<GameTarget>();
        rules.Targets.Add<EditorTarget>();
        rules.StartupTarget<EditorTarget>();
    }
}
