using System.Collections.Immutable;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Configuration;
using RoxyBuildTool.Graph;
using RoxyBuildTool.Model;
using RoxyBuildTool.Toolchains;

namespace RoxyBuildTool.Benchmarks;

internal static class BenchmarkModelFactory
{
    public static ToolchainDescriptor Toolchain { get; } = CreateToolchain();

    public static MatrixDefinition CreateMatrix(int axisCount, int valuesPerAxis)
    {
        var builder = new MatrixBuilder();
        var values = Enumerable.Range(0, axisCount)
            .Select(axis => Enumerable.Range(0, valuesPerAxis)
                .Select(value => new FragmentValue(new FragmentId($"Benchmark.Axis{axis}"), $"Value{value}"))
                .ToArray())
            .ToArray();

        foreach (var axis in values) builder.Axis(axis);

        builder.Exclude(
            view => view.Is(values[0][0]) && view.Is(values[2][0]),
            "The first and third hot-path features are incompatible.");
        builder.Require(
            view => view.Is(values[1][1]),
            view => !view.Is(values[4][0]),
            "The second feature requires a non-default runtime.");
        builder.Exclude(
            view => view.Is(values[3][2]) && view.Is(values[5][2]),
            "The selected backend and packaging mode cannot be combined.");
        return builder.Build();
    }

    public static DefinitionGraph CreateDefinitionGraph(
        int moduleCount,
        int sourcesPerModule,
        BenchmarkGraphShape graphShape = BenchmarkGraphShape.TransitiveChain)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(moduleCount, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourcesPerModule, 1);

        var modules = Enumerable.Range(0, moduleCount)
            .Select(index => CreateModule(index, moduleCount, sourcesPerModule, graphShape))
            .ToImmutableArray();
        var roots = graphShape == BenchmarkGraphShape.TransitiveChain
            ? [ModuleId(moduleCount - 1)]
            : modules.Select(module => module.Id).ToImmutableArray();
        var target = new TargetDefinition(
            "production",
            "ProductionTarget",
            roots,
            new MatrixBuilder().Build());
        var workspace = new WorkspaceDefinition(
            "production",
            "ProductionWorkspace",
            [target.Id],
            target.Id);
        return new DefinitionGraph(modules, [target], [workspace]);
    }

    public static ImmutableArray<ConfigurationKey> CreateConfigurations(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var flavorCount = (count + 7) / 8;
        var flavor = new FragmentId("Benchmark.Flavor");
        var configurations =
            from flavorIndex in Enumerable.Range(0, flavorCount)
            from profile in BuildProfiles.All
            from linkModel in new[] { LinkModels.Modular, LinkModels.Monolithic }
            select new ConfigurationKey([
                Configuration.Platforms.Windows,
                Architectures.X64,
                profile,
                Configuration.Toolchains.Msvc,
                linkModel,
                new FragmentValue(flavor, $"Flavor{flavorIndex:D2}")
            ]);
        return configurations.Take(count).ToImmutableArray();
    }

    private static ModuleDefinition CreateModule(
        int index,
        int moduleCount,
        int sourcesPerModule,
        BenchmarkGraphShape graphShape)
    {
        var id = ModuleId(index);
        var kind = index == moduleCount - 1
            ? ModuleKind.Executable
            : index > 0 && index % 7 == 0
                ? ModuleKind.SharedLibrary
                : ModuleKind.StaticLibrary;
        var sources = Enumerable.Range(0, sourcesPerModule)
            .Select(source => new LogicalPath(SourcePath(id, source)))
            .ToImmutableArray();
        var dependencies = Dependencies(index, moduleCount, graphShape);
        var publicUsage = new UsageRequirements(
            [new UsageValue($"include/{id}/public", $"{id}:public")],
            [new UsageValue($"ROXY_{index:D4}_API=1", $"{id}:public")],
            [],
            kind == ModuleKind.SharedLibrary
                ? [new UsageValue($"out/runtime/{id}.dll", $"{id}:runtime")]
                : []);
        var privateUsage = new UsageRequirements(
            [new UsageValue($"src/{id}/private", $"{id}:private")],
            [new UsageValue($"ROXY_{index:D4}_IMPLEMENTATION=1", $"{id}:private")],
            [],
            []);
        return new ModuleDefinition(
            id,
            $"Module{index:D4}",
            kind,
            sources,
            publicUsage,
            privateUsage,
            dependencies,
            []);
    }

    private static ImmutableArray<DependencyEdge> Dependencies(
        int index,
        int moduleCount,
        BenchmarkGraphShape graphShape)
    {
        return graphShape switch
        {
            BenchmarkGraphShape.TransitiveChain => TransitiveChainDependencies(index),
            BenchmarkGraphShape.SharedCoreHub => SharedCoreDependencies(index),
            BenchmarkGraphShape.LayeredEngine => LayeredEngineDependencies(index, moduleCount),
            _ => throw new ArgumentOutOfRangeException(nameof(graphShape), graphShape, null)
        };
    }

    private static ImmutableArray<DependencyEdge> TransitiveChainDependencies(int index)
    {
        var dependencies = ImmutableArray.CreateBuilder<DependencyEdge>(3);
        if (index >= 1) dependencies.Add(new DependencyEdge(ModuleId(index - 1), DependencyVisibility.Public));

        if (index >= 2) dependencies.Add(new DependencyEdge(ModuleId(index - 2), DependencyVisibility.Private));

        if (index >= 4) dependencies.Add(new DependencyEdge(ModuleId(index - 4), DependencyVisibility.Interface));

        return dependencies.ToImmutable();
    }

    private static ImmutableArray<DependencyEdge> SharedCoreDependencies(int index)
    {
        if (index == 0) return [];

        return index % 8 == 0
            ? [new DependencyEdge(ModuleId(0), DependencyVisibility.Public)]
            : [new DependencyEdge(ModuleId(0), DependencyVisibility.Interface)];
    }

    private static ImmutableArray<DependencyEdge> LayeredEngineDependencies(int index, int moduleCount)
    {
        if (index == 0) return [];

        var coreCount = Math.Min(32, Math.Max(4, moduleCount / 20));
        if (index < coreCount) return [new DependencyEdge(ModuleId(index - 1), DependencyVisibility.Public)];

        var dependencies = ImmutableArray.CreateBuilder<DependencyEdge>(3);
        dependencies.Add(new DependencyEdge(ModuleId(index % coreCount), DependencyVisibility.Interface));
        dependencies.Add(new DependencyEdge(ModuleId(index - coreCount), DependencyVisibility.Private));
        if (index >= coreCount * 2 && index % 4 == 0)
            dependencies.Add(new DependencyEdge(ModuleId(index - coreCount - 1), DependencyVisibility.BuildOrderOnly));

        return dependencies.ToImmutable();
    }

    private static string SourcePath(string module, int source)
    {
        var extension = source % 11 == 10 ? "rc" : source % 5 == 4 ? "h" : "cpp";
        return $"src/{module}/file{source:D3}.{extension}";
    }

    private static string ModuleId(int index)
    {
        return $"module{index:D4}";
    }

    private static ToolchainDescriptor CreateToolchain()
    {
        var profiles = BuildProfiles.All.ToImmutableDictionary(
            profile => profile.Value,
            profile => new CxxProfilePolicy(
                profile == BuildProfiles.Debug ? ["/Od", "/RTC1"] : ["/O2"],
                profile == BuildProfiles.Debug ? ["/DEBUG"] : ["/OPT:REF", "/OPT:ICF"],
                profile != BuildProfiles.Debug,
                profile != BuildProfiles.Shipping,
                profile != BuildProfiles.Shipping,
                profile == BuildProfiles.Shipping),
            StringComparer.OrdinalIgnoreCase);
        return new ToolchainDescriptor(
            new ToolchainId("Msvc14.4"),
            new PlatformId("windows"),
            "x64",
            "cl.exe",
            "lib.exe",
            "link.exe",
            "v143",
            profiles,
            new CapabilitySet(["WindowsResource"]),
            "rc.exe");
    }
}