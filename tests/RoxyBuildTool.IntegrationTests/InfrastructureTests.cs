using System.Xml.Linq;
using RoxyBuildTool.Configuration;
using RoxyBuildTool.Generators.VisualStudio;
using RoxyBuildTool.Model;
using Xunit;

namespace RoxyBuildTool.IntegrationTests;

public sealed class InfrastructureTests
{
    [Fact]
    public async Task AssemblyDiscoveryRegistersModulesTargetsAndWorkspacesDeterministically()
    {
        using var output = new StringWriter();
        var exitCode = await global::RoxyBuildTool.BuildToolApp.Create([
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
    public void RuleBaseTypesAreMarkersWithoutConfigureEntryPoints()
    {
        Assert.DoesNotContain(typeof(global::RoxyBuildTool.CxxModule).GetMethods(), method => method.Name == "Configure");
        Assert.DoesNotContain(typeof(global::RoxyBuildTool.CSharpModule).GetMethods(), method => method.Name == "Configure");
        Assert.DoesNotContain(typeof(global::RoxyBuildTool.BuildTarget).GetMethods(), method => method.Name == "Configure");
        Assert.DoesNotContain(typeof(global::RoxyBuildTool.BuildWorkspace).GetMethods(), method => method.Name == "Configure");
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

public sealed class ReflectedHeaderModule : global::RoxyBuildTool.CxxModule
{
    [global::RoxyBuildTool.Configure]
    private static void ConfigureAll(global::RoxyBuildTool.ModuleRules rules) =>
        rules.Output = global::RoxyBuildTool.CxxOutput.HeaderOnly;

    [global::RoxyBuildTool.Configure("profile", "debug", Priority = 100)]
    private static void ConfigureDebug(global::RoxyBuildTool.ModuleRules rules) =>
        rules.Private.Defines.Add("REFLECTED_DEBUG=1");
}

public abstract class ReflectedTargetBase : global::RoxyBuildTool.BuildTarget
{
    [global::RoxyBuildTool.Configure(Priority = -100)]
    protected static void ConfigureWindows(global::RoxyBuildTool.TargetRules rules)
    {
        rules.Matrix
            .Axis(RoxyBuildTool.Configuration.Platforms.Windows)
            .Axis(Architectures.X64)
            .Axis(BuildProfiles.Debug)
            .Axis(RoxyBuildTool.Configuration.Toolchains.Msvc)
            .Axis(LinkModels.Modular);
    }
}

public sealed class ReflectedTarget : ReflectedTargetBase
{
    [global::RoxyBuildTool.Configure]
    private static void ConfigureTarget(global::RoxyBuildTool.TargetRules rules) =>
        rules.RootModules.Add<ReflectedHeaderModule>();
}

public sealed class ReflectedWorkspace : global::RoxyBuildTool.BuildWorkspace
{
    [global::RoxyBuildTool.Configure]
    private static void ConfigureWorkspace(global::RoxyBuildTool.WorkspaceRules rules)
    {
        rules.Targets.Add<ReflectedTarget>();
        rules.StartupTarget<ReflectedTarget>();
        rules.IncludeBuildHost = false;
    }
}
