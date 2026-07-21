using System.Xml.Linq;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Configuration;
using RoxyBuildTool.Generators.VisualStudio;
using RoxyBuildTool.Model;
using RoxyBuildTool.Platforms.Windows;
using Xunit;

namespace RoxyBuildTool.IntegrationTests;

public sealed class InfrastructureTests
{
    [Fact]
    public async Task AssemblyDiscoveryRegistersModulesTargetsAndWorkspacesDeterministically()
    {
        using var output = new StringWriter();
        var exitCode = await BuildToolApp.Create([
                "explain", "ReflectedTarget", "--profile", "debug", "--setting", "usage",
            ])
            .WithOutput(output)
            .DiscoverRulesFromAssemblyContaining<ReflectedRulesMarker>()
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("ReflectedHeader", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Reflected", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("define REFLECTED_DEBUG=1", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateDefinitionIdsFailBeforeGraphConstructionWithBothRuleTypesNamed()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await BuildToolApp.Create([])
            .WithOutput(output, error)
            .AddRules<DuplicateDefinitionRules>()
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Contains("RBT2101", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Multiple module rule types produce definition ID 'Collision'", error.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(typeof(DuplicateDefinitions.Left.CollisionModule).FullName!, error.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(typeof(DuplicateDefinitions.Right.CollisionModule).FullName!, error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateGeneratedPathsFailBeforeAnyFileIsWritten()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"RoxyDuplicateOutput-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspaceRoot);
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var app = BuildToolApp.Create([
                    "generate", "ReflectedWorkspace", "--workspace", "Duplicate",
                ])
                .WithWorkspaceRoot(workspaceRoot)
                .WithOutput(output, error)
                .DiscoverRulesFromAssemblyContaining<ReflectedRulesMarker>()
                .UseWindowsPlatform();
            app.AddService<IWorkspaceGenerator>(new DuplicateOutputGenerator());

            var exitCode = await app.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(2, exitCode);
            Assert.Contains("RBT4001", error.ToString(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(workspaceRoot, ".roxy", "generated")));
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task UnsupportedLinkModelFailsBeforeGeneratorOrExternalToolExecution()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await BuildToolApp.Create([
                "generate", "UnsupportedLinkWorkspace", "--workspace", "Vs2022",
            ])
            .WithOutput(output, error)
            .AddRules<UnsupportedLinkRules>()
            .UseWindowsPlatform()
            .UseVisualStudio()
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Contains("RBT1104", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Monolithic", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModuleDefinitionsAreCachedByRelevantConfigurationFragments()
    {
        CacheProbeModule.Reset();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"RoxyCacheProbe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspaceRoot);
        try
        {
            var exitCode = await BuildToolApp.Create(["generate", "CacheProbe", "--workspace", "Vs2022"])
                .WithWorkspaceRoot(workspaceRoot)
                .WithOutput(TextWriter.Null)
                .AddRules<CacheProbeRules>()
                .UseWindowsPlatform()
                .UseVisualStudio()
                .RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Equal(2, CacheProbeModule.ConfigureCount);
        }
        finally
        {
            Directory.Delete(workspaceRoot, true);
        }
    }

    [Fact]
    public async Task UnregisteredRuleReferencesHaveAStableDefinitionDiagnostic()
    {
        using var error = new StringWriter();

        var exitCode = await BuildToolApp.Create([])
            .WithOutput(new StringWriter(), error)
            .AddRules<MissingReferenceRules>()
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Contains("RBT2102", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Unregistered", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidNativeSettingsHaveAStableDefinitionDiagnostic()
    {
        using var error = new StringWriter();

        var exitCode = await BuildToolApp.Create([])
            .WithOutput(TextWriter.Null, error)
            .AddRules<InvalidNativeSettingsRules>()
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Contains("RBT2103", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("must either both be set", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RuleBaseTypesAreMarkersWithoutConfigureEntryPoints()
    {
        Assert.DoesNotContain(typeof(CxxModule).GetMethods(), method => method.Name == "Configure");
        Assert.DoesNotContain(typeof(CSharpModule).GetMethods(), method => method.Name == "Configure");
        Assert.DoesNotContain(typeof(BuildTarget).GetMethods(), method => method.Name == "Configure");
        Assert.DoesNotContain(typeof(BuildWorkspace).GetMethods(), method => method.Name == "Configure");
    }

    [Fact]
    public void CompareBeforeWritePreservesTimestampForIdenticalContent()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"RoxyTests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "generated.txt");
        try
        {
            Assert.True(CompareBeforeWrite.Write(path, "stable\n"));
            var timestamp = File.GetLastWriteTimeUtc(path);

            Assert.False(CompareBeforeWrite.Write(path, "stable\r\n"));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void CoreProjectsDoNotReferenceGenerators()
    {
        var root = FindRepositoryRoot();
        var coreProjects = new[]
        {
            "src/RoxyBuildTool.Abstractions/RoxyBuildTool.Abstractions.csproj",
            "src/RoxyBuildTool.Model/RoxyBuildTool.Model.csproj",
            "src/RoxyBuildTool.Configuration/RoxyBuildTool.Configuration.csproj",
            "src/RoxyBuildTool.Graph/RoxyBuildTool.Graph.csproj",
            "src/RoxyBuildTool.Toolchains/RoxyBuildTool.Toolchains.csproj",
        };

        foreach (var relativePath in coreProjects)
        {
            var document = XDocument.Load(Path.Combine(root, relativePath));
            var references = document.Descendants("ProjectReference")
                .Select(element => (string?)element.Attribute("Include"))
                .Where(value => value is not null);
            Assert.DoesNotContain(references, reference => reference!.Contains("Generators", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void VisualStudioGeneratorProducesWellFormedMsbuildXml()
    {
        var configuration = new ConfigurationKey([
            new(new("platform"), "windows"),
            new(new("architecture"), "x64"),
            new(new("profile"), "debug"),
            new(new("toolchain"), "Msvc14.4"),
            new(new("LinkModel"), "modular"),
        ]);
        var module = new ConfiguredModule("native", "Native", ModuleLanguage.Cxx, ModuleKind.StaticLibrary,
            [new("src/native.cpp")], UsageRequirements.Empty, UsageRequirements.Empty,
            UsageRequirements.Empty, UsageRequirements.Empty, [], [], []);
        var model = new WorkspaceModel("Xml", "native",
            [new("native", "Native", ModuleLanguage.Cxx, [new("native", configuration, module)], [])],
            [new(configuration, new("native", "Native", ["native"]), [module], [])],
            []);

        var result = new VisualStudio2022Generator().Generate(model,
            new("C:/checkout", new(".roxy/generated/vs2022/xml")));

        foreach (var file in result.Files.Where(file => file.Path.Value.EndsWith("proj", StringComparison.Ordinal) ||
                                                        file.Path.Value.EndsWith("filters", StringComparison.Ordinal)))
        {
            _ = XDocument.Parse(file.Content);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RoxyBuildTool.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}

public sealed class ReflectedRulesMarker;

public sealed class DuplicateDefinitionRules : IRulesModule
{
    public void Register(BuildRegistry registry)
    {
        registry.AddModule<DuplicateDefinitions.Left.CollisionModule>();
        registry.AddModule<DuplicateDefinitions.Right.CollisionModule>();
    }
}

public sealed class DuplicateOutputGenerator : IWorkspaceGenerator
{
    public WorkspaceGeneratorId Id { get; } = new("Duplicate");
    public CapabilitySet Capabilities { get; } = CapabilitySet.Empty;

    public GenerationResult Generate(WorkspaceModel workspace, GenerationContext context) => new(
        Id,
        [new(new("same.txt"), "first"), new(new("same.txt"), "second")],
        []);
}

public sealed class UnsupportedLinkRules : IRulesModule
{
    public void Register(BuildRegistry registry)
    {
        registry.AddModule<UnsupportedLinkModule>();
        registry.AddTarget<UnsupportedLinkTarget>();
        registry.AddWorkspace<UnsupportedLinkWorkspace>();
    }
}

public sealed class CacheProbeRules : IRulesModule
{
    public void Register(BuildRegistry registry)
    {
        registry.AddModule<CacheProbeModule>();
        registry.AddTarget<CacheProbeTarget>();
        registry.AddWorkspace<CacheProbeWorkspace>();
    }
}

[BuildRuleIgnore]
public sealed class CacheProbeModule : CxxModule
{
    private static int configureCount;
    public static int ConfigureCount => Volatile.Read(ref configureCount);

    public static void Reset()
    {
        Volatile.Write(ref configureCount, 0);
    }

    [Configure]
    private static void Configure(ModuleRules rules)
    {
        Interlocked.Increment(ref configureCount);
        rules.Output = CxxOutput.HeaderOnly;
    }
}

[BuildRuleIgnore]
public sealed class CacheProbeTarget : BuildTarget
{
    [Configure]
    private static void Configure(TargetRules rules)
    {
        rules.RootModules.Add<CacheProbeModule>();
        rules.Matrix
            .Axis(Configuration.Platforms.Windows)
            .Axis(Architectures.X64)
            .Axis(BuildProfiles.All.ToArray())
            .Axis(Configuration.Toolchains.Msvc)
            .Axis(LinkModels.Modular);
    }
}

[BuildRuleIgnore]
public sealed class CacheProbeWorkspace : BuildWorkspace
{
    [Configure]
    private static void Configure(WorkspaceRules rules)
    {
        rules.Targets.Add<CacheProbeTarget>();
        rules.StartupTarget<CacheProbeTarget>();
        rules.IncludeBuildHost = false;
    }
}

public sealed class MissingReferenceRules : IRulesModule
{
    public void Register(BuildRegistry registry) =>
        registry.AddModule<MissingReferenceModule>();
}

public sealed class InvalidNativeSettingsRules : IRulesModule
{
    public void Register(BuildRegistry registry)
    {
        registry.AddModule<InvalidNativeSettingsModule>();
    }
}

[BuildRuleIgnore]
public sealed class InvalidNativeSettingsModule : CxxModule
{
    [Configure]
    private static void Configure(ModuleRules rules)
    {
        rules.Cxx.PrecompiledHeader = "include/pch.h";
    }
}

[BuildRuleIgnore]
public sealed class MissingReferenceModule : CxxModule
{
    [Configure]
    private static void Configure(ModuleRules rules) =>
        rules.Dependencies.Private<UnregisteredModule>();
}

[BuildRuleIgnore]
public sealed class UnregisteredModule : CxxModule;

[BuildRuleIgnore]
public sealed class UnsupportedLinkModule : CxxModule
{
    [Configure]
    private static void Configure(ModuleRules rules) =>
        rules.Output = CxxOutput.HeaderOnly;
}

[BuildRuleIgnore]
public sealed class UnsupportedLinkTarget : BuildTarget
{
    [Configure]
    private static void Configure(TargetRules rules)
    {
        rules.RootModules.Add<UnsupportedLinkModule>();
        rules.Matrix
            .Axis(Configuration.Platforms.Windows)
            .Axis(Architectures.X64)
            .Axis(BuildProfiles.Debug)
            .Axis(Configuration.Toolchains.Msvc)
            .Axis(LinkModels.Monolithic);
    }
}

[BuildRuleIgnore]
public sealed class UnsupportedLinkWorkspace : BuildWorkspace
{
    [Configure]
    private static void Configure(WorkspaceRules rules)
    {
        rules.Targets.Add<UnsupportedLinkTarget>();
        rules.IncludeBuildHost = false;
    }
}

public static class DuplicateDefinitions
{
    public static class Left
    {
        [BuildRuleIgnore]
        public sealed class CollisionModule : CxxModule;
    }

    public static class Right
    {
        [BuildRuleIgnore]
        public sealed class CollisionModule : CxxModule;
    }
}

public sealed class ReflectedHeaderModule : CxxModule
{
    [Configure]
    private static void ConfigureAll(ModuleRules rules) =>
        rules.Output = CxxOutput.HeaderOnly;

    [Configure("profile", "debug", Priority = 100)]
    private static void ConfigureDebug(ModuleRules rules) =>
        rules.Private.Defines.Add("REFLECTED_DEBUG=1");
}

public abstract class ReflectedTargetBase : BuildTarget
{
    [Configure(Priority = -100)]
    protected static void ConfigureWindows(TargetRules rules)
    {
        rules.Matrix
            .Axis(Configuration.Platforms.Windows)
            .Axis(Architectures.X64)
            .Axis(BuildProfiles.Debug)
            .Axis(Configuration.Toolchains.Msvc)
            .Axis(LinkModels.Modular);
    }
}

public sealed class ReflectedTarget : ReflectedTargetBase
{
    [Configure]
    private static void ConfigureTarget(TargetRules rules) =>
        rules.RootModules.Add<ReflectedHeaderModule>();
}

public sealed class ReflectedWorkspace : BuildWorkspace
{
    [Configure]
    private static void ConfigureWorkspace(WorkspaceRules rules)
    {
        rules.Targets.Add<ReflectedTarget>();
        rules.StartupTarget<ReflectedTarget>();
        rules.IncludeBuildHost = false;
    }
}