using System.Collections.Immutable;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Configuration;
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
            Module("b", ModuleKind.StaticLibrary, publicIncludes: ["B/Public"],
                dependencies: [new DependencyEdge("a", DependencyVisibility.Private)]),
            Module("c", ModuleKind.Executable, dependencies: [new("b", DependencyVisibility.Public)]),
            Module("d", ModuleKind.HeaderOnly, publicIncludes: ["D/Public"],
                dependencies: [new DependencyEdge("a", DependencyVisibility.Interface)]),
            Module("e", ModuleKind.Executable, dependencies: [new("d", DependencyVisibility.Public)]),
        ], [Target("app", ["c", "e"])], []);

        var graph = DependencyResolver.Resolve(definitions, definitions.Targets[0], TestConfiguration());

        Assert.Contains(graph.GetModule("b").CompileUsage.IncludeDirectories, value => value.Value == "A/Public");
        Assert.DoesNotContain(graph.GetModule("b").ConsumerUsage.IncludeDirectories,
            value => value.Value == "A/Public");
        Assert.Contains(graph.GetModule("c").CompileUsage.IncludeDirectories, value => value.Value == "B/Public");
        Assert.DoesNotContain(graph.GetModule("c").CompileUsage.IncludeDirectories, value => value.Value == "A/Public");
        Assert.DoesNotContain(graph.GetModule("d").CompileUsage.IncludeDirectories, value => value.Value == "A/Public");
        Assert.Contains(graph.GetModule("e").CompileUsage.IncludeDirectories, value => value.Value == "A/Public");
    }

    [Fact]
    public void LoweringProducesNativeActionsWithoutLeakingProjectSystemDetails()
    {
        var definitions = new DefinitionGraph([
            Module("runtime", ModuleKind.SharedLibrary, sources: ["src/runtime.cpp"]),
            Module("NativeApp", ModuleKind.Executable, sources: ["src/main.cpp"],
                dependencies: [new DependencyEdge("runtime", DependencyVisibility.Private)]),
        ], [Target("native", ["NativeApp"])], []);
        var configured = DependencyResolver.Resolve(definitions, definitions.Targets[0], TestConfiguration());

        var actions = ActionGraphLowerer.Lower(configured, Toolchain(), "NativeWorkspace");

        Assert.Contains(actions.Actions, action => action.Kind == BuildActionKind.Compile);
        Assert.Contains(actions.Actions, action => action.Kind == BuildActionKind.Link);
        Assert.DoesNotContain(actions.Actions.SelectMany(action => action.Arguments),
            argument => argument.Contains(".roxy/generated/Vs2022", StringComparison.Ordinal));
        Assert.Empty(actions.Validate());
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
        string[]? sources = null,
        string[]? publicIncludes = null,
        DependencyEdge[]? dependencies = null) => new(
        id,
        id,
        kind,
        (sources ?? []).Select(source => new LogicalPath(source)).ToImmutableArray(),
        new UsageRequirements(
            (publicIncludes ?? []).Select(value => new UsageValue(value, $"{id}:public")).ToImmutableArray(), [], [],
            []),
        UsageRequirements.Empty,
        (dependencies ?? []).ToImmutableArray(),
        []);

    private static TargetDefinition Target(string id, string[] roots) => new(
        id,
        id,
        roots.ToImmutableArray(),
        new MatrixBuilder().Build());

    private static ConfigurationKey TestConfiguration() => new([
        Configuration.Platforms.Windows,
        Architectures.X64,
        BuildProfiles.Development,
        Configuration.Toolchains.Msvc,
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