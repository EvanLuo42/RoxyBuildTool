using System.Collections.Immutable;
using RoxyBuildTool.Configuration;
using RoxyBuildTool.Model;
using RoxyBuildTool.Toolchains;

namespace RoxyBuildTool.Graph;

/// <summary>Lowers a configured graph into structured native compiler, linker, archive, and copy actions.</summary>
public static class ActionGraphLowerer
{
    private static readonly LogicalPath WorkingDirectory = new(".");
    private static readonly ImmutableArray<string> CompileEnvironment = ["INCLUDE", "TMP", "TEMP"];
    private static readonly ImmutableArray<string> LibraryEnvironment = ["LIB", "TMP", "TEMP"];

    /// <summary>Creates the action graph for one configured target and toolchain.</summary>
    public static ActionGraph Lower(ConfiguredGraph graph, ToolchainDescriptor toolchain, string workspaceName)
    {
        var actions = ImmutableArray.CreateBuilder<BuildAction>();
        var artifacts = ImmutableArray.CreateBuilder<BuildArtifact>();
        var finalActions = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal);
        var outputRoot = BuildPathLayout.OutputRoot(graph.Configuration, graph.Target.Id);
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
            var objectActions = new List<string>(module.Sources.Length);
            var objectPaths = new List<string>(module.Sources.Length);
            var settings = module.CxxSettings ?? CxxModuleSettings.Empty;
            if (module.Kind == ModuleKind.HeaderOnly)
            {
                finalActions[module.Id] = [];
                return;
            }

            var pchSource = settings.PrecompiledSource;
            var pchHeader = settings.PrecompiledHeader;
            if (pchSource is not null && !module.Sources.Contains(pchSource.Value))
                throw new InvalidOperationException(
                    $"Module '{module.Id}' precompiled source '{pchSource}' is not part of its source set.");

            var pchPath = pchSource is null
                ? null
                : BuildPathLayout.PrecompiledHeaderFile(graph.Configuration, graph.Target.Id, module.Id);
            var pchActionId = pchSource is null
                ? null
                : ActionId(module.Id, $"compile-{BuildPathLayout.StableToken(pchSource.Value.Value)}");
            var includeArguments = module.CompileUsage.IncludeDirectories
                .Select(include => $"/I{include.Value}").ToImmutableArray();
            var systemIncludeArguments = SystemIncludes(module)
                .Select(include => $"/external:I{include.Value}").ToImmutableArray();
            var defineArguments = module.CompileUsage.Defines
                .Select(define => $"/D{define.Value}").ToImmutableArray();
            var forcedIncludeArguments = settings.ForcedIncludes
                .Select(include => $"/FI{include.Value}").ToImmutableArray();
            var forcedIncludeInputs = settings.ForcedIncludes.Select(include => include.Value).ToImmutableArray();
            var commonArgumentCount = 2 + policy.CompileArguments.Length + settings.CompilerArguments.Length +
                                      includeArguments.Length + systemIncludeArguments.Length +
                                      defineArguments.Length + forcedIncludeArguments.Length;
            var commonArgumentsBuilder = ImmutableArray.CreateBuilder<string>(commonArgumentCount);
            commonArgumentsBuilder.Add("/nologo");
            commonArgumentsBuilder.Add("/c");
            commonArgumentsBuilder.AddRange(policy.CompileArguments);
            commonArgumentsBuilder.AddRange(settings.CompilerArguments);
            commonArgumentsBuilder.AddRange(includeArguments);
            commonArgumentsBuilder.AddRange(systemIncludeArguments);
            commonArgumentsBuilder.AddRange(defineArguments);
            commonArgumentsBuilder.AddRange(forcedIncludeArguments);
            var commonArguments = commonArgumentsBuilder.MoveToImmutable();
            var pchCreateArguments = PchArguments("Yc");
            var pchUseArguments = PchArguments("Yu");

            var resourceArgumentsBuilder = ImmutableArray.CreateBuilder<string>();
            resourceArgumentsBuilder.Add("/nologo");
            resourceArgumentsBuilder.AddRange(policy.CompileArguments
                .Where(argument => argument.StartsWith("/D", StringComparison.OrdinalIgnoreCase))
                .Select(argument => $"/d{argument[2..]}"));
            resourceArgumentsBuilder.AddRange(includeArguments);
            resourceArgumentsBuilder.AddRange(module.CompileUsage.Defines.Select(define => $"/d{define.Value}"));
            var commonResourceArguments = resourceArgumentsBuilder.ToImmutable();

            foreach (var source in module.Sources.Where(source => BuildFileKinds.IsCxxSource(source.Value)))
            {
                var sourceToken = BuildPathLayout.StableToken(source.Value);
                var objectPath = BuildPathLayout.ObjectFile(
                    graph.Configuration, graph.Target.Id, module.Id, source, sourceToken);
                var actionId = ActionId(module.Id, $"compile-{sourceToken}");
                var createsPch = pchSource is not null && source == pchSource.Value;
                var sharedArguments = pchPath is null || pchHeader is null
                    ? commonArguments
                    : createsPch
                        ? pchCreateArguments
                        : pchUseArguments;
                var actionArguments = ImmutableArray.Create(source.Value, $"/Fo{objectPath}");
                var inputs = new List<string>(1 + forcedIncludeInputs.Length + (pchHeader is null ? 0 : 1))
                {
                    source.Value
                };
                inputs.AddRange(forcedIncludeInputs);
                if (pchHeader is not null) inputs.Add(pchHeader.Value.Value);
                var compileOutputs = new List<string> { objectPath };
                if (createsPch) compileOutputs.Add(pchPath!);
                actions.Add(new BuildAction(
                    actionId,
                    BuildActionKind.Compile,
                    toolchain.Compiler,
                    new ActionArgumentSequence(sharedArguments, actionArguments),
                    WorkingDirectory,
                    [.. inputs.Distinct(StringComparer.Ordinal)],
                    [.. compileOutputs],
                    createsPch || pchActionId is null ? [] : [pchActionId],
                    CompileEnvironment,
                    false,
                    false,
                    []));
                artifacts.Add(new BuildArtifact($"{module.Id}:object:{sourceToken}", ArtifactKind.ObjectFile,
                    new LogicalPath(objectPath), actionId));
                if (createsPch)
                    artifacts.Add(new BuildArtifact($"{module.Id}:pch", ArtifactKind.PrecompiledHeader,
                        new LogicalPath(pchPath!), actionId));
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
                    graph.Configuration, graph.Target.Id, module.Id, source, sourceToken);
                var actionId = ActionId(module.Id, $"resource-{sourceToken}");
                var actionArguments = ImmutableArray.Create($"/fo{resourcePath}", source.Value);
                actions.Add(new BuildAction(
                    actionId,
                    BuildActionKind.ResourceCompile,
                    resourceCompiler,
                    new ActionArgumentSequence(commonResourceArguments, actionArguments),
                    WorkingDirectory,
                    [source.Value],
                    [resourcePath],
                    [],
                    CompileEnvironment,
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
                var outputName = settings.OutputName ?? module.Id;
                var libraryOutput = $"{outputRoot}/{outputName}.lib";
                var objectInputs = objectPaths.ToImmutableArray();
                actions.Add(new BuildAction(finalActionId, BuildActionKind.Archive, toolchain.Librarian,
                    new ActionArgumentSequence(
                        ["/nologo", .. settings.LibrarianArguments, $"/OUT:{libraryOutput}"], objectInputs),
                    WorkingDirectory, objectInputs,
                    [libraryOutput],
                    dependencies, LibraryEnvironment, true, false, []));
                artifacts.Add(new BuildArtifact($"{module.Id}:StaticLibrary", ArtifactKind.StaticLibrary,
                    new LogicalPath(libraryOutput), finalActionId));
                finalActions[module.Id] = [finalActionId];
                return;
            }

            var extension = module.Kind == ModuleKind.SharedLibrary ? "dll" : "exe";
            var binaryName = settings.OutputName ?? module.Id;
            var output = $"{outputRoot}/{binaryName}.{extension}";
            var linkArguments = new List<string> { "/NOLOGO", $"/OUT:{output}" };
            var outputs = new List<string> { output };
            if (module.Kind == ModuleKind.SharedLibrary)
            {
                var importLibrary = $"{outputRoot}/{binaryName}.lib";
                linkArguments.Add("/DLL");
                linkArguments.Add($"/IMPLIB:{importLibrary}");
                outputs.Add(importLibrary);
            }

            linkArguments.AddRange(policy.LinkArguments);
            linkArguments.AddRange(settings.LinkerArguments);
            var linkInputs = objectPaths.Concat(module.CompileUsage.LinkInputs.Select(input => input.Value))
                .ToImmutableArray();
            actions.Add(new BuildAction(finalActionId, BuildActionKind.Link, toolchain.Linker,
                new ActionArgumentSequence([.. linkArguments], linkInputs), WorkingDirectory,
                linkInputs,
                [.. outputs], dependencies, LibraryEnvironment, true, false, []));
            artifacts.Add(new BuildArtifact($"{module.Id}:{extension}",
                module.Kind == ModuleKind.SharedLibrary ? ArtifactKind.SharedLibrary : ArtifactKind.Executable,
                new LogicalPath(output), finalActionId));
            if (module.Kind == ModuleKind.SharedLibrary)
                artifacts.Add(new BuildArtifact($"{module.Id}:ImportLibrary", ArtifactKind.ImportLibrary,
                    new LogicalPath($"{outputRoot}/{binaryName}.lib"), finalActionId));

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
                        ["/Y", runtime.Value, destination], WorkingDirectory, [runtime.Value], [destination],
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

            ImmutableArray<string> PchArguments(string mode)
            {
                if (pchPath is null || pchHeader is null) return commonArguments;

                return [.. commonArguments, $"/{mode}{pchHeader.Value.Value}", $"/Fp{pchPath}"];
            }
        }

        static ImmutableArray<UsageValue> SystemIncludes(ConfiguredModule module)
        {
            return module.CompileUsage.SystemIncludeDirectories.IsDefault
                ? []
                : module.CompileUsage.SystemIncludeDirectories;
        }

        void LowerCSharp(ConfiguredModule module)
        {
            // Managed projects are lowered by their selected project-system backend. Keeping generated
            // project paths out of the core action graph prevents the graph from depending on Vs2022.
            finalActions[module.Id] = [];
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