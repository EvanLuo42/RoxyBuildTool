using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Model;

namespace RoxyBuildTool.Generators.VisualStudio;

/// <summary>Generates a native C++ Visual Studio workspace.</summary>
public sealed class VisualStudio2022Generator : IWorkspaceGenerator, IWorkspaceGeneratorFingerprintProvider
{
    private const string SolutionPlatformName = "Win64";
    private const string MsBuild = "http://schemas.microsoft.com/developer/msbuild/2003";
    private static readonly Guid CxxProjectType = new("8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942");

    private static readonly ToolchainBuildSettings UnknownToolchain = new("Unknown", "v143", [], []);

    private static readonly XmlWriterSettings ProjectWriterSettings = new()
    {
        OmitXmlDeclaration = true,
        Indent = true,
        IndentChars = "  ",
        NewLineChars = "\n",
        NewLineHandling = NewLineHandling.Replace,
        NamespaceHandling = NamespaceHandling.OmitDuplicates,
    };

    public WorkspaceGeneratorId Id { get; } = new("Vs2022");

    /// <inheritdoc />
    public string GetAdditionalFingerprint(WorkspaceModel workspace, GenerationContext context)
    {
        return string.Empty;
    }

    public CapabilitySet Capabilities { get; } = new([
        "NativeMsbuild",
        "ProjectReference",
    ]);

    /// <inheritdoc />
    public GenerationResult Generate(WorkspaceModel workspace, GenerationContext context)
    {
        var files = ImmutableArray.CreateBuilder<GeneratedFile>();
        var projectsById = workspace.Projects.ToDictionary(project => project.Id, StringComparer.Ordinal);
        var configurationNames = ConfigurationNames(workspace);
        var toolchains = workspace.ActionGraphs.ToDictionary(
            graph => (graph.Target, graph.Configuration.Canonical),
            graph => graph.Toolchain ?? UnknownToolchain);
        foreach (var project in workspace.Projects)
        {
            files.Add(new GeneratedFile(new LogicalPath($"{ProjectFileStem(project)}.vcxproj"),
                GenerateVcxproj(project, projectsById, toolchains, configurationNames, context)));
            files.Add(new GeneratedFile(new LogicalPath($"{ProjectFileStem(project)}.vcxproj.filters"),
                GenerateFilters(project, context)));
        }

        files.Add(new GeneratedFile(
            new LogicalPath($"{workspace.Name}.sln"),
            GenerateSolution(workspace, configurationNames)));
        return new GenerationResult(Id,
            [.. files.OrderBy(file => file.Path.Value, StringComparer.Ordinal)], []);
    }

    private static string GenerateSolution(
        WorkspaceModel workspace,
        IReadOnlyDictionary<(string Target, string Configuration), string> configurationNames)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
        builder.AppendLine("# Visual Studio Version 17");
        builder.AppendLine("VisualStudioVersion = 17.0.31903.59");
        builder.AppendLine("MinimumVisualStudioVersion = 10.0.40219.1");
        var projectIds = workspace.Projects.Select(project => project.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var project in workspace.Projects.OrderBy(project => project.Id, StringComparer.Ordinal))
        {
            var projectGuid = ProjectGuid(project.Id);
            var path = $"{ProjectFileStem(project)}.vcxproj";
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"Project(\"{{{CxxProjectType.ToString().ToUpperInvariant()}}}\") = \"{ProjectFileStem(project)}\", \"{path}\", \"{{{projectGuid.ToString().ToUpperInvariant()}}}\"");
            var solutionDependencies = SolutionDependencies(project, projectIds);
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

        var configurations = WorkspaceConfigurations(workspace, configurationNames);
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
                var mapped = DisplayName(
                    MapProjectConfiguration(project, configuration.Target, configuration.Key),
                    configurationNames);
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"\t\t{{{guid}}}.{configuration.Name}|{SolutionPlatformName}.ActiveCfg = {mapped}|x64");
                if (project.Variants.Any(variant =>
                        variant.Target == configuration.Target &&
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
        WorkspaceProject project,
        IReadOnlyDictionary<string, WorkspaceProject> projectsById,
        IReadOnlyDictionary<(string Target, string Configuration), ToolchainBuildSettings> toolchains,
        IReadOnlyDictionary<(string Target, string Configuration), string> configurationNames,
        GenerationContext context)
    {
        return WriteXml(writer =>
        {
            StartMsBuild(writer, "Project");
            writer.WriteAttributeString("DefaultTargets", "Build");

            StartMsBuild(writer, "ItemGroup");
            writer.WriteAttributeString("Label", "ProjectConfigurations");
            foreach (var variant in project.Variants)
            {
                var display = DisplayName(variant, configurationNames);
                StartMsBuild(writer, "ProjectConfiguration");
                writer.WriteAttributeString("Include", $"{display}|x64");
                WriteMsBuild(writer, "Configuration", display);
                WriteMsBuild(writer, "Platform", "x64");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();

            StartMsBuild(writer, "PropertyGroup");
            writer.WriteAttributeString("Label", "Globals");
            WriteMsBuild(writer, "ProjectGuid", $"{{{ProjectGuid(project.Id).ToString().ToUpperInvariant()}}}");
            WriteMsBuild(writer, "RootNamespace", ProjectFileStem(project));
            WriteMsBuild(writer, "WindowsTargetPlatformVersion", "10.0");
            WriteMsBuild(writer, "RoxyWorkspaceRoot", ToWindows(RelativeFromOutput(context, ".")));
            writer.WriteEndElement();
            WriteImport(writer, "$(VCTargetsPath)\\Microsoft.Cpp.Default.props");

            foreach (var variant in project.Variants)
            {
                var toolchain = ToolchainSettings(toolchains, variant);
                var debugRuntime = UsesDebugRuntime(toolchain, variant.Configuration);
                StartMsBuild(writer, "PropertyGroup");
                writer.WriteAttributeString("Condition", Condition(variant, configurationNames));
                writer.WriteAttributeString("Label", "Configuration");
                WriteMsBuild(writer, "ConfigurationType", ConfigurationType(variant.Module.Kind));
                WriteMsBuild(writer, "UseDebugLibraries", debugRuntime ? "true" : "false");
                WriteMsBuild(writer, "PlatformToolset", toolchain.VisualStudioPlatformToolset);
                WriteMsBuild(writer, "CharacterSet", "Unicode");
                writer.WriteEndElement();
            }

            WriteImport(writer, "$(VCTargetsPath)\\Microsoft.Cpp.props");
            foreach (var variant in project.Variants)
                WriteNativeVariant(writer, variant, ToolchainSettings(toolchains, variant), configurationNames);

            WriteNativeSourceItemGroups(writer, project, configurationNames, context);
            WriteProjectReferences(writer, project, projectsById, configurationNames, MsBuild);
            WriteImport(writer, "$(VCTargetsPath)\\Microsoft.Cpp.targets");
            writer.WriteEndElement();
        });

        static void WriteNativeVariant(
            XmlWriter writer,
            WorkspaceProjectVariant variant,
            ToolchainBuildSettings toolchain,
            IReadOnlyDictionary<(string Target, string Configuration), string> configurationNames)
        {
            var native = variant.Module.CxxSettings ?? CxxModuleSettings.Empty;
            var debugRuntime = UsesDebugRuntime(toolchain, variant.Configuration);
            var outputRoot =
                $@"$(RoxyWorkspaceRoot)\{ToWindows(BuildPathLayout.OutputRoot(variant.Configuration, variant.Target))}\";
            var intermediate =
                $@"$(RoxyWorkspaceRoot)\{ToWindows(BuildPathLayout.IntermediateRoot(
                    variant.Configuration, variant.Target, variant.Module.Id))}\";

            StartMsBuild(writer, "PropertyGroup");
            writer.WriteAttributeString("Condition", Condition(variant, configurationNames));
            WriteMsBuild(writer, "OutDir", outputRoot);
            WriteMsBuild(writer, "IntDir", intermediate);
            WriteMsBuild(writer, "TargetName", native.OutputName ?? variant.Module.Id);
            writer.WriteEndElement();

            StartMsBuild(writer, "ItemDefinitionGroup");
            writer.WriteAttributeString("Condition", Condition(variant, configurationNames));
            StartMsBuild(writer, "ClCompile");
            WriteMsBuild(writer, "LanguageStandard", "stdcpplatest");
            WriteMsBuild(writer, "Optimization",
                UsesDisabledOptimization(toolchain, variant.Configuration) ? "Disabled" : "MaxSpeed");
            WriteMsBuild(writer, "BasicRuntimeChecks", debugRuntime ? "EnableFastChecks" : "Default");
            WriteMsBuild(writer, "RuntimeLibrary", debugRuntime ? "MultiThreadedDebugDLL" : "MultiThreadedDLL");
            WriteMsBuild(writer, "AdditionalIncludeDirectories", JoinMsbuild(
                variant.Module.CompileUsage.IncludeDirectories.Select(value => FormatIncludePath(value.Value)),
                "AdditionalIncludeDirectories"));
            WriteMsBuild(writer, "PreprocessorDefinitions", JoinMsbuild(
                variant.Module.CompileUsage.Defines.Select(value => value.Value), "PreprocessorDefinitions"));
            WriteMsBuild(writer, "AdditionalOptions", JoinCommandLine(
                toolchain.CompileArguments.Concat(native.CompilerArguments), "%(AdditionalOptions)"));
            var systemIncludes = SystemIncludes(variant.Module);
            if (!systemIncludes.IsEmpty)
                WriteMsBuild(writer, "ExternalIncludePath", JoinMsbuild(
                    systemIncludes.Select(value => FormatIncludePath(value.Value)), "ExternalIncludePath"));
            if (!native.ForcedIncludes.IsEmpty)
                WriteMsBuild(writer, "ForcedIncludeFiles", JoinMsbuild(
                    native.ForcedIncludes.Select(value => FormatIncludePath(value.Value)), "ForcedIncludeFiles"));
            writer.WriteEndElement();

            if (variant.Module.Sources.Any(source => BuildFileKinds.IsResource(source.Value)))
            {
                StartMsBuild(writer, "ResourceCompile");
                WriteMsBuild(writer, "AdditionalIncludeDirectories", JoinMsbuild(
                    variant.Module.CompileUsage.IncludeDirectories.Select(value => FormatIncludePath(value.Value)),
                    "AdditionalIncludeDirectories"));
                WriteMsBuild(writer, "PreprocessorDefinitions", JoinMsbuild(
                    toolchain.CompileArguments
                        .Where(argument => argument.StartsWith("/D", StringComparison.OrdinalIgnoreCase))
                        .Select(argument => argument[2..])
                        .Concat(variant.Module.CompileUsage.Defines.Select(value => value.Value)),
                    "PreprocessorDefinitions"));
                writer.WriteEndElement();
            }

            if (variant.Module.Kind is ModuleKind.Executable or ModuleKind.SharedLibrary)
            {
                StartMsBuild(writer, "Link");
                WriteMsBuild(writer, "AdditionalDependencies", JoinMsbuild(
                    variant.Module.CompileUsage.LinkInputs.Select(value => FormatLinkInput(value.Value)),
                    "AdditionalDependencies"));
                WriteMsBuild(writer, "GenerateDebugInformation", "true");
                WriteMsBuild(writer, "AdditionalOptions", JoinCommandLine(
                    toolchain.LinkArguments.Concat(native.LinkerArguments), "%(AdditionalOptions)"));
                writer.WriteEndElement();
            }
            else if (variant.Module.Kind == ModuleKind.StaticLibrary && !native.LibrarianArguments.IsEmpty)
            {
                StartMsBuild(writer, "Lib");
                WriteMsBuild(writer, "AdditionalOptions",
                    JoinCommandLine(native.LibrarianArguments, "%(AdditionalOptions)"));
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }
    }

    private static string GenerateFilters(WorkspaceProject project, GenerationContext context)
    {
        return WriteXml(writer =>
        {
            StartMsBuild(writer, "Project");
            writer.WriteAttributeString("ToolsVersion", "4.0");
            var sources = Sources(project);
            var filterRoot = CommonSourceRoot(sources);
            var sourceFilters = sources.ToDictionary(
                source => source.Value,
                source => SourceFilter(source, filterRoot),
                LogicalPath.FileSystemComparer);
            var filters = sourceFilters.Values
                .Where(filter => filter is not null)
                .SelectMany(FilterHierarchy)
                .Distinct(LogicalPath.FileSystemComparer)
                .Order(StringComparer.Ordinal)
                .ToImmutableArray();
            if (!filters.IsEmpty)
            {
                StartMsBuild(writer, "ItemGroup");
                foreach (var filter in filters)
                {
                    StartMsBuild(writer, "Filter");
                    writer.WriteAttributeString("Include", ToWindows(filter));
                    WriteMsBuild(writer, "UniqueIdentifier",
                        $"{{{ProjectGuid($"filter:{project.Id}:{filter}").ToString().ToUpperInvariant()}}}");
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            foreach (var group in sources.GroupBy(NativeItemName, StringComparer.Ordinal))
            {
                StartMsBuild(writer, "ItemGroup");
                foreach (var source in group)
                {
                    StartMsBuild(writer, group.Key);
                    writer.WriteAttributeString("Include", ToWindows(RelativeFromOutput(context, source.Value)));
                    if (sourceFilters[source.Value] is { } filter)
                        WriteMsBuild(writer, "Filter", ToWindows(filter));
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        });
    }

    private static string CommonSourceRoot(ImmutableArray<LogicalPath> sources)
    {
        if (sources.IsEmpty) return string.Empty;
        var directories = sources
            .Select(source => source.Value.Split('/')[..^1])
            .ToImmutableArray();
        var commonLength = directories.Min(directory => directory.Length);
        for (var index = 0; index < commonLength; index++)
        {
            var segment = directories[0][index];
            if (directories.Any(directory =>
                    !LogicalPath.FileSystemComparer.Equals(directory[index], segment)))
                return string.Join('/', directories[0][..index]);
        }

        return string.Join('/', directories[0][..commonLength]);
    }

    private static string? SourceFilter(LogicalPath source, string root)
    {
        var directory = string.Join('/', source.Value.Split('/')[..^1]);
        var relative = root.Length == 0
            ? directory
            : directory.Length == root.Length
                ? string.Empty
                : directory[(root.Length + 1)..];
        return relative.Length == 0 ? null : relative;
    }

    private static IEnumerable<string> FilterHierarchy(string? filter)
    {
        if (filter is null) yield break;
        var segments = filter.Split('/');
        for (var length = 1; length <= segments.Length; length++)
            yield return string.Join('/', segments[..length]);
    }

    private static void WriteProjectReferences(
        XmlWriter writer,
        WorkspaceProject project,
        IReadOnlyDictionary<string, WorkspaceProject> projectsById,
        IReadOnlyDictionary<(string Target, string Configuration), string> configurationNames,
        string xmlNamespace)
    {
        var dependencies = ProjectDependencies(project)
            .Where(projectsById.ContainsKey)
            .ToImmutableArray();
        if (dependencies.IsEmpty) return;

        writer.WriteStartElement(null, "ItemGroup", xmlNamespace);
        foreach (var dependency in dependencies)
        {
            var dependencyProject = projectsById[dependency];
            writer.WriteStartElement(null, "ProjectReference", xmlNamespace);
            writer.WriteAttributeString("Include",
                $"{ProjectFileStem(dependencyProject)}.vcxproj");
            if (VariantCondition(project, ReferenceVariants(project, dependency), configurationNames) is { } condition)
                writer.WriteAttributeString("Condition", condition);
            writer.WriteElementString(null, "Project", xmlNamespace,
                $"{{{ProjectGuid(dependency).ToString().ToUpperInvariant()}}}");
            writer.WriteElementString(null, "LinkLibraryDependencies", xmlNamespace, "false");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static ImmutableArray<LogicalPath> Sources(WorkspaceProject project)
    {
        return
        [
            ..project.Variants
                .SelectMany(variant => variant.Module.Sources)
                .GroupBy(source => source.Value, LogicalPath.FileSystemComparer)
                .Select(group => group.OrderBy(source => source.Value, StringComparer.Ordinal).First())
                .OrderBy(source => source.Value, StringComparer.Ordinal)
        ];
    }

    private static ImmutableArray<SourceVariantGroup> SourceVariants(WorkspaceProject project) =>
    [
        .. project.Variants
            .SelectMany(variant => variant.Module.Sources.Select(source => (Variant: variant, Source: source)))
            .GroupBy(item => item.Source.Value, LogicalPath.FileSystemComparer)
            .Select(group => new SourceVariantGroup(
                group.Select(item => item.Source).OrderBy(source => source.Value, StringComparer.Ordinal).First(),
                [
                    .. group.Select(item => item.Variant).Distinct()
                        .OrderBy(variant => variant.Target, StringComparer.Ordinal)
                        .ThenBy(variant => variant.Configuration)
                ],
                BuildPathLayout.StableToken(group.Select(item => item.Source.Value)
                    .Order(StringComparer.Ordinal).First())))
            .OrderBy(group => group.Source.Value, StringComparer.Ordinal)
    ];

    private static void WriteNativeSourceItemGroups(
        XmlWriter writer,
        WorkspaceProject project,
        IReadOnlyDictionary<(string Target, string Configuration), string> configurationNames,
        GenerationContext context)
    {
        foreach (var group in SourceVariants(project).GroupBy(
                     item => NativeItemName(item.Source), StringComparer.Ordinal))
        {
            StartMsBuild(writer, "ItemGroup");
            foreach (var item in group)
            {
                StartMsBuild(writer, group.Key);
                writer.WriteAttributeString("Include", ToWindows(RelativeFromOutput(context, item.Source.Value)));
                if (VariantCondition(project, item.Variants, configurationNames) is { } itemCondition)
                    writer.WriteAttributeString("Condition", itemCondition);

                if (group.Key is "ClCompile" or "ResourceCompile")
                {
                    foreach (var variant in item.Variants.Where(variant =>
                                 variant.Module.Kind == ModuleKind.HeaderOnly))
                    {
                        WriteConditionalMsBuild(
                            writer,
                            "ExcludedFromBuild",
                            Condition(variant, configurationNames),
                            "true");
                    }
                }

                if (group.Key == "ClCompile")
                {
                    foreach (var variant in item.Variants)
                    {
                        var condition = Condition(variant, configurationNames);
                        WriteConditionalMsBuild(writer, "ObjectFileName", condition,
                            $@"$(RoxyWorkspaceRoot)\{ToWindows(BuildPathLayout.ObjectFile(
                                variant.Configuration, variant.Target, variant.Module.Id, item.Source,
                                item.SourceToken))}");
                        var native = variant.Module.CxxSettings;
                        if (native?.PrecompiledHeader is not null && native.PrecompiledSource is not null)
                        {
                            var creates = item.Source == native.PrecompiledSource.Value;
                            WriteConditionalMsBuild(writer, "PrecompiledHeader", condition,
                                creates ? "Create" : "Use");
                            WriteConditionalMsBuild(writer, "PrecompiledHeaderFile", condition,
                                ToWindows(native.PrecompiledHeader.Value.Value));
                            WriteConditionalMsBuild(writer, "PrecompiledHeaderOutputFile", condition,
                                $@"$(RoxyWorkspaceRoot)\{ToWindows(BuildPathLayout.PrecompiledHeaderFile(
                                    variant.Configuration, variant.Target, variant.Module.Id))}");
                        }
                    }
                }
                else if (group.Key == "ResourceCompile")
                {
                    foreach (var variant in item.Variants)
                    {
                        WriteConditionalMsBuild(
                            writer,
                            "ResourceOutputFileName",
                            Condition(variant, configurationNames),
                            $@"$(RoxyWorkspaceRoot)\{ToWindows(BuildPathLayout.ResourceFile(
                                variant.Configuration, variant.Target, variant.Module.Id, item.Source,
                                item.SourceToken))}");
                    }
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }
    }

    private static string NativeItemName(LogicalPath source) => source.Value switch
    {
        var value when BuildFileKinds.IsCxxSource(value) => "ClCompile",
        var value when BuildFileKinds.IsHeader(value) => "ClInclude",
        var value when BuildFileKinds.IsResource(value) => "ResourceCompile",
        _ => "None",
    };

    private static ImmutableArray<WorkspaceProjectVariant> ReferenceVariants(
        WorkspaceProject project,
        string dependency) =>
    [
        .. project.Variants.Where(variant => variant.Module.Dependencies
            .Any(candidate => candidate.Module.Equals(dependency, StringComparison.Ordinal)))
    ];

    private static ImmutableArray<string> ProjectDependencies(WorkspaceProject project) =>
    [
        .. project.Variants
            .SelectMany(variant => variant.Module.Dependencies)
            .Select(dependency => dependency.Module)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    private static ImmutableArray<string> SolutionDependencies(
        WorkspaceProject project,
        IReadOnlySet<string> projectIds)
    {
        return
        [
            .. ProjectDependencies(project)
                .Where(projectIds.Contains)
                .Where(dependency => ReferenceVariants(project, dependency).Length == project.Variants.Length)
        ];
    }

    private static string? VariantCondition(
        WorkspaceProject project,
        ImmutableArray<WorkspaceProjectVariant> variants,
        IReadOnlyDictionary<(string Target, string Configuration), string> configurationNames) =>
        variants.Length == project.Variants.Length
        ? null
        : string.Join(" Or ", variants
            .OrderBy(variant => variant.Target, StringComparer.Ordinal)
            .ThenBy(variant => variant.Configuration)
            .Select(variant => Condition(variant, configurationNames)));

    private static ImmutableArray<SolutionConfiguration> WorkspaceConfigurations(
        WorkspaceModel workspace,
        IReadOnlyDictionary<(string Target, string Configuration), string> configurationNames)
    {
        return
        [
            ..workspace
                .Projects
                .SelectMany(project => project.Variants)
                .GroupBy(variant => (variant.Target, variant.Configuration.Canonical))
                .Select(group =>
                {
                    var variant = group.First();
                    return new SolutionConfiguration(
                        DisplayName(variant, configurationNames),
                        variant.Target,
                        variant.Configuration);
                })
                .OrderBy(configuration => configuration.Name, StringComparer.Ordinal)
                .ThenBy(configuration => configuration.Target, StringComparer.Ordinal)
                .ThenBy(configuration => configuration.Key)
        ];
    }

    private static WorkspaceProjectVariant MapProjectConfiguration(
        WorkspaceProject project,
        string solutionTarget,
        ConfigurationKey solutionConfiguration)
    {
        foreach (var variant in project.Variants)
            if (variant.Target == solutionTarget && variant.Configuration == solutionConfiguration)
                return variant;

        return project.Variants
            .OrderByDescending(variant => variant.Target == solutionTarget)
            .ThenByDescending(variant => SameFragment(variant.Configuration, solutionConfiguration, "Platform"))
            .ThenByDescending(variant => SameFragment(variant.Configuration, solutionConfiguration, "Architecture"))
            .ThenByDescending(variant => SameFragment(variant.Configuration, solutionConfiguration, "Profile"))
            .ThenByDescending(variant => SameFragment(variant.Configuration, solutionConfiguration, "Toolchain"))
            .ThenByDescending(variant => SameFragment(variant.Configuration, solutionConfiguration, "LinkModel"))
            .ThenByDescending(variant => SharedFragmentCount(variant.Configuration, solutionConfiguration))
            .ThenBy(variant => variant.Configuration)
            .ThenBy(variant => variant.Target, StringComparer.Ordinal)
            .First();
    }

    private static bool SameFragment(ConfigurationKey left, ConfigurationKey right, string fragment)
    {
        var id = new FragmentId(fragment);
        return left.TryGet(id, out var leftValue) && right.TryGet(id, out var rightValue) && leftValue == rightValue;
    }

    private static int SharedFragmentCount(ConfigurationKey left, ConfigurationKey right) =>
        left.Values.Count(value => right.Is(value));

    private static string DisplayName(
        WorkspaceProjectVariant variant,
        IReadOnlyDictionary<(string Target, string Configuration), string> configurationNames) =>
        configurationNames[(variant.Target, variant.Configuration.Canonical)];

    private static string Condition(
        WorkspaceProjectVariant variant,
        IReadOnlyDictionary<(string Target, string Configuration), string> configurationNames)
    {
        return $"'$(Configuration)|$(Platform)'=='{DisplayName(variant, configurationNames)}|x64'";
    }

    private static Dictionary<(string Target, string Configuration), string> ConfigurationNames(
        WorkspaceModel workspace)
    {
        var variants = workspace.Projects
            .SelectMany(project => project.Variants)
            .GroupBy(variant => (variant.Target, variant.Configuration.Canonical))
            .Select(group => group.First())
            .ToImmutableArray();
        var names = new Dictionary<(string Target, string Configuration), string>();

        foreach (var group in variants.GroupBy(
                     variant => BuildConfigurationNames.DisplayName(variant.Configuration),
                     StringComparer.Ordinal))
        {
            var colliding = group
                .OrderBy(variant => variant.Target, StringComparer.Ordinal)
                .ThenBy(variant => variant.Configuration)
                .ToImmutableArray();
            if (colliding.Length == 1)
            {
                var variant = colliding[0];
                names.Add((variant.Target, variant.Configuration.Canonical), group.Key);
                continue;
            }

            var targetsDiffer = colliding.Select(variant => variant.Target)
                .Distinct(StringComparer.Ordinal)
                .Count() > 1;
            var differingFragments = colliding
                .SelectMany(variant => variant.Configuration.Values.Select(value => value.Fragment))
                .Distinct()
                .Where(fragment => colliding
                    .Select(variant => variant.Configuration.TryGet(fragment, out var value) ? value.Value : null)
                    .Distinct(StringComparer.Ordinal)
                    .Count() > 1)
                .Order()
                .ToImmutableArray();
            foreach (var variant in colliding)
            {
                var qualifiers = new List<string>(differingFragments.Length + (targetsDiffer ? 1 : 0));
                if (targetsDiffer) qualifiers.Add($"Target={variant.Target}");
                qualifiers.AddRange(differingFragments.Select(fragment =>
                    variant.Configuration.TryGet(fragment, out var value)
                        ? $"{fragment.Value}={value.Value}"
                        : $"{fragment.Value}=None"));
                names.Add(
                    (variant.Target, variant.Configuration.Canonical),
                    $"{group.Key} ({string.Join(", ", qualifiers)})");
            }
        }

        return names;
    }

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

    private static string ProjectFileStem(WorkspaceProject project) => ToPascalCase(project.Id);

    private static string ToPascalCase(string value) => string.Concat(value
        .Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries)
        .Select(segment => char.ToUpperInvariant(segment[0]) + segment[1..]));

    private static Guid ProjectGuid(string id) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"roxy:{id}"))[..16]);

    private static ToolchainBuildSettings ToolchainSettings(
        IReadOnlyDictionary<(string Target, string Configuration), ToolchainBuildSettings> toolchains,
        WorkspaceProjectVariant variant)
    {
        return toolchains.GetValueOrDefault(
            (variant.Target, variant.Configuration.Canonical), UnknownToolchain);
    }

    private static string JoinMsbuild(IEnumerable<string> values, string inheritedMetadata) =>
        string.Join(';', values.Append($"%({inheritedMetadata})"));

    private static string JoinCommandLine(IEnumerable<string> values, string inheritedValue) =>
        string.Join(' ', values.Append(inheritedValue));

    private static string FormatLinkInput(string value)
    {
        return IsExternalPath(value) ||
               !value.Contains('/') && !value.Contains('\\')
            ? value
            : FormatProjectPath(value);
    }

    private static string FormatIncludePath(string value)
    {
        return FormatProjectPath(value);
    }

    private static string FormatProjectPath(string value)
    {
        return IsExternalPath(value)
            ? ToWindows(value)
            : $"$(RoxyWorkspaceRoot)\\{ToWindows(value)}";
    }

    private static bool IsExternalPath(string value)
    {
        return Path.IsPathRooted(value) ||
               value.StartsWith("$(", StringComparison.Ordinal) ||
               value.StartsWith("%(", StringComparison.Ordinal);
    }

    private static ImmutableArray<UsageValue> SystemIncludes(ConfiguredModule module)
    {
        return module.CompileUsage.SystemIncludeDirectories.IsDefault
            ? []
            : module.CompileUsage.SystemIncludeDirectories;
    }

    private static bool HasArgument(ImmutableArray<string> arguments, string expected)
    {
        return arguments.Any(argument => argument.Equals(expected, StringComparison.OrdinalIgnoreCase));
    }

    private static bool UsesDebugRuntime(ToolchainBuildSettings settings, ConfigurationKey configuration)
    {
        if (HasArgument(settings.CompileArguments, "/MDd") || HasArgument(settings.CompileArguments, "/MTd"))
            return true;
        if (HasArgument(settings.CompileArguments, "/MD") || HasArgument(settings.CompileArguments, "/MT"))
            return false;
        return Fragment(configuration, "Profile") == "Debug";
    }

    private static bool UsesDisabledOptimization(
        ToolchainBuildSettings settings,
        ConfigurationKey configuration)
    {
        if (HasArgument(settings.CompileArguments, "/Od")) return true;
        if (settings.CompileArguments.Any(argument =>
                argument.Equals("/O1", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("/O2", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("/Ox", StringComparison.OrdinalIgnoreCase))) return false;
        return Fragment(configuration, "Profile") == "Debug";
    }

    private static string RelativeFromOutput(GenerationContext context, string logicalPath) =>
        Path.GetRelativePath(context.OutputDirectory.Value, logicalPath).Replace('\\', '/');

    private static string ToWindows(string path) => path.Replace('/', '\\');

    private static void StartMsBuild(XmlWriter writer, string name) =>
        writer.WriteStartElement(null, name, MsBuild);

    private static void WriteMsBuild(XmlWriter writer, string name, string value) =>
        writer.WriteElementString(null, name, MsBuild, value);

    private static void WriteConditionalMsBuild(XmlWriter writer, string name, string condition, string value)
    {
        StartMsBuild(writer, name);
        writer.WriteAttributeString("Condition", condition);
        writer.WriteString(value);
        writer.WriteEndElement();
    }

    private static void WriteImport(XmlWriter writer, string project)
    {
        StartMsBuild(writer, "Import");
        writer.WriteAttributeString("Project", project);
        writer.WriteEndElement();
    }

    private static string WriteXml(Action<XmlWriter> write)
    {
        var builder = new StringBuilder();
        using (var textWriter = new StringWriter(builder, CultureInfo.InvariantCulture))
        using (var writer = XmlWriter.Create(textWriter, ProjectWriterSettings))
        {
            write(writer);
        }

        return builder.Append('\n').ToString();
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private sealed record SolutionConfiguration(string Name, string Target, ConfigurationKey Key);

    private sealed record SourceVariantGroup(
        LogicalPath Source,
        ImmutableArray<WorkspaceProjectVariant> Variants,
        string SourceToken);
}

/// <summary>Registers the Visual Studio workspace generator.</summary>
public sealed class VisualStudioPlugin : IPlugin
{
    public PluginId Id { get; } = new("Roxy.Generator.Vs2022");
    public Version Version { get; } = new(0, 1, 0);
    public CapabilitySet Capabilities { get; } = new(["Workspace.Vs2022", "NativeMsbuild"]);

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
