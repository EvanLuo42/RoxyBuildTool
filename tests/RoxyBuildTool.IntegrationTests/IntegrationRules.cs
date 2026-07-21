using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Configuration;

namespace RoxyBuildTool.IntegrationTests;

[BuildFragment("Integration.Flavor")]
public enum IntegrationFlavor
{
    Client,
    Editor,
}

public enum UnannotatedFlavor
{
    Value,
}

public sealed class IntegrationRulesModule : IRulesModule
{
    public void Register(BuildRegistry registry)
    {
        registry.AddModule<IntegrationHeadersModule>();
        registry.AddModule<IntegrationNativeModule>();
        registry.AddModule<IntegrationOptionalModule>();
        registry.AddModule<IntegrationApplicationModule>();
        registry.AddTarget<IntegrationTarget>();
        registry.AddWorkspace<IntegrationWorkspace>();
    }
}

public sealed class IntegrationHeadersModule : CxxModule
{
    [Configure]
    private static void ConfigureModule(ModuleRules rules)
    {
        rules.Output = CxxOutput.HeaderOnly;
        rules.Public.IncludeDirectories.Add("include");
        rules.Public.Defines.Add("INTEGRATION_HEADERS=1");
    }
}

public sealed class IntegrationNativeModule : CxxModule
{
    [Configure(Priority = -10)]
    private static void ConfigureModule(ModuleRules rules)
    {
        rules.Output = CxxOutput.SharedLibrary;
        rules.Sources.From(".", "**/*.cpp");
        rules.Sources.Exclude("excluded.cpp");
        rules.Sources.Exclude("filtered/**");
        rules.Public.IncludeDirectories.Add("include");
        rules.Public.IncludeDirectories.Add("include");
        rules.Private.Defines.Add("INTEGRATION_NATIVE=1");
        rules.Public.LinkInputs.Add("external/base.lib");
        rules.Public.RuntimeFiles.Add("external/base.dll");
        rules.Dependencies.Public<IntegrationHeadersModule>();
    }

    [Configure<IntegrationFlavor>("editor", Priority = 10)]
    private static void ConfigureEditor(ModuleRules rules)
    {
        rules.Private.Defines.Add("NATIVE_EDITOR=1");
        rules.Sources.From("filtered", "**/*.cpp");
    }
}

public sealed class IntegrationOptionalModule : CxxModule
{
    [Configure]
    private static void ConfigureModule(ModuleRules rules)
    {
        rules.Output = CxxOutput.ObjectLibrary;
        rules.Dependencies.Private<IntegrationHeadersModule>();
        rules.When(IntegrationFlavor.Editor)
            .AddDefine("OPTIONAL_EDITOR=1")
            .RemoveDependency<IntegrationHeadersModule>();
        rules.When(IntegrationFlavor.Client).Disable();
    }
}

public sealed class IntegrationApplicationModule : CxxModule
{
    [Configure]
    private static void ConfigureModule(ModuleRules rules)
    {
        rules.Output = CxxOutput.Executable;
        rules.Dependencies.Public<IntegrationNativeModule>();
        rules.Dependencies.Interface<IntegrationHeadersModule>();
        rules.Dependencies.BuildOrderOnly<IntegrationOptionalModule>();
    }
}

public sealed class IntegrationTarget : BuildTarget
{
    [Configure]
    private static void ConfigureTarget(TargetRules rules)
    {
        rules.RootModules.Add<IntegrationApplicationModule>();
        rules.RootModules.Add<IntegrationOptionalModule>();
        rules.Matrix
            .Axis(Configuration.Platforms.Windows)
            .Axis(Architectures.X64)
            .Axis(BuildProfiles.Debug, BuildProfiles.Development, BuildProfiles.Shipping)
            .Axis(Configuration.Toolchains.Msvc)
            .Axis(LinkModels.Modular)
            .Axis(IntegrationFlavor.Client, IntegrationFlavor.Editor)
            .Exclude(view => view.Is(IntegrationFlavor.Editor) && view.Is(BuildProfiles.Shipping),
                "Editor shipping is unsupported");
    }
}

public sealed class IntegrationWorkspace : BuildWorkspace
{
    [Configure]
    private static void ConfigureWorkspace(WorkspaceRules rules)
    {
        rules.Targets.Add<IntegrationTarget>();
        rules.Targets.Add<IntegrationTarget>();
        rules.StartupTarget<IntegrationTarget>();
    }
}

public sealed class CycleRulesModule : IRulesModule
{
    public void Register(BuildRegistry registry)
    {
        registry.AddModule<CycleAModule>();
        registry.AddModule<CycleBModule>();
        registry.AddTarget<CycleTarget>();
        registry.AddWorkspace<CycleWorkspace>();
    }
}

public sealed class CycleAModule : CxxModule
{
    [Configure]
    private static void ConfigureModule(ModuleRules rules) => rules.Dependencies.Public<CycleBModule>();
}

public sealed class CycleBModule : CxxModule
{
    [Configure]
    private static void ConfigureModule(ModuleRules rules) => rules.Dependencies.Public<CycleAModule>();
}

public sealed class CycleTarget : BuildTarget
{
    [Configure]
    private static void ConfigureTarget(TargetRules rules)
    {
        rules.RootModules.Add<CycleAModule>();
        rules.Matrix
            .Axis(Configuration.Platforms.Windows)
            .Axis(Architectures.X64)
            .Axis(BuildProfiles.Debug)
            .Axis(Configuration.Toolchains.Msvc)
            .Axis(LinkModels.Modular);
    }
}

public sealed class CycleWorkspace : BuildWorkspace
{
    [Configure]
    private static void ConfigureWorkspace(WorkspaceRules rules)
    {
        rules.Targets.Add<CycleTarget>();
    }
}