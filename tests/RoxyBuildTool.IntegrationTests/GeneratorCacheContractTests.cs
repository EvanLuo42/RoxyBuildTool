using System.Globalization;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Model;
using RoxyBuildTool.Platforms.Windows;
using Xunit;

namespace RoxyBuildTool.IntegrationTests;

public sealed class GeneratorCacheContractTests
{
    [Fact]
    public async Task GeneratorWithoutAnExternalInputFingerprintNeverUsesTheGenerationSnapshot()
    {
        using var workspace = new TemporaryWorkspace();
        var generator = new UnfingerprintedGenerator();

        Assert.Equal(0, await Run(workspace.Path, generator));
        Assert.Equal(0, await Run(workspace.Path, generator));

        Assert.Equal(2, generator.InvocationCount);
    }

    [Fact]
    public async Task DeclaredExternalInputFingerprintParticipatesInGenerationSnapshotIdentity()
    {
        using var workspace = new TemporaryWorkspace();
        var generator = new FingerprintedGenerator { ExternalFingerprint = "first" };

        Assert.Equal(0, await Run(workspace.Path, generator));
        Assert.Equal(0, await Run(workspace.Path, generator));
        Assert.Equal(1, generator.InvocationCount);

        generator.ExternalFingerprint = "second";
        Assert.Equal(0, await Run(workspace.Path, generator));
        Assert.Equal(2, generator.InvocationCount);
    }

    private static Task<int> Run(string workspaceRoot, IWorkspaceGenerator generator)
    {
        var app = BuildToolApp.Create([
                "generate", "ReflectedWorkspace", "--workspace", generator.Id.Value,
            ])
            .WithWorkspaceRoot(workspaceRoot)
            .WithOutput(TextWriter.Null)
            .DiscoverRulesFromAssemblyContaining<ReflectedRulesMarker>()
            .UseWindowsPlatform();
        app.AddService(generator);
        return app.RunAsync(TestContext.Current.CancellationToken);
    }

    private sealed class UnfingerprintedGenerator : IWorkspaceGenerator
    {
        public int InvocationCount { get; private set; }
        public WorkspaceGeneratorId Id { get; } = new("Unfingerprinted");
        public CapabilitySet Capabilities { get; } = CapabilitySet.Empty;

        public GenerationResult Generate(WorkspaceModel workspace, GenerationContext context)
        {
            InvocationCount++;
            return new(Id, [new(new("output.txt"), InvocationCount.ToString(CultureInfo.InvariantCulture))], []);
        }
    }

    private sealed class FingerprintedGenerator : IWorkspaceGenerator, IWorkspaceGeneratorFingerprintProvider
    {
        public string ExternalFingerprint { get; set; } = string.Empty;
        public int InvocationCount { get; private set; }
        public WorkspaceGeneratorId Id { get; } = new("Fingerprinted");
        public CapabilitySet Capabilities { get; } = CapabilitySet.Empty;

        public GenerationResult Generate(WorkspaceModel workspace, GenerationContext context)
        {
            InvocationCount++;
            return new(Id, [new(new("output.txt"), ExternalFingerprint)], []);
        }

        public string GetAdditionalFingerprint(WorkspaceModel workspace, GenerationContext context) =>
            ExternalFingerprint;
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"RoxyGeneratorCache-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}