using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Model;

namespace RoxyBuildTool.Generators.VisualStudio;

/// <summary>Generates a mixed C++ and C# Visual Studio workspace.</summary>
public sealed class VisualStudio2022Generator : IWorkspaceGenerator
{
    private const string SolutionPlatformName = "Win64";
    private static readonly XNamespace MsBuild = "http://schemas.microsoft.com/developer/msbuild/2003";
    private static readonly Guid CxxProjectType = new("8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942");
    private static readonly Guid CSharpProjectType = new("FAE04EC0-301F-11D3-BF4B-00C04F79EFBC");

    public WorkspaceGeneratorId Id { get; } = new("Vs2022");

    public CapabilitySet Capabilities { get; } = new([
        "MixedWorkspace",
        "NativeMsbuild",
        "SdkStyleCsharp",
        "ProjectReference",
    ]);

    /// <inheritdoc />
    public GenerationResult Generate(WorkspaceModel workspace, GenerationContext context)
    {
        var files = ImmutableArray.CreateBuilder<GeneratedFile>();
        var projectsById = workspace.Projects.ToDictionary(project => project.Id, StringComparer.Ordinal);
        if (workspace.Projects.Any(project => !project.IsBuildHost && project.Language == ModuleLanguage.CSharp))
        {
            files.Add(new GeneratedFile(new LogicalPath("Directory.Build.props"),
                GenerateDirectoryBuildProps(context)));
        }

        foreach (var project in workspace.Projects.Where(project => !project.IsBuildHost))
        {
            if (project.Language == ModuleLanguage.Cxx)
            {
                files.Add(new GeneratedFile(new LogicalPath($"{ProjectFileStem(project)}.vcxproj"),
                    GenerateVcxproj(workspace, project, projectsById, context)));
                files.Add(new GeneratedFile(new LogicalPath($"{ProjectFileStem(project)}.vcxproj.filters"),
                    GenerateFilters(project, context)));
            }
            else
            {
                files.Add(new GeneratedFile(new LogicalPath($"{ProjectFileStem(project)}.csproj"),
                    GenerateCsproj(project, projectsById, context)));
            }
        }

        files.Add(new GeneratedFile(new LogicalPath($"{workspace.Name}.sln"), GenerateSolution(workspace, context)));
        return new GenerationResult(Id,
            [..files.OrderBy(file => file.Path.Value, StringComparer.Ordinal)], []);
    }

    private static string GenerateSolution(WorkspaceModel workspace, GenerationContext context)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
        builder.AppendLine("# Visual Studio Version 17");
        builder.AppendLine("VisualStudioVersion = 17.0.31903.59");
        builder.AppendLine("MinimumVisualStudioVersion = 10.0.40219.1");

        foreach (var project in workspace.Projects.OrderBy(project => project.Id, StringComparer.Ordinal))
        {
            var typeGuid = project.Language == ModuleLanguage.Cxx ? CxxProjectType : CSharpProjectType;
            var projectGuid = ProjectGuid(project.Id);
            var path = project.IsBuildHost
                ? ToWindows(RelativeFromOutput(context, project.ImportedProject!.Value.Value))
                : $"{ProjectFileStem(project)}.{ProjectExtension(project)}";
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"Project(\"{{{typeGuid.ToString().ToUpperInvariant()}}}\") = \"{ProjectFileStem(project)}\", \"{path}\", \"{{{projectGuid.ToString().ToUpperInvariant()}}}\"");
            var solutionDependencies = SolutionDependencies(project);
            if (!solutionDependencies.IsEmpty)
            {
                builder.AppendLine("\tProjectSection(ProjectDependencies) = postProject");
                foreach (var dependency in solutionDependencies)
                {
                    var dependencyGuid = ProjectGuid(dependency).ToString().ToUpperInvariant();
                    builder.AppendLine(CultureInfo.InvariantCulture,
                        $"\t\t{{{dependencyGuid}}} = {{{dependencyGuid}}}");
                }

                builder.AppendLine("\tEndProjectSection");
            }

            builder.AppendLine("EndProject");
        }

        var configurations = WorkspaceConfigurations(workspace);
        builder.AppendLine("Global");
        builder.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
        foreach (var configuration in configurations)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"\t\t{configuration.Name}|{SolutionPlatformName} = {configuration.Name}|{SolutionPlatformName}");
        }

        builder.AppendLine("\tEndGlobalSection");
        builder.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
        foreach (var project in workspace.Projects.OrderBy(project => project.Id, StringComparer.Ordinal))
        {
            var guid = ProjectGuid(project.Id).ToString().ToUpperInvariant();
            foreach (var configuration in configurations)
            {
                var mapped = project.IsBuildHost
                    ? "Debug"
                    : DisplayName(MapProjectConfiguration(project, configuration.Key));
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"\t\t{{{guid}}}.{configuration.Name}|{SolutionPlatformName}.ActiveCfg = {mapped}|{(project.IsBuildHost ? "Any CPU" : "x64")}");
                if (!project.IsBuildHost && project.Variants.Any(variant =>
                        variant.Configuration.Canonical == configuration.Key.Canonical))
                {
                    builder.AppendLine(CultureInfo.InvariantCulture,
                        $"\t\t{{{guid}}}.{configuration.Name}|{SolutionPlatformName}.Build.0 = {mapped}|x64");
                }
            }
        }

        builder.AppendLine("\tEndGlobalSection");
        builder.AppendLine("EndGlobal");
        return Normalize(builder.ToString());
    }

    private static string GenerateVcxproj(
        WorkspaceModel workspace,
        WorkspaceProject project,
        IReadOnlyDictionary<string, WorkspaceProject> projectsById,
        GenerationContext context)
    {
        var root = new XElement(MsBuild + "Project", new XAttribute("DefaultTargets", "Build"));
        root.Add(new XElement(MsBuild + "ItemGroup",
            new XAttribute("Label", "ProjectConfigurations"),
            project.Variants.Select(variant =>
            {
                var display = DisplayName(variant);
                return new XElement(MsBuild + "ProjectConfiguration",
                    new XAttribute("Include", $"{display}|x64"),
                    new XElement(MsBuild + "Configuration", display),
                    new XElement(MsBuild + "Platform", "x64"));
            })));
        root.Add(new XElement(MsBuild + "PropertyGroup",
            new XAttribute("Label", "Globals"),
            new XElement(MsBuild + "ProjectGuid", $"{{{ProjectGuid(project.Id).ToString().ToUpperInvariant()}}}"),
            new XElement(MsBuild + "RootNamespace", ProjectFileStem(project)),
            new XElement(MsBuild + "WindowsTargetPlatformVersion", "10.0"),
            new XElement(MsBuild + "RoxyWorkspaceRoot", ToWindows(RelativeFromOutput(context, ".")))));
        root.Add(new XElement(MsBuild + "Import",
            new XAttribute("Project", "$(VCTargetsPath)\\Microsoft.Cpp.Default.props")));

        foreach (var variant in project.Variants)
        {
            var profile = Fragment(variant.Configuration, "Profile");
            var toolchain = ToolchainSettings(workspace, variant);
            root.Add(new XElement(MsBuild + "PropertyGroup",
                new XAttribute("Condition", Condition(variant)),
                new XAttribute("Label", "Configuration"),
                new XElement(MsBuild + "ConfigurationType", ConfigurationType(variant.Module.Kind)),
                new XElement(MsBuild + "UseDebugLibraries", profile == "Debug" ? "true" : "false"),
                new XElement(MsBuild + "PlatformToolset", toolchain.VisualStudioPlatformToolset),
                new XElement(MsBuild + "CharacterSet", "Unicode")));
        }

        root.Add(new XElement(MsBuild + "Import", new XAttribute("Project", "$(VCTargetsPath)\\Microsoft.Cpp.props")));

        foreach (var variant in project.Variants)
        {
            var profile = Fragment(variant.Configuration, "Profile");
            var toolchain = ToolchainSettings(workspace, variant);
            var outputRoot =
                $@"$(RoxyWorkspaceRoot)\{ToWindows(BuildPathLayout.OutputRoot(variant.Configuration, variant.Target))}\";
            var intermediate =
                $@"$(RoxyWorkspaceRoot)\{ToWindows(BuildPathLayout.IntermediateRoot(variant.Configuration, variant.Target))}\{variant.Module.Id}\";
            root.Add(new XElement(MsBuild + "PropertyGroup",
                new XAttribute("Condition", Condition(variant)),
                new XElement(MsBuild + "OutDir", outputRoot),
                new XElement(MsBuild + "IntDir", intermediate),
                new XElement(MsBuild + "TargetName", variant.Module.Id)));

            var compile = new XElement(MsBuild + "ClCompile",
                new XElement(MsBuild + "LanguageStandard", "stdcpplatest"),
                new XElement(MsBuild + "Optimization", profile == "Debug" ? "Disabled" : "MaxSpeed"),
                new XElement(MsBuild + "BasicRuntimeChecks", profile == "Debug" ? "EnableFastChecks" : "Default"),
                new XElement(MsBuild + "RuntimeLibrary",
                    profile == "Debug" ? "MultiThreadedDebugDLL" : "MultiThreadedDLL"),
                new XElement(MsBuild + "AdditionalIncludeDirectories", JoinMsbuild(
                    variant.Module.CompileUsage.IncludeDirectories.Select(value =>
                        $"$(RoxyWorkspaceRoot)\\{ToWindows(value.Value)}"),
                    "AdditionalIncludeDirectories")),
                new XElement(MsBuild + "PreprocessorDefinitions", JoinMsbuild(
                    variant.Module.CompileUsage.Defines.Select(value => value.Value),
                    "PreprocessorDefinitions")),
                new XElement(MsBuild + "AdditionalOptions",
                    JoinCommandLine(toolchain.CompileArguments, "%(AdditionalOptions)")));
            var definitions = new XElement(MsBuild + "ItemDefinitionGroup",
                new XAttribute("Condition", Condition(variant)),
                compile);
            if (variant.Module.Sources.Any(source => BuildFileKinds.IsResource(source.Value)))
            {
                definitions.Add(new XElement(MsBuild + "ResourceCompile",
                    new XElement(MsBuild + "AdditionalIncludeDirectories", JoinMsbuild(
                        variant.Module.CompileUsage.IncludeDirectories.Select(value =>
                            $"$(RoxyWorkspaceRoot)\\{ToWindows(value.Value)}"),
                        "AdditionalIncludeDirectories")),
                    new XElement(MsBuild + "PreprocessorDefinitions", JoinMsbuild(
                        toolchain.CompileArguments
                            .Where(argument => argument.StartsWith("/D", StringComparison.OrdinalIgnoreCase))
                            .Select(argument => argument[2..])
                            .Concat(variant.Module.CompileUsage.Defines.Select(value => value.Value)),
                        "PreprocessorDefinitions"))));
            }

            if (variant.Module.Kind is ModuleKind.Executable or ModuleKind.SharedLibrary)
            {
                definitions.Add(new XElement(MsBuild + "Link",
                    new XElement(MsBuild + "AdditionalDependencies", JoinMsbuild(
                        variant.Module.CompileUsage.LinkInputs.Select(value => FormatLinkInput(value.Value)),
                        "AdditionalDependencies")),
                    new XElement(MsBuild + "GenerateDebugInformation", "true"),
                    new XElement(MsBuild + "AdditionalOptions",
                        JoinCommandLine(toolchain.LinkArguments, "%(AdditionalOptions)"))));
            }

            root.Add(definitions);
        }

        foreach (var itemGroup in CreateNativeSourceItemGroups(project, context))
        {
            root.Add(itemGroup);
        }

        var references = CreateProjectReferences(project, projectsById, MsBuild);
        if (references is not null)
        {
            root.Add(references);
        }

        root.Add(new XElement(MsBuild + "Import",
            new XAttribute("Project", "$(VCTargetsPath)\\Microsoft.Cpp.targets")));
        return Serialize(new XDocument(new XDeclaration("1.0", "utf-8", null), root));
    }

    private static string GenerateCsproj(
        WorkspaceProject project,
        IReadOnlyDictionary<string, WorkspaceProject> projectsById,
        GenerationContext context)
    {
        var representative = project.Variants[0].Module;
        var targetFrameworks = TargetFrameworks(representative);
        var commonTargetFrameworks = project.Variants.All(variant =>
            TargetFrameworks(variant.Module).SequenceEqual(targetFrameworks, StringComparer.Ordinal));
        var outputType = ManagedOutputType(representative);
        var commonOutputType = project.Variants.All(variant => ManagedOutputType(variant.Module) == outputType);
        var rootNamespace = ManagedRootNamespace(project, representative);
        var commonRootNamespace = project.Variants.All(variant =>
            ManagedRootNamespace(project, variant.Module) == rootNamespace);
        var properties = new XElement("PropertyGroup",
            new XElement("LangVersion", "14.0"),
            new XElement("Nullable", "enable"),
            new XElement("ImplicitUsings", "enable"),
            new XElement("Deterministic", "true"),
            new XElement("ManagePackageVersionsCentrally", "false"),
            new XElement("RestorePackagesWithLockFile", "true"),
            new XElement("NuGetLockFilePath", "$(BaseIntermediateOutputPath)packages.lock.json"),
            new XElement("EnableDefaultItems", "false"),
            new XElement("Platforms", "x64"),
            new XElement("AssemblyName", representative.Id),
            new XElement("RoxyWorkspaceRoot", ToWindows(RelativeFromOutput(context, "."))));
        if (commonOutputType)
            properties.Add(new XElement("OutputType", outputType));
        if (commonRootNamespace)
            properties.Add(new XElement("RootNamespace", rootNamespace));
        if (commonTargetFrameworks)
            AddTargetFrameworks(properties, targetFrameworks);

        var root = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"), properties);

        foreach (var variant in project.Variants)
        {
            var profile = Fragment(variant.Configuration, "Profile");
            var variantProperties = new XElement("PropertyGroup",
                new XAttribute("Condition", Condition(variant)),
                new XElement("Optimize", profile == "Debug" ? "false" : "true"),
                new XElement("OutputPath",
                    $@"$(RoxyWorkspaceRoot)\{ToWindows(BuildPathLayout.OutputRoot(variant.Configuration, variant.Target))}\{variant.Module.Id}\"),
                new XElement("IntermediateOutputPath",
                    $@"$(RoxyWorkspaceRoot)\{ToWindows(BuildPathLayout.IntermediateRoot(variant.Configuration, variant.Target))}\{variant.Module.Id}\"));
            if (!commonTargetFrameworks)
                AddTargetFrameworks(variantProperties, TargetFrameworks(variant.Module));
            if (!commonOutputType)
                variantProperties.Add(new XElement("OutputType", ManagedOutputType(variant.Module)));
            if (!commonRootNamespace)
                variantProperties.Add(new XElement("RootNamespace", ManagedRootNamespace(project, variant.Module)));

            root.Add(variantProperties);
            if (!variant.Module.CompileUsage.RuntimeFiles.IsEmpty)
            {
                root.Add(new XElement("ItemGroup",
                    new XAttribute("Condition", Condition(variant)),
                    variant.Module.CompileUsage.RuntimeFiles.Select(runtime =>
                        new XElement("Content",
                            new XAttribute("Include", $"$(RoxyWorkspaceRoot)\\{ToWindows(runtime.Value)}"),
                            new XElement("Link", Path.GetFileName(runtime.Value)),
                            new XElement("Visible", "false"),
                            new XElement("CopyToOutputDirectory", "PreserveNewest")))));
            }
        }

        root.Add(new XElement("ItemGroup", SourceVariants(project).Select(item =>
        {
            var element = new XElement("Compile",
                new XAttribute("Include", ToWindows(RelativeFromOutput(context, item.Source.Value))));
            AddVariantCondition(element, project, item.Variants);
            return element;
        })));
        var references = CreateProjectReferences(project, projectsById, XNamespace.None);
        if (references is not null)
        {
            root.Add(references);
        }

        var packages = project.Variants
            .SelectMany(variant => variant.Module.Packages.Select(package => (Variant: variant, Package: package)))
            .GroupBy(item => item.Package)
            .OrderBy(group => group.Key.Id, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Version, StringComparer.Ordinal)
            .ToImmutableArray();
        if (!packages.IsEmpty)
        {
            root.Add(new XElement("ItemGroup", packages.Select(group =>
            {
                var package = group.Key;
                var element = new XElement("PackageReference",
                    new XAttribute("Include", package.Id),
                    new XAttribute("Version", package.Version));
                if (package.PrivateAssets)
                {
                    element.Add(new XAttribute("PrivateAssets", "all"));
                }

                AddVariantCondition(element, project,
                    [.. group.Select(item => item.Variant).Distinct()]);
                return element;
            })));
        }

        return Serialize(new XDocument(root));
    }

    private static string GenerateFilters(WorkspaceProject project, GenerationContext context)
    {
        var root = new XElement(MsBuild + "Project", new XAttribute("ToolsVersion", "4.0"));
        foreach (var group in Sources(project).GroupBy(NativeItemName, StringComparer.Ordinal))
        {
            root.Add(new XElement(MsBuild + "ItemGroup", group.Select(source =>
                new XElement(MsBuild + group.Key,
                    new XAttribute("Include", ToWindows(RelativeFromOutput(context, source.Value)))))));
        }

        return Serialize(new XDocument(new XDeclaration("1.0", "utf-8", null), root));
    }

    private static XElement? CreateProjectReferences(
        WorkspaceProject project,
        IReadOnlyDictionary<string, WorkspaceProject> projectsById,
        XNamespace xmlNamespace)
    {
        var dependencies = project.ProjectDependencies
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        if (dependencies.IsEmpty)
        {
            return null;
        }

        return new XElement(xmlNamespace + "ItemGroup",
            dependencies.Select(dependency =>
            {
                var dependencyProject = projectsById[dependency];
                var reference = new XElement(xmlNamespace + "ProjectReference",
                    new XAttribute("Include",
                        $"{ProjectFileStem(dependencyProject)}.{ProjectExtension(dependencyProject)}"),
                    new XElement(xmlNamespace + "Project",
                        $"{{{ProjectGuid(dependency).ToString().ToUpperInvariant()}}}"));
                AddVariantCondition(reference, project, ReferenceVariants(project, dependency));
                if (dependencyProject.Language != project.Language)
                {
                    reference.Add(
                        new XElement(xmlNamespace + "ReferenceOutputAssembly", "false"),
                        new XElement(xmlNamespace + "SkipGetTargetFrameworkProperties", "true"));
                }

                if (project.Language == ModuleLanguage.Cxx && dependencyProject.Language == ModuleLanguage.Cxx)
                {
                    // Native link inputs are explicit usage requirements. Project references provide ordering only;
                    // implicit library linking would duplicate normal inputs and incorrectly link build-order edges.
                    reference.Add(new XElement(xmlNamespace + "LinkLibraryDependencies", "false"));
                }

                return reference;
            }));
    }

    private static string GenerateDirectoryBuildProps(GenerationContext context)
    {
        var root = new XElement("Project",
            new XElement("PropertyGroup",
                new XAttribute("Condition", "'$(MSBuildProjectExtension)'=='.csproj'"),
                new XElement("RoxyWorkspaceRoot", ToWindows(RelativeFromOutput(context, "."))),
                new XElement("BaseIntermediateOutputPath",
                    @"$(RoxyWorkspaceRoot)\intermediate\msbuild\$(MSBuildProjectName)\$(Configuration)\")));
        return Serialize(new XDocument(root));
    }

    private static ImmutableArray<LogicalPath> Sources(WorkspaceProject project)
    {
        return
        [
            ..project.Variants
                .SelectMany(variant => variant.Module.Sources)
                .Distinct()
                .OrderBy(source => source.Value, StringComparer.Ordinal)
        ];
    }

    private static ImmutableArray<SourceVariantGroup> SourceVariants(WorkspaceProject project) =>
    [
        .. project.Variants
            .SelectMany(variant => variant.Module.Sources.Select(source => (Variant: variant, Source: source)))
            .GroupBy(item => item.Source)
            .Select(group => new SourceVariantGroup(
                group.Key,
                [
                    .. group.Select(item => item.Variant).Distinct()
                        .OrderBy(variant => variant.Configuration)
                ]))
            .OrderBy(group => group.Source.Value, StringComparer.Ordinal)
    ];

    private static IEnumerable<XElement> CreateNativeSourceItemGroups(
        WorkspaceProject project,
        GenerationContext context) => SourceVariants(project)
        .GroupBy(group => NativeItemName(group.Source), StringComparer.Ordinal)
        .Select(group => new XElement(MsBuild + "ItemGroup", group.Select(item =>
        {
            var element = new XElement(MsBuild + group.Key,
                new XAttribute("Include", ToWindows(RelativeFromOutput(context, item.Source.Value))));
            AddVariantCondition(element, project, item.Variants);
            if (group.Key is "ClCompile" or "ResourceCompile")
            {
                foreach (var variant in item.Variants.Where(variant =>
                             variant.Module.Kind == ModuleKind.HeaderOnly))
                {
                    element.Add(new XElement(MsBuild + "ExcludedFromBuild",
                        new XAttribute("Condition", Condition(variant)), "true"));
                }
            }

            if (group.Key == "ClCompile")
            {
                foreach (var variant in item.Variants)
                {
                    element.Add(new XElement(MsBuild + "ObjectFileName",
                        new XAttribute("Condition", Condition(variant)),
                        $@"$(RoxyWorkspaceRoot)\{ToWindows(BuildPathLayout.ObjectFile(
                            variant.Configuration, variant.Target, variant.Module.Id, item.Source))}"));
                }
            }
            else if (group.Key == "ResourceCompile")
            {
                foreach (var variant in item.Variants)
                {
                    element.Add(new XElement(MsBuild + "ResourceOutputFileName",
                        new XAttribute("Condition", Condition(variant)),
                        $@"$(RoxyWorkspaceRoot)\{ToWindows(BuildPathLayout.ResourceFile(
                            variant.Configuration, variant.Target, variant.Module.Id, item.Source))}"));
                }
            }

            return element;
        })));

    private static string NativeItemName(LogicalPath source) => source.Value switch
    {
        var value when BuildFileKinds.IsCxxSource(value) => "ClCompile",
        var value when BuildFileKinds.IsHeader(value) => "ClInclude",
        var value when BuildFileKinds.IsResource(value) => "ResourceCompile",
        _ => "None",
    };

    private static ImmutableArray<WorkspaceProjectVariant> ReferenceVariants(
        WorkspaceProject project,
        string dependency)
    {
        if (project.DependencyVariants.IsDefaultOrEmpty)
            return project.Variants;

        var identities = project.DependencyVariants
            .Where(item => item.ProjectId == dependency)
            .Select(item => (item.Target, item.Configuration))
            .ToHashSet();
        return
        [
            .. project.Variants.Where(variant => identities.Contains((variant.Target, variant.Configuration)))
        ];
    }

    private static ImmutableArray<string> SolutionDependencies(WorkspaceProject project)
    {
        if (project.DependencyVariants.IsDefaultOrEmpty)
            return [.. project.ProjectDependencies.Order(StringComparer.Ordinal)];

        return
        [
            .. project.ProjectDependencies
                .Where(dependency => ReferenceVariants(project, dependency).Length == project.Variants.Length)
                .Order(StringComparer.Ordinal)
        ];
    }

    private static void AddVariantCondition(
        XElement element,
        WorkspaceProject project,
        ImmutableArray<WorkspaceProjectVariant> variants)
    {
        if (variants.Length == project.Variants.Length)
            return;

        element.Add(new XAttribute("Condition", string.Join(" Or ", variants
            .OrderBy(variant => variant.Configuration)
            .Select(Condition))));
    }

    private static ImmutableArray<SolutionConfiguration> WorkspaceConfigurations(WorkspaceModel workspace)
    {
        return
        [
            ..workspace
                .Projects
                .SelectMany(project => project.Variants)
                .GroupBy(variant => variant.Configuration.Canonical, StringComparer.Ordinal)
                .Select(group => new SolutionConfiguration(
                    DisplayName(group.First()),
                    group.OrderBy(variant => variant.Configuration)
                        .ThenBy(variant => variant.Target, StringComparer.Ordinal)
                        .First().Configuration))
                .OrderBy(configuration => configuration.Name, StringComparer.Ordinal)
        ];
    }

    private static WorkspaceProjectVariant MapProjectConfiguration(
        WorkspaceProject project,
        ConfigurationKey solutionConfiguration) => project.Variants
        .OrderByDescending(variant => SameFragment(variant.Configuration, solutionConfiguration, "Platform"))
        .ThenByDescending(variant => SameFragment(variant.Configuration, solutionConfiguration, "Architecture"))
        .ThenByDescending(variant => SameFragment(variant.Configuration, solutionConfiguration, "Profile"))
        .ThenByDescending(variant => SameFragment(variant.Configuration, solutionConfiguration, "Toolchain"))
        .ThenByDescending(variant => SameFragment(variant.Configuration, solutionConfiguration, "LinkModel"))
        .ThenByDescending(variant => SharedFragmentCount(variant.Configuration, solutionConfiguration))
        .ThenBy(variant => variant.Configuration)
        .ThenBy(variant => variant.Target, StringComparer.Ordinal)
        .First();

    private static bool SameFragment(ConfigurationKey left, ConfigurationKey right, string fragment)
    {
        var id = new FragmentId(fragment);
        return left.TryGet(id, out var leftValue) && right.TryGet(id, out var rightValue) && leftValue == rightValue;
    }

    private static int SharedFragmentCount(ConfigurationKey left, ConfigurationKey right) =>
        left.Values.Count(value => right.Is(value));

    private static string DisplayName(WorkspaceProjectVariant variant) =>
        BuildConfigurationNames.DisplayName(variant.Configuration);

    private static string Condition(WorkspaceProjectVariant variant) =>
        $"'$(Configuration)|$(Platform)'=='{DisplayName(variant)}|x64'";

    private static string Fragment(ConfigurationKey key, string fragment) =>
        key.Values.Single(value => value.Fragment.Value == fragment).Value;

    private static string ConfigurationType(ModuleKind kind) => kind switch
    {
        ModuleKind.HeaderOnly => "Utility",
        ModuleKind.ObjectLibrary => "StaticLibrary",
        ModuleKind.StaticLibrary => "StaticLibrary",
        ModuleKind.SharedLibrary => "DynamicLibrary",
        ModuleKind.Executable => "Application",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static string ProjectExtension(WorkspaceProject project) =>
        project.Language == ModuleLanguage.Cxx ? "vcxproj" : "csproj";

    private static string ProjectFileStem(WorkspaceProject project)
    {
        var name = project.Name.EndsWith("Module", StringComparison.Ordinal)
            ? project.Name[..^"Module".Length]
            : project.Name;
        var sanitized = new string(name.Where(character => char.IsLetterOrDigit(character) || character is '_' or '.')
            .ToArray());
        return sanitized.Length == 0 ? ToPascalCase(project.Id) : sanitized;
    }

    private static string ToPascalCase(string value) => string.Concat(value
        .Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries)
        .Select(segment => char.ToUpperInvariant(segment[0]) + segment[1..]));

    private static Guid ProjectGuid(string id) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"roxy:{id}"))[..16]);

    private static ToolchainBuildSettings ToolchainSettings(
        WorkspaceModel workspace,
        WorkspaceProjectVariant variant) => workspace.ActionGraphs
        .SingleOrDefault(graph => graph.Target == variant.Target && graph.Configuration == variant.Configuration)
        ?.Toolchain ?? new ToolchainBuildSettings("Unknown", "v143", [], []);

    private static string JoinMsbuild(IEnumerable<string> values, string inheritedMetadata) =>
        string.Join(';', values.Append($"%({inheritedMetadata})"));

    private static string JoinCommandLine(IEnumerable<string> values, string inheritedValue) =>
        string.Join(' ', values.Append(inheritedValue));

    private static ImmutableArray<string> TargetFrameworks(ConfiguredModule module) => module.TargetFrameworks
        .DefaultIfEmpty("net10.0")
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToImmutableArray();

    private static void AddTargetFrameworks(XElement propertyGroup, ImmutableArray<string> frameworks) =>
        propertyGroup.Add(frameworks.Length == 1
            ? new XElement("TargetFramework", frameworks[0])
            : new XElement("TargetFrameworks", string.Join(';', frameworks)));

    private static string ManagedOutputType(ConfiguredModule module) =>
        module.Kind == ModuleKind.CSharpConsoleApplication ? "Exe" : "Library";

    private static string ManagedRootNamespace(WorkspaceProject project, ConfiguredModule module) =>
        module.RootNamespace ?? project.Name.Replace(' ', '.');

    private static string FormatLinkInput(string value) => Path.IsPathRooted(value) ||
                                                           !value.Contains('/') && !value.Contains('\\')
        ? value
        : $"$(RoxyWorkspaceRoot)\\{ToWindows(value)}";

    private static string RelativeFromOutput(GenerationContext context, string logicalPath) =>
        Path.GetRelativePath(context.OutputDirectory.Value, logicalPath).Replace('\\', '/');

    private static string ToWindows(string path) => path.Replace('/', '\\');
    private static string Serialize(XDocument document) => Normalize(document.ToString(SaveOptions.None)) + "\n";
    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private sealed record SolutionConfiguration(string Name, ConfigurationKey Key);

    private sealed record SourceVariantGroup(
        LogicalPath Source,
        ImmutableArray<WorkspaceProjectVariant> Variants);
}

/// <summary>Registers the Visual Studio workspace generator.</summary>
public sealed class VisualStudioPlugin : IPlugin
{
    public PluginId Id { get; } = new("Roxy.Generator.Vs2022");
    public Version Version { get; } = new(0, 1, 0);
    public CapabilitySet Capabilities { get; } = new(["Workspace.Vs2022", "MixedWorkspace"]);

    public void Register(IPluginRegistry registry)
    {
        registry.AddService<IWorkspaceGenerator>(new VisualStudio2022Generator());
    }
}

/// <summary>Provides Visual Studio generator composition extensions.</summary>
public static class VisualStudioExtensions
{
    /// <summary>Adds the Visual Studio plugin and returns the same builder.</summary>
    public static T UseVisualStudio<T>(this T builder) where T : IBuildToolBuilder
    {
        builder.AddPlugin(new VisualStudioPlugin());
        return builder;
    }
}