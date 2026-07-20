using RoxyBuildTool;

namespace WindowsMvp.Build;

public sealed class ManagedToolModule : CSharpModule
{
    [Configure]
    private static void ConfigureAll(CSharpModuleRules rules)
    {
        rules.ManagedOutput = CSharpOutput.ConsoleApplication;
        rules.Sources.From("Managed/Tool", "**/*.cs");
        rules.Sources.Exclude("**/*.Module.cs");
        rules.TargetFrameworks.Add("net10.0");
        rules.Dependencies.Public<ManagedProtocolModule>();
        rules.Dependencies.Runtime<EngineRuntimeModule>();
        rules.RootNamespace = "WindowsMvp.Managed.Tool";
    }
}
