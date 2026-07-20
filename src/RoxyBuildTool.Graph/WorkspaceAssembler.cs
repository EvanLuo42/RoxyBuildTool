using System.Collections.Immutable;
using RoxyBuildTool.Model;

namespace RoxyBuildTool.Graph;

public static class WorkspaceAssembler
{
    public static WorkspaceModel Assemble(
        WorkspaceDefinition definition,
        IEnumerable<ConfiguredGraph> configuredGraphs,
        IEnumerable<ActionGraph> actionGraphs)
    {
        var graphs = configuredGraphs.OrderBy(graph => graph.Target.Id, StringComparer.Ordinal)
            .ThenBy(graph => graph.Configuration).ToImmutableArray();
        var actions = actionGraphs.OrderBy(graph => graph.Target, StringComparer.Ordinal)
            .ThenBy(graph => graph.Configuration).ToImmutableArray();
        var projects = graphs.SelectMany(graph => graph.Modules.Select(module => (graph, module)))
            .GroupBy(pair => $"{pair.graph.Target.Id}.{pair.module.Id}", StringComparer.Ordinal)
            .Select(group =>
            {
                var firstPair = group.First();
                var first = firstPair.module;
                var targetId = firstPair.graph.Target.Id;
                var targetName = TrimSuffix(firstPair.graph.Target.DisplayName, "Target");
                var moduleName = TrimSuffix(first.DisplayName, "Module");
                var presentationName = moduleName.Equals(targetName, StringComparison.Ordinal) ||
                                       moduleName.Equals(targetName + "Executable", StringComparison.Ordinal)
                    ? targetName
                    : $"{moduleName}.{targetName}";
                return new WorkspaceProject(
                    group.Key,
                    presentationName,
                    first.Language,
                    group.Select(pair => new WorkspaceProjectVariant(
                            pair.graph.Target.Id,
                            pair.graph.Configuration,
                            pair.module))
                        .Distinct()
                        .OrderBy(variant => variant.Target, StringComparer.Ordinal)
                        .ThenBy(variant => variant.Configuration)
                        .ToImmutableArray(),
                    group.SelectMany(pair => pair.module.Dependencies)
                        .Select(dependency => $"{targetId}.{dependency.Module}").Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal).ToImmutableArray());
            })
            .OrderBy(project => project.Id, StringComparer.Ordinal)
            .ToImmutableArray();

        if (definition.IncludeBuildHost && definition.BuildHostProject is not null)
        {
            projects = projects.Add(new(
                "build-rules",
                "Build Rules",
                ModuleLanguage.CSharp,
                [],
                [],
                true,
                definition.BuildHostProject));
        }

        return new(definition.DisplayName, definition.StartupTarget, projects, graphs, actions);
    }

    private static string TrimSuffix(string value, string suffix) =>
        value.EndsWith(suffix, StringComparison.Ordinal) ? value[..^suffix.Length] : value;
}
