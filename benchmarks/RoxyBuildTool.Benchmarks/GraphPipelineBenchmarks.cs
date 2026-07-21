using BenchmarkDotNet.Attributes;
using RoxyBuildTool.Graph;
using RoxyBuildTool.Model;

namespace RoxyBuildTool.Benchmarks;

/// <summary>Measures dependency propagation and action expansion independently and together.</summary>
[MemoryDiagnoser]
public class GraphPipelineBenchmarks
{
    private ConfigurationKey _configuration = null!;
    private ConfiguredGraph _configuredGraph = null!;
    private DefinitionGraph _definitions = null!;
    private TargetDefinition _target = null!;

    [Params(25, 100)] public int ModuleCount { get; set; }

    [Params(4, 16)] public int SourcesPerModule { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _definitions = BenchmarkModelFactory.CreateDefinitionGraph(ModuleCount, SourcesPerModule);
        _target = _definitions.Targets[0];
        _configuration = BenchmarkModelFactory.CreateConfigurations(1)[0];
        _configuredGraph = DependencyResolver.Resolve(_definitions, _target, _configuration);
    }

    [Benchmark(Baseline = true)]
    public ConfiguredGraph ResolveDependenciesAndPropagateUsage()
    {
        return DependencyResolver.Resolve(_definitions, _target, _configuration);
    }

    [Benchmark]
    public ActionGraph LowerConfiguredGraphToActions()
    {
        return ActionGraphLowerer.Lower(_configuredGraph, BenchmarkModelFactory.Toolchain, "ProductionWorkspace");
    }

    [Benchmark]
    public ActionGraph ResolveAndLowerCorePipeline()
    {
        var graph = DependencyResolver.Resolve(_definitions, _target, _configuration);
        return ActionGraphLowerer.Lower(graph, BenchmarkModelFactory.Toolchain, "ProductionWorkspace");
    }
}