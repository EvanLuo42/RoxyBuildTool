using System.Collections.Immutable;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RoxyBuildTool.Abstractions;
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

        Assert.Equal(ReadGolden("compile_commands.json"), Assert.Single(result.Files).Content);
    }

    [Fact]
    public void SolutionUsesHumanReadableConfigurationAndWin64DisplayPlatform()
    {
        var result = new VisualStudio2022Generator().Generate(Workspace(),
            new("C:/checkout", new(".roxy/generated/vs2022/mini")));
        var solution = result.Files.Single(file => file.Path.Value == "Mini.sln").Content;

        Assert.Contains("Development|Win64 = Development|Win64", solution, StringComparison.Ordinal);
        Assert.Contains("Development|Win64.ActiveCfg = Development|x64", solution, StringComparison.Ordinal);
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
            new WorkspaceProject("editor", "Editor", ModuleLanguage.Cxx,
                [new("editor", editorConfiguration, editorModule)], []),
            new WorkspaceProject("game", "Game", ModuleLanguage.Cxx,
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

        Assert.Matches(@"\{[^}]+\}\.Development Client\|Win64\.ActiveCfg = Development Editor\|x64", solution);
        Assert.Matches(@"\{[^}]+\}\.Development Client\|Win64\.Build\.0 = Development Editor\|x64", solution);
    }

    [Fact]
    public void CSharpProjectOnlyIncludesExplicitRoxyItems()
    {
        var configuration = new ConfigurationKey([
            Configuration.Platforms.Windows,
            Architectures.X64,
            BuildProfiles.Development,
            Configuration.Toolchains.Msvc,
            LinkModels.Modular,
        ]);
        var module = new ConfiguredModule(
            "ManagedTool", "ManagedTool", ModuleLanguage.CSharp, ModuleKind.CSharpConsoleApplication,
            [new("src/Program.cs")], UsageRequirements.Empty, UsageRequirements.Empty,
            new([], [], [], [new("out/NativeRuntime.dll", "NativeRuntime:runtime")]),
            UsageRequirements.Empty, [], ["net10.0"], [], "Managed.Tool");
        var protocol = new ConfiguredModule(
            "ManagedProtocol", "ManagedProtocol", ModuleLanguage.CSharp, ModuleKind.CSharpClassLibrary,
            [new("src/Protocol.cs")], UsageRequirements.Empty, UsageRequirements.Empty,
            UsageRequirements.Empty, UsageRequirements.Empty, [], ["net10.0"], [], "Managed.Protocol");
        var native = new ConfiguredModule(
            "NativeRuntime", "NativeRuntime", ModuleLanguage.Cxx, ModuleKind.SharedLibrary,
            [new("src/Runtime.cpp")], UsageRequirements.Empty, UsageRequirements.Empty,
            UsageRequirements.Empty, UsageRequirements.Empty, [], [], []);
        var project = new WorkspaceProject("ManagedTool", "ManagedTool", ModuleLanguage.CSharp,
            [new("ManagedTool", configuration, module)], ["ManagedProtocol", "NativeRuntime"]);
        var protocolProject = new WorkspaceProject("ManagedProtocol", "ManagedProtocol", ModuleLanguage.CSharp,
            [new("ManagedTool", configuration, protocol)], []);
        var nativeProject = new WorkspaceProject("NativeRuntime", "NativeRuntime", ModuleLanguage.Cxx,
            [new("ManagedTool", configuration, native)], []);
        var model = new WorkspaceModel("Managed", "ManagedTool", [project, protocolProject, nativeProject],
            [new(configuration, new("ManagedTool", "ManagedTool", ["ManagedTool"]), [module, protocol, native], [])], []);

        var result = new VisualStudio2022Generator().Generate(model,
            new("C:/checkout", new(".roxy/generated/vs2022/managed")));
        var content = result.Files.Single(file => file.Path.Value == "ManagedTool.csproj").Content;
        var document = XDocument.Parse(content);

        Assert.Equal("false", Assert.Single(document.Descendants("EnableDefaultItems")).Value);
        Assert.Equal("$(BaseIntermediateOutputPath)packages.lock.json",
            Assert.Single(document.Descendants("NuGetLockFilePath")).Value);
        Assert.Empty(document.Descendants("EnableDefaultCompileItems"));
        Assert.Equal(@"..\..\..\..\src\Program.cs",
            (string?)Assert.Single(document.Descendants("Compile")).Attribute("Include"));
        Assert.Empty(document.Descendants("None"));
        var runtime = Assert.Single(document.Descendants("Content"));
        Assert.Equal("false", runtime.Element("Visible")?.Value);
        Assert.Equal("PreserveNewest", runtime.Element("CopyToOutputDirectory")?.Value);

        var references = document.Descendants("ProjectReference")
            .ToDictionary(reference => (string)reference.Attribute("Include")!, StringComparer.Ordinal);
        Assert.DoesNotContain("ReferenceOutputAssembly", references["ManagedProtocol.csproj"].Elements().Select(element => element.Name.LocalName));
        Assert.Equal("false", references["NativeRuntime.vcxproj"].Element("ReferenceOutputAssembly")?.Value);
        Assert.Equal("true", references["NativeRuntime.vcxproj"].Element("SkipGetTargetFrameworkProperties")?.Value);
    }

    private static WorkspaceModel Workspace()
    {
        var configuration = new ConfigurationKey([
            Configuration.Platforms.Windows,
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
            "mini", "MiniNative", ModuleLanguage.Cxx, ModuleKind.StaticLibrary,
            [new("src/mini.cpp")], usage, UsageRequirements.Empty, usage, usage,
            [], [], []);
        var configured = new ConfiguredGraph(configuration,
            new("mini", "Mini", ["mini"]), [module], []);
        var compile = new BuildAction(
            "mini:compile", BuildActionKind.Compile, "cl.exe", ["/c", "src/mini.cpp"], new("."),
            ["src/mini.cpp"], ["intermediate/mini.obj"], [], [], true, true, []);
        var actions = new ActionGraph(configuration, "mini", [compile], []);
        var project = new WorkspaceProject("mini", "MiniNative", ModuleLanguage.Cxx,
            [new("mini", configuration, module)], []);
        return new("Mini", "mini", [project], [configured], [actions]);
    }

    private static ConfigurationKey GameConfiguration(string flavor) => new([
        Configuration.Platforms.Windows,
        Architectures.X64,
        BuildProfiles.Development,
        Configuration.Toolchains.Msvc,
        LinkModels.Modular,
        new(new("Game.Flavor"), flavor),
    ]);

    private static ConfiguredModule Module(string id, string name, string source) => new(
        id, name, ModuleLanguage.Cxx, ModuleKind.Executable,
        [new(source)], UsageRequirements.Empty, UsageRequirements.Empty,
        UsageRequirements.Empty, UsageRequirements.Empty, [], [], []);

    private static string ReadGolden(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", name)).Replace("\r\n", "\n", StringComparison.Ordinal);

    [GeneratedRegex("<ProjectGuid>.*?</ProjectGuid>", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectGuidPattern();
}
