using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Configuration;
using RoxyBuildTool.Generators.CompilationDatabase;
using static RoxyBuildTool.Platforms.Windows.WindowsPlatformExtensions;

namespace RoxyBuildTool.Benchmarks;

/// <summary>Measures validated generation-snapshot reuse against a complete pipeline rebuild.</summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.HostProcess, 1, 3, 5)]
public class GenerationSnapshotBenchmarks
{
    private string _root = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"RoxyBuildTool-Snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "core"));
        Directory.CreateDirectory(Path.Combine(_root, "app"));
        for (var index = 0; index < 24; index++)
        {
            File.WriteAllText(Path.Combine(_root, "core", $"core{index:D2}.cpp"), "int core() { return 1; }");
            File.WriteAllText(Path.Combine(_root, "app", $"app{index:D2}.cpp"), "int app() { return 2; }");
        }

        if (await Run(true) != 0)
            throw new InvalidOperationException("The generation snapshot benchmark setup failed.");
    }

    [Benchmark(Baseline = true)]
    public Task<int> RebuildPipeline()
    {
        return Run(false);
    }

    [Benchmark]
    public Task<int> LoadValidatedGenerationSnapshot()
    {
        return Run(true);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private Task<int> Run(bool useCache)
    {
        return BuildToolApp.Create([
                "generate", "SnapshotWorkspace", "--workspace", "CompileDb"
            ])
            .WithWorkspaceRoot(_root)
            .WithOutput(TextWriter.Null)
            .WithIncrementalCache(useCache)
            .AddRules<SnapshotRulesModule>()
            .UseWindowsPlatform()
            .UseCompilationDatabase()
            .RunAsync();
    }
}

[BuildFragment("Benchmark.SnapshotFlavor")]
internal enum SnapshotFlavor
{
    Client,
    Editor
}

internal sealed class SnapshotRulesModule : IRulesModule
{
    public void Register(BuildRegistry registry)
    {
        registry.AddModule<SnapshotCoreModule>();
        registry.AddModule<SnapshotApplicationModule>();
        registry.AddTarget<SnapshotTarget>();
        registry.AddWorkspace<SnapshotWorkspace>();
    }
}

internal sealed class SnapshotCoreModule : CxxModule
{
    [Configure]
    private static void ConfigureModule(ModuleRules rules)
    {
        rules.Output = CxxOutput.StaticLibrary;
        rules.Sources.From("core", "**/*.cpp");
        rules.Public.IncludeDirectories.Add("core");
    }
}

internal sealed class SnapshotApplicationModule : CxxModule
{
    [Configure]
    private static void ConfigureModule(ModuleRules rules)
    {
        rules.Output = CxxOutput.Executable;
        rules.Sources.From("app", "**/*.cpp");
        rules.Dependencies.Public<SnapshotCoreModule>();
    }
}

internal sealed class SnapshotTarget : BuildTarget
{
    [Configure]
    private static void ConfigureTarget(TargetRules rules)
    {
        rules.RootModules.Add<SnapshotApplicationModule>();
        rules.Matrix
            .Axis(Configuration.Platforms.Windows)
            .Axis(Architectures.X64)
            .Axis(BuildProfiles.Debug, BuildProfiles.Development, BuildProfiles.Release, BuildProfiles.Shipping)
            .Axis(Configuration.Toolchains.Msvc)
            .Axis(LinkModels.Modular, LinkModels.Monolithic)
            .Axis(SnapshotFlavor.Client, SnapshotFlavor.Editor);
    }
}

internal sealed class SnapshotWorkspace : BuildWorkspace
{
    [Configure]
    private static void ConfigureWorkspace(WorkspaceRules rules)
    {
        rules.Targets.Add<SnapshotTarget>();
        rules.IncludeBuildHost = false;
    }
}