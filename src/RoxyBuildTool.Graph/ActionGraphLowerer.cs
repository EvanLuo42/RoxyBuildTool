using System.Collections.Immutable;
using RoxyBuildTool.Configuration;
using RoxyBuildTool.Model;
using RoxyBuildTool.Toolchains;

namespace RoxyBuildTool.Graph;

/// <summary>Lowers a configured graph into structured compiler, linker, copy, and .NET actions.</summary>
public static class ActionGraphLowerer
{
    /// <summary>Creates the action graph for one configured target and toolchain.</summary>
    public static ActionGraph Lower(ConfiguredGraph graph, ToolchainDescriptor toolchain, string workspaceName)
    {
        var actions = ImmutableArray.CreateBuilder<BuildAction>();
        var artifacts = ImmutableArray.CreateBuilder<BuildArtifact>();
        var finalActions = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal);
        var configurationName = BuildConfigurationNames.DisplayName(graph.Configuration);
        var outputRoot = BuildPathLayout.OutputRoot(graph.Configuration, graph.Target.Id);
        var policy = toolchain.GetPolicy(graph.Configuration);
        var msbuildPlatform = toolchain.Architecture.ToLowerInvariant();

        foreach (var module in TopologicalOrder(graph.Modules))
        {
            if (module.Language == ModuleLanguage.CSharp)
            {
                LowerCSharp(module);
            }
            else
            {
                LowerCxx(module);
            }
        }

        return new ActionGraph(
            graph.Configuration,
            graph.Target.Id,
            [.. actions.OrderBy(action => action.Id, StringComparer.Ordinal)],
            [.. artifacts.OrderBy(artifact => artifact.Id, StringComparer.Ordinal)],
            new ToolchainBuildSettings(
                toolchain.Id.Value,
                toolchain.VisualStudioPlatformToolset,
                policy.CompileArguments,
                policy.LinkArguments));

        void LowerCxx(ConfiguredModule module)
        {
            var objectActions = new List<string>();
            var objectPaths = new List<string>();
            if (module.Kind == ModuleKind.HeaderOnly)
            {
                finalActions[module.Id] = [];
                return;
            }

            foreach (var source in module.Sources.Where(source => BuildFileKinds.IsCxxSource(source.Value)))
            {
                var sourceToken = BuildPathLayout.StableToken(source.Value);
                var objectPath = BuildPathLayout.ObjectFile(graph.Configuration, graph.Target.Id, module.Id, source);
                var actionId = ActionId(module.Id, $"compile-{sourceToken}");
                var arguments = new List<string> { "/nologo", "/c", "/EHsc", "/std:c++latest" };
                arguments.AddRange(policy.CompileArguments);
                arguments.AddRange(module.CompileUsage.IncludeDirectories.Select(include => $"/I{include.Value}"));
                arguments.AddRange(module.CompileUsage.Defines.Select(define => $"/D{define.Value}"));
                arguments.Add(source.Value);
                arguments.Add($"/Fo{objectPath}");
                actions.Add(new BuildAction(
                    actionId,
                    BuildActionKind.Compile,
                    toolchain.Compiler,
                    [.. arguments],
                    new LogicalPath("."),
                    [source.Value],
                    [objectPath],
                    [],
                    ["INCLUDE", "TMP", "TEMP"],
                    false,
                    false,
                    []));
                artifacts.Add(new BuildArtifact($"{module.Id}:object:{sourceToken}", ArtifactKind.ObjectFile,
                    new LogicalPath(objectPath), actionId));
                objectActions.Add(actionId);
                objectPaths.Add(objectPath);
            }

            foreach (var source in module.Sources.Where(source => BuildFileKinds.IsResource(source.Value)))
            {
                var resourceCompiler = toolchain.ResourceCompiler
                                       ?? throw new InvalidOperationException(
                                           $"Toolchain '{toolchain.Id}' cannot compile resource source '{source}'.");
                var sourceToken = BuildPathLayout.StableToken(source.Value);
                var resourcePath = BuildPathLayout.ResourceFile(
                    graph.Configuration, graph.Target.Id, module.Id, source);
                var actionId = ActionId(module.Id, $"resource-{sourceToken}");
                var arguments = new List<string> { "/nologo" };
                arguments.AddRange(policy.CompileArguments
                    .Where(argument => argument.StartsWith("/D", StringComparison.OrdinalIgnoreCase))
                    .Select(argument => $"/d{argument[2..]}"));
                arguments.AddRange(module.CompileUsage.IncludeDirectories.Select(include => $"/I{include.Value}"));
                arguments.AddRange(module.CompileUsage.Defines.Select(define => $"/d{define.Value}"));
                arguments.Add($"/fo{resourcePath}");
                arguments.Add(source.Value);
                actions.Add(new BuildAction(
                    actionId,
                    BuildActionKind.ResourceCompile,
                    resourceCompiler,
                    [.. arguments],
                    new LogicalPath("."),
                    [source.Value],
                    [resourcePath],
                    [],
                    ["INCLUDE", "TMP", "TEMP"],
                    false,
                    false,
                    []));
                artifacts.Add(new BuildArtifact($"{module.Id}:resource:{sourceToken}", ArtifactKind.ResourceFile,
                    new LogicalPath(resourcePath), actionId));
                objectActions.Add(actionId);
                objectPaths.Add(resourcePath);
            }

            if (module.Kind == ModuleKind.ObjectLibrary)
            {
                finalActions[module.Id] = [.. objectActions.Order(StringComparer.Ordinal)];
                return;
            }

            var finalActionId = ActionId(module.Id, module.Kind == ModuleKind.StaticLibrary ? "archive" : "link");
            var dependencies = objectActions.Concat(DependencyActions(module)).Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
            if (module.Kind == ModuleKind.StaticLibrary)
            {
                var libraryOutput = $"{outputRoot}/{module.Id}.lib";
                actions.Add(new BuildAction(finalActionId, BuildActionKind.Archive, toolchain.Librarian,
                    ["/nologo", $"/OUT:{libraryOutput}", .. objectPaths], new LogicalPath("."), [.. objectPaths],
                    [libraryOutput],
                    dependencies, ["LIB", "TMP", "TEMP"], true, false, []));
                artifacts.Add(new BuildArtifact($"{module.Id}:StaticLibrary", ArtifactKind.StaticLibrary,
                    new LogicalPath(libraryOutput), finalActionId));
                finalActions[module.Id] = [finalActionId];
                return;
            }

            var extension = module.Kind == ModuleKind.SharedLibrary ? "dll" : "exe";
            var output = $"{outputRoot}/{module.Id}.{extension}";
            var linkArguments = new List<string> { "/NOLOGO", $"/OUT:{output}" };
            var outputs = new List<string> { output };
            if (module.Kind == ModuleKind.SharedLibrary)
            {
                var importLibrary = $"{outputRoot}/{module.Id}.lib";
                linkArguments.Add("/DLL");
                linkArguments.Add($"/IMPLIB:{importLibrary}");
                outputs.Add(importLibrary);
            }

            linkArguments.AddRange(policy.LinkArguments);
            linkArguments.AddRange(objectPaths);
            linkArguments.AddRange(module.CompileUsage.LinkInputs.Select(input => input.Value));
            actions.Add(new BuildAction(finalActionId, BuildActionKind.Link, toolchain.Linker,
                [.. linkArguments], new LogicalPath("."),
                [.. objectPaths, .. module.CompileUsage.LinkInputs.Select(input => input.Value)],
                [.. outputs], dependencies, ["LIB", "TMP", "TEMP"], true, false, []));
            artifacts.Add(new BuildArtifact($"{module.Id}:{extension}",
                module.Kind == ModuleKind.SharedLibrary ? ArtifactKind.SharedLibrary : ArtifactKind.Executable,
                new LogicalPath(output), finalActionId));
            if (module.Kind == ModuleKind.SharedLibrary)
                artifacts.Add(new BuildArtifact($"{module.Id}:ImportLibrary", ArtifactKind.ImportLibrary,
                    new LogicalPath($"{outputRoot}/{module.Id}.lib"), finalActionId));

            var copyActions = new List<string>();
            if (module.Kind == ModuleKind.Executable)
            {
                foreach (var runtime in module.CompileUsage.RuntimeFiles.OrderBy(value => value.Value,
                             StringComparer.Ordinal))
                {
                    var destination = $"{outputRoot}/{Path.GetFileName(runtime.Value)}";
                    if (string.Equals(runtime.Value, destination, StringComparison.Ordinal)) continue;

                    var copyId = CopyActionId(module.Id, runtime.Value);
                    actions.Add(new BuildAction(copyId, BuildActionKind.Copy, "copy",
                        ["/Y", runtime.Value, destination], new LogicalPath("."), [runtime.Value], [destination],
                        [finalActionId], [], true, false, []));
                    artifacts.Add(new BuildArtifact(
                        $"{module.Id}:runtime:{Path.GetFileName(runtime.Value)}:{BuildPathLayout.StableToken(runtime.Value, 8)}",
                        ArtifactKind.RuntimeFile, new LogicalPath(destination), copyId));
                    copyActions.Add(copyId);
                }
            }

            finalActions[module.Id] = copyActions.Count == 0
                ? [finalActionId]
                : [.. copyActions.Order(StringComparer.Ordinal)];
        }

        void LowerCSharp(ConfiguredModule module)
        {
            var moduleName = module.DisplayName.EndsWith("Module", StringComparison.Ordinal)
                ? module.DisplayName[..^"Module".Length]
                : module.DisplayName;
            var targetName = graph.Target.DisplayName.EndsWith("Target", StringComparison.Ordinal)
                ? graph.Target.DisplayName[..^"Target".Length]
                : graph.Target.DisplayName;
            var projectName = moduleName.Equals(targetName, StringComparison.Ordinal) ||
                              moduleName.Equals(targetName + "Executable", StringComparison.Ordinal)
                ? targetName
                : $"{moduleName}.{targetName}";
            var project = $".roxy/generated/Vs2022/{workspaceName}/{projectName}.csproj";
            var restoreId = ActionId(module.Id, "DotnetRestore");
            var buildId = ActionId(module.Id, "DotnetBuild");
            var dependencies = DependencyActions(module);
            var restoreOutput =
                $"intermediate/msbuild/{projectName}/{configurationName}/packages.lock.json";
            actions.Add(new BuildAction(restoreId, BuildActionKind.DotNetRestore, "dotnet",
                [
                    "restore", project, "--use-lock-file",
                    $"-p:Configuration={configurationName}", $"-p:Platform={msbuildPlatform}"
                ],
                new LogicalPath("."), [project], [restoreOutput], [],
                ["NUGET_PACKAGES", "NUGET_HTTP_CACHE_PATH", "TMP", "TEMP"], false, false, []));
            var frameworks = module.TargetFrameworks.DefaultIfEmpty("net10.0")
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
            var assemblies = frameworks.Select(framework =>
                $"{outputRoot}/{module.Id}/{framework}/{module.Id}.dll").ToImmutableArray();
            actions.Add(new BuildAction(buildId, BuildActionKind.DotNetBuild, "dotnet",
                [
                    "build", project, "--no-restore", "--configuration", configurationName,
                    $"-p:Platform={msbuildPlatform}", $"-p:RoxyConfigurationHash={graph.Configuration.ShortHash}"
                ],
                new LogicalPath("."), [project, .. module.Sources.Select(source => source.Value)], assemblies,
                [restoreId, .. dependencies], ["DOTNET_ROOT", "NUGET_PACKAGES", "TMP", "TEMP"], false, false, []));
            foreach (var (framework, assembly) in frameworks.Zip(assemblies))
                artifacts.Add(new BuildArtifact($"{module.Id}:ManagedAssembly:{framework}",
                    ArtifactKind.ManagedAssembly, new LogicalPath(assembly), buildId));

            var copyActions = new List<string>();
            foreach (var runtime in module.CompileUsage.RuntimeFiles.OrderBy(value => value.Value,
                         StringComparer.Ordinal))
            {
                var copyId = CopyActionId(module.Id, runtime.Value);
                var destination = $"{outputRoot}/{module.Id}/{Path.GetFileName(runtime.Value)}";
                actions.Add(new BuildAction(copyId, BuildActionKind.Copy, "copy",
                    ["/Y", runtime.Value, destination], new LogicalPath("."), [runtime.Value], [destination],
                    [buildId], [], true, false, []));
                artifacts.Add(new BuildArtifact(
                    $"{module.Id}:runtime:{Path.GetFileName(runtime.Value)}:{BuildPathLayout.StableToken(runtime.Value, 8)}",
                    ArtifactKind.RuntimeFile, new LogicalPath(destination), copyId));
                copyActions.Add(copyId);
            }

            finalActions[module.Id] = copyActions.Count == 0
                ? [buildId]
                : [.. copyActions.Order(StringComparer.Ordinal)];
        }

        ImmutableArray<string> DependencyActions(ConfiguredModule module)
        {
            return
            [
                .. module.Dependencies
                    .SelectMany(dependency => finalActions.GetValueOrDefault(dependency.Module, []))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
            ];
        }

        string ActionId(string module, string operation) =>
            $"{graph.Target.Id}:{graph.Configuration.ShortHash}:{module}:{FragmentRegistry.ToPascalCase(operation)}";

        string CopyActionId(string module, string runtime)
        {
            return ActionId(module,
                $"copy-{Path.GetFileNameWithoutExtension(runtime)}-{BuildPathLayout.StableToken(runtime, 8)}");
        }
    }

    private static ImmutableArray<ConfiguredModule> TopologicalOrder(ImmutableArray<ConfiguredModule> modules)
    {
        var byId = modules.ToDictionary(module => module.Id, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<ConfiguredModule>();
        foreach (var module in modules.OrderBy(module => module.Id, StringComparer.Ordinal))
        {
            Visit(module);
        }

        return result.ToImmutable();

        void Visit(ConfiguredModule module)
        {
            if (!visited.Add(module.Id))
            {
                return;
            }

            foreach (var dependency in module.Dependencies.OrderBy(dependency => dependency.Module,
                         StringComparer.Ordinal))
            {
                if (byId.TryGetValue(dependency.Module, out var dependencyModule))
                {
                    Visit(dependencyModule);
                }
            }

            result.Add(module);
        }
    }
}