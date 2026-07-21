using System.Collections.Immutable;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Model;

namespace RoxyBuildTool.Graph;

/// <summary>Resolves module dependencies and usage propagation for one target configuration.</summary>
public static class DependencyResolver
{
    /// <summary>Creates a configured graph and reports missing, disabled, or cyclic dependencies.</summary>
    public static ConfiguredGraph Resolve(DefinitionGraph definitions, TargetDefinition target,
        ConfigurationKey configuration)
    {
        var states = new Dictionary<string, VisitState>(StringComparer.Ordinal);
        var configured = new Dictionary<string, ConfiguredModule>(StringComparer.Ordinal);
        var moduleDefinitions = definitions.Modules.ToDictionary(module => module.Id, StringComparer.Ordinal);
        var stack = new List<string>();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        foreach (var root in target.RootModules.Order(StringComparer.Ordinal))
        {
            Visit(root);
        }

        if (diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return new ConfiguredGraph(configuration,
                new ConfiguredTarget(target.Id, target.DisplayName, target.RootModules),
                [.. configured.Values.OrderBy(module => module.Id, StringComparer.Ordinal)],
                diagnostics.ToImmutable());
        }

        return new ConfiguredGraph(configuration,
            new ConfiguredTarget(target.Id, target.DisplayName, target.RootModules),
            [.. configured.Values.OrderBy(module => module.Id, StringComparer.Ordinal)],
            diagnostics.ToImmutable());

        ConfiguredModule? Visit(string moduleId)
        {
            if (states.TryGetValue(moduleId, out var state))
            {
                if (state == VisitState.Complete)
                {
                    return configured.GetValueOrDefault(moduleId);
                }

                var cycleStart = stack.IndexOf(moduleId);
                var cycle = stack.Skip(cycleStart).Append(moduleId);
                diagnostics.Add(new Diagnostic("RBT2001", DiagnosticSeverity.Error,
                    $"Dependency cycle detected: {string.Join(" -> ", cycle)}.", moduleId, configuration.Canonical));
                return null;
            }

            if (!moduleDefinitions.TryGetValue(moduleId, out var definition))
            {
                diagnostics.Add(new Diagnostic("RBT2002", DiagnosticSeverity.Error,
                    $"Module '{moduleId}' is not registered.", moduleId, configuration.Canonical));
                return null;
            }

            definition = definition.ConfigureForConfiguration?.Invoke(configuration) ?? definition;

            var conditionalRules = definition.ConditionalRules.Where(rule => configuration.Is(rule.Match))
                .ToImmutableArray();
            if (conditionalRules.Any(rule => rule.Disable))
            {
                states[moduleId] = VisitState.Complete;
                return null;
            }

            states[moduleId] = VisitState.Visiting;
            stack.Add(moduleId);

            var assemblySources = definition.Sources.Where(source => BuildFileKinds.IsAssemblySource(source.Value))
                .Select(source => source.Value).Order(StringComparer.Ordinal).ToImmutableArray();
            if (!assemblySources.IsEmpty)
                diagnostics.Add(new Diagnostic("RBT2005", DiagnosticSeverity.Error,
                    $"Module '{moduleId}' contains unsupported assembly-language sources: " +
                    $"{string.Join(", ", assemblySources)}. ASM, MASM, NASM, and GAS inputs are not supported.",
                    moduleId, configuration.Canonical));

            if (definition.CxxSettings?.PrecompiledSource is { } precompiledSource &&
                !definition.Sources.Any(source =>
                    LogicalPath.FileSystemComparer.Equals(source.Value, precompiledSource.Value)))
                diagnostics.Add(new Diagnostic("RBT2006", DiagnosticSeverity.Error,
                    $"Module '{moduleId}' precompiled source '{precompiledSource}' is not part of its source set.",
                    moduleId, configuration.Canonical));

            var removedDependencies = conditionalRules.SelectMany(rule => rule.RemoveDependencies)
                .ToHashSet(StringComparer.Ordinal);
            var dependencies = definition.Dependencies
                .Where(dependency => !removedDependencies.Contains(dependency.Module))
                .OrderBy(dependency => dependency.Module, StringComparer.Ordinal)
                .ThenBy(dependency => dependency.Visibility)
                .ToImmutableArray();
            var publicUsage = definition.PublicUsage;
            var privateUsage = definition.PrivateUsage;
            var conditionalDefines = conditionalRules.SelectMany(rule => rule.AddDefines)
                .Select(value => new UsageValue(value, $"{moduleId}:conditional"));
            privateUsage = privateUsage with { Defines = privateUsage.Defines.AddRange(conditionalDefines) };
            var compileUsage = publicUsage.Union(privateUsage);
            var consumerUsage = ExportedPublicRequirements(definition.Kind, publicUsage)
                .Union(GetOwnLinkUsage(definition, target, configuration))
                .Union(PrivateRequirementsCarriedBy(definition.Kind, privateUsage));

            foreach (var dependency in dependencies)
            {
                var resolvedDependency = Visit(dependency.Module);
                if (resolvedDependency is null)
                {
                    if (dependency.Visibility is not DependencyVisibility.BuildOrderOnly)
                    {
                        diagnostics.Add(new Diagnostic("RBT2003", DiagnosticSeverity.Error,
                            $"Module '{moduleId}' requires disabled or invalid module '{dependency.Module}'.",
                            moduleId, configuration.Canonical));
                    }

                    continue;
                }

                if (dependency.Visibility is DependencyVisibility.Private or DependencyVisibility.Public)
                {
                    compileUsage = compileUsage.Union(resolvedDependency.ConsumerUsage);
                }

                switch (dependency.Visibility)
                {
                    case DependencyVisibility.Public:
                        consumerUsage = consumerUsage.Union(ExportedPublicRequirements(
                            definition.Kind,
                            resolvedDependency.ConsumerUsage));
                        break;
                    case DependencyVisibility.Interface:
                        consumerUsage = consumerUsage.Union(resolvedDependency.ConsumerUsage);
                        break;
                    case DependencyVisibility.Private:
                        consumerUsage = consumerUsage.Union(PrivateRequirementsCarriedBy(
                            definition.Kind,
                            resolvedDependency.ConsumerUsage));
                        break;
                    case DependencyVisibility.Runtime:
                        var runtime = RuntimeOnly(resolvedDependency.ConsumerUsage);
                        compileUsage = compileUsage.Union(runtime);
                        consumerUsage = consumerUsage.Union(runtime);
                        break;
                }
            }

            var result = new ConfiguredModule(
                definition.Id,
                definition.DisplayName,
                definition.Kind,
                definition.Sources.OrderBy(source => source.Value, StringComparer.Ordinal).ToImmutableArray(),
                publicUsage,
                privateUsage,
                compileUsage,
                consumerUsage,
                dependencies,
                definition.CxxSettings);
            configured.Add(moduleId, result);
            stack.RemoveAt(stack.Count - 1);
            states[moduleId] = VisitState.Complete;
            return result;
        }
    }

    private static UsageRequirements GetOwnLinkUsage(
        ModuleDefinition module,
        TargetDefinition target,
        ConfigurationKey configuration)
    {
        var outputRoot = BuildPathLayout.OutputRoot(configuration, target.Id);
        var outputName = module.CxxSettings?.OutputName ?? module.Id;
        return module.Kind switch
        {
            ModuleKind.StaticLibrary => new UsageRequirements([], [],
                [new UsageValue($"{outputRoot}/{outputName}.lib", $"{module.Id}:artifact")],
                []),
            ModuleKind.SharedLibrary => new UsageRequirements([], [],
                [new UsageValue($"{outputRoot}/{outputName}.lib", $"{module.Id}:artifact")],
                [new UsageValue($"{outputRoot}/{outputName}.dll", $"{module.Id}:runtime")]),
            ModuleKind.ObjectLibrary => new UsageRequirements([], [],
            [
                .. module.Sources
                    .Where(source => BuildFileKinds.IsCxxSource(source.Value))
                    .Select(source => new UsageValue(
                        BuildPathLayout.ObjectFile(configuration, target.Id, module.Id, source),
                        $"{module.Id}:object"))
                    .Concat(module.Sources
                        .Where(source => BuildFileKinds.IsResource(source.Value))
                        .Select(source => new UsageValue(
                            BuildPathLayout.ResourceFile(configuration, target.Id, module.Id, source),
                            $"{module.Id}:resource")))
            ], []),
            _ => UsageRequirements.Empty,
        };
    }

    private static UsageRequirements ExportedPublicRequirements(
        ModuleKind consumerKind,
        UsageRequirements usage)
    {
        // Object and resource files are implementation inputs once a real linker or librarian consumes
        // them. Exporting them as well would make downstream links see the same object twice.
        return AbsorbsObjectFiles(consumerKind)
            ? WithoutObjectAndResourceInputs(usage)
            : usage;
    }

    private static UsageRequirements PrivateRequirementsCarriedBy(
        ModuleKind consumerKind,
        UsageRequirements usage)
    {
        return consumerKind switch
        {
            // Static libraries cannot consume library link requirements. Object-library files are
            // different: the librarian absorbs their object files into the archive, while any
            // libraries needed by those objects still have to reach the eventual linker.
            ModuleKind.StaticLibrary => WithoutObjectAndResourceInputs(LinkAndRuntimeOnly(usage)),

            // Object and header-only libraries have no link step. Their eventual consumer must
            // receive the complete private link/runtime closure, but not private compile settings.
            ModuleKind.ObjectLibrary => LinkAndRuntimeOnly(usage),

            // A shared library consumes link inputs itself, but its private DLL closure remains a
            // runtime requirement of every executable that loads it.
            ModuleKind.SharedLibrary => RuntimeOnly(usage),
            _ => UsageRequirements.Empty,
        };
    }

    private static bool AbsorbsObjectFiles(ModuleKind kind) => kind is
        ModuleKind.StaticLibrary or ModuleKind.SharedLibrary or ModuleKind.Executable;

    private static UsageRequirements LinkAndRuntimeOnly(UsageRequirements usage) => new(
        [],
        [],
        usage.LinkInputs,
        usage.RuntimeFiles);

    private static UsageRequirements RuntimeOnly(UsageRequirements usage) => new(
        [],
        [],
        [],
        usage.RuntimeFiles);

    private static UsageRequirements WithoutObjectAndResourceInputs(UsageRequirements usage) => usage with
    {
        LinkInputs =
        [
            .. usage.LinkInputs.Where(input =>
                !input.Value.EndsWith(".obj", StringComparison.OrdinalIgnoreCase) &&
                !input.Value.EndsWith(".res", StringComparison.OrdinalIgnoreCase))
        ]
    };

    private enum VisitState
    {
        Visiting,
        Complete,
    }
}