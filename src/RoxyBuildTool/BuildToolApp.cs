using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.CommandLine;
using RoxyBuildTool.Configuration;
using RoxyBuildTool.Graph;
using RoxyBuildTool.Model;
using RoxyBuildTool.Toolchains;

namespace RoxyBuildTool;

/// <summary>Provides IDs for built-in workspace generators.</summary>
public static class WorkspaceGenerators
{
    public static WorkspaceGeneratorId VisualStudio2022 { get; } = new("Vs2022");
    public static WorkspaceGeneratorId CompilationDatabase { get; } = new("CompileDb");
}

/// <summary>Configures the default workspace generation request.</summary>
public sealed class GenerateRequestBuilder
{
    private readonly List<string> _generators = [];
    private readonly Dictionary<FragmentId, string> _selectors = [];

    internal string? WorkspaceId { get; set; }
    internal ImmutableArray<string> Generators => _generators.Distinct(StringComparer.Ordinal).ToImmutableArray();
    internal ImmutableDictionary<FragmentId, string> Selectors => _selectors.ToImmutableDictionary();

    /// <summary>Selects the workspace generators used by the request.</summary>
    public GenerateRequestBuilder Workspace(params WorkspaceGeneratorId[] generators)
    {
        _generators.AddRange(generators.Select(generator => generator.Value));
        return this;
    }

    /// <summary>Adds a built-in or encoded fragment selector.</summary>
    public GenerateRequestBuilder Select(FragmentValue value)
    {
        _selectors[value.Fragment] = value.Value;
        return this;
    }

    /// <summary>Adds a selector from an enum-backed fragment value.</summary>
    public GenerateRequestBuilder Select<T>(T value) where T : struct, Enum => Select(FragmentEncoding.Encode(value));
}

/// <summary>Coordinates rule discovery, plugin composition, commands, generation, and delegated builds.</summary>
public sealed class BuildToolApp : IBuildToolBuilder, IPluginRegistry
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private readonly string[] _args;
    private readonly GenerateRequestBuilder _defaultGenerate = new();
    private readonly List<IPlugin> _plugins = [];
    private readonly List<Assembly> _ruleAssemblies = [];
    private readonly List<Type> _rulesModules = [];
    private readonly Dictionary<Type, List<object>> _services = [];
    private TextWriter _error = Console.Error;
    private string? _msbuildPath;
    private TextWriter _output = Console.Out;
    private string _workspaceRoot;

    private BuildToolApp(string[] args)
    {
        _args = args;
        _workspaceRoot = Directory.GetCurrentDirectory();
    }

    /// <inheritdoc />
    public void AddPlugin(IPlugin plugin)
    {
        if (_plugins.Any(existing => existing.Id == plugin.Id))
        {
            throw new InvalidOperationException($"Plugin '{plugin.Id}' is already registered.");
        }

        _plugins.Add(plugin);
    }

    /// <inheritdoc />
    public void AddService<T>(T service) where T : class
    {
        var type = typeof(T);
        if (!_services.TryGetValue(type, out var services))
        {
            services = [];
            _services.Add(type, services);
        }

        services.Add(service);
    }

    /// <summary>Creates an application for the supplied command-line arguments.</summary>
    public static BuildToolApp Create(string[] args) => new(args);

    /// <summary>Sets the root used to resolve logical source and output paths.</summary>
    public BuildToolApp WithWorkspaceRoot(string path)
    {
        _workspaceRoot = Path.GetFullPath(path, Directory.GetCurrentDirectory());
        return this;
    }

    /// <summary>Redirects standard output and optional error output.</summary>
    public BuildToolApp WithOutput(TextWriter output, TextWriter? error = null)
    {
        _output = output;
        _error = error ?? output;
        return this;
    }

    /// <summary>Selects the full MSBuild executable or assembly used by the build command.</summary>
    public BuildToolApp WithMsBuild(string path)
    {
        _msbuildPath = Path.GetFullPath(path, Directory.GetCurrentDirectory());
        return this;
    }

    /// <summary>Adds an explicit rules registration module.</summary>
    public BuildToolApp AddRules<T>() where T : IRulesModule, new()
    {
        _rulesModules.Add(typeof(T));
        return this;
    }

    /// <summary>Discovers rule types from the assembly containing <typeparamref name="T"/>.</summary>
    public BuildToolApp DiscoverRulesFromAssemblyContaining<T>()
    {
        var assembly = typeof(T).Assembly;
        if (!_ruleAssemblies.Contains(assembly))
        {
            _ruleAssemblies.Add(assembly);
        }

        return this;
    }

    /// <summary>Configures the generation request used when no command-line arguments are supplied.</summary>
    public BuildToolApp DefaultGenerate<TWorkspace>(Action<GenerateRequestBuilder> configure)
        where TWorkspace : BuildWorkspace
    {
        _defaultGenerate.WorkspaceId = BuildRegistry.DefinitionId(typeof(TWorkspace));
        configure(_defaultGenerate);
        return this;
    }

    /// <summary>Executes the selected command and returns a process-compatible exit code.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var plugin in _plugins.OrderBy(plugin => plugin.Id))
            {
                plugin.Register(this);
            }

            var registry = new BuildRegistry(_workspaceRoot);
            foreach (var assembly in _ruleAssemblies.OrderBy(assembly => assembly.FullName, StringComparer.Ordinal))
            {
                registry.ScanAssembly(assembly);
            }

            foreach (var rulesType in _rulesModules.OrderBy(type => type.FullName, StringComparer.Ordinal))
            {
                ((IRulesModule)Activator.CreateInstance(rulesType)!).Register(registry);
            }

            var definitions = registry.Build();
            var defaultRequest = new CommandRequest(
                CommandKind.Generate,
                _defaultGenerate.WorkspaceId,
                _defaultGenerate.Generators.IsEmpty ? ["Vs2022"] : _defaultGenerate.Generators,
                _defaultGenerate.Selectors,
                null,
                false,
                "dot");
            var request = CommandLineParser.Parse(_args, defaultRequest);

            PrintRegistrationSummary(definitions);
            return request.Kind switch
            {
                CommandKind.Generate => await GenerateAsync(definitions, request, cancellationToken),
                CommandKind.Build => await BuildAsync(definitions, request, cancellationToken),
                CommandKind.QueryMatrix => QueryMatrix(definitions, request),
                CommandKind.QueryGraph => QueryGraph(definitions, request),
                CommandKind.Explain => Explain(definitions, request),
                _ => throw new InvalidOperationException($"Unsupported command '{request.Kind}'."),
            };
        }
        catch (FragmentException exception)
        {
            await _error.WriteLineAsync($"{exception.Diagnostic.Code}: {exception.Message}");
            return 2;
        }
        catch (RuleDefinitionException exception)
        {
            await _error.WriteLineAsync($"{exception.Diagnostic.Code}: {exception.Message}");
            return 2;
        }
        catch (CommandLineException exception)
        {
            await _error.WriteLineAsync($"RBT0001: {exception.Message}");
            return 2;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _error.WriteLineAsync($"RBT0000: {exception.Message}");
            return 1;
        }
    }

    private async Task<int> GenerateAsync(
        DefinitionGraph definitions,
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        var workspace = ResolveWorkspace(definitions, request.Subject ?? _defaultGenerate.WorkspaceId);
        var generated = await ResolveWorkspaceModelAsync(
            definitions, workspace, request.Selectors, cancellationToken);
        var pipelineDiagnostics = ValidateWorkspaceModel(generated);
        await WriteDiagnosticsAsync(pipelineDiagnostics);
        if (pipelineDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return 2;
        }

        var generationResults = new List<(GenerationResult Result, LogicalPath OutputDirectory)>();
        foreach (var generatorId in request.WorkspaceGenerators)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generator = Services<IWorkspaceGenerator>().SingleOrDefault(service => service.Id.Value == generatorId)
                            ?? throw new InvalidOperationException(
                                $"Workspace generator '{generatorId}' is not registered.");
            var outputDirectory = new LogicalPath($".roxy/generated/{generator.Id}/{workspace.Id}");
            var result = generator.Generate(generated, new GenerationContext(_workspaceRoot, outputDirectory));
            generationResults.Add((result, outputDirectory));
        }

        var generatorDiagnostics = generationResults.SelectMany(item => item.Result.Diagnostics)
            .Concat(generationResults.SelectMany(item => item.Result.Files.Select(file =>
                    (Output: $"{item.OutputDirectory.Value}/{file.Path.Value}", item.Result.Generator)))
                .GroupBy(item => item.Output, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => new Diagnostic("RBT4001", DiagnosticSeverity.Error,
                    $"Generated path '{group.Key}' is emitted {group.Count()} times.")))
            .ToImmutableArray();
        await WriteDiagnosticsAsync(generatorDiagnostics);
        if (generatorDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return 2;
        }

        foreach (var (result, outputDirectory) in generationResults)
        {
            foreach (var file in result.Files)
            {
                var absolutePath = Path.Combine(_workspaceRoot, outputDirectory.Value, file.Path.Value);
                var changed = CompareBeforeWrite.Write(absolutePath, file.Content);
                await _output.WriteLineAsync(
                    $"{(changed ? "write" : "unchanged")} {Path.GetRelativePath(_workspaceRoot, absolutePath)}");
            }

            foreach (var removed in GeneratedOutputOwnership.Update(
                         _workspaceRoot,
                         outputDirectory,
                         result.Files.Select(file => file.Path)))
            {
                await _output.WriteLineAsync($"remove {removed}");
            }
        }

        await WriteManifestAsync(workspace, generated, request.WorkspaceGenerators, cancellationToken);
        return 0;
    }

    private async Task<int> BuildAsync(
        DefinitionGraph definitions,
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        var target = ResolveTarget(definitions, request.Subject);
        var resolution = new MatrixResolver(new()).Resolve(target.Matrix, request.Selectors);
        if (resolution.Configurations.Length != 1)
        {
            throw new InvalidOperationException(
                $"Build requires exactly one configuration; selectors matched {resolution.Configurations.Length}. Add --profile and custom fragment selectors.");
        }

        var configuration = resolution.Configurations[0];
        var workspace =
            definitions.Workspaces.FirstOrDefault(candidate =>
                candidate.Targets.Contains(target.Id, StringComparer.Ordinal))
            ?? throw new InvalidOperationException($"No workspace contains target '{target.Id}'.");
        var scopedWorkspace = workspace with
        {
            Id = $"{workspace.Id}-build-{target.Id}-{configuration.ShortHash}",
            DisplayName = $"{workspace.DisplayName}.{target.DisplayName}.Build",
            Targets = [target.Id],
            StartupTarget = target.Id,
            IncludeBuildHost = false,
            BuildHostProject = null,
        };
        var buildRequest = request with
        {
            Kind = CommandKind.Generate,
            Subject = scopedWorkspace.Id,
            WorkspaceGenerators = ["Vs2022"],
        };
        var scopedDefinitions = definitions with
        {
            Workspaces = definitions.Workspaces.Add(scopedWorkspace),
        };
        var generateResult = await GenerateAsync(scopedDefinitions, buildRequest, cancellationToken);
        if (generateResult != 0)
        {
            return generateResult;
        }

        var configurationName = BuildConfigurationNames.DisplayName(configuration);
        var solution = Path.Combine(
            _workspaceRoot,
            ".roxy",
            "generated",
            "Vs2022",
            scopedWorkspace.Id,
            $"{scopedWorkspace.DisplayName}.sln");
        var msbuild = LocateMsBuild();
        var startInfo = CreateBuildProcess(msbuild.Executable, [.. msbuild.PrefixArguments, solution]);
        startInfo.ArgumentList.Add("/m");
        startInfo.ArgumentList.Add("/restore");
        startInfo.ArgumentList.Add("/t:Build");
        startInfo.ArgumentList.Add($"/p:Configuration={configurationName}");
        startInfo.ArgumentList.Add("/p:Platform=Win64");
        startInfo.ArgumentList.Add("/verbosity:minimal");
        return await RunProcessAsync(startInfo, cancellationToken);
    }

    private int QueryMatrix(DefinitionGraph definitions, CommandRequest request)
    {
        var target = ResolveTarget(definitions, request.Subject);
        var resolution = new MatrixResolver(new()).Resolve(target.Matrix, request.Selectors);
        foreach (var configuration in resolution.Configurations)
        {
            _output.WriteLine(configuration.Canonical);
        }

        if (request.WhyExcluded)
        {
            foreach (var excluded in resolution.Excluded.OrderBy(item => item.AssignedPrefix, StringComparer.Ordinal))
            {
                _output.WriteLine($"excluded {excluded.AssignedPrefix}: {excluded.Reason}");
            }
        }

        _output.WriteLine(
            $"{resolution.Configurations.Length} configurations; {resolution.CandidateCount} candidates visited");
        return 0;
    }

    private int QueryGraph(DefinitionGraph definitions, CommandRequest request)
    {
        var target = ResolveTarget(definitions, request.Subject);
        var configuration = SingleOrFirstConfiguration(target, request.Selectors);
        var graph = DependencyResolver.Resolve(definitions, target, configuration);
        if (request.Format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            _output.WriteLine(JsonSerializer.Serialize(graph, IndentedJson));
        }
        else
        {
            _output.WriteLine("digraph roxy {");
            foreach (var module in graph.Modules)
            {
                _output.WriteLine($"  \"{module.Id}\";");
                foreach (var dependency in module.Dependencies)
                {
                    _output.WriteLine(
                        $"  \"{module.Id}\" -> \"{dependency.Module}\" [label=\"{dependency.Visibility.ToString().ToLowerInvariant()}\"];");
                }
            }

            _output.WriteLine("}");
        }

        return graph.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) ? 2 : 0;
    }

    private int Explain(DefinitionGraph definitions, CommandRequest request)
    {
        var target = ResolveTarget(definitions, request.Subject);
        var configuration = SingleOrFirstConfiguration(target, request.Selectors);
        var graph = DependencyResolver.Resolve(definitions, target, configuration);
        var setting = request.Setting ?? "usage";
        _output.WriteLine($"{target.Id} {configuration.Canonical}");
        if (setting == "Compiler.Optimization")
        {
            var profile = configuration.Values.Single(value => value.Fragment.Value == "Profile").Value;
            _output.WriteLine($"Compiler.Optimization = {(profile == "Debug" ? "off" : "speed")} (Profile:{profile})");
            return 0;
        }

        foreach (var module in graph.Modules)
        {
            _output.WriteLine($"[{module.Id}]");
            WriteUsage("include", module.CompileUsage.IncludeDirectories);
            WriteUsage("define", module.CompileUsage.Defines);
            WriteUsage("link", module.CompileUsage.LinkInputs);
            WriteUsage("runtime", module.CompileUsage.RuntimeFiles);
        }

        return 0;

        void WriteUsage(string kind, ImmutableArray<UsageValue> values)
        {
            foreach (var value in values)
            {
                _output.WriteLine($"  {kind} {value.Value} <- {value.Origin}");
            }
        }
    }

    private async Task<WorkspaceModel> ResolveWorkspaceModelAsync(
        DefinitionGraph definitions,
        WorkspaceDefinition workspace,
        IReadOnlyDictionary<FragmentId, string> selectors,
        CancellationToken cancellationToken)
    {
        var work = workspace.Targets.SelectMany(targetId =>
        {
            var target = definitions.GetTarget(targetId);
            var matrix = new MatrixResolver(new FragmentRegistry()).Resolve(target.Matrix, selectors);
            return matrix.Configurations.Select(configuration => (Target: target, Configuration: configuration));
        }).ToImmutableArray();
        var resolved = await Task.WhenAll(work.Select(item => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = item.Target;
            var configuration = item.Configuration;
            var configured = DependencyResolver.Resolve(definitions, target, configuration);
            var actionGraph = configured.Diagnostics.Any(diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error)
                ? null
                : ActionGraphLowerer.Lower(configured, ResolveToolchain(configuration), workspace.Id);
            return (Configured: configured, Actions: actionGraph);
        }, cancellationToken)));
        return WorkspaceAssembler.Assemble(
            workspace,
            resolved.Select(item => item.Configured),
            resolved.Where(item => item.Actions is not null).Select(item => item.Actions!));
    }

    private ToolchainDescriptor ResolveToolchain(ConfigurationKey configuration)
    {
        var platformValue = Fragment(configuration, "Platform");
        var architectureValue = Fragment(configuration, "Architecture");
        var toolchainValue = Fragment(configuration, "Toolchain");
        var linkModelValue = Fragment(configuration, "LinkModel");
        var platform = Services<PlatformDescriptor>()
                           .SingleOrDefault(service => service.Id.Value == platformValue)
                       ?? throw Unsupported($"Platform '{platformValue}' is not registered by a platform plugin.");
        var toolchain = Services<ToolchainDescriptor>()
                            .SingleOrDefault(service => service.Id.Value == toolchainValue)
                        ?? throw Unsupported($"Toolchain '{toolchainValue}' is not registered by a platform plugin.");
        if (!platform.Architectures.Contains(architectureValue, StringComparer.Ordinal) ||
            !platform.Toolchains.Contains(toolchain.Id) ||
            toolchain.Platform != platform.Id ||
            !toolchain.Architecture.Equals(architectureValue, StringComparison.Ordinal))
        {
            throw Unsupported(
                $"Configuration '{configuration}' combines incompatible platform, architecture, and toolchain values.");
        }

        if (!toolchain.Capabilities.Contains($"LinkModel.{linkModelValue}"))
        {
            throw Unsupported($"Toolchain '{toolchain.Id}' does not support link model '{linkModelValue}'.");
        }

        return toolchain;

        static FragmentException Unsupported(string message) =>
            new(new Diagnostic("RBT1104", DiagnosticSeverity.Error, message));
    }

    private static ImmutableArray<Diagnostic> ValidateWorkspaceModel(WorkspaceModel model)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(model.ConfiguredGraphs.SelectMany(graph => graph.Diagnostics));
        diagnostics.AddRange(model.ActionGraphs.SelectMany(graph => graph.Validate()));
        foreach (var duplicate in model.ActionGraphs
                     .SelectMany(graph => graph.Actions.SelectMany(action => action.Outputs.Select(output =>
                         (Output: output, Action: action.Id))))
                     .GroupBy(item => item.Output, StringComparer.Ordinal)
                     .Where(group => group.Select(item => item.Action).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            diagnostics.Add(new Diagnostic("RBT3007", DiagnosticSeverity.Error,
                $"Workspace output '{duplicate.Key}' has multiple producers: " +
                $"{string.Join(", ", duplicate.Select(item => item.Action).Distinct(StringComparer.Ordinal))}."));
        }

        return diagnostics.ToImmutable();
    }

    private async Task WriteDiagnosticsAsync(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics.OrderBy(item => item.Code, StringComparer.Ordinal)
                     .ThenBy(item => item.Definition, StringComparer.Ordinal))
        {
            await _error.WriteLineAsync($"{diagnostic.Code}: {diagnostic.Message}");
        }
    }

    private static string Fragment(ConfigurationKey configuration, string fragment) =>
        configuration.Values.Single(value => value.Fragment.Value == fragment).Value;

    private async Task WriteManifestAsync(
        WorkspaceDefinition workspace,
        WorkspaceModel model,
        ImmutableArray<string> generators,
        CancellationToken cancellationToken)
    {
        var identity = string.Join('|', workspace.Id,
            string.Join(',', generators.Order(StringComparer.Ordinal)),
            string.Join(',',
                model.ConfiguredGraphs.Select(graph => graph.Configuration.Canonical).Order(StringComparer.Ordinal)),
            string.Join(',', _plugins.OrderBy(plugin => plugin.Id).Select(plugin => $"{plugin.Id}@{plugin.Version}")));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
        var manifest = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            workspace = workspace.Id,
            requestHash = hash,
            generators = generators.Order(StringComparer.Ordinal),
            configurations = model.ConfiguredGraphs.Select(graph => graph.Configuration.Canonical).Distinct()
                .Order(StringComparer.Ordinal),
            actions = model.ActionGraphs.SelectMany(graph => graph.Actions)
                .Select(action => new { action.Id, action.SemanticHash }).OrderBy(action => action.Id),
            plugins = _plugins.OrderBy(plugin => plugin.Id)
                .Select(plugin => new { id = plugin.Id.Value, version = plugin.Version.ToString() }),
        }, IndentedJson).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        var path = Path.Combine(_workspaceRoot, ".roxy", "manifests", $"{hash}.json");
        CompareBeforeWrite.Write(path, manifest);
        await _output.WriteLineAsync($"manifest {Path.GetRelativePath(_workspaceRoot, path)}");
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void PrintRegistrationSummary(DefinitionGraph definitions)
    {
        _output.WriteLine(
            $"definitions modules=[{string.Join(',', definitions.Modules.Select(module => module.Id))}] targets=[{string.Join(',', definitions.Targets.Select(target => target.Id))}] workspaces=[{string.Join(',', definitions.Workspaces.Select(workspace => workspace.Id))}]");
        var fragments = definitions.Targets.SelectMany(target => target.Matrix.Axes).Select(axis => axis.Fragment.Value)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        _output.WriteLine($"fragments [{string.Join(',', fragments)}]");
        _output.WriteLine(
            $"plugins [{string.Join(',', _plugins.OrderBy(plugin => plugin.Id).Select(plugin => plugin.Id.Value))}]");
        _output.WriteLine(
            $"capabilities [{string.Join(',', _plugins.SelectMany(plugin => plugin.Capabilities.Values).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))}]");
    }

    private IEnumerable<T> Services<T>() where T : class =>
        _services.GetValueOrDefault(typeof(T), []).Cast<T>();

    private static WorkspaceDefinition ResolveWorkspace(DefinitionGraph definitions, string? subject)
    {
        if (subject is null)
        {
            throw new CommandLineException("No workspace was specified and no default generate request is configured.");
        }

        return definitions.Workspaces.SingleOrDefault(workspace =>
                   workspace.Id.Equals(subject, StringComparison.OrdinalIgnoreCase) ||
                   workspace.DisplayName.Equals(subject, StringComparison.OrdinalIgnoreCase))
               ?? throw new CommandLineException($"Unknown workspace '{subject}'.");
    }

    private TargetDefinition ResolveTarget(DefinitionGraph definitions, string? subject)
    {
        if (subject is null && _defaultGenerate.WorkspaceId is not null)
        {
            var workspace = definitions.GetWorkspace(_defaultGenerate.WorkspaceId);
            return definitions.GetTarget(workspace.StartupTarget);
        }

        if (subject is null)
        {
            throw new CommandLineException("A target is required.");
        }

        return definitions.Targets.SingleOrDefault(target =>
                   target.Id.Equals(subject, StringComparison.OrdinalIgnoreCase) ||
                   target.DisplayName.Equals(subject, StringComparison.OrdinalIgnoreCase))
               ?? throw new CommandLineException($"Unknown target '{subject}'.");
    }

    private static ConfigurationKey SingleOrFirstConfiguration(
        TargetDefinition target,
        IReadOnlyDictionary<FragmentId, string> selectors)
    {
        var resolution = new MatrixResolver(new()).Resolve(target.Matrix, selectors);
        return resolution.Configurations.FirstOrDefault()
               ?? throw new InvalidOperationException($"No configuration matches target '{target.Id}'.");
    }

    private ProcessStartInfo CreateBuildProcess(string executable, params string[] initialArguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = _workspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in initialArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private async Task<int> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException($"Could not start '{startInfo.FileName}'.");
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the state check and Kill.
            }
        });
        using var writeGate = new SemaphoreSlim(1, 1);
        var stdout = PumpAsync(process.StandardOutput, _output);
        var stderr = PumpAsync(process.StandardError, _error);
        await Task.WhenAll(process.WaitForExitAsync(cancellationToken), stdout, stderr);
        return process.ExitCode;

        async Task PumpAsync(StreamReader reader, TextWriter writer)
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                await writeGate.WaitAsync(cancellationToken);
                try
                {
                    await writer.WriteLineAsync(line);
                    await writer.FlushAsync(cancellationToken);
                }
                finally
                {
                    writeGate.Release();
                }
            }
        }
    }

    private MsBuildInvocation LocateMsBuild()
    {
        var configured = _msbuildPath ?? Environment.GetEnvironmentVariable("MSBUILD_EXE_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!File.Exists(configured))
            {
                throw new InvalidOperationException($"The configured global MSBuild was not found at '{configured}'.");
            }

            return CreateMsBuildInvocation(configured);
        }

        var candidates = new[]
        {
            @"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
            @"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
            @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
            @"C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        };
        var discovered = candidates.FirstOrDefault(File.Exists)
                         ?? throw new InvalidOperationException(
                             "No global MSBuild was found. Select one in Rider Toolset and Build, call WithMsBuild, or set MSBUILD_EXE_PATH for CLI builds.");
        return CreateMsBuildInvocation(discovered);
    }

    private static MsBuildInvocation CreateMsBuildInvocation(string path)
    {
        if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return new(path, []);
        }

        var sdkDirectory = Path.GetDirectoryName(path)
                           ?? throw new InvalidOperationException($"Could not resolve the SDK directory for '{path}'.");
        var dotnetRoot = Directory.GetParent(sdkDirectory)?.Parent?.FullName
                         ?? throw new InvalidOperationException($"Could not resolve the dotnet root for '{path}'.");
        var dotnet = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (!File.Exists(dotnet))
        {
            throw new InvalidOperationException($"The dotnet host for '{path}' was not found at '{dotnet}'.");
        }

        return new(dotnet, [path]);
    }

    private sealed record MsBuildInvocation(string Executable, ImmutableArray<string> PrefixArguments);
}

/// <summary>Writes normalized generated text only when its content changed.</summary>
public static class CompareBeforeWrite
{
    /// <summary>Writes <paramref name="content"/> and returns whether the destination changed.</summary>
    public static bool Write(string path, string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == normalized)
        {
            return false;
        }

        var directory = Path.GetDirectoryName(path) ??
                        throw new InvalidOperationException($"Path '{path}' has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, normalized, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        return true;
    }
}

internal static class GeneratedOutputOwnership
{
    private const string OwnershipFile = ".roxy-outputs.json";
    private static readonly JsonSerializerOptions OwnershipJsonOptions = new() { WriteIndented = true };

    public static ImmutableArray<string> Update(
        string workspaceRoot,
        LogicalPath outputDirectory,
        IEnumerable<LogicalPath> currentFiles)
    {
        var outputRoot = Path.GetFullPath(Path.Combine(workspaceRoot, outputDirectory.Value));
        var ownershipPath = Path.Combine(outputRoot, OwnershipFile);
        var current = currentFiles.Select(path => path.Value).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToImmutableArray();
        var previous = ReadPreviousOwnership(ownershipPath);
        if (previous.IsDefault)
            previous = [];

        var removed = ImmutableArray.CreateBuilder<string>();
        foreach (var stale in previous.Except(current, StringComparer.Ordinal))
        {
            LogicalPath logical;
            try
            {
                logical = new LogicalPath(stale);
            }
            catch (ArgumentException)
            {
                continue;
            }

            var absolute = Path.GetFullPath(Path.Combine(outputRoot, logical.Value));
            if (!IsWithin(outputRoot, absolute))
                continue;

            if (File.Exists(absolute))
            {
                File.Delete(absolute);
                removed.Add(Path.GetRelativePath(workspaceRoot, absolute));
            }
        }

        var content = JsonSerializer.Serialize(current, OwnershipJsonOptions)
            .Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        CompareBeforeWrite.Write(ownershipPath, content);
        return removed.ToImmutable();
    }

    private static ImmutableArray<string> ReadPreviousOwnership(string ownershipPath)
    {
        if (!File.Exists(ownershipPath))
            return [];

        try
        {
            return JsonSerializer.Deserialize<ImmutableArray<string>>(File.ReadAllText(ownershipPath));
        }
        catch (JsonException)
        {
            // A corrupt ownership record must never authorize deletion or block regeneration.
            return [];
        }
    }

    private static bool IsWithin(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalizedRoot, comparison);
    }
}