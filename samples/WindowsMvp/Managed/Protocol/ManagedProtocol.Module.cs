using RoxyBuildTool;

namespace WindowsMvp.Build;

public sealed class ManagedProtocolModule : CSharpModule
{
    [Configure]
    private static void ConfigureAll(CSharpModuleRules rules)
    {
        rules.ManagedOutput = CSharpOutput.ClassLibrary;
        rules.Sources.From("Managed/Protocol", "**/*.cs");
        rules.Sources.Exclude("**/*.Module.cs");
        rules.TargetFrameworks.Add("net10.0");
        rules.RootNamespace = "WindowsMvp.Managed.Protocol";
    }
}
