using System.Collections.Immutable;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Model;
using RoxyBuildTool.Toolchains;
using Xunit;

namespace RoxyBuildTool.Graph.Tests;

public sealed class ActionGraphLowererBoundaryTests
{
    [Fact]
    public void GeneratedDirectoryNamesUsePascalCase()
    {
        var configuration = DependencyResolverBoundaryTests.Configuration();

        Assert.Equal(
            $"Binaries/Windows/X64/Development/{configuration.ShortHash}/GameClient",
            BuildPathLayout.OutputRoot(configuration, "game-client"));
        Assert.Equal(
            $"Intermediate/{configuration.ShortHash}/GameClient/RenderCore",
            BuildPathLayout.IntermediateRoot(configuration, "game-client", "render-core"));
    }

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
    public void StaticLibrariesAbsorbPrivateObjectLibraryFilesExactlyOnce()
    {
        var configuration = DependencyResolverBoundaryTests.Configuration();
        var definitions = new DefinitionGraph([
            DependencyResolverBoundaryTests.Module("leaf", ModuleKind.StaticLibrary),
            DependencyResolverBoundaryTests.Module(
                "objects", ModuleKind.ObjectLibrary, sources: ["src/object.cpp"],
                dependencies: [new("leaf", DependencyVisibility.Private)]),
            DependencyResolverBoundaryTests.Module(
                "library", ModuleKind.StaticLibrary, sources: ["src/library.cpp"],
                dependencies: [new("objects", DependencyVisibility.Private)]),
            DependencyResolverBoundaryTests.Module(
                "app", ModuleKind.Executable, sources: ["src/main.cpp"],
                dependencies: [new("library", DependencyVisibility.Private)]),
        ], [DependencyResolverBoundaryTests.Target("app", ["app"])], []);
        var configured = DependencyResolver.Resolve(definitions, definitions.Targets[0], configuration);

        var lowered = ActionGraphLowerer.Lower(configured, Toolchain(), "workspace");

        var objectOutput = Assert.Single(lowered.Actions,
                action => action.Kind == BuildActionKind.Compile && action.Inputs.Contains("src/object.cpp"))
            .Outputs.Single();
        var archive = Assert.Single(lowered.Actions,
            action => action.Kind == BuildActionKind.Archive &&
                      action.Id.Contains("library", StringComparison.Ordinal));
        var appLink = Assert.Single(lowered.Actions,
            action => action.Kind == BuildActionKind.Link && action.Id.Contains("app", StringComparison.Ordinal));
        Assert.Contains(objectOutput, archive.Inputs);
        Assert.DoesNotContain(objectOutput, appLink.Inputs);
        Assert.Contains(
            $"{BuildPathLayout.OutputRoot(configuration, "app")}/leaf.lib",
            appLink.Inputs);
        Assert.Empty(lowered.Validate());
    }

    [Fact]
    public void StaticLibrariesDoNotReexportAuthoredObjectInputsThatTheyArchive()
    {
        var configuration = DependencyResolverBoundaryTests.Configuration();
        var libraryUsage = new UsageRequirements([], [],
            [new("generated.obj", "test"), new("external.lib", "test")], []);
        var definitions = new DefinitionGraph([
            DependencyResolverBoundaryTests.Module(
                "library", ModuleKind.StaticLibrary, publicUsage: libraryUsage),
            DependencyResolverBoundaryTests.Module(
                "app", ModuleKind.Executable,
                dependencies: [new("library", DependencyVisibility.Private)]),
        ], [DependencyResolverBoundaryTests.Target("app", ["app"])], []);
        var configured = DependencyResolver.Resolve(definitions, definitions.Targets[0], configuration);

        var lowered = ActionGraphLowerer.Lower(configured, Toolchain(), "workspace");

        var archive = Assert.Single(lowered.Actions, action => action.Kind == BuildActionKind.Archive);
        var link = Assert.Single(lowered.Actions, action => action.Kind == BuildActionKind.Link);
        Assert.Contains("generated.obj", archive.Inputs);
        Assert.DoesNotContain("generated.obj", link.Inputs);
        Assert.Contains("external.lib", link.Inputs);
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
    public void NativeSettingsLowerToPchSystemIncludesForcedIncludesArgumentsAndOutputName()
    {
        var configuration = DependencyResolverBoundaryTests.Configuration();
        var module = Module("app", ModuleKind.Executable, ["src/pch.cpp", "src/main.cpp"]) with
        {
            CompileUsage = new UsageRequirements([], [], [], [], [new UsageValue("third_party/sdk", "test")]),
            CxxSettings = new CxxModuleSettings(
                ["/WX"], ["/OPT:ICF"], [], [new LogicalPath("config/forced.h")],
                new LogicalPath("include/pch.h"), new LogicalPath("src/pch.cpp"), "renamed-app")
        };
        var graph = new ConfiguredGraph(configuration, new ConfiguredTarget("app", "App", ["app"]), [module], []);

        var lowered = ActionGraphLowerer.Lower(graph, Toolchain(), "workspace");
        var pchCompile = lowered.Actions.Single(action =>
            action.Kind == BuildActionKind.Compile && action.Inputs.Contains("src/pch.cpp"));
        var mainCompile = lowered.Actions.Single(action =>
            action.Kind == BuildActionKind.Compile && action.Inputs.Contains("src/main.cpp"));
        var link = lowered.Actions.Single(action => action.Kind == BuildActionKind.Link);

        Assert.Contains("/Ycinclude/pch.h", pchCompile.Arguments);
        Assert.Contains("/Yuinclude/pch.h", mainCompile.Arguments);
        Assert.Contains(pchCompile.Id, mainCompile.Dependencies);
        Assert.All([pchCompile, mainCompile], action =>
        {
            Assert.Contains("/external:Ithird_party/sdk", action.Arguments);
            Assert.Contains("/FIconfig/forced.h", action.Arguments);
            Assert.Contains("/WX", action.Arguments);
        });
        Assert.Contains("/OPT:ICF", link.Arguments);
        Assert.Contains(link.Outputs, output => output.EndsWith("/renamed-app.exe", StringComparison.Ordinal));
        Assert.Single(lowered.Artifacts, artifact => artifact.Kind == ArtifactKind.PrecompiledHeader);
        Assert.Empty(lowered.Validate());
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
        id, id, kind, sources.Select(path => new LogicalPath(path)).ToImmutableArray(),
        UsageRequirements.Empty, UsageRequirements.Empty, UsageRequirements.Empty, UsageRequirements.Empty,
        dependencies.IsDefault ? [] : dependencies);

    internal static ToolchainDescriptor Toolchain() => new(
        new("Msvc14.4"), new("windows"), "x64", "cl.exe", "lib.exe", "link.exe", "v143",
        new Dictionary<string, CxxProfilePolicy>(StringComparer.Ordinal)
        {
            ["debug"] = new(["/Od"], ["/DEBUG"], false, true, true, false),
            ["development"] = new(["/O2", "/DROXY_DEVELOPMENT=1"], ["/DEBUG"], true, true, true, false),
        }.ToImmutableDictionary(StringComparer.Ordinal), CapabilitySet.Empty, "rc.exe");
}