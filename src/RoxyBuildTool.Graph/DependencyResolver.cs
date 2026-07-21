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
                [..configured.Values.OrderBy(module => module.Id, StringComparer.Ordinal)],
                diagnostics.ToImmutable());
        }

        return new ConfiguredGraph(configuration,
            new ConfiguredTarget(target.Id, target.DisplayName, target.RootModules),
            [..configured.Values.OrderBy(module => module.Id, StringComparer.Ordinal)],
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

            ModuleDefinition definition;
            try
            {
                definition = definitions.GetModule(moduleId);
                definition = definition.ConfigureForConfiguration?.Invoke(configuration) ?? definition;
            }
            catch (InvalidOperationException)
            {
                diagnostics.Add(new Diagnostic("RBT2002", DiagnosticSeverity.Error,
                    $"Module '{moduleId}' is not registered.", moduleId, configuration.Canonical));
                return null;
            }

            var conditionalRules = definition.ConditionalRules.Where(rule => configuration.Is(rule.Match))
                .ToImmutableArray();
            if (conditionalRules.Any(rule => rule.Disable))
            {
                states[moduleId] = VisitState.Complete;
                return null;
            }

            states[moduleId] = VisitState.Visiting;
            stack.Add(moduleId);

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
            var consumerUsage = publicUsage.Union(GetOwnLinkUsage(definition, target, configuration));

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
                    case DependencyVisibility.Public or DependencyVisibility.Interface:
                        consumerUsage = consumerUsage.Union(resolvedDependency.ConsumerUsage);
                        break;
                    case DependencyVisibility.Runtime:
                        compileUsage = compileUsage.Union(new UsageRequirements([], [], [],
                            resolvedDependency.ConsumerUsage.RuntimeFiles));
                        break;
                }
            }

            var result = new ConfiguredModule(
                definition.Id,
                definition.DisplayName,
                definition.Language,
                definition.Kind,
                definition.Sources.OrderBy(source => source.Value, StringComparer.Ordinal).ToImmutableArray(),
                publicUsage,
                privateUsage,
                compileUsage,
                consumerUsage,
                dependencies,
                definition.TargetFrameworks,
                definition.Packages,
                definition.RootNamespace);
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
        var platform = GetValue(configuration, "Platform");
        var architecture = GetValue(configuration, "Architecture");
        var profile = GetValue(configuration, "Profile");
        var outputRoot =
            $"out/{platform.ToLowerInvariant()}/{architecture.ToLowerInvariant()}/{profile.ToLowerInvariant()}/{target.Id}";
        return module.Kind switch
        {
            ModuleKind.StaticLibrary => new UsageRequirements([], [],
                [new UsageValue($"{outputRoot}/{module.Id}.lib", $"{module.Id}:artifact")],
                []),
            ModuleKind.SharedLibrary => new UsageRequirements([], [],
                [new UsageValue($"{outputRoot}/{module.Id}.lib", $"{module.Id}:artifact")],
                [new UsageValue($"{outputRoot}/{module.Id}.dll", $"{module.Id}:runtime")]),
            _ => UsageRequirements.Empty,
        };
    }

    private static string GetValue(ConfigurationKey configuration, string fragment) =>
        configuration.Values.Single(value => value.Fragment.Value == fragment).Value;

    private enum VisitState
    {
        Visiting,
        Complete,
    }
}