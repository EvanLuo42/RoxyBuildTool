using System.Text.Json;
using RoxyBuildTool.FakeMsBuild;
using RoxyBuildTool.Generators.CompilationDatabase;
using RoxyBuildTool.Generators.VisualStudio;
using RoxyBuildTool.Platforms.Windows;
using WindowsMvp.Build;
using Xunit;

namespace RoxyBuildTool.IntegrationTests;

public sealed class WindowsGameWorkspaceEndToEndTests
{
    [Fact]
    public async Task FullGameWorkspaceGeneratesAllNativeProjectsAndCompilationCommands()
    {
        using var workspace = GameWorkspaceFixture.Create();
        using var output = new StringWriter();

        var exitCode = await App([], workspace.Path, output)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("write .roxy", output.ToString(), StringComparison.Ordinal);

        var generatedRoot = Path.Combine(workspace.Path, ".roxy", "generated", "Vs2022", "Game");
        var solution = File.ReadAllText(Path.Combine(generatedRoot, "GameWorkspace.sln"));
        Assert.Contains("Project(\"{", solution, StringComparison.Ordinal);
        Assert.Contains("= \"Game\", \"Game.vcxproj\"", solution, StringComparison.Ordinal);
        Assert.Contains("= \"Editor\", \"Editor.vcxproj\"", solution, StringComparison.Ordinal);
        Assert.Contains("Shipping DedicatedServer-", solution, StringComparison.Ordinal);
        Assert.Contains("Release Editor-", solution, StringComparison.Ordinal);
        Assert.DoesNotContain("Shipping Editor-", solution, StringComparison.Ordinal);
        Assert.Equal(8, Directory.GetFiles(generatedRoot, "*.vcxproj").Length);

        var game = File.ReadAllText(Path.Combine(generatedRoot, "Game.vcxproj"));
        var editor = File.ReadAllText(Path.Combine(generatedRoot, "Editor.vcxproj"));
        var core = File.ReadAllText(Path.Combine(generatedRoot, "EngineCore.Game.vcxproj"));
        var runtime = File.ReadAllText(Path.Combine(generatedRoot, "EngineRuntime.Game.vcxproj"));
        var headers = File.ReadAllText(Path.Combine(generatedRoot, "EngineHeaders.Game.vcxproj"));

        Assert.Contains(@"Game\Main.cpp", game, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"Editor\Main.cpp", editor, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"Engine\Core\EngineCore.cpp", core, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"Engine\Core\Private", core, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ROXY_DEDICATED_SERVER=1", core, StringComparison.Ordinal);
        Assert.Contains(@"Engine\Runtime\EngineRuntime.cpp", runtime, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"Engine\Runtime\Public", runtime, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ROXY_WITH_CORE=1", runtime, StringComparison.Ordinal);
        Assert.Contains(@"Engine\Headers", headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ROXY_ENGINE_HEADERS=1", headers, StringComparison.Ordinal);

        var compileDatabasePath = Path.Combine(
            workspace.Path, ".roxy", "generated", "CompileDb", "Game", "compile_commands.json");
        using var compileDatabase = JsonDocument.Parse(File.ReadAllText(compileDatabasePath));
        Assert.Equal(33, compileDatabase.RootElement.GetArrayLength());

        var dedicatedCoreCommands = compileDatabase.RootElement.EnumerateArray()
            .Where(command => command.GetProperty("file").GetString()!.EndsWith(
                "Engine/Core/EngineCore.cpp", StringComparison.OrdinalIgnoreCase))
            .Where(command => command.GetProperty("arguments").EnumerateArray()
                .Any(argument => argument.GetString() == "/DROXY_DEDICATED_SERVER=1"))
            .ToArray();
        Assert.Equal(4, dedicatedCoreCommands.Length);
    }

    [Fact]
    public async Task GameAndEditorTargetsCoverInheritedMatricesQueriesAndBuildInvocation()
    {
        using var workspace = GameWorkspaceFixture.Create();

        var gameMatrix = await Run(["query", "matrix", "GameTarget"], workspace.Path);
        Assert.Equal(0, gameMatrix.ExitCode);
        Assert.Contains("8 configurations", gameMatrix.Output, StringComparison.Ordinal);
        Assert.Contains("Client", gameMatrix.Output, StringComparison.Ordinal);
        Assert.Contains("DedicatedServer", gameMatrix.Output, StringComparison.Ordinal);

        var editorMatrix = await Run([
            "query", "matrix", "EditorTarget", "--why-excluded",
        ], workspace.Path);
        Assert.Equal(0, editorMatrix.ExitCode);
        Assert.Contains("3 configurations", editorMatrix.Output, StringComparison.Ordinal);
        Assert.Contains("Editor is never shipped.", editorMatrix.Output, StringComparison.Ordinal);

        var graph = await Run([
            "query", "graph", "GameTarget", "--profile", "development",
            "--fragment", "Game.Flavor=DedicatedServer", "--format", "json",
        ], workspace.Path);
        Assert.Equal(0, graph.ExitCode);
        Assert.Contains("GameExecutable", graph.Output, StringComparison.Ordinal);
        Assert.Contains("EngineRuntime", graph.Output, StringComparison.Ordinal);
        Assert.Contains("EngineCore", graph.Output, StringComparison.Ordinal);
        Assert.Contains("EngineHeaders", graph.Output, StringComparison.Ordinal);

        var fakeMsBuild = Path.ChangeExtension(typeof(FakeMsBuildMarker).Assembly.Location, ".exe");
        Assert.True(File.Exists(fakeMsBuild));
        var buildExitCode = await App([
                "build", "GameTarget", "--platform", "windows", "--arch", "x64",
                "--profile", "development", "--fragment", "Game.Flavor=DedicatedServer",
            ], workspace.Path, TextWriter.Null)
            .WithMsBuild(fakeMsBuild)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, buildExitCode);
        var arguments = File.ReadAllLines(Path.Combine(workspace.Path, ".roxy", "fake-msbuild.args"));
        Assert.EndsWith("GameWorkspace.GameTarget.Build.sln", arguments[0], StringComparison.Ordinal);
        Assert.Contains(arguments, argument =>
            argument.StartsWith("/p:Configuration=Development DedicatedServer-", StringComparison.Ordinal));
        Assert.Contains("/p:Platform=Win64", arguments);
    }

    private static BuildToolApp App(string[] args, string root, TextWriter output, TextWriter? error = null) =>
        BuildToolApp.Create(args)
            .WithWorkspaceRoot(root)
            .WithOutput(output, error)
            .DiscoverRulesFromAssemblyContaining<WindowsMvpRules>()
            .UseWindowsPlatform()
            .UseVisualStudio()
            .UseCompilationDatabase()
            .DefaultGenerate<GameWorkspace>(request => request.Workspace(
                WorkspaceGenerators.VisualStudio2022,
                WorkspaceGenerators.CompilationDatabase));

    private static async Task<(int ExitCode, string Output, string Error)> Run(string[] args, string root)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await App(args, root, output, error)
            .RunAsync(TestContext.Current.CancellationToken);
        return (exitCode, output.ToString(), error.ToString());
    }

    private sealed class GameWorkspaceFixture : IDisposable
    {
        private GameWorkspaceFixture(string path) => Path = path;

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }

        public static GameWorkspaceFixture Create()
        {
            var fixtureRoot = System.IO.Path.Combine(AppContext.BaseDirectory, "WindowsGameWorkspaceFixture");
            Assert.True(Directory.Exists(fixtureRoot), $"Missing game workspace fixture: {fixtureRoot}");
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"RoxyWindowsMvpIntegration-{Guid.NewGuid():N}");
            CopyDirectory(fixtureRoot, path);
            return new GameWorkspaceFixture(path);
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(System.IO.Path.Combine(
                    destination, System.IO.Path.GetRelativePath(source, directory)));
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, System.IO.Path.Combine(destination, System.IO.Path.GetRelativePath(source, file)));
        }
    }
}
