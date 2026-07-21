using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using RoxyBuildTool.Generators.CompilationDatabase;
using RoxyBuildTool.Generators.VisualStudio;
using RoxyBuildTool.Graph;
using RoxyBuildTool.Model;

namespace RoxyBuildTool.Benchmarks;

public enum BenchmarkGraphShape
{
    TransitiveChain,
    SharedCoreHub,
    LayeredEngine
}

public enum GameEngineScenario
{
    LargeModuleGraph,
    HighVariantWorkspace
}

/// <summary>Compares graph shapes that stress different dependency-resolution behavior.</summary>
[MemoryDiagnoser]
public class DependencyShapeBenchmarks
{
    private ConfigurationKey _configuration = null!;
    private DefinitionGraph _definitions = null!;

    [Params(100, 250)] public int ModuleCount { get; set; }

    [Params(
        BenchmarkGraphShape.TransitiveChain,
        BenchmarkGraphShape.SharedCoreHub,
        BenchmarkGraphShape.LayeredEngine)]
    public BenchmarkGraphShape GraphShape { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _definitions = BenchmarkModelFactory.CreateDefinitionGraph(ModuleCount, 8, GraphShape);
        _configuration = BenchmarkModelFactory.CreateConfigurations(1)[0];
    }

    [Benchmark]
    public ConfiguredGraph ResolveProductionDependencyShape()
    {
        return DependencyResolver.Resolve(_definitions, _definitions.Targets[0], _configuration);
    }
}

/// <summary>Exercises complete generation at representative engine module and variant counts.</summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.HostProcess, 1, 3, 5)]
public class GameEngineScaleBenchmarks
{
    private static readonly GenerationContext VisualStudioContext =
        new("D:/engine", new LogicalPath(".roxy/generated/Vs2022/Engine"));

    private static readonly GenerationContext CompilationDatabaseContext =
        new("D:/engine", new LogicalPath(".roxy/generated/CompileDb/Engine"));

    private ImmutableArray<ActionGraph> _actionGraphs = [];
    private ImmutableArray<ConfigurationKey> _configurations = [];
    private ImmutableArray<ConfiguredGraph> _configuredGraphs = [];

    private DefinitionGraph _definitions = null!;
    private WorkspaceModel _workspace = null!;
    private WorkspaceDefinition _workspaceDefinition = null!;

    [Params(GameEngineScenario.LargeModuleGraph, GameEngineScenario.HighVariantWorkspace)]
    public GameEngineScenario Scenario { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var (moduleCount, sourcesPerModule, variantCount) = Scenario switch
        {
            GameEngineScenario.LargeModuleGraph => (1000, 12, 4),
            GameEngineScenario.HighVariantWorkspace => (250, 24, 16),
            _ => throw new ArgumentOutOfRangeException(nameof(Scenario), Scenario, null)
        };
        _definitions = BenchmarkModelFactory.CreateDefinitionGraph(
            moduleCount,
            sourcesPerModule,
            BenchmarkGraphShape.LayeredEngine);
        _workspaceDefinition = _definitions.Workspaces[0];
        _configurations = BenchmarkModelFactory.CreateConfigurations(variantCount);
        _configuredGraphs = ResolveAll();
        _actionGraphs = LowerAll(_configuredGraphs);
        _workspace = WorkspaceAssembler.Assemble(_workspaceDefinition, _configuredGraphs, _actionGraphs);
    }

    [Benchmark]
    public ImmutableArray<ConfiguredGraph> ResolveAllConfigurations()
    {
        return ResolveAll();
    }

    [Benchmark]
    public ImmutableArray<ActionGraph> LowerAllConfiguredGraphs()
    {
        return LowerAll(_configuredGraphs);
    }

    [Benchmark]
    public GenerationResult GenerateVisualStudioWorkspace()
    {
        return new VisualStudio2022Generator().Generate(_workspace, VisualStudioContext);
    }

    [Benchmark]
    public GenerationResult GenerateCompilationDatabase()
    {
        return new CompilationDatabaseGenerator().Generate(_workspace, CompilationDatabaseContext);
    }

    [Benchmark]
    public int RunGameEngineEndToEndPipeline()
    {
        var configuredGraphs = ResolveAll();
        var actionGraphs = LowerAll(configuredGraphs);
        var workspace = WorkspaceAssembler.Assemble(_workspaceDefinition, configuredGraphs, actionGraphs);
        var visualStudio = new VisualStudio2022Generator().Generate(workspace, VisualStudioContext);
        var compilationDatabase = new CompilationDatabaseGenerator().Generate(workspace, CompilationDatabaseContext);
        return visualStudio.Files.Sum(file => file.Content.Length) +
               compilationDatabase.Files.Sum(file => file.Content.Length);
    }

    private ImmutableArray<ConfiguredGraph> ResolveAll()
    {
        return
        [
            .._configurations.Select(configuration =>
                DependencyResolver.Resolve(_definitions, _definitions.Targets[0], configuration))
        ];
    }

    private static ImmutableArray<ActionGraph> LowerAll(ImmutableArray<ConfiguredGraph> configuredGraphs)
    {
        return
        [
            ..configuredGraphs.Select(graph =>
                ActionGraphLowerer.Lower(graph, BenchmarkModelFactory.Toolchain, "Engine"))
        ];
    }
}