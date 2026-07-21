using System.Collections.Immutable;
using System.Text.Json;
using System.Xml.Linq;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Configuration;
using RoxyBuildTool.Generators.CompilationDatabase;
using RoxyBuildTool.Generators.VisualStudio;
using RoxyBuildTool.Model;
using Xunit;

namespace RoxyBuildTool.BackendGoldenTests;

public sealed class GeneratorBoundaryTests
{
    private static readonly XNamespace MsBuild = "http://schemas.microsoft.com/developer/msbuild/2003";

    [Fact]
    public void NativeGenerationCoversAllProjectKindsProfilesUsageAndReferences()
    {
        var debug = Configuration(BuildProfiles.Debug);
        var release = Configuration(BuildProfiles.Release);
        var header = Module("header", "HeaderModule", ModuleKind.HeaderOnly, ["include/header.h"]);
        var objects = Module("objects", "Objects", ModuleKind.ObjectLibrary, ["src/z.cpp", "src/a.cpp"]);
        var library = Module("library", "Library", ModuleKind.StaticLibrary, ["src/library.cpp"]);
        var shared = Module("shared", "Shared", ModuleKind.SharedLibrary, ["src/shared.cpp"]);
        var debugExecutable = Module("application", "Application", ModuleKind.Executable, ["src/main.cpp"]) with
        {
            CompileUsage = new(
                [new("include/public", "test")],
                [new("APP=1", "test")],
                [new("out/library.lib", "test")],
                []),
        };
        var releaseExecutable = debugExecutable with { Sources = [new("src/main.cpp"), new("src/release.cpp")] };
        var projects = new[]
        {
            Project("header", "HeaderModule", [new("app", debug, header)]),
            Project("objects", "Objects", [new("app", debug, objects)]),
            Project("library", "Library", [new("app", debug, library)]),
            Project("shared", "Shared", [new("app", debug, shared)]),
            Project("application", "Application", [new("app", debug, debugExecutable), new("app", release, releaseExecutable)], ["library"]),
        };
        var model = new WorkspaceModel("Native", "app", [.. projects], [], []);

        var result = Generator().Generate(model, Context("native"));

        Assert.Equal("Utility", ConfigurationType(result, "Header.vcxproj"));
        Assert.Equal("StaticLibrary", ConfigurationType(result, "Objects.vcxproj"));
        Assert.Equal("StaticLibrary", ConfigurationType(result, "Library.vcxproj"));
        Assert.Equal("DynamicLibrary", ConfigurationType(result, "Shared.vcxproj"));
        Assert.Equal("Application", ConfigurationType(result, "Application.vcxproj"));

        var application = Parse(result, "Application.vcxproj");
        var configurations = application.Descendants(MsBuild + "PropertyGroup")
            .Where(element => (string?)element.Attribute("Label") == "Configuration").ToArray();
        Assert.Contains(configurations, group => group.Element(MsBuild + "UseDebugLibraries")?.Value == "true");
        Assert.Contains(configurations, group => group.Element(MsBuild + "UseDebugLibraries")?.Value == "false");
        var compile = application.Descendants(MsBuild + "ClCompile").First(element => element.Element(MsBuild + "LanguageStandard") is not null);
        Assert.Contains("$(RoxyWorkspaceRoot)\\include\\public", compile.Element(MsBuild + "AdditionalIncludeDirectories")?.Value, StringComparison.Ordinal);
        Assert.Contains("APP=1", compile.Element(MsBuild + "PreprocessorDefinitions")?.Value, StringComparison.Ordinal);
        Assert.Contains(application.Descendants(MsBuild + "BasicRuntimeChecks"), element => element.Value == "EnableFastChecks");
        Assert.Contains(application.Descendants(MsBuild + "Optimization"), element => element.Value == "MaxSpeed");
        var link = Assert.Single(application.Descendants(MsBuild + "Link").Take(1));
        Assert.Contains("out\\library.lib", link.Element(MsBuild + "AdditionalDependencies")?.Value, StringComparison.Ordinal);
        Assert.Equal("true", link.Element(MsBuild + "GenerateDebugInformation")?.Value);
        Assert.Equal("Library.vcxproj",
            (string?)Assert.Single(application.Descendants(MsBuild + "ProjectReference")).Attribute("Include"));

        var sourceItems = application.Descendants(MsBuild + "ClCompile")
            .Where(element => element.Attribute("Include") is not null)
            .Select(element => (string)element.Attribute("Include")!).ToArray();
        Assert.Equal(["..\\..\\..\\..\\src\\main.cpp", "..\\..\\..\\..\\src\\release.cpp"], sourceItems);
        var filters = Parse(result, "Application.vcxproj.filters");
        Assert.Equal(sourceItems, filters.Descendants(MsBuild + "ClCompile").Select(element => (string)element.Attribute("Include")!));

        var solution = File(result, "Native.sln");
        Assert.Contains("Debug|Win64", solution, StringComparison.Ordinal);
        Assert.Contains("Release|Win64", solution, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Build.props", result.Files.Select(file => file.Path.Value));
    }

    [Fact]
    public void ManagedGenerationCoversDefaultsPackagesVariantsAndDirectoryProps()
    {
        var debug = Configuration(BuildProfiles.Debug);
        var shipping = Configuration(BuildProfiles.Shipping);
        var debugModule = Managed("library", "Managed Library", ModuleKind.CSharpClassLibrary, debug,
            targetFrameworks: [], packages:
            [
                new("Private.Package", "2.0.0", true),
                new("Public.Package", "1.0.0"),
            ]);
        var shippingModule = debugModule with
        {
            Sources = [new("managed/Library.cs"), new("managed/Shipping.cs")],
            Packages = [new("Public.Package", "1.0.0")],
        };
        var project = new WorkspaceProject("library", "Managed Library", ModuleLanguage.CSharp,
            [new("app", debug, debugModule), new("app", shipping, shippingModule)], []);
        var result = Generator().Generate(new("Managed", "app", [project], [], []), Context("ManagedDefaults"));

        var document = Parse(result, "ManagedLibrary.csproj");

        Assert.Equal("Library", Assert.Single(document.Descendants("OutputType")).Value);
        Assert.Equal("net10.0", Assert.Single(document.Descendants("TargetFramework")).Value);
        Assert.Equal("Managed.Library", Assert.Single(document.Descendants("RootNamespace")).Value);
        Assert.Equal(["Library.cs", "Shipping.cs"], document.Descendants("Compile")
            .Select(element => Path.GetFileName((string)element.Attribute("Include")!)));
        Assert.Empty(document.Descendants("Content"));
        var packages = document.Descendants("PackageReference").ToDictionary(
            element => (string)element.Attribute("Include")!, StringComparer.Ordinal);
        Assert.Equal("all", (string?)packages["Private.Package"].Attribute("PrivateAssets"));
        Assert.Null(packages["Public.Package"].Attribute("PrivateAssets"));
        Assert.Equal(2, packages.Count);
        Assert.Contains(document.Descendants("Optimize"), element => element.Value == "false");
        Assert.Contains(document.Descendants("Optimize"), element => element.Value == "true");

        var props = Parse(result, "Directory.Build.props");
        Assert.Contains("intermediate\\msbuild", Assert.Single(props.Descendants("BaseIntermediateOutputPath")).Value, StringComparison.Ordinal);
    }

    [Fact]
    public void SolutionIncludesImportedBuildHostAndExplicitDependencySection()
    {
        var configuration = Configuration(BuildProfiles.Development);
        var module = Module("native", "Native", ModuleKind.StaticLibrary, ["native.cpp"]);
        var project = Project("native", "Native", [new("app", configuration, module)], ["BuildRules"]);
        var host = new WorkspaceProject("BuildRules", "Build Rules", ModuleLanguage.CSharp, [], [], true,
            new("Build/RoxyBuild.csproj"));

        var result = Generator().Generate(new("Hosted", "app", [project, host], [], []), Context("hosted"));
        var solution = File(result, "Hosted.sln");

        Assert.Contains("..\\..\\..\\..\\Build\\RoxyBuild.csproj", solution, StringComparison.Ordinal);
        Assert.Contains("ProjectSection(ProjectDependencies)", solution, StringComparison.Ordinal);
        Assert.Contains("Debug|Any CPU", solution, StringComparison.Ordinal);
        Assert.DoesNotContain(".Build.0 = Debug|Any CPU", solution, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectFileNamesAreSanitizedWithFallbackAndModuleSuffixRemoval()
    {
        var configuration = Configuration(BuildProfiles.Development);
        var first = Module("FallbackName", "!!!", ModuleKind.StaticLibrary, ["first.cpp"]);
        var second = Module("suffix", "CleanModule", ModuleKind.StaticLibrary, ["second.cpp"]);
        var result = Generator().Generate(new("Names", "target",
            [
                Project("FallbackName", "!!!", [new("target", configuration, first)]),
                Project("suffix", "CleanModule", [new("target", configuration, second)]),
            ], [], []), Context("names"));

        Assert.Contains(result.Files, file => file.Path.Value == "FallbackName.vcxproj");
        Assert.Contains(result.Files, file => file.Path.Value == "Clean.vcxproj");
    }

    [Fact]
    public void InvalidNativeKindAndUnknownProjectDependencyFailFast()
    {
        var configuration = Configuration(BuildProfiles.Development);
        var invalid = Module("invalid", "Invalid", ModuleKind.CSharpClassLibrary, ["invalid.cpp"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => Generator().Generate(
            new("Invalid", "target", [Project("invalid", "Invalid", [new("target", configuration, invalid)])], [], []),
            Context("invalid")));

        var valid = Module("valid", "Valid", ModuleKind.StaticLibrary, ["valid.cpp"]);
        Assert.Throws<KeyNotFoundException>(() => Generator().Generate(
            new("Missing", "target", [Project("valid", "Valid", [new("target", configuration, valid)], ["missing"])], [], []),
            Context("missing")));
    }

    [Fact]
    public void CompilationDatabaseHandlesEmptyOrderingAndUppercaseCppExtensions()
    {
        var generator = new CompilationDatabaseGenerator();
        var empty = generator.Generate(new("Empty", "target", [], [], []), Context("empty"));
        Assert.Equal("[]\n", Assert.Single(empty.Files).Content);

        var configuration = Configuration(BuildProfiles.Development);
        var later = Compile("z", "src/Z.CPP", "z.obj");
        var earlier = Compile("a", "src/a.cpp", "a.obj");
        var model = new WorkspaceModel("Commands", "target", [], [], [new(configuration, "target", [later, earlier], [])]);
        var result = generator.Generate(model, Context("commands"));
        using var json = JsonDocument.Parse(Assert.Single(result.Files).Content);

        Assert.Equal("src/a.cpp", json.RootElement[0].GetProperty("file").GetString());
        Assert.Equal("a.obj", json.RootElement[0].GetProperty("output").GetString());
        Assert.Equal("cl.exe", json.RootElement[0].GetProperty("arguments")[0].GetString());
        Assert.Equal("src/Z.CPP", json.RootElement[1].GetProperty("file").GetString());
    }

    [Fact]
    public void CompilationDatabaseRejectsMalformedCompileActions()
    {
        var configuration = Configuration(BuildProfiles.Development);
        var noSource = Compile("invalid", "header.h", "out.obj");
        var twoOutputs = Compile("two", "source.cpp", "out.obj") with { Outputs = ["one.obj", "two.obj"] };

        Assert.Throws<InvalidOperationException>(() => GenerateCompilationDatabase(configuration, noSource));
        Assert.Throws<InvalidOperationException>(() => GenerateCompilationDatabase(configuration, twoOutputs));
    }

    [Fact]
    public void GeneratorPluginsAndBuilderExtensionsExposeContracts()
    {
        var registry = new RecordingRegistry();
        var visualStudio = new VisualStudioPlugin();
        var compileDatabase = new CompilationDatabasePlugin();
        visualStudio.Register(registry);
        compileDatabase.Register(registry);

        Assert.Equal("Roxy.Generator.Vs2022", visualStudio.Id.Value);
        Assert.Equal(new Version(0, 1, 0), visualStudio.Version);
        Assert.True(visualStudio.Capabilities.Contains("Workspace.Vs2022"));
        Assert.Equal("Roxy.Generator.CompileDb", compileDatabase.Id.Value);
        Assert.True(compileDatabase.Capabilities.Contains("Workspace.CompileDb"));
        Assert.Contains(registry.Services, service => service is VisualStudio2022Generator);
        Assert.Contains(registry.Services, service => service is CompilationDatabaseGenerator);

        var builder = new RecordingBuilder();
        Assert.Same(builder, builder.UseVisualStudio());
        Assert.Same(builder, builder.UseCompilationDatabase());
        Assert.Collection(builder.Plugins,
            plugin => Assert.IsType<VisualStudioPlugin>(plugin),
            plugin => Assert.IsType<CompilationDatabasePlugin>(plugin));

        Assert.Equal("Vs2022", new VisualStudio2022Generator().Id.Value);
        Assert.True(new VisualStudio2022Generator().Capabilities.Contains("MixedWorkspace"));
        Assert.Equal("CompileDb", new CompilationDatabaseGenerator().Id.Value);
        Assert.True(new CompilationDatabaseGenerator().Capabilities.Contains("ArgumentsArray"));
    }

    private static VisualStudio2022Generator Generator() => new();
    private static GenerationContext Context(string workspace) => new("C:/checkout", new($".roxy/generated/vs2022/{workspace}"));

    private static ConfigurationKey Configuration(FragmentValue profile) => new([
        RoxyBuildTool.Configuration.Platforms.Windows,
        Architectures.X64,
        profile,
        RoxyBuildTool.Configuration.Toolchains.Msvc,
        LinkModels.Modular,
    ]);

    private static ConfiguredModule Module(
        string id,
        string name,
        ModuleKind kind,
        ImmutableArray<string> sources) => new(
        id, name, ModuleLanguage.Cxx, kind, sources.Select(path => new LogicalPath(path)).ToImmutableArray(),
        UsageRequirements.Empty, UsageRequirements.Empty, UsageRequirements.Empty, UsageRequirements.Empty,
        [], [], []);

    private static ConfiguredModule Managed(
        string id,
        string name,
        ModuleKind kind,
        ConfigurationKey configuration,
        ImmutableArray<string> targetFrameworks,
        ImmutableArray<PackageReferenceModel> packages) => new(
        id, name, ModuleLanguage.CSharp, kind, [new("managed/Library.cs")],
        UsageRequirements.Empty, UsageRequirements.Empty, UsageRequirements.Empty, UsageRequirements.Empty,
        [], targetFrameworks, packages);

    private static WorkspaceProject Project(
        string id,
        string name,
        ImmutableArray<WorkspaceProjectVariant> variants,
        ImmutableArray<string> dependencies = default) => new(
        id, name, ModuleLanguage.Cxx, variants, dependencies.IsDefault ? [] : dependencies);

    private static string ConfigurationType(GenerationResult result, string path) =>
        Assert.Single(Parse(result, path).Descendants(MsBuild + "ConfigurationType").Select(element => element.Value).Distinct());

    private static XDocument Parse(GenerationResult result, string path) => XDocument.Parse(File(result, path));
    private static string File(GenerationResult result, string path) =>
        result.Files.Single(file => file.Path.Value == path).Content;

    private static BuildAction Compile(string id, string input, string output) => new(
        id, BuildActionKind.Compile, "cl.exe", ["/c", input], new("."), [input], [output], [], [], true, true, []);

    private static void GenerateCompilationDatabase(ConfigurationKey configuration, BuildAction action) =>
        _ = new CompilationDatabaseGenerator().Generate(
            new("Invalid", "target", [], [], [new(configuration, "target", [action], [])]), Context("invalid"));

    private sealed class RecordingRegistry : IPluginRegistry
    {
        public List<object> Services { get; } = [];
        public void AddService<T>(T service) where T : class => Services.Add(service);
    }

    private sealed class RecordingBuilder : IBuildToolBuilder
    {
        public List<IPlugin> Plugins { get; } = [];
        public void AddPlugin(IPlugin plugin) => Plugins.Add(plugin);
    }
}
