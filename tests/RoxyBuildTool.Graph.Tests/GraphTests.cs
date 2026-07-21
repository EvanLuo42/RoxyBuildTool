using System.Collections.Immutable;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Configuration;
using RoxyBuildTool.Graph;
using RoxyBuildTool.Model;
using RoxyBuildTool.Toolchains;
using Xunit;

namespace RoxyBuildTool.Graph.Tests;

public sealed class GraphTests
{
    [Fact]
    public void PublicPrivateAndInterfaceEdgesHaveDistinctPropagation()
    {
        var definitions = new DefinitionGraph([
            Module("a", ModuleKind.HeaderOnly, publicIncludes: ["A/Public"]),
            Module("b", ModuleKind.StaticLibrary, publicIncludes: ["B/Public"], dependencies: [new("a", DependencyVisibility.Private)]),
            Module("c", ModuleKind.Executable, dependencies: [new("b", DependencyVisibility.Public)]),
            Module("d", ModuleKind.HeaderOnly, publicIncludes: ["D/Public"], dependencies: [new("a", DependencyVisibility.Interface)]),
            Module("e", ModuleKind.Executable, dependencies: [new("d", DependencyVisibility.Public)]),
        ], [Target("app", ["c", "e"])], []);

        var graph = DependencyResolver.Resolve(definitions, definitions.Targets[0], TestConfiguration());

        Assert.Contains(graph.GetModule("b").CompileUsage.IncludeDirectories, value => value.Value == "A/Public");
        Assert.DoesNotContain(graph.GetModule("b").ConsumerUsage.IncludeDirectories, value => value.Value == "A/Public");
        Assert.Contains(graph.GetModule("c").CompileUsage.IncludeDirectories, value => value.Value == "B/Public");
        Assert.DoesNotContain(graph.GetModule("c").CompileUsage.IncludeDirectories, value => value.Value == "A/Public");
        Assert.DoesNotContain(graph.GetModule("d").CompileUsage.IncludeDirectories, value => value.Value == "A/Public");
        Assert.Contains(graph.GetModule("e").CompileUsage.IncludeDirectories, value => value.Value == "A/Public");
    }

    [Fact]
    public void LoweringProducesTypedNativeManagedAndStagingActions()
    {
        var definitions = new DefinitionGraph([
            Module("runtime", ModuleKind.SharedLibrary, sources: ["src/runtime.cpp"]),
            Module("NativeApp", ModuleKind.Executable, sources: ["src/main.cpp"], dependencies: [new("runtime", DependencyVisibility.Private)]),
            Module("ManagedApp", ModuleKind.CSharpConsoleApplication, ModuleLanguage.CSharp,
                sources: ["managed/Program.cs"], dependencies: [new("runtime", DependencyVisibility.Runtime)]),
        ], [Target("mixed", ["NativeApp", "ManagedApp"])], []);
        var configured = DependencyResolver.Resolve(definitions, definitions.Targets[0], TestConfiguration());

        var actions = ActionGraphLowerer.Lower(configured, Toolchain(), "MixedWorkspace");

        Assert.Contains(actions.Actions, action => action.Kind == BuildActionKind.Compile);
        Assert.Contains(actions.Actions, action => action.Kind == BuildActionKind.Link);
        Assert.Contains(actions.Actions, action => action.Kind == BuildActionKind.DotNetRestore);
        Assert.Contains(actions.Actions, action => action.Kind == BuildActionKind.DotNetBuild);
        Assert.Contains(actions.Actions, action => action.Kind == BuildActionKind.Copy && action.Id.Contains("ManagedApp", StringComparison.Ordinal));
        Assert.Empty(actions.Validate());
        Assert.All(actions.Actions.Where(action => action.Kind is BuildActionKind.DotNetBuild or BuildActionKind.DotNetRestore),
            action => Assert.False(action.RemoteExecutable));
    }

    [Fact]
    public void DependencyCycleIncludesTheShortestObservedPath()
    {
        var definitions = new DefinitionGraph([
            Module("a", ModuleKind.StaticLibrary, dependencies: [new("b", DependencyVisibility.Public)]),
            Module("b", ModuleKind.StaticLibrary, dependencies: [new("a", DependencyVisibility.Public)]),
        ], [Target("cycle", ["a"])], []);

        var graph = DependencyResolver.Resolve(definitions, definitions.Targets[0], TestConfiguration());

        var diagnostic = Assert.Single(graph.Diagnostics, diagnostic => diagnostic.Code == "RBT2001");
        Assert.Contains("a -> b -> a", diagnostic.Message, StringComparison.Ordinal);
    }

    private static ModuleDefinition Module(
        string id,
        ModuleKind kind,
        ModuleLanguage language = ModuleLanguage.Cxx,
        string[]? sources = null,
        string[]? publicIncludes = null,
        DependencyEdge[]? dependencies = null) => new(
            id,
            id,
            language,
            kind,
            (sources ?? []).Select(source => new LogicalPath(source)).ToImmutableArray(),
            new((publicIncludes ?? []).Select(value => new UsageValue(value, $"{id}:public")).ToImmutableArray(), [], [], []),
            UsageRequirements.Empty,
            (dependencies ?? []).ToImmutableArray(),
            language == ModuleLanguage.CSharp ? ["net10.0"] : [],
            [],
            []);

    private static TargetDefinition Target(string id, string[] roots) => new(
        id,
        id,
        roots.ToImmutableArray(),
        new MatrixBuilder().Build());

    private static ConfigurationKey TestConfiguration() => new([
        RoxyBuildTool.Configuration.Platforms.Windows,
        Architectures.X64,
        BuildProfiles.Development,
        RoxyBuildTool.Configuration.Toolchains.Msvc,
        LinkModels.Modular,
    ]);

    private static ToolchainDescriptor Toolchain() => new(
        new("Msvc14.4"),
        new("windows"),
        "x64",
        "cl.exe",
        "lib.exe",
        "link.exe",
        "v143",
        new Dictionary<string, CxxProfilePolicy>
        {
            ["development"] = new(["/O2"], ["/DEBUG"], true, true, true, false),
        }.ToImmutableDictionary(StringComparer.Ordinal),
        CapabilitySet.Empty);
}
