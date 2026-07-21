using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Model;

namespace RoxyBuildTool.Generators.VisualStudio;

/// <summary>Generates a mixed C++ and C# Visual Studio workspace.</summary>
public sealed class VisualStudio2022Generator : IWorkspaceGenerator
{
    private const string SolutionPlatformName = "Win64";
    private const string MsBuild = "http://schemas.microsoft.com/developer/msbuild/2003";
    private static readonly Guid CxxProjectType = new("8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942");
    private static readonly Guid CSharpProjectType = new("FAE04EC0-301F-11D3-BF4B-00C04F79EFBC");

    private static readonly ConditionalWeakTable<ConfigurationKey, ConfigurationPresentation>
        ConfigurationPresentations = new();

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
        var toolchains = workspace.ActionGraphs.ToDictionary(
            graph => (graph.Target, graph.Configuration.Canonical),
            graph => graph.Toolchain ?? UnknownToolchain);
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
                    GenerateVcxproj(project, projectsById, toolchains, context)));
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
            [.. files.OrderBy(file => file.Path.Value, StringComparer.Ordinal)], []);
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
        WorkspaceProject project,
        IReadOnlyDictionary<string, WorkspaceProject> projectsById,
        IReadOnlyDictionary<(string Target, string Configuration), ToolchainBuildSettings> toolchains,
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
                var display = DisplayName(variant);
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
                writer.WriteAttributeString("Condition", Condition(variant));
                writer.WriteAttributeString("Label", "Configuration");
                WriteMsBuild(writer, "ConfigurationType", ConfigurationType(variant.Module.Kind));
                WriteMsBuild(writer, "UseDebugLibraries", debugRuntime ? "true" : "false");
                WriteMsBuild(writer, "PlatformToolset", toolchain.VisualStudioPlatformToolset);
                WriteMsBuild(writer, "CharacterSet", "Unicode");
                writer.WriteEndElement();
            }

            WriteImport(writer, "$(VCTargetsPath)\\Microsoft.Cpp.props");
            foreach (var variant in project.Variants)
                WriteNativeVariant(writer, variant, ToolchainSettings(toolchains, variant));

            WriteNativeSourceItemGroups(writer, project, context);
            WriteProjectReferences(writer, project, projectsById, MsBuild);
            WriteImport(writer, "$(VCTargetsPath)\\Microsoft.Cpp.targets");
            writer.WriteEndElement();
        });

        static void WriteNativeVariant(
            XmlWriter writer,
            WorkspaceProjectVariant variant,
            ToolchainBuildSettings toolchain)
        {
            var native = variant.Module.CxxSettings ?? CxxModuleSettings.Empty;
            var debugRuntime = UsesDebugRuntime(toolchain, variant.Configuration);
            var outputRoot =
                $@"$(RoxyWorkspaceRoot)\{ToWindows(BuildPathLayout.OutputRoot(variant.Configuration, variant.Target))}\";
            var intermediate =
                $@"$(RoxyWorkspaceRoot)\{ToWindows(BuildPathLayout.IntermediateRoot(variant.Configuration, variant.Target))}\{variant.Module.Id}\";

            StartMsBuild(writer, "PropertyGroup");
            writer.WriteAttributeString("Condition", Condition(variant));
            WriteMsBuild(writer, "OutDir", outputRoot);
            WriteMsBuild(writer, "IntDir", intermediate);
            WriteMsBuild(writer, "TargetName", native.OutputName ?? variant.Module.Id);
            writer.WriteEndElement();

            StartMsBuild(writer, "ItemDefinitionGroup");
            writer.WriteAttributeString("Condition", Condition(variant));
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
        return WriteXml(writer =>
        {
            writer.WriteStartElement("Project");
            writer.WriteAttributeString("Sdk", "Microsoft.NET.Sdk");
            writer.WriteStartElement("PropertyGroup");
            writer.WriteElementString("LangVersion", "14.0");
            writer.WriteElementString("Nullable", "enable");
            writer.WriteElementString("ImplicitUsings", "enable");
            writer.WriteElementString("Deterministic", "true");
            writer.WriteElementString("ManagePackageVersionsCentrally", "false");
            writer.WriteElementString("RestorePackagesWithLockFile", "true");
            writer.WriteElementString("NuGetLockFilePath", "$(BaseIntermediateOutputPath)packages.lock.json");
            writer.WriteElementString("EnableDefaultItems", "false");
            writer.WriteElementString("Platforms", "x64");
            writer.WriteElementString("AssemblyName", representative.Id);
            writer.WriteElementString("RoxyWorkspaceRoot", ToWindows(RelativeFromOutput(context, ".")));
            if (commonOutputType) writer.WriteElementString("OutputType", outputType);
            if (commonRootNamespace) writer.WriteElementString("RootNamespace", rootNamespace);
            if (commonTargetFrameworks) WriteTargetFrameworks(writer, targetFrameworks);
            writer.WriteEndElement();

            foreach (var variant in project.Variants)
            {
                var profile = Fragment(variant.Configuration, "Profile");
                writer.WriteStartElement("PropertyGroup");
                writer.WriteAttributeString("Condition", Condition(variant));
                writer.WriteElementString("Optimize", profile == "Debug" ? "false" : "true");
                writer.WriteElementString("OutputPath",
                    $@"$(RoxyWorkspaceRoot)\{ToWindows(BuildPathLayout.OutputRoot(variant.Configuration, variant.Target))}\{variant.Module.Id}\");
                writer.WriteElementString("IntermediateOutputPath",
                    $@"$(RoxyWorkspaceRoot)\{ToWindows(BuildPathLayout.IntermediateRoot(variant.Configuration, variant.Target))}\{variant.Module.Id}\");
                if (!commonTargetFrameworks) WriteTargetFrameworks(writer, TargetFrameworks(variant.Module));
                if (!commonOutputType) writer.WriteElementString("OutputType", ManagedOutputType(variant.Module));
                if (!commonRootNamespace)
                    writer.WriteElementString("RootNamespace", ManagedRootNamespace(project, variant.Module));
                writer.WriteEndElement();

                if (!variant.Module.CompileUsage.RuntimeFiles.IsEmpty)
                {
                    writer.WriteStartElement("ItemGroup");
                    writer.WriteAttributeString("Condition", Condition(variant));
                    foreach (var runtime in variant.Module.CompileUsage.RuntimeFiles)
                    {
                        writer.WriteStartElement("Content");
                        writer.WriteAttributeString("Include", FormatProjectPath(runtime.Value));
                        writer.WriteElementString("Link", Path.GetFileName(runtime.Value));
                        writer.WriteElementString("Visible", "false");
                        writer.WriteElementString("CopyToOutputDirectory", "PreserveNewest");
                        writer.WriteEndElement();
                    }

                    writer.WriteEndElement();
                }
            }

            writer.WriteStartElement("ItemGroup");
            foreach (var item in SourceVariants(project))
            {
                writer.WriteStartElement("Compile");
                writer.WriteAttributeString("Include", ToWindows(RelativeFromOutput(context, item.Source.Value)));
                if (VariantCondition(project, item.Variants) is { } condition)
                    writer.WriteAttributeString("Condition", condition);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();

            WriteProjectReferences(writer, project, projectsById, string.Empty);
            var packages = project.Variants
                .SelectMany(variant => variant.Module.Packages.Select(package => (Variant: variant, Package: package)))
                .GroupBy(item => item.Package)
                .OrderBy(group => group.Key.Id, StringComparer.Ordinal)
                .ThenBy(group => group.Key.Version, StringComparer.Ordinal)
                .ToImmutableArray();
            if (!packages.IsEmpty)
            {
                writer.WriteStartElement("ItemGroup");
                foreach (var group in packages)
                {
                    var package = group.Key;
                    writer.WriteStartElement("PackageReference");
                    writer.WriteAttributeString("Include", package.Id);
                    writer.WriteAttributeString("Version", package.Version);
                    if (package.PrivateAssets) writer.WriteAttributeString("PrivateAssets", "all");
                    if (VariantCondition(project, [.. group.Select(item => item.Variant).Distinct()]) is
                        { } condition)
                        writer.WriteAttributeString("Condition", condition);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        });
    }

    private static string GenerateFilters(WorkspaceProject project, GenerationContext context)
    {
        return WriteXml(writer =>
        {
            StartMsBuild(writer, "Project");
            writer.WriteAttributeString("ToolsVersion", "4.0");
            foreach (var group in Sources(project).GroupBy(NativeItemName, StringComparer.Ordinal))
            {
                StartMsBuild(writer, "ItemGroup");
                foreach (var source in group)
                {
                    StartMsBuild(writer, group.Key);
                    writer.WriteAttributeString("Include", ToWindows(RelativeFromOutput(context, source.Value)));
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        });
    }

    private static void WriteProjectReferences(
        XmlWriter writer,
        WorkspaceProject project,
        IReadOnlyDictionary<string, WorkspaceProject> projectsById,
        string xmlNamespace)
    {
        if (project.ProjectDependencies.IsEmpty) return;

        writer.WriteStartElement(null, "ItemGroup", xmlNamespace);
        foreach (var dependency in project.ProjectDependencies.Order(StringComparer.Ordinal))
        {
            var dependencyProject = projectsById[dependency];
            writer.WriteStartElement(null, "ProjectReference", xmlNamespace);
            writer.WriteAttributeString("Include",
                $"{ProjectFileStem(dependencyProject)}.{ProjectExtension(dependencyProject)}");
            if (VariantCondition(project, ReferenceVariants(project, dependency)) is { } condition)
                writer.WriteAttributeString("Condition", condition);
            writer.WriteElementString(null, "Project", xmlNamespace,
                $"{{{ProjectGuid(dependency).ToString().ToUpperInvariant()}}}");
            if (dependencyProject.Language != project.Language)
            {
                writer.WriteElementString(null, "ReferenceOutputAssembly", xmlNamespace, "false");
                writer.WriteElementString(null, "SkipGetTargetFrameworkProperties", xmlNamespace,
                    "true");
            }

            if (project.Language == ModuleLanguage.Cxx && dependencyProject.Language == ModuleLanguage.Cxx)
                writer.WriteElementString(null, "LinkLibraryDependencies", xmlNamespace, "false");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static string GenerateDirectoryBuildProps(GenerationContext context)
    {
        return WriteXml(writer =>
        {
            writer.WriteStartElement("Project");
            writer.WriteStartElement("PropertyGroup");
            writer.WriteAttributeString("Condition", "'$(MSBuildProjectExtension)'=='.csproj'");
            writer.WriteElementString("RoxyWorkspaceRoot", ToWindows(RelativeFromOutput(context, ".")));
            writer.WriteElementString("BaseIntermediateOutputPath",
                @"$(RoxyWorkspaceRoot)\intermediate\msbuild\$(MSBuildProjectName)\$(Configuration)\");
            writer.WriteEndElement();
            writer.WriteEndElement();
        });
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
                ],
                BuildPathLayout.StableToken(group.Key.Value)))
            .OrderBy(group => group.Source.Value, StringComparer.Ordinal)
    ];

    private static void WriteNativeSourceItemGroups(
        XmlWriter writer,
        WorkspaceProject project,
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
                if (VariantCondition(project, item.Variants) is { } itemCondition)
                    writer.WriteAttributeString("Condition", itemCondition);

                if (group.Key is "ClCompile" or "ResourceCompile")
                {
                    foreach (var variant in item.Variants.Where(variant =>
                                 variant.Module.Kind == ModuleKind.HeaderOnly))
                    {
                        WriteConditionalMsBuild(writer, "ExcludedFromBuild", Condition(variant), "true");
                    }
                }

                if (group.Key == "ClCompile")
                {
                    foreach (var variant in item.Variants)
                    {
                        var condition = Condition(variant);
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
                        WriteConditionalMsBuild(writer, "ResourceOutputFileName", Condition(variant),
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

    private static string? VariantCondition(
        WorkspaceProject project,
        ImmutableArray<WorkspaceProjectVariant> variants) => variants.Length == project.Variants.Length
        ? null
        : string.Join(" Or ", variants.OrderBy(variant => variant.Configuration).Select(Condition));

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
        ConfigurationKey solutionConfiguration)
    {
        foreach (var variant in project.Variants)
            if (variant.Configuration == solutionConfiguration)
                return variant;

        return project.Variants
            .OrderByDescending(variant => SameFragment(variant.Configuration, solutionConfiguration, "Platform"))
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

    private static string DisplayName(WorkspaceProjectVariant variant) =>
        Presentation(variant.Configuration).DisplayName;

    private static string Condition(WorkspaceProjectVariant variant) =>
        Presentation(variant.Configuration).Condition;

    private static ConfigurationPresentation Presentation(ConfigurationKey configuration)
    {
        return ConfigurationPresentations.GetValue(configuration, static key =>
        {
            var displayName = BuildConfigurationNames.DisplayName(key);
            return new ConfigurationPresentation(
                displayName,
                $"'$(Configuration)|$(Platform)'=='{displayName}|x64'");
        });
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

    private static string ProjectExtension(WorkspaceProject project) =>
        project.Language == ModuleLanguage.Cxx ? "vcxproj" : "csproj";

    private static string ProjectFileStem(WorkspaceProject project)
    {
        var name = project.Name.EndsWith("Module", StringComparison.Ordinal)
            ? project.Name[..^"Module".Length]
            : project.Name;
        var validCharacters = name.Count(character => char.IsLetterOrDigit(character) || character is '_' or '.');
        if (validCharacters == 0) return ToPascalCase(project.Id);
        if (validCharacters == name.Length) return name;

        return string.Create(validCharacters, name, static (destination, value) =>
        {
            var index = 0;
            foreach (var character in value)
                if (char.IsLetterOrDigit(character) || character is '_' or '.')
                    destination[index++] = character;
        });
    }

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

    private static ImmutableArray<string> TargetFrameworks(ConfiguredModule module) => module.TargetFrameworks
        .DefaultIfEmpty("net10.0")
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToImmutableArray();

    private static void WriteTargetFrameworks(XmlWriter writer, ImmutableArray<string> frameworks) =>
        writer.WriteElementString(frameworks.Length == 1 ? "TargetFramework" : "TargetFrameworks",
            frameworks.Length == 1 ? frameworks[0] : string.Join(';', frameworks));

    private static string ManagedOutputType(ConfiguredModule module) =>
        module.Kind == ModuleKind.CSharpConsoleApplication ? "Exe" : "Library";

    private static string ManagedRootNamespace(WorkspaceProject project, ConfiguredModule module) =>
        module.RootNamespace ?? project.Name.Replace(' ', '.');

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

    private sealed record SolutionConfiguration(string Name, ConfigurationKey Key);

    private sealed record ConfigurationPresentation(string DisplayName, string Condition);

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
