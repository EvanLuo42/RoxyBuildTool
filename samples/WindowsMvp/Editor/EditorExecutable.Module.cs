using RoxyBuildTool;

namespace WindowsMvp.Build;

public sealed class EditorExecutableModule : CxxModule
{
    [Configure]
    private static void ConfigureAll(ModuleRules rules)
    {
        rules.Output = CxxOutput.Executable;
        rules.Sources.From("Editor", "**/*.cpp");
        rules.Dependencies.Private<EngineRuntimeModule>();
    }
}
