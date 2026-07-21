using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using RoxyBuildTool.Generators.CompilationDatabase;
using RoxyBuildTool.Generators.VisualStudio;
using RoxyBuildTool.Graph;
using RoxyBuildTool.Model;

namespace RoxyBuildTool.Benchmarks;

/// <summary>Measures multi-variant workspace aggregation and backend serialization.</summary>
[MemoryDiagnoser]
public class WorkspaceGenerationBenchmarks
{
    private static readonly GenerationContext VisualStudioContext =
        new("C:/agent/_work/roxy", new LogicalPath(".roxy/generated/Vs2022/ProductionWorkspace"));

    private static readonly GenerationContext CompilationDatabaseContext =
        new("C:/agent/_work/roxy", new LogicalPath(".roxy/generated/CompileDb/ProductionWorkspace"));

    private ImmutableArray<ActionGraph> _actionGraphs = [];
    private ImmutableArray<ConfigurationKey> _configurations = [];
    private ImmutableArray<ConfiguredGraph> _configuredGraphs = [];

    private DefinitionGraph _definitions = null!;
    private WorkspaceModel _workspace = null!;
    private WorkspaceDefinition _workspaceDefinition = null!;

    [Params(25, 100)] public int ModuleCount { get; set; }

    [Params(2, 8)] public int VariantCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _definitions = BenchmarkModelFactory.CreateDefinitionGraph(ModuleCount, 8);
        _workspaceDefinition = _definitions.Workspaces[0];
        _configurations = BenchmarkModelFactory.CreateConfigurations(VariantCount);
        _configuredGraphs = ResolveAll();
        _actionGraphs = LowerAll(_configuredGraphs);
        _workspace = WorkspaceAssembler.Assemble(_workspaceDefinition, _configuredGraphs, _actionGraphs);
    }

    [Benchmark]
    public WorkspaceModel AssembleMultiVariantWorkspace()
    {
        return WorkspaceAssembler.Assemble(_workspaceDefinition, _configuredGraphs, _actionGraphs);
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
    public int RunEndToEndGenerationPipeline()
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
                ActionGraphLowerer.Lower(graph, BenchmarkModelFactory.Toolchain, "ProductionWorkspace"))
        ];
    }
}