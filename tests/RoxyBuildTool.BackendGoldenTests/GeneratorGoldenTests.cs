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
            new("C:/checkout", new(".roxy/generated/compile-db/mini")));

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
            "managed-tool", "ManagedTool", ModuleLanguage.CSharp, ModuleKind.CSharpConsoleApplication,
            [new("src/Program.cs")], UsageRequirements.Empty, UsageRequirements.Empty,
            UsageRequirements.Empty, UsageRequirements.Empty, [], ["net10.0"], [], "Managed.Tool");
        var project = new WorkspaceProject("managed-tool", "ManagedTool", ModuleLanguage.CSharp,
            [new("managed-tool", configuration, module)], []);
        var model = new WorkspaceModel("Managed", "managed-tool", [project],
            [new(configuration, new("managed-tool", "ManagedTool", ["managed-tool"]), [module], [])], []);

        var result = new VisualStudio2022Generator().Generate(model,
            new("C:/checkout", new(".roxy/generated/vs2022/managed")));
        var content = result.Files.Single(file => file.Path.Value == "ManagedTool.csproj").Content;
        var document = XDocument.Parse(content);

        Assert.Equal("false", Assert.Single(document.Descendants("EnableDefaultItems")).Value);
        Assert.Empty(document.Descendants("EnableDefaultCompileItems"));
        Assert.Equal(@"..\..\..\..\src\Program.cs",
            (string?)Assert.Single(document.Descendants("Compile")).Attribute("Include"));
        Assert.Empty(document.Descendants("None"));
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

    private static string ReadGolden(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", name)).Replace("\r\n", "\n", StringComparison.Ordinal);

    [GeneratedRegex("<ProjectGuid>.*?</ProjectGuid>", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectGuidPattern();
}
