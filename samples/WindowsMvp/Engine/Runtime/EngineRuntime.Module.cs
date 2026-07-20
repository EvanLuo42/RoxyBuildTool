using RoxyBuildTool;

namespace WindowsMvp.Build;

public sealed class EngineRuntimeModule : CxxModule
{
    [Configure]
    private static void ConfigureAll(ModuleRules rules)
    {
        rules.Output = CxxOutput.SharedLibrary;
        rules.Sources.From("Engine/Runtime", "**/*.cpp");
        rules.Public.IncludeDirectories.Add("Engine/Runtime/Public");
        rules.Dependencies.Public<EngineCoreModule>();
    }
}
