using System.Text.Json;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Configuration;
using RoxyBuildTool.FakeMsBuild;
using RoxyBuildTool.Generators.CompilationDatabase;
using RoxyBuildTool.Generators.VisualStudio;
using RoxyBuildTool.Graph;
using RoxyBuildTool.Model;
using RoxyBuildTool.Platforms.Windows;
using Xunit;

namespace RoxyBuildTool.IntegrationTests;

public sealed class BuildToolEndToEndTests
{
    [Fact]
    public async Task DefaultGenerateRunsTheFullRulesGraphActionsAndBothGeneratorsIdempotently()
    {
        using var workspace = TestWorkspace.Create();
        var firstOutput = new StringWriter();

        var firstExit = await App([], workspace.Path, firstOutput).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, firstExit);
        Assert.Contains("write .roxy", firstOutput.ToString(), StringComparison.Ordinal);
        var solution = workspace.File(".roxy/generated/vs2022/integration/IntegrationWorkspace.sln");
        var nativeProject = workspace.File(".roxy/generated/vs2022/integration/IntegrationNative.Integration.vcxproj");
        var commands = workspace.File(".roxy/generated/CompileDb/integration/compile_commands.json");
        Assert.Contains("Development Editor-", solution, StringComparison.Ordinal);
        Assert.Contains("keep.cpp", nativeProject, StringComparison.Ordinal);
        Assert.Contains("nested.cpp", nativeProject, StringComparison.Ordinal);
        Assert.DoesNotContain("excluded.cpp", nativeProject, StringComparison.Ordinal);
        Assert.Contains("NATIVE_EDITOR=1", nativeProject, StringComparison.Ordinal);
        Assert.Contains("keep.cpp", commands, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(workspace.Path, ".roxy", "generated", "vs2022", "integration"),
            "*.csproj"));

        var manifestPath =
            Assert.Single(Directory.GetFiles(Path.Combine(workspace.Path, ".roxy", "manifests"), "*.json"));
        using (var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath)))
        {
            Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("Integration", manifest.RootElement.GetProperty("workspace").GetString());
            Assert.Equal(2, manifest.RootElement.GetProperty("generators").GetArrayLength());
            Assert.Equal(1, manifest.RootElement.GetProperty("configurations").GetArrayLength());
            Assert.True(manifest.RootElement.GetProperty("actions").GetArrayLength() > 0);
            Assert.Equal(3, manifest.RootElement.GetProperty("plugins").GetArrayLength());
        }

        var manifestContent = File.ReadAllText(manifestPath);
        var manifestTimestamp = File.GetLastWriteTimeUtc(manifestPath);
        var cacheRoot = Path.Combine(workspace.Path, ".roxy", "cache", "v1");
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(cacheRoot, "actions"), "*.bin"));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(cacheRoot, "generation"), "*.json"));

        var secondOutput = new StringWriter();
        var secondExit = await App([], workspace.Path, secondOutput).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, secondExit);
        Assert.Contains("unchanged .roxy", secondOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(manifestContent, File.ReadAllText(manifestPath));
        Assert.Equal(manifestTimestamp, File.GetLastWriteTimeUtc(manifestPath));

        var nativeProjectPath = Path.Combine(
            workspace.Path,
            ".roxy/generated/vs2022/integration/IntegrationNative.Integration.vcxproj");
        File.WriteAllText(nativeProjectPath, "tampered");
        using var repairedOutput = new StringWriter();
        Assert.Equal(0, await App([], workspace.Path, repairedOutput)
            .RunAsync(TestContext.Current.CancellationToken));
        Assert.Contains("write .roxy", repairedOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("keep.cpp", File.ReadAllText(nativeProjectPath), StringComparison.Ordinal);

        File.WriteAllText(
            Path.Combine(workspace.Path, "snapshot-added.cpp"),
            "int snapshot_added() { return 4; }");
        Assert.Equal(0, await App([], workspace.Path, new StringWriter())
            .RunAsync(TestContext.Current.CancellationToken));
        Assert.Contains("snapshot-added.cpp", File.ReadAllText(nativeProjectPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncrementalCacheInvalidatesChangedSourceSetsAndRecoversFromCorruptEntries()
    {
        using var workspace = TestWorkspace.Create();
        Assert.Equal(0, await App([], workspace.Path, new StringWriter())
            .RunAsync(TestContext.Current.CancellationToken));
        var cacheRoot = Path.Combine(workspace.Path, ".roxy", "cache", "v1");
        var actions = Directory.GetFiles(Path.Combine(cacheRoot, "actions"), "*.bin");
        Assert.NotEmpty(actions);

        foreach (var cacheFile in actions)
            File.WriteAllText(cacheFile, "not a valid cache entry");
        Directory.Delete(Path.Combine(cacheRoot, "generation"), true);

        using var recoveredOutput = new StringWriter();
        Assert.Equal(0, await App([], workspace.Path, recoveredOutput)
            .RunAsync(TestContext.Current.CancellationToken));
        Assert.Contains("unchanged .roxy", recoveredOutput.ToString(), StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(workspace.Path, "added.cpp"), "int added() { return 3; }");
        Assert.Equal(0, await App([], workspace.Path, new StringWriter())
            .RunAsync(TestContext.Current.CancellationToken));

        var nativeProject = workspace.File(
            ".roxy/generated/vs2022/integration/IntegrationNative.Integration.vcxproj");
        Assert.Contains("added.cpp", nativeProject, StringComparison.Ordinal);
        Assert.True(Directory.GetFiles(Path.Combine(cacheRoot, "actions"), "*.bin").Length >
                    actions.Length);

        File.WriteAllText(
            Path.Combine(workspace.Path, "filtered", "filtered-added.cpp"),
            "int filtered_added() { return 5; }");
        Assert.Equal(0, await App([], workspace.Path, new StringWriter())
            .RunAsync(TestContext.Current.CancellationToken));
        Assert.Contains("filtered-added.cpp", workspace.File(
                ".roxy/generated/vs2022/integration/IntegrationNative.Integration.vcxproj"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void IncrementalCacheFingerprintTreatsDefaultImmutableArraysAsEmpty()
    {
        var usage = new UsageRequirements([], [], [], []);
        var module = new ModuleDefinition(
            "module",
            "Module",
            ModuleKind.StaticLibrary,
            [],
            usage,
            usage,
            [],
            []);
        var target = new TargetDefinition("target", "Target", [module.Id], new MatrixBuilder().Build());
        var definitions = new DefinitionGraph([module], [target], []);

        var fingerprint = PipelineCache.DefinitionFingerprint(definitions, target);

        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
        Assert.True(module.PublicUsage.SystemIncludeDirectories.IsDefault);
    }

    [Fact]
    public async Task GenerateRemovesOnlyPreviouslyOwnedStaleFiles()
    {
        using var workspace = TestWorkspace.Create();
        Assert.Equal(0, await App([], workspace.Path, new StringWriter())
            .RunAsync(TestContext.Current.CancellationToken));
        var outputRoot = Path.Combine(workspace.Path, ".roxy", "generated", "Vs2022", "integration");
        var ownershipPath = Path.Combine(outputRoot, ".roxy-outputs.json");
        var stalePath = Path.Combine(outputRoot, "OldGenerated.vcxproj");
        var userPath = Path.Combine(outputRoot, "UserNotes.txt");
        File.WriteAllText(stalePath, "stale");
        File.WriteAllText(userPath, "keep");
        var tracked = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ownershipPath))!;
        tracked.Add("OldGenerated.vcxproj");
        File.WriteAllText(ownershipPath, JsonSerializer.Serialize(tracked));
        using var output = new StringWriter();

        var exitCode = await App([], workspace.Path, output).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(stalePath));
        Assert.True(File.Exists(userPath));
        Assert.Contains("remove", output.ToString(), StringComparison.Ordinal);

        File.WriteAllText(ownershipPath, "not json");
        Assert.Equal(0, await App([], workspace.Path, new StringWriter())
            .RunAsync(TestContext.Current.CancellationToken));
        Assert.True(File.Exists(userPath));
    }

    [Fact]
    public async Task FullMatrixGenerationIsDeterministicWithParallelConfigurationResolution()
    {
        using var workspace = TestWorkspace.Create();

        var first = await Run([
            "generate", "IntegrationWorkspace", "--workspace", "CompileDb"
        ], workspace.Path);
        var firstContent = workspace.File(
            ".roxy/generated/CompileDb/integration/compile_commands.json");
        var second = await Run([
            "generate", "IntegrationWorkspace", "--workspace", "CompileDb"
        ], workspace.Path);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("unchanged", second.Output, StringComparison.Ordinal);
        Assert.Equal(firstContent, workspace.File(
            ".roxy/generated/CompileDb/integration/compile_commands.json"));
        using var commands = JsonDocument.Parse(firstContent);
        Assert.True(commands.RootElement.GetArrayLength() > 5);
    }

    [Fact]
    public async Task QueryAndExplainCommandsCoverTextJsonExclusionsAndOptimization()
    {
        using var workspace = TestWorkspace.Create();

        var matrix = await Run(["query", "matrix", "IntegrationTarget", "--why-excluded"], workspace.Path);
        Assert.Equal(0, matrix.ExitCode);
        Assert.Contains("5 configurations", matrix.Output, StringComparison.Ordinal);
        Assert.Contains("excluded", matrix.Output, StringComparison.Ordinal);
        Assert.Contains("Editor shipping is unsupported", matrix.Output, StringComparison.Ordinal);

        var dot = await Run([
            "query", "graph", "IntegrationTarget", "--profile", "development",
            "--fragment", "Integration.Flavor=editor",
        ], workspace.Path);
        Assert.Equal(0, dot.ExitCode);
        Assert.Contains("digraph roxy", dot.Output, StringComparison.Ordinal);
        Assert.Contains("IntegrationApplication", dot.Output, StringComparison.Ordinal);
        Assert.Contains("label=\"public\"", dot.Output, StringComparison.Ordinal);

        var json = await Run([
            "query", "graph", "IntegrationTarget", "--profile", "development",
            "--fragment", "Integration.Flavor=client", "--format", "json",
        ], workspace.Path);
        Assert.Equal(0, json.ExitCode);
        using (var document = JsonDocument.Parse(json.Output))
        {
            Assert.True(document.RootElement.TryGetProperty("Configuration", out _));
            Assert.Contains("IntegrationNative", json.Output, StringComparison.Ordinal);
        }

        var optimization = await Run([
            "explain", "IntegrationTarget", "--profile", "debug",
            "--fragment", "Integration.Flavor=client", "--setting", "Compiler.Optimization",
        ], workspace.Path);
        Assert.Equal(0, optimization.ExitCode);
        Assert.Contains("Compiler.Optimization = off (Profile:Debug)", optimization.Output, StringComparison.Ordinal);

        var usage = await Run([
            "explain", "IntegrationTarget", "--profile", "development",
            "--fragment", "Integration.Flavor=editor",
        ], workspace.Path);
        Assert.Equal(0, usage.ExitCode);
        Assert.Contains("define NATIVE_EDITOR=1", usage.Output, StringComparison.Ordinal);
        Assert.Contains("runtime", usage.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildGeneratesAScopedWorkspaceAndInvokesTheConfiguredMsBuildProcess()
    {
        using var workspace = TestWorkspace.Create();
        using var output = new StringWriter();
        var fakeMsBuild = Path.ChangeExtension(typeof(FakeMsBuildMarker).Assembly.Location, ".exe");
        Assert.True(File.Exists(fakeMsBuild));

        var exitCode = await App([
                "build", "IntegrationTarget", "--profile", "development",
                "--fragment", "Integration.Flavor=client",
            ], workspace.Path, output)
            .WithMsBuild(fakeMsBuild)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        var arguments = File.ReadAllLines(Path.Combine(workspace.Path, ".roxy", "fake-msbuild.args"));
        Assert.EndsWith("IntegrationWorkspace.IntegrationTarget.Build.sln", arguments[0], StringComparison.Ordinal);
        Assert.Contains("/m", arguments);
        Assert.Contains("/restore", arguments);
        Assert.Contains("/t:Build", arguments);
        Assert.Contains(arguments, argument =>
            argument.StartsWith("/p:Configuration=Development Client-", StringComparison.Ordinal));
        Assert.Contains("/p:Platform=Win64", arguments);
        Assert.Contains("/verbosity:minimal", arguments);

        var editorExitCode = await App([
                "build", "IntegrationTarget", "--profile", "development",
                "--fragment", "Integration.Flavor=editor"
            ], workspace.Path, new StringWriter())
            .WithMsBuild(fakeMsBuild)
            .RunAsync(TestContext.Current.CancellationToken);
        var editorArguments = File.ReadAllLines(Path.Combine(workspace.Path, ".roxy", "fake-msbuild.args"));

        Assert.Equal(0, editorExitCode);
        Assert.NotEqual(arguments[0], editorArguments[0]);
        Assert.Contains(editorArguments, argument =>
            argument.StartsWith("/p:Configuration=Development Editor-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationIsNotConvertedIntoAGenericDiagnostic()
    {
        using var workspace = TestWorkspace.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            App([], workspace.Path, new StringWriter()).RunAsync(cancellation.Token));
    }

    [Fact]
    public async Task CommandFragmentAndRuntimeFailuresHaveDistinctExitCodes()
    {
        using var workspace = TestWorkspace.Create();

        var command = await Run(["unknown"], workspace.Path);
        Assert.Equal(2, command.ExitCode);
        Assert.Contains("RBT0001", command.Error, StringComparison.Ordinal);

        var selector = await Run([
            "query", "matrix", "IntegrationTarget", "--fragment", "unknown=value",
        ], workspace.Path);
        Assert.Equal(2, selector.ExitCode);
        Assert.Contains("RBT1103", selector.Error, StringComparison.Ordinal);

        var noMatch = await Run([
            "query", "graph", "IntegrationTarget", "--profile", "shipping",
            "--fragment", "Integration.Flavor=editor",
        ], workspace.Path);
        Assert.Equal(2, noMatch.ExitCode);
        Assert.Contains("RBT0001", noMatch.Error, StringComparison.Ordinal);

        var ambiguous = await Run(["query", "graph", "IntegrationTarget"], workspace.Path);
        Assert.Equal(2, ambiguous.ExitCode);
        Assert.Contains("requires exactly one configuration", ambiguous.Error, StringComparison.Ordinal);

        var ambiguousBuild = await Run(["build", "IntegrationTarget"], workspace.Path);
        Assert.Equal(2, ambiguousBuild.ExitCode);
        Assert.Contains("requires exactly one configuration", ambiguousBuild.Error, StringComparison.Ordinal);

        using var missingOutput = new StringWriter();
        using var missingError = new StringWriter();
        var missingGenerator = await BuildToolApp.Create([
                "generate", "IntegrationWorkspace", "--workspace", "missing",
                "--profile", "debug", "--fragment", "Integration.Flavor=client",
            ])
            .WithWorkspaceRoot(workspace.Path)
            .WithOutput(missingOutput, missingError)
            .AddRules<IntegrationRulesModule>()
            .UseWindowsPlatform()
            .RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, missingGenerator);
        Assert.Contains("not registered", missingError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DependencyDiagnosticsFlowThroughQueryGraphExitCode()
    {
        using var workspace = TestWorkspace.Create();
        using var output = new StringWriter();

        var exitCode = await BuildToolApp.Create(["query", "graph", "CycleTarget"])
            .WithWorkspaceRoot(workspace.Path)
            .WithOutput(output)
            .AddRules<CycleRulesModule>()
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Contains("CycleA", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpAndVersionDoNotRequireRulesOrPlugins()
    {
        using var helpOutput = new StringWriter();
        var helpExit = await BuildToolApp.Create(["--help"]).WithOutput(helpOutput)
            .RunAsync(TestContext.Current.CancellationToken);
        using var versionOutput = new StringWriter();
        var versionExit = await BuildToolApp.Create(["--version"]).WithOutput(versionOutput)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, helpExit);
        Assert.Contains("query graph", helpOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, versionExit);
        Assert.Matches("^[0-9]+\\.[0-9]+", versionOutput.ToString());
    }

    [Fact]
    public async Task IncompatiblePluginsFailBeforeRegistration()
    {
        using var versionError = new StringWriter();
        var versionApp = BuildToolApp.Create([]).WithOutput(TextWriter.Null, versionError);
        versionApp.AddPlugin(new TestPlugin("Future", new Version(99, 0)));
        var versionExit = await versionApp.RunAsync(TestContext.Current.CancellationToken);

        using var capabilityError = new StringWriter();
        var capabilityApp = BuildToolApp.Create([]).WithOutput(TextWriter.Null, capabilityError);
        capabilityApp.AddPlugin(new TestPlugin("Consumer", required: ["Missing.Capability"]));
        var capabilityExit = await capabilityApp.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, versionExit);
        Assert.Contains("RBT0201", versionError.ToString(), StringComparison.Ordinal);
        Assert.Contains("host API", versionError.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, capabilityExit);
        Assert.Contains("Missing.Capability", capabilityError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicatePluginsAreRejectedBeforeExecution()
    {
        var app = BuildToolApp.Create([]).UseWindowsPlatform();
        var exception = Assert.Throws<InvalidOperationException>(() => app.UseWindowsPlatform());
        Assert.Contains("already registered", exception.Message, StringComparison.Ordinal);
    }

    private static BuildToolApp App(string[] args, string root, TextWriter output, TextWriter? error = null)
    {
        return BuildToolApp.Create(args)
            .WithWorkspaceRoot(root)
            .WithOutput(output, error)
            .AddRules<IntegrationRulesModule>()
            .UseWindowsPlatform()
            .UseVisualStudio()
            .UseCompilationDatabase()
            .DefaultGenerate<IntegrationWorkspace>(request => request
                .Workspace(WorkspaceGenerators.VisualStudio2022,
                    WorkspaceGenerators.CompilationDatabase,
                    WorkspaceGenerators.VisualStudio2022)
                .Select(BuildProfiles.Development)
                .Select(IntegrationFlavor.Editor));
    }

    private static async Task<(int ExitCode, string Output, string Error)> Run(string[] args, string root)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await App(args, root, output, error).RunAsync(TestContext.Current.CancellationToken);
        return (exitCode, output.ToString(), error.ToString());
    }

    private sealed class TestWorkspace : IDisposable
    {
        private TestWorkspace(string path) => Path = path;
        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }

        public static TestWorkspace Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"RoxyIntegration-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            Directory.CreateDirectory(System.IO.Path.Combine(path, "include"));
            Directory.CreateDirectory(System.IO.Path.Combine(path, "external"));
            Directory.CreateDirectory(System.IO.Path.Combine(path, "nested"));
            Directory.CreateDirectory(System.IO.Path.Combine(path, "filtered"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(path, "keep.cpp"), "int keep() { return 1; }");
            System.IO.File.WriteAllText(System.IO.Path.Combine(path, "nested", "nested.cpp"),
                "int nested() { return 2; }");
            System.IO.File.WriteAllText(System.IO.Path.Combine(path, "excluded.cpp"), "int excluded() { return 0; }");
            System.IO.File.WriteAllText(System.IO.Path.Combine(path, "filtered", "initial.cpp"),
                "int filtered_initial() { return 4; }");
            return new(path);
        }

        public string File(string relativePath)
        {
            return System.IO.File.ReadAllText(System.IO.Path.Combine(Path, relativePath));
        }
    }

    private sealed class TestPlugin(
        string id,
        Version? minimum = null,
        IEnumerable<string>? capabilities = null,
        IEnumerable<string>? required = null) : IPlugin
    {
        public PluginId Id { get; } = new(id);
        public Version Version { get; } = new(1, 0);
        public Version MinimumHostApiVersion { get; } = minimum ?? new Version(0, 1, 0);
        public CapabilitySet Capabilities { get; } = new(capabilities ?? []);
        public CapabilitySet RequiredCapabilities { get; } = new(required ?? []);

        public void Register(IPluginRegistry registry)
        {
            throw new InvalidOperationException("Incompatible plugins must not be registered.");
        }
    }
}