using RoxyBuildTool;

namespace WindowsMvp.Build;

public sealed class EngineCoreModule : CxxModule
{
    [Configure]
    private static void ConfigureAll(ModuleRules rules)
    {
        rules.Output = CxxOutput.StaticLibrary;
        rules.Sources.From("Engine/Core", "**/*.cpp");
        rules.Public.IncludeDirectories.Add("Engine/Core/Public");
        rules.Private.IncludeDirectories.Add("Engine/Core/Private");
        rules.Public.Defines.Add("ROXY_WITH_CORE=1");
        rules.Dependencies.Public<EngineHeadersModule>();
    }

    [Configure<GameFlavor>(nameof(GameFlavor.DedicatedServer), Priority = 100)]
    private static void ConfigureDedicatedServer(ModuleRules rules) =>
        rules.Private.Defines.Add("ROXY_DEDICATED_SERVER=1");
}
