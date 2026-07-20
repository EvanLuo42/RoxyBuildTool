using RoxyBuildTool;

namespace WindowsMvp.Build;

public sealed class EngineHeadersModule : CxxModule
{
    [Configure]
    private static void ConfigureAll(ModuleRules rules)
    {
        rules.Output = CxxOutput.HeaderOnly;
        rules.Public.IncludeDirectories.Add("Engine/Headers");
        rules.Public.Defines.Add("ROXY_ENGINE_HEADERS=1");
    }
}
