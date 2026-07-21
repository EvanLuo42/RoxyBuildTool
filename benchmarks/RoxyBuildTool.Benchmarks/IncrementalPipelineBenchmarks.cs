using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Graph;
using RoxyBuildTool.Model;

namespace RoxyBuildTool.Benchmarks;

/// <summary>Compares a full graph rebuild with cross-invocation disk-cache reuse.</summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.HostProcess, 1, 3, 5)]
public class IncrementalPipelineBenchmarks
{
    private ImmutableArray<ConfigurationKey> _configurations = [];
    private DefinitionGraph _definitions = null!;
    private string _root = null!;
    private TargetDefinition _target = null!;

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"RoxyBuildTool-Benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _definitions = BenchmarkModelFactory.CreateDefinitionGraph(
            250,
            24,
            BenchmarkGraphShape.LayeredEngine,
            false);
        _target = _definitions.Targets[0];
        _configurations = BenchmarkModelFactory.CreateConfigurations(16);

        var cache = new PipelineCache(_root);
        var definitionFingerprint = PipelineCache.DefinitionFingerprint(_definitions, _target);
        foreach (var configuration in _configurations)
        {
            var configuredFingerprint =
                PipelineCache.ConfiguredGraphFingerprint(definitionFingerprint, configuration);
            var configured = DependencyResolver.Resolve(_definitions, _target, configuration);
            if (configured.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                throw new InvalidOperationException("The incremental benchmark graph must be valid.");
            cache.GetOrAddActionGraph(
                PipelineCache.ActionGraphFingerprint(
                    configuredFingerprint,
                    BenchmarkModelFactory.Toolchain,
                    _definitions.Workspaces[0].Id),
                () => ActionGraphLowerer.Lower(
                    configured,
                    BenchmarkModelFactory.Toolchain,
                    _definitions.Workspaces[0].Id));
        }
    }

    [Benchmark(Baseline = true)]
    public int ResolveAndLowerWithoutCache()
    {
        var count = 0;
        foreach (var configuration in _configurations)
        {
            var configured = DependencyResolver.Resolve(_definitions, _target, configuration);
            var actions = ActionGraphLowerer.Lower(
                configured,
                BenchmarkModelFactory.Toolchain,
                _definitions.Workspaces[0].Id);
            count += configured.Modules.Length + actions.Actions.Length;
        }

        return count;
    }

    [Benchmark]
    public int ResolveConfigurationsAndLoadWarmActionGraphs()
    {
        var cache = new PipelineCache(_root);
        var definitionFingerprint = PipelineCache.DefinitionFingerprint(_definitions, _target);
        var count = 0;
        foreach (var configuration in _configurations)
        {
            var configured = DependencyResolver.Resolve(_definitions, _target, configuration);
            var configuredFingerprint =
                PipelineCache.ConfiguredGraphFingerprint(definitionFingerprint, configuration);
            var actions = cache.GetOrAddActionGraph(
                PipelineCache.ActionGraphFingerprint(
                    configuredFingerprint,
                    BenchmarkModelFactory.Toolchain,
                    _definitions.Workspaces[0].Id),
                static () => throw new InvalidOperationException("The action graph cache was not warm."));
            count += configured.Modules.Length + actions.Actions.Length;
        }

        return count;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}