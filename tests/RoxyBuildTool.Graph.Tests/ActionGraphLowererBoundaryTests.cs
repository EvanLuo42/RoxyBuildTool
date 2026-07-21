using System.Collections.Immutable;
using RoxyBuildTool.Abstractions;
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
        var outputRoot = BuildPathLayout.OutputRoot(configuration, "app");
        var header = Module("header", ModuleKind.HeaderOnly, []);
        var objects = Module("objects", ModuleKind.ObjectLibrary, ["src/object.cpp"],
            dependencies: [new("header", DependencyVisibility.BuildOrderOnly)]);
        var library = Module("library", ModuleKind.StaticLibrary, ["src/library.cpp"],
            dependencies: [new("objects", DependencyVisibility.Private)]);
        var shared = Module("shared", ModuleKind.SharedLibrary, ["src/shared.cpp"],
            dependencies: [new("library", DependencyVisibility.Public)]);
        var executable = Module("app", ModuleKind.Executable, ["src/main.cpp", "resources/app.rc"],
                dependencies: [new("shared", DependencyVisibility.Private)]) with
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
        var resource = Assert.Single(lowered.Actions, action => action.Kind == BuildActionKind.ResourceCompile);
        Assert.Equal("rc.exe", resource.Command);
        Assert.Contains("/dROXY_DEVELOPMENT=1", resource.Arguments);
        Assert.Contains("/dAPP=1", resource.Arguments);
        Assert.Single(lowered.Actions, action => action.Kind == BuildActionKind.Archive);
        Assert.Equal(2, lowered.Actions.Count(action => action.Kind == BuildActionKind.Link));
        var copy = Assert.Single(lowered.Actions, action => action.Kind == BuildActionKind.Copy);
        Assert.EndsWith("extra.dll", Assert.Single(copy.Outputs), StringComparison.Ordinal);
        Assert.DoesNotContain(lowered.Actions,
            action => action.Outputs.Any(output => output.EndsWith("already.dll", StringComparison.Ordinal)));

        var appCompile = lowered.Actions.Single(action =>
            action.Kind == BuildActionKind.Compile && action.Inputs.Contains("src/main.cpp"));
        Assert.Contains("/Iinclude", appCompile.Arguments);
        Assert.Contains("/DAPP=1", appCompile.Arguments);
        Assert.Contains("/O2", appCompile.Arguments);
        Assert.Equal(["INCLUDE", "TMP", "TEMP"], appCompile.EnvironmentWhitelist);
        Assert.False(appCompile.Cacheable);
        Assert.False(appCompile.RemoteExecutable);

        var archive = lowered.Actions.Single(action => action.Kind == BuildActionKind.Archive);
        Assert.Equal("lib.exe", archive.Command);
        Assert.False(archive.RemoteExecutable);
        var sharedLink = lowered.Actions.Single(action => action.Id.EndsWith("shared:Link", StringComparison.Ordinal));
        Assert.Contains("/DLL", sharedLink.Arguments);
        Assert.Contains(sharedLink.Arguments, argument => argument.StartsWith("/IMPLIB:", StringComparison.Ordinal));
        var appLink = lowered.Actions.Single(action => action.Id.EndsWith("app:Link", StringComparison.Ordinal));
        Assert.Contains("extra.lib", appLink.Inputs);
        Assert.Contains(Assert.Single(resource.Outputs), appLink.Inputs);
        Assert.Contains(sharedLink.Id, appLink.Dependencies);

        Assert.Contains(lowered.Artifacts, artifact => artifact.Kind == ArtifactKind.StaticLibrary);
        Assert.Contains(lowered.Artifacts, artifact => artifact.Kind == ArtifactKind.SharedLibrary);
        Assert.Contains(lowered.Artifacts, artifact => artifact.Kind == ArtifactKind.ImportLibrary);
        Assert.Contains(lowered.Artifacts, artifact => artifact.Kind == ArtifactKind.ResourceFile);
        Assert.Contains(lowered.Artifacts, artifact => artifact.Kind == ArtifactKind.Executable);
        Assert.Contains(lowered.Artifacts, artifact => artifact.Kind == ArtifactKind.RuntimeFile);
        Assert.All(lowered.Actions.Where(action => action.Kind == BuildActionKind.Compile),
            action => Assert.Empty(action.Dependencies));
        var objectCompile = lowered.Actions.Single(action =>
            action.Kind == BuildActionKind.Compile && action.Inputs.Contains("src/object.cpp"));
        Assert.Contains(objectCompile.Id, archive.Dependencies);
        Assert.Equal("v143", lowered.Toolchain?.VisualStudioPlatformToolset);
        Assert.Equal(lowered.Actions.OrderBy(action => action.Id, StringComparer.Ordinal), lowered.Actions);
        Assert.Empty(lowered.Validate());
    }

    [Fact]
    public void OutputPathsAreConfigurationIsolatedAndObjectNamesDoNotDependOnSourceOrdering()
    {
        var baseConfiguration = DependencyResolverBoundaryTests.Configuration();
        var client = new ConfigurationKey([
            .. baseConfiguration.Values,
            new(new("Game.Flavor"), "client")
        ]);
        var editor = new ConfigurationKey([
            .. baseConfiguration.Values,
            new(new("Game.Flavor"), "editor")
        ]);
        var target = new ConfiguredTarget("app", "App", ["app"]);
        var oneSource = Module("app", ModuleKind.Executable, ["src/z.cpp"]);
        var twoSources = oneSource with { Sources = [new("src/a.cpp"), new("src/z.cpp")] };

        var clientGraph = ActionGraphLowerer.Lower(
            new(client, target, [oneSource], []), Toolchain(), "workspace");
        var editorGraph = ActionGraphLowerer.Lower(
            new(editor, target, [oneSource], []), Toolchain(), "workspace");
        var reorderedGraph = ActionGraphLowerer.Lower(
            new(client, target, [twoSources], []), Toolchain(), "workspace");

        Assert.Empty(clientGraph.Actions.SelectMany(action => action.Outputs)
            .Intersect(editorGraph.Actions.SelectMany(action => action.Outputs), StringComparer.Ordinal));
        var originalObject = clientGraph.Actions.Single(action =>
            action.Kind == BuildActionKind.Compile && action.Inputs.Contains("src/z.cpp")).Outputs.Single();
        var reorderedObject = reorderedGraph.Actions.Single(action =>
            action.Kind == BuildActionKind.Compile && action.Inputs.Contains("src/z.cpp")).Outputs.Single();
        Assert.Equal(originalObject, reorderedObject);
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
            TargetFrameworks = ["net8.0", "net10.0"],
        };
        var graph = new ConfiguredGraph(configuration, new("game", "GameTarget", ["tool"]), [tool, game, native], []);

        var lowered = ActionGraphLowerer.Lower(graph, Toolchain(), "mixed");

        var gameRestore =
            lowered.Actions.Single(action => action.Id.EndsWith("game:DotnetRestore", StringComparison.Ordinal));
        Assert.Contains(".roxy/generated/Vs2022/mixed/Game.csproj", gameRestore.Arguments);
        Assert.Contains("--use-lock-file", gameRestore.Arguments);
        Assert.Contains($"-p:Configuration={BuildConfigurationNames.DisplayName(configuration)}",
            gameRestore.Arguments);
        Assert.Contains("-p:Platform=x64", gameRestore.Arguments);
        Assert.EndsWith("packages.lock.json", Assert.Single(gameRestore.Outputs), StringComparison.Ordinal);
        Assert.False(gameRestore.Cacheable);
        Assert.Empty(gameRestore.Dependencies);
        var gameBuild =
            lowered.Actions.Single(action => action.Id.EndsWith("game:DotnetBuild", StringComparison.Ordinal));
        Assert.Contains(
            lowered.Actions.Single(action => action.Id.EndsWith("native:Archive", StringComparison.Ordinal)).Id,
            gameBuild.Dependencies);

        var toolRestore =
            lowered.Actions.Single(action => action.Id.EndsWith("tool:DotnetRestore", StringComparison.Ordinal));
        Assert.Contains(".roxy/generated/Vs2022/mixed/Tool.Game.csproj", toolRestore.Arguments);
        var toolBuild =
            lowered.Actions.Single(action => action.Id.EndsWith("tool:DotnetBuild", StringComparison.Ordinal));
        Assert.Contains(BuildConfigurationNames.DisplayName(configuration), toolBuild.Arguments);
        Assert.Contains(toolRestore.Id, toolBuild.Dependencies);
        Assert.Contains(toolBuild.Arguments,
            argument => argument.StartsWith("-p:RoxyConfigurationHash=", StringComparison.Ordinal));
        Assert.Contains("-p:Platform=x64", toolBuild.Arguments);
        Assert.Equal(2, toolBuild.Outputs.Length);
        Assert.Contains(toolBuild.Outputs, output => output.Contains("/net8.0/", StringComparison.Ordinal));
        Assert.Contains(toolBuild.Outputs, output => output.Contains("/net10.0/", StringComparison.Ordinal));
        Assert.False(toolBuild.Cacheable);
        Assert.Equal(["DOTNET_ROOT", "NUGET_PACKAGES", "TMP", "TEMP"], toolBuild.EnvironmentWhitelist);

        var copy = lowered.Actions.Single(action =>
            action.Kind == BuildActionKind.Copy && action.Inputs.Contains("runtime/tool.runtime.dll"));
        Assert.Contains(toolBuild.Id, copy.Dependencies);
        Assert.Contains(lowered.Artifacts, artifact => artifact.Kind == ArtifactKind.ManagedAssembly);
    }

    [Fact]
    public void HeaderOnlyModuleNeverCreatesCompileActions()
    {
        var graph = new ConfiguredGraph(DependencyResolverBoundaryTests.Configuration(),
            new("target", "Target", ["header"]),
            [Module("header", ModuleKind.HeaderOnly, ["header-only-metadata.cpp"])], []);

        var lowered = ActionGraphLowerer.Lower(graph, Toolchain(), "workspace");

        Assert.Empty(lowered.Actions);
        Assert.Empty(lowered.Artifacts);
    }

    [Fact]
    public void ToolchainPolicyRequiresProfileAndKnownPolicy()
    {
        var toolchain = Toolchain();

        Assert.True(toolchain.GetPolicy(DependencyResolverBoundaryTests.Configuration()).Optimize);
        Assert.Throws<InvalidOperationException>(() =>
            toolchain.GetPolicy(DependencyResolverBoundaryTests.Configuration("shipping")));
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

    private static ConfiguredModule
        Managed(string id, string displayName, ImmutableArray<DependencyEdge> dependencies) => new(
        id, displayName, ModuleLanguage.CSharp, ModuleKind.CSharpConsoleApplication, [new($"{id}.cs")],
        UsageRequirements.Empty, UsageRequirements.Empty, UsageRequirements.Empty, UsageRequirements.Empty,
        dependencies, ["net10.0"], []);

    internal static ToolchainDescriptor Toolchain() => new(
        new("Msvc14.4"), new("windows"), "x64", "cl.exe", "lib.exe", "link.exe", "v143",
        new Dictionary<string, CxxProfilePolicy>(StringComparer.Ordinal)
        {
            ["debug"] = new(["/Od"], ["/DEBUG"], false, true, true, false),
            ["development"] = new(["/O2", "/DROXY_DEVELOPMENT=1"], ["/DEBUG"], true, true, true, false),
        }.ToImmutableDictionary(StringComparer.Ordinal), CapabilitySet.Empty, "rc.exe");
}