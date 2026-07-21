using BenchmarkDotNet.Attributes;
using RoxyBuildTool.Configuration;

namespace RoxyBuildTool.Benchmarks;

/// <summary>Measures configuration-space expansion and early constraint pruning.</summary>
[MemoryDiagnoser]
public class MatrixResolutionBenchmarks
{
    private MatrixDefinition _matrix = null!;
    private FragmentRegistry _registry = null!;

    [Params(3, 5)] public int ValuesPerAxis { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _registry = new FragmentRegistry();
        _matrix = BenchmarkModelFactory.CreateMatrix(6, ValuesPerAxis);
    }

    [Benchmark]
    public MatrixResolution ExpandAndPruneConfigurationMatrix()
    {
        return new MatrixResolver(_registry).Resolve(_matrix);
    }
}