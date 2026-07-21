using System.Text.Json;
using System.Text.RegularExpressions;
using RoxyBuildTool.Configuration;
using RoxyBuildTool.Generators.CompilationDatabase;
using RoxyBuildTool.Generators.VisualStudio;
using RoxyBuildTool.Model;
using Xunit;

namespace RoxyBuildTool.BackendGoldenTests;

public sealed partial class GeneratorGoldenTests
{
    [Fact]
    public void VisualStudioProjectMatchesGoldenFile()
    {
        var model = Workspace();
        var result = new VisualStudio2022Generator().Generate(model,
            new("C:/checkout", new(".roxy/generated/vs2022/mini")));
        var actual = result.Files.Single(file => file.Path.Value == "MiniNative.vcxproj").Content;
        actual = ProjectGuidPattern().Replace(actual, "<ProjectGuid>{GUID}</ProjectGuid>");

        Assert.Equal(ReadGolden("mini.vcxproj"), actual);
    }

    [Fact]
    public void CompilationDatabaseMatchesGoldenFile()
    {
        var result = new CompilationDatabaseGenerator().Generate(Workspace(),
            new("C:/checkout", new(".roxy/generated/CompileDb/mini")));
        var actual = Assert.Single(result.Files).Content.Replace(
            JsonSerializer.Serialize(Path.GetFullPath("C:/checkout")),
            JsonSerializer.Serialize("."),
            StringComparison.Ordinal);

        Assert.Equal(ReadGolden("compile_commands.json"), actual);
    }

    [Fact]
    public void SolutionUsesHumanReadableConfigurationAndWin64DisplayPlatform()
    {
        var model = Workspace();
        var result = new VisualStudio2022Generator().Generate(model,
            new("C:/checkout", new(".roxy/generated/vs2022/mini")));
        var solution = result.Files.Single(file => file.Path.Value == "Mini.sln").Content;

        var displayName = BuildConfigurationNames.DisplayName(model.Projects[0].Variants[0].Configuration);
        Assert.Contains($"{displayName}|Win64 = {displayName}|Win64", solution, StringComparison.Ordinal);
        Assert.Contains($"{displayName}|Win64.ActiveCfg = {displayName}|x64", solution, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedSolutionConfigurationMapsToExistingProjectConfiguration()
    {
        var clientConfiguration = GameConfiguration("client");
        var editorConfiguration = GameConfiguration("editor");
        var gameModule = Module("game", "Game", "Game/Main.cpp");
        var editorModule = Module("editor", "Editor", "Editor/Main.cpp");
        var projects = new[]
        {
            new WorkspaceProject("editor", "Editor",
                [new("editor", editorConfiguration, editorModule)], []),
            new WorkspaceProject("game", "Game",
                [new("game", clientConfiguration, gameModule)], []),
        };
        var model = new WorkspaceModel("Mapped", "game", [.. projects],
        [
            new(clientConfiguration, new("game", "Game", ["game"]), [gameModule], []),
            new(editorConfiguration, new("editor", "Editor", ["editor"]), [editorModule], []),
        ], []);

        var result = new VisualStudio2022Generator().Generate(model,
            new("C:/checkout", new(".roxy/generated/vs2022/mapped")));
        var solution = result.Files.Single(file => file.Path.Value == "Mapped.sln").Content;

        var client = Regex.Escape(BuildConfigurationNames.DisplayName(clientConfiguration));
        var editor = Regex.Escape(BuildConfigurationNames.DisplayName(editorConfiguration));
        Assert.Matches($@"\{{[^}}]+\}}\.{client}\|Win64\.ActiveCfg = {editor}\|x64", solution);
        Assert.DoesNotMatch($@"\{{[^}}]+\}}\.{client}\|Win64\.Build\.0 = {editor}\|x64", solution);
    }

    private static WorkspaceModel Workspace()
    {
        var configuration = new ConfigurationKey([
            Platforms.Windows,
            Architectures.X64,
            BuildProfiles.Development,
            Configuration.Toolchains.Msvc,
            LinkModels.Modular,
        ]);
        var usage = new UsageRequirements(
            [new("include", "mini:public")],
            [new("MINI=1", "mini:public")],
            [],
            []);
        var module = new ConfiguredModule(
            "mini", "MiniNative", ModuleKind.StaticLibrary,
            [new("src/mini.cpp")], usage, UsageRequirements.Empty, usage, usage,
            []);
        var configured = new ConfiguredGraph(configuration,
            new("mini", "Mini", ["mini"]), [module], []);
        var compile = new BuildAction(
            "mini:compile", BuildActionKind.Compile, "cl.exe", ["/c", "src/mini.cpp"], new("."),
            ["src/mini.cpp"], ["intermediate/mini.obj"], [], [], true, true, []);
        var actions = new ActionGraph(configuration, "mini", [compile], []);
        var project = new WorkspaceProject("mini", "MiniNative",
            [new("mini", configuration, module)], []);
        return new("Mini", "mini", [project], [configured], [actions]);
    }

    private static ConfigurationKey GameConfiguration(string flavor) => new([
        Platforms.Windows,
        Architectures.X64,
        BuildProfiles.Development,
        Configuration.Toolchains.Msvc,
        LinkModels.Modular,
        new(new("Game.Flavor"), flavor),
    ]);

    private static ConfiguredModule Module(string id, string name, string source) => new(
        id, name, ModuleKind.Executable,
        [new(source)], UsageRequirements.Empty, UsageRequirements.Empty,
        UsageRequirements.Empty, UsageRequirements.Empty, []);

    private static string ReadGolden(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", name))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    [GeneratedRegex("<ProjectGuid>.*?</ProjectGuid>", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectGuidPattern();
}