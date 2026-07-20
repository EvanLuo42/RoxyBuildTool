using RoxyBuildTool;

namespace WindowsMvp.Build;

public sealed class GameExecutableModule : CxxModule
{
    [Configure]
    private static void ConfigureAll(ModuleRules rules)
    {
        rules.Output = CxxOutput.Executable;
        rules.Sources.From("Game", "**/*.cpp");
        rules.Dependencies.Private<EngineRuntimeModule>();
    }
}
