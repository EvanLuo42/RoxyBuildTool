using System.Collections.Immutable;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Graph;
using RoxyBuildTool.Model;
using RoxyBuildTool.Toolchains;
using Xunit;

namespace RoxyBuildTool.Graph.Tests;

public sealed class ActionGraphLowererBoundaryTests
{
    [Fact]
    public void NativeLoweringCoversEveryOutputKindAndRuntimeStagingBoundary()
    {
        var configuration = DependencyResolverBoundaryTests.Configuration();
        var outputRoot = "out/windows/x64/development/app";
        var header = Module("header", ModuleKind.HeaderOnly, []);
        var objects = Module("objects", ModuleKind.ObjectLibrary, ["src/object.cpp"], dependencies: [new("header", DependencyVisibility.BuildOrderOnly)]);
        var library = Module("library", ModuleKind.StaticLibrary, ["src/library.cpp"], dependencies: [new("objects", DependencyVisibility.Private)]);
        var shared = Module("shared", ModuleKind.SharedLibrary, ["src/shared.cpp"], dependencies: [new("library", DependencyVisibility.Public)]);
        var executable = Module("app", ModuleKind.Executable, ["src/main.cpp"], dependencies: [new("shared", DependencyVisibility.Private)]) with
        {
            CompileUsage = new(
                [new("include", "test")],
                [new("APP=1", "test")],
                [new("extra.lib", "test")],
                [new("runtime/extra.dll", "test"), new($"{outputRoot}/already.dll", "test")]),
        };
        var graph = new ConfiguredGraph(configuration, new("app", "AppTarget", ["app"]),
            [executable, shared, library, objects, header], []);

        var lowered = ActionGraphLowerer.Lower(graph, Toolchain(), "workspace");

        Assert.Equal(4, lowered.Actions.Count(action => action.Kind == BuildActionKind.Compile));
        Assert.Single(lowered.Actions, action => action.Kind == BuildActionKind.Archive);
        Assert.Equal(2, lowered.Actions.Count(action => action.Kind == BuildActionKind.Link));
        var copy = Assert.Single(lowered.Actions, action => action.Kind == BuildActionKind.Copy);
        Assert.EndsWith("extra.dll", Assert.Single(copy.Outputs), StringComparison.Ordinal);
        Assert.DoesNotContain(lowered.Actions, action => action.Outputs.Any(output => output.EndsWith("already.dll", StringComparison.Ordinal)));

        var appCompile = lowered.Actions.Single(action => action.Id.EndsWith("app:Compile0000", StringComparison.Ordinal));
        Assert.Contains("/Iinclude", appCompile.Arguments);
        Assert.Contains("/DAPP=1", appCompile.Arguments);
        Assert.Contains("/O2", appCompile.Arguments);
        Assert.Equal(["INCLUDE", "TMP", "TEMP"], appCompile.EnvironmentWhitelist);

        var archive = lowered.Actions.Single(action => action.Kind == BuildActionKind.Archive);
        Assert.Equal("lib.exe", archive.Command);
        Assert.False(archive.RemoteExecutable);
        var sharedLink = lowered.Actions.Single(action => action.Id.EndsWith("shared:Link", StringComparison.Ordinal));
        Assert.Contains("/DLL", sharedLink.Arguments);
        Assert.Contains(sharedLink.Arguments, argument => argument.StartsWith("/IMPLIB:", StringComparison.Ordinal));
        var appLink = lowered.Actions.Single(action => action.Id.EndsWith("app:Link", StringComparison.Ordinal));
        Assert.Contains("extra.lib", appLink.Inputs);
        Assert.Contains(sharedLink.Id, appLink.Dependencies);

        Assert.Contains(lowered.Artifacts, artifact => artifact.Kind == ArtifactKind.StaticLibrary);
        Assert.Contains(lowered.Artifacts, artifact => artifact.Kind == ArtifactKind.SharedLibrary);
        Assert.Contains(lowered.Artifacts, artifact => artifact.Kind == ArtifactKind.Executable);
        Assert.Contains(lowered.Artifacts, artifact => artifact.Kind == ArtifactKind.RuntimeFile);
        Assert.Equal(lowered.Actions.OrderBy(action => action.Id, StringComparer.Ordinal), lowered.Actions);
        Assert.Empty(lowered.Validate());
    }

    [Fact]
    public void ManagedLoweringUsesStableProjectNamesDependenciesAndRuntimeCopies()
    {
        var configuration = DependencyResolverBoundaryTests.Configuration("debug");
        var native = Module("native", ModuleKind.StaticLibrary, ["native.cpp"]);
        var game = Managed("game", "GameExecutableModule", [new("native", DependencyVisibility.BuildOrderOnly)]);
        var tool = Managed("tool", "ToolModule", [new("game", DependencyVisibility.Public)]) with
        {
            CompileUsage = new([], [], [], [new("runtime/tool.runtime.dll", "test")]),
        };
        var graph = new ConfiguredGraph(configuration, new("game", "GameTarget", ["tool"]), [tool, game, native], []);

        var lowered = ActionGraphLowerer.Lower(graph, Toolchain(), "mixed");

        var gameRestore = lowered.Actions.Single(action => action.Id.EndsWith("game:DotnetRestore", StringComparison.Ordinal));
        Assert.Contains(".roxy/generated/vs2022/mixed/Game.csproj", gameRestore.Arguments);
        Assert.Contains(lowered.Actions.Single(action => action.Id.EndsWith("native:Archive", StringComparison.Ordinal)).Id,
            gameRestore.Dependencies);

        var toolRestore = lowered.Actions.Single(action => action.Id.EndsWith("tool:DotnetRestore", StringComparison.Ordinal));
        Assert.Contains(".roxy/generated/vs2022/mixed/Tool.Game.csproj", toolRestore.Arguments);
        var toolBuild = lowered.Actions.Single(action => action.Id.EndsWith("tool:DotnetBuild", StringComparison.Ordinal));
        Assert.Contains("Debug", toolBuild.Arguments);
        Assert.Contains(toolRestore.Id, toolBuild.Dependencies);
        Assert.Contains(toolBuild.Arguments, argument => argument.StartsWith("-p:RoxyConfigurationHash=", StringComparison.Ordinal));
        Assert.Equal(["DOTNET_ROOT", "NUGET_PACKAGES", "TMP", "TEMP"], toolBuild.EnvironmentWhitelist);

        var copy = lowered.Actions.Single(action => action.Id.EndsWith("tool:CopyTool.Runtime", StringComparison.Ordinal));
        Assert.Contains(toolBuild.Id, copy.Dependencies);
        Assert.Contains(lowered.Artifacts, artifact => artifact.Kind == ArtifactKind.ManagedAssembly);
    }

    [Fact]
    public void HeaderOnlyModuleWithNoSourcesDoesNotCreateAnAction()
    {
        var graph = new ConfiguredGraph(DependencyResolverBoundaryTests.Configuration(),
            new("target", "Target", ["header"]), [Module("header", ModuleKind.HeaderOnly, [])], []);

        var lowered = ActionGraphLowerer.Lower(graph, Toolchain(), "workspace");

        Assert.Empty(lowered.Actions);
        Assert.Empty(lowered.Artifacts);
    }

    [Fact]
    public void ToolchainPolicyRequiresProfileAndKnownPolicy()
    {
        var toolchain = Toolchain();

        Assert.True(toolchain.GetPolicy(DependencyResolverBoundaryTests.Configuration()).Optimize);
        Assert.Throws<InvalidOperationException>(() => toolchain.GetPolicy(DependencyResolverBoundaryTests.Configuration("shipping")));
        Assert.Throws<InvalidOperationException>(() => toolchain.GetPolicy(new([])));
    }

    private static ConfiguredModule Module(
        string id,
        ModuleKind kind,
        ImmutableArray<string> sources,
        ImmutableArray<DependencyEdge> dependencies = default) => new(
        id, id, ModuleLanguage.Cxx, kind, sources.Select(path => new LogicalPath(path)).ToImmutableArray(),
        UsageRequirements.Empty, UsageRequirements.Empty, UsageRequirements.Empty, UsageRequirements.Empty,
        dependencies.IsDefault ? [] : dependencies, [], []);

    private static ConfiguredModule Managed(string id, string displayName, ImmutableArray<DependencyEdge> dependencies) => new(
        id, displayName, ModuleLanguage.CSharp, ModuleKind.CSharpConsoleApplication, [new($"{id}.cs")],
        UsageRequirements.Empty, UsageRequirements.Empty, UsageRequirements.Empty, UsageRequirements.Empty,
        dependencies, ["net10.0"], []);

    internal static ToolchainDescriptor Toolchain() => new(
        new("Msvc14.4"), new("windows"), "x64", "cl.exe", "lib.exe", "link.exe", "v143",
        new Dictionary<string, CxxProfilePolicy>(StringComparer.Ordinal)
        {
            ["debug"] = new(["/Od"], ["/DEBUG"], false, true, true, false),
            ["development"] = new(["/O2"], ["/DEBUG"], true, true, true, false),
        }.ToImmutableDictionary(StringComparer.Ordinal), CapabilitySet.Empty);
}
