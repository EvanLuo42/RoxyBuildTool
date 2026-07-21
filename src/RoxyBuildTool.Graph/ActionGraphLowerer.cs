using System.Collections.Immutable;
using RoxyBuildTool.Configuration;
using RoxyBuildTool.Model;
using RoxyBuildTool.Toolchains;

namespace RoxyBuildTool.Graph;

public static class ActionGraphLowerer
{
    public static ActionGraph Lower(ConfiguredGraph graph, ToolchainDescriptor toolchain, string workspaceName)
    {
        var actions = ImmutableArray.CreateBuilder<BuildAction>();
        var artifacts = ImmutableArray.CreateBuilder<BuildArtifact>();
        var finalActions = new Dictionary<string, string>(StringComparer.Ordinal);
        var platform = Value(graph.Configuration, "Platform");
        var architecture = Value(graph.Configuration, "Architecture");
        var profile = Value(graph.Configuration, "Profile");
        var outputRoot = $"out/{platform.ToLowerInvariant()}/{architecture.ToLowerInvariant()}/{profile.ToLowerInvariant()}/{graph.Target.Id}";
        var intermediateRoot = $"intermediate/{graph.Configuration.ShortHash}/{graph.Target.Id}";
        var policy = toolchain.GetPolicy(graph.Configuration);

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

        var result = new ActionGraph(
            graph.Configuration,
            graph.Target.Id,
            actions.OrderBy(action => action.Id, StringComparer.Ordinal).ToImmutableArray(),
            artifacts.OrderBy(artifact => artifact.Id, StringComparer.Ordinal).ToImmutableArray());
        return result;

        void LowerCxx(ConfiguredModule module)
        {
            var objectActions = new List<string>();
            var objectPaths = new List<string>();
            for (var index = 0; index < module.Sources.Length; index++)
            {
                var source = module.Sources[index];
                var objectPath = $"{intermediateRoot}/{module.Id}/{index:D4}-{Path.GetFileNameWithoutExtension(source.Value)}.obj";
                var actionId = ActionId(module.Id, $"compile-{index:D4}");
                var arguments = new List<string> { "/nologo", "/c", "/EHsc", "/std:c++latest" };
                arguments.AddRange(policy.CompileArguments);
                arguments.AddRange(module.CompileUsage.IncludeDirectories.Select(include => $"/I{include.Value}"));
                arguments.AddRange(module.CompileUsage.Defines.Select(define => $"/D{define.Value}"));
                arguments.Add(source.Value);
                arguments.Add($"/Fo{objectPath}");
                actions.Add(new(
                    actionId,
                    BuildActionKind.Compile,
                    toolchain.Compiler,
                    arguments.ToImmutableArray(),
                    new("."),
                    [source.Value],
                    [objectPath],
                    DependencyActions(module),
                    ["INCLUDE", "TMP", "TEMP"],
                    true,
                    true,
                    []));
                artifacts.Add(new($"{module.Id}:object:{index:D4}", ArtifactKind.ObjectFile, new(objectPath), actionId));
                objectActions.Add(actionId);
                objectPaths.Add(objectPath);
            }

            if (module.Kind is ModuleKind.HeaderOnly or ModuleKind.ObjectLibrary)
            {
                finalActions[module.Id] = objectActions.LastOrDefault() ?? string.Empty;
                return;
            }

            var finalActionId = ActionId(module.Id, module.Kind == ModuleKind.StaticLibrary ? "archive" : "link");
            var dependencies = objectActions.Concat(DependencyActions(module)).Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
            if (module.Kind == ModuleKind.StaticLibrary)
            {
                var output = $"{outputRoot}/{module.Id}.lib";
                actions.Add(new(finalActionId, BuildActionKind.Archive, toolchain.Librarian,
                    ["/nologo", $"/OUT:{output}", .. objectPaths], new("."), [.. objectPaths], [output], dependencies,
                    ["LIB", "TMP", "TEMP"], true, false, []));
                artifacts.Add(new($"{module.Id}:StaticLibrary", ArtifactKind.StaticLibrary, new(output), finalActionId));
            }
            else
            {
                var extension = module.Kind == ModuleKind.SharedLibrary ? "dll" : "exe";
                var output = $"{outputRoot}/{module.Id}.{extension}";
                var arguments = new List<string> { "/NOLOGO", $"/OUT:{output}" };
                if (module.Kind == ModuleKind.SharedLibrary)
                {
                    arguments.Add("/DLL");
                    arguments.Add($"/IMPLIB:{outputRoot}/{module.Id}.lib");
                }
                arguments.AddRange(policy.LinkArguments);
                arguments.AddRange(objectPaths);
                arguments.AddRange(module.CompileUsage.LinkInputs.Select(input => input.Value));
                actions.Add(new(finalActionId, BuildActionKind.Link, toolchain.Linker,
                    arguments.ToImmutableArray(), new("."),
                    [.. objectPaths, .. module.CompileUsage.LinkInputs.Select(input => input.Value)],
                    [output], dependencies, ["LIB", "TMP", "TEMP"], true, false, []));
                artifacts.Add(new($"{module.Id}:{extension}",
                    module.Kind == ModuleKind.SharedLibrary ? ArtifactKind.SharedLibrary : ArtifactKind.Executable,
                    new(output), finalActionId));

                if (module.Kind == ModuleKind.Executable)
                {
                    foreach (var runtime in module.CompileUsage.RuntimeFiles.OrderBy(value => value.Value, StringComparer.Ordinal))
                    {
                        var copyId = ActionId(module.Id, $"copy-{Path.GetFileNameWithoutExtension(runtime.Value)}");
                        var destination = $"{outputRoot}/{Path.GetFileName(runtime.Value)}";
                        if (string.Equals(runtime.Value, destination, StringComparison.Ordinal))
                        {
                            continue;
                        }
                        actions.Add(new(copyId, BuildActionKind.Copy, "copy",
                            ["/Y", runtime.Value, destination], new("."), [runtime.Value], [destination],
                            [finalActionId, .. DependencyActions(module)], [], true, false, []));
                        artifacts.Add(new($"{module.Id}:runtime:{Path.GetFileName(runtime.Value)}",
                            ArtifactKind.RuntimeFile, new(destination), copyId));
                    }
                }
            }

            finalActions[module.Id] = finalActionId;
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
            var project = $".roxy/generated/vs2022/{workspaceName}/{projectName}.csproj";
            var restoreId = ActionId(module.Id, "DotnetRestore");
            var buildId = ActionId(module.Id, "DotnetBuild");
            var dependencies = DependencyActions(module);
            actions.Add(new(restoreId, BuildActionKind.DotNetRestore, "dotnet",
                ["restore", project, "--locked-mode"], new("."), [project], [$"{intermediateRoot}/{module.Id}/restore.stamp"],
                dependencies, ["NUGET_PACKAGES", "NUGET_HTTP_CACHE_PATH", "TMP", "TEMP"], true, false, []));
            var assembly = $"{outputRoot}/{module.Id}/{module.Id}.dll";
            actions.Add(new(buildId, BuildActionKind.DotNetBuild, "dotnet",
                ["build", project, "--no-restore", "--configuration", profile, $"-p:RoxyConfigurationHash={graph.Configuration.ShortHash}"],
                new("."), [project, .. module.Sources.Select(source => source.Value)], [assembly],
                [restoreId, .. dependencies], ["DOTNET_ROOT", "NUGET_PACKAGES", "TMP", "TEMP"], true, false, []));
            artifacts.Add(new($"{module.Id}:ManagedAssembly", ArtifactKind.ManagedAssembly, new(assembly), buildId));
            var finalAction = buildId;
            foreach (var runtime in module.CompileUsage.RuntimeFiles.OrderBy(value => value.Value, StringComparer.Ordinal))
            {
                var copyId = ActionId(module.Id, $"copy-{Path.GetFileNameWithoutExtension(runtime.Value)}");
                var destination = $"{outputRoot}/{module.Id}/{Path.GetFileName(runtime.Value)}";
                actions.Add(new(copyId, BuildActionKind.Copy, "copy",
                    ["/Y", runtime.Value, destination], new("."), [runtime.Value], [destination],
                    [buildId, .. dependencies], [], true, false, []));
                artifacts.Add(new($"{module.Id}:runtime:{Path.GetFileName(runtime.Value)}",
                    ArtifactKind.RuntimeFile, new(destination), copyId));
                finalAction = copyId;
            }
            finalActions[module.Id] = finalAction;
        }

        ImmutableArray<string> DependencyActions(ConfiguredModule module) => module.Dependencies
            .Select(dependency => finalActions.GetValueOrDefault(dependency.Module))
            .Where(action => !string.IsNullOrEmpty(action))
            .Select(action => action!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();

        string ActionId(string module, string operation) =>
            $"{graph.Target.Id}:{graph.Configuration.ShortHash}:{module}:{FragmentRegistry.ToPascalCase(operation)}";
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

            foreach (var dependency in module.Dependencies.OrderBy(dependency => dependency.Module, StringComparer.Ordinal))
            {
                if (byId.TryGetValue(dependency.Module, out var dependencyModule))
                {
                    Visit(dependencyModule);
                }
            }

            result.Add(module);
        }
    }

    private static string Value(ConfigurationKey configuration, string fragment) =>
        configuration.Values.Single(value => value.Fragment.Value == fragment).Value;
}
