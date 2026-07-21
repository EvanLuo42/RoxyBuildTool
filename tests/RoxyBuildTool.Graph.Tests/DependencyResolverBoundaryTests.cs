using System.Collections.Immutable;
using RoxyBuildTool.Configuration;
using RoxyBuildTool.Model;
using Xunit;

namespace RoxyBuildTool.Graph.Tests;

public sealed class DependencyResolverBoundaryTests
{
    [Fact]
    public void DefinitionGraphLookupsRequireExactlyOneDefinition()
    {
        var module = Module("module");
        var target = Target("target", ["module"]);
        var workspace = new WorkspaceDefinition("workspace", "Workspace", ["target"], "target", false, null);
        var definitions = new DefinitionGraph([module], [target], [workspace]);

        Assert.Same(module, definitions.GetModule("module"));
        Assert.Same(target, definitions.GetTarget("target"));
        Assert.Same(workspace, definitions.GetWorkspace("workspace"));
        Assert.Throws<InvalidOperationException>(() => definitions.GetModule("missing"));
        Assert.Throws<InvalidOperationException>(() => definitions.GetTarget("missing"));
        Assert.Throws<InvalidOperationException>(() => definitions.GetWorkspace("missing"));
    }

    [Fact]
    public void MissingRootAndDependencyProduceStableDiagnostics()
    {
        var definitions = new DefinitionGraph([
            Module("root", dependencies: [new("MissingDependency", DependencyVisibility.Private)]),
        ], [Target("target", ["MissingRoot", "root"])], []);

        var graph = DependencyResolver.Resolve(definitions, definitions.Targets[0], Configuration());

        Assert.Equal(2, graph.Diagnostics.Count(item => item.Code == "RBT2002"));
        Assert.Single(graph.Diagnostics, item => item.Code == "RBT2003");
        Assert.Contains(graph.Diagnostics, item => item.Definition == "MissingRoot");
    }

    [Fact]
    public void BuildOrderOnlySuppressesTheInvalidDependencyDiagnostic()
    {
        var definitions = new DefinitionGraph([
            Module("root", dependencies: [new("missing", DependencyVisibility.BuildOrderOnly)]),
        ], [Target("target", ["root"])], []);

        var graph = DependencyResolver.Resolve(definitions, definitions.Targets[0], Configuration());

        Assert.Single(graph.Diagnostics, item => item.Code == "RBT2002");
        Assert.DoesNotContain(graph.Diagnostics, item => item.Code == "RBT2003");
        Assert.Single(graph.Modules);
    }

    [Fact]
    public void DisabledModulesAreOmittedAndRequiredDisabledDependenciesAreDiagnosed()
    {
        var disabled = Module("disabled") with
        {
            ConditionalRules = [new(BuildProfiles.Development, true, [], [])],
        };
        var definitions = new DefinitionGraph([
            disabled,
            Module("required", dependencies: [new("disabled", DependencyVisibility.Public)]),
            Module("ordered", dependencies: [new("disabled", DependencyVisibility.BuildOrderOnly)]),
        ], [Target("target", ["disabled", "ordered", "required"])], []);

        var graph = DependencyResolver.Resolve(definitions, definitions.Targets[0], Configuration());

        Assert.DoesNotContain(graph.Modules, module => module.Id == "disabled");
        Assert.Single(graph.Diagnostics, item => item.Code == "RBT2003" && item.Definition == "required");
    }

    [Fact]
    public void ConfigurationCallbackConditionalDefinesAndRemovedDependenciesAreApplied()
    {
        var dependency = Module("dependency", publicUsage: Usage(include: "dependency/include"));
        var root = Module("root", dependencies: [new("dependency", DependencyVisibility.Private)]) with
        {
            ConfigureForConfiguration = _ =>
                Module("root", dependencies: [new DependencyEdge("dependency", DependencyVisibility.Private)]) with
                {
                    DisplayName = "Configured Root",
                    ConditionalRules =
                    [new ConditionalModuleRule(BuildProfiles.Development, false, ["CONDITIONAL=1"], ["dependency"])]
                }
        };
        var definitions = new DefinitionGraph([root, dependency], [Target("target", ["root"])], []);

        var graph = DependencyResolver.Resolve(definitions, definitions.Targets[0], Configuration());
        var configured = graph.GetModule("root");

        Assert.Equal("Configured Root", configured.DisplayName);
        Assert.Empty(configured.Dependencies);
        Assert.Contains(configured.PrivateUsage.Defines,
            item => item.Value == "CONDITIONAL=1" && item.Origin == "root:conditional");
        Assert.DoesNotContain(configured.CompileUsage.IncludeDirectories, item => item.Value == "dependency/include");
    }

    [Fact]
    public void ConfigurationCallbackFailuresAreNotMisreportedAsMissingModules()
    {
        var root = Module("root") with
        {
            ConfigureForConfiguration = _ => throw new InvalidOperationException("configuration callback failed")
        };
        var definitions = new DefinitionGraph([root], [Target("target", ["root"])], []);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DependencyResolver.Resolve(definitions, definitions.Targets[0], Configuration()));

        Assert.Equal("configuration callback failed", exception.Message);
    }

    [Fact]
    public void StaticAndSharedLibrariesPublishLinkAndRuntimeArtifacts()
    {
        var definitions = new DefinitionGraph([
            Module("static", ModuleKind.StaticLibrary),
            Module("shared", ModuleKind.SharedLibrary),
            Module("consumer", ModuleKind.Executable, dependencies:
            [
                new("static", DependencyVisibility.Public),
                new("shared", DependencyVisibility.Public),
            ]),
        ], [Target("app", ["consumer"])], []);

        var graph = DependencyResolver.Resolve(definitions, definitions.Targets[0], Configuration());
        var consumer = graph.GetModule("consumer");
        var outputRoot = BuildPathLayout.OutputRoot(graph.Configuration, graph.Target.Id);

        Assert.Equal(
            [$"{outputRoot}/shared.lib", $"{outputRoot}/static.lib"],
            consumer.CompileUsage.LinkInputs.Select(item => item.Value));
        Assert.Equal($"{outputRoot}/shared.dll",
            Assert.Single(graph.GetModule("shared").ConsumerUsage.RuntimeFiles).Value);
    }

    [Fact]
    public void ObjectLibrariesPublishTheirStableObjectFilesInsteadOfASyntheticLibrary()
    {
        var definitions = new DefinitionGraph([
            Module("objects", ModuleKind.ObjectLibrary, sources: ["src/a.cpp", "src/z.cpp", "resources/app.rc"]),
            Module("consumer", ModuleKind.Executable,
                dependencies: [new DependencyEdge("objects", DependencyVisibility.Private)])
        ], [Target("app", ["consumer"])], []);

        var graph = DependencyResolver.Resolve(definitions, definitions.Targets[0], Configuration());
        var linkInputs = graph.GetModule("consumer").CompileUsage.LinkInputs.Select(item => item.Value).ToArray();

        Assert.Equal(3, linkInputs.Length);
        Assert.Equal(2, linkInputs.Count(input => input.EndsWith(".obj", StringComparison.Ordinal)));
        Assert.Single(linkInputs, input => input.EndsWith(".res", StringComparison.Ordinal));
        Assert.DoesNotContain(linkInputs, input => input.EndsWith("objects.lib", StringComparison.Ordinal));
    }

    [Fact]
    public void RuntimeEdgesPropagateOnlyRuntimeFiles()
    {
        var runtimeUsage = new UsageRequirements(
            [new UsageValue("include", "runtime")], [new UsageValue("DEFINE", "runtime")],
            [new UsageValue("runtime.lib", "runtime")],
            [new UsageValue("runtime.dll", "runtime")]);
        var definitions = new DefinitionGraph([
            Module("runtime", publicUsage: runtimeUsage),
            Module("consumer", dependencies: [new("runtime", DependencyVisibility.Runtime)]),
        ], [Target("target", ["consumer"])], []);

        var consumer = DependencyResolver.Resolve(definitions, definitions.Targets[0], Configuration())
            .GetModule("consumer");

        Assert.Empty(consumer.CompileUsage.IncludeDirectories);
        Assert.Empty(consumer.CompileUsage.Defines);
        Assert.Empty(consumer.CompileUsage.LinkInputs);
        Assert.Equal("runtime.dll", Assert.Single(consumer.CompileUsage.RuntimeFiles).Value);
    }

    [Fact]
    public void ResolutionSortsRootsDependenciesAndSources()
    {
        var definitions = new DefinitionGraph([
            Module("b", sources: ["z.cpp", "a.cpp"]),
            Module("a"),
            Module("root", dependencies:
            [
                new("b", DependencyVisibility.Public),
                new("a", DependencyVisibility.Private),
            ]),
        ], [Target("target", ["root", "b"])], []);

        var graph = DependencyResolver.Resolve(definitions, definitions.Targets[0], Configuration());

        Assert.Equal(["a", "b", "root"], graph.Modules.Select(module => module.Id));
        Assert.Equal(["a.cpp", "z.cpp"], graph.GetModule("b").Sources.Select(source => source.Value));
        Assert.Equal(["a", "b"], graph.GetModule("root").Dependencies.Select(edge => edge.Module));
    }

    internal static ModuleDefinition Module(
        string id,
        ModuleKind kind = ModuleKind.HeaderOnly,
        ModuleLanguage language = ModuleLanguage.Cxx,
        string[]? sources = null,
        UsageRequirements? publicUsage = null,
        UsageRequirements? privateUsage = null,
        DependencyEdge[]? dependencies = null) => new(
        id,
        id,
        language,
        kind,
        (sources ?? []).Select(path => new LogicalPath(path)).ToImmutableArray(),
        publicUsage ?? UsageRequirements.Empty,
        privateUsage ?? UsageRequirements.Empty,
        (dependencies ?? []).ToImmutableArray(),
        language == ModuleLanguage.CSharp ? ["net10.0"] : [],
        [],
        []);

    internal static TargetDefinition Target(string id, string[] roots) => new(
        id, id, roots.ToImmutableArray(), new MatrixBuilder().Build());

    internal static UsageRequirements Usage(string? include = null) => new(
        include is null ? [] : [new(include, "test")], [], [], []);

    internal static ConfigurationKey Configuration(string profile = "development") => new([
        RoxyBuildTool.Configuration.Platforms.Windows,
        Architectures.X64,
        new(FragmentIds.Profile, profile),
        RoxyBuildTool.Configuration.Toolchains.Msvc,
        LinkModels.Modular,
    ]);
}