using System.Collections.Immutable;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Model;
using RoxyBuildTool.Platforms.Windows;
using RoxyBuildTool.Toolchains;
using Xunit;

namespace RoxyBuildTool.Graph.Tests;

public sealed class WorkspaceAndPlatformTests
{
    [Fact]
    public void WorkspaceAssemblyGroupsVariantsNamesProjectsAndAddsBuildHost()
    {
        var debug = DependencyResolverBoundaryTests.Configuration("debug");
        var development = DependencyResolverBoundaryTests.Configuration();
        var dependency = Configured("core", "CoreModule", ModuleKind.StaticLibrary);
        var gameDebug = Configured("game", "GameExecutableModule", ModuleKind.Executable);
        var gameDevelopment = gameDebug with
        {
            Sources = [new("development.cpp")],
            Dependencies = [new("core", DependencyVisibility.Public)],
        };
        var configuredGraphs = new[]
        {
            new ConfiguredGraph(development, new("game", "GameTarget", ["game"]), [gameDevelopment, dependency], []),
            new ConfiguredGraph(debug, new("game", "GameTarget", ["game"]), [gameDebug, dependency], []),
        };
        var actionGraphs = new[]
        {
            new ActionGraph(development, "game", [], []),
            new ActionGraph(debug, "game", [], []),
        };
        var definition =
            new WorkspaceDefinition("workspace", "Workspace", ["game"], "game", true, new("Build/Rules.csproj"));

        var workspace = WorkspaceAssembler.Assemble(definition, configuredGraphs, actionGraphs);

        Assert.Equal("Workspace", workspace.Name);
        Assert.Equal("game", workspace.StartupTarget);
        Assert.Equal(["Game.Core", "Game.Game", "BuildRules"], workspace.Projects.Select(project => project.Id));
        var game = workspace.Projects.Single(project => project.Id == "Game.Game");
        Assert.Equal("Game", game.Name);
        Assert.Equal(2, game.Variants.Length);
        Assert.Equal(["Game.Core"], game.ProjectDependencies);
        var dependencyVariant = Assert.Single(game.DependencyVariants);
        Assert.Equal(development, dependencyVariant.Configuration);
        Assert.Equal("Game.Core", dependencyVariant.ProjectId);
        var core = workspace.Projects.Single(project => project.Id == "Game.Core");
        Assert.Equal("Core.Game", core.Name);
        var host = workspace.Projects.Single(project => project.IsBuildHost);
        Assert.Equal(new LogicalPath("Build/Rules.csproj"), host.ImportedProject);
        Assert.Equal(["Debug", "Development"],
            workspace.ConfiguredGraphs.Select(graph => Profile(graph.Configuration)));
        Assert.Equal(["Debug", "Development"], workspace.ActionGraphs.Select(graph => Profile(graph.Configuration)));
    }

    [Fact]
    public void WorkspaceAssemblyHandlesEqualPlainNamesAndOptionalBuildHost()
    {
        var configuration = DependencyResolverBoundaryTests.Configuration();
        var module = Configured("plain", "Plain", ModuleKind.HeaderOnly);
        var graph = new ConfiguredGraph(configuration, new("plain", "Plain", ["plain"]), [module], []);
        var definition = new WorkspaceDefinition("plain", "Plain", ["plain"], "plain", false, new("ignored.csproj"));

        var workspace = WorkspaceAssembler.Assemble(definition, [graph], []);

        Assert.Equal("Plain", Assert.Single(workspace.Projects).Name);
        Assert.DoesNotContain(workspace.Projects, project => project.IsBuildHost);
    }

    [Fact]
    public void WindowsPluginRegistersDescriptorsAndEveryProfilePolicy()
    {
        var registry = new RecordingRegistry();
        var plugin = new WindowsPlatformPlugin();

        plugin.Register(registry);

        Assert.Equal(new PluginId("Roxy.Windows"), plugin.Id);
        Assert.Equal(new Version(0, 1, 0), plugin.Version);
        Assert.True(plugin.Capabilities.Contains("Platform.Windows"));
        var platform =
            Assert.IsType<PlatformDescriptor>(registry.Services.Single(service => service is PlatformDescriptor));
        Assert.Equal(new PlatformId("windows"), platform.Id);
        Assert.Equal(["X64"], platform.Architectures);
        var toolchain =
            Assert.IsType<ToolchainDescriptor>(registry.Services.Single(service => service is ToolchainDescriptor));
        Assert.Equal("cl.exe", toolchain.Compiler);
        Assert.Equal("lib.exe", toolchain.Librarian);
        Assert.Equal("link.exe", toolchain.Linker);
        Assert.Equal("rc.exe", toolchain.ResourceCompiler);
        Assert.True(toolchain.Capabilities.Contains("WindowsResource"));
        Assert.Equal("v143", toolchain.VisualStudioPlatformToolset);
        Assert.Equal(["Debug", "Development", "Release", "Shipping"],
            toolchain.Profiles.Keys.Order(StringComparer.Ordinal));
        Assert.False(toolchain.Profiles["Debug"].Optimize);
        Assert.True(toolchain.Profiles["Shipping"].MinimalDiagnostics);
    }

    [Fact]
    public void WindowsBuilderExtensionAddsPluginAndReturnsBuilder()
    {
        var builder = new RecordingBuilder();

        var returned = builder.UseWindowsPlatform();

        Assert.Same(builder, returned);
        Assert.IsType<WindowsPlatformPlugin>(Assert.Single(builder.Plugins));
    }

    private static ConfiguredModule Configured(
        string id,
        string displayName,
        ModuleKind kind,
        ImmutableArray<DependencyEdge> dependencies = default) => new(
        id, displayName, ModuleLanguage.Cxx, kind, [new($"{id}.cpp")],
        UsageRequirements.Empty, UsageRequirements.Empty, UsageRequirements.Empty, UsageRequirements.Empty,
        dependencies.IsDefault ? [] : dependencies, [], []);

    private static string Profile(ConfigurationKey key) =>
        key.Values.Single(value => value.Fragment.Value == "Profile").Value;

    private sealed class RecordingRegistry : IPluginRegistry
    {
        public List<object> Services { get; } = [];
        public void AddService<T>(T service) where T : class => Services.Add(service);
    }

    private sealed class RecordingBuilder : IBuildToolBuilder
    {
        public List<IPlugin> Plugins { get; } = [];
        public void AddPlugin(IPlugin plugin) => Plugins.Add(plugin);
    }
}