using System.Collections.Immutable;
using RoxyBuildTool.Model;

namespace RoxyBuildTool.Graph;

/// <summary>Groups configured module variants into a generator-neutral workspace model.</summary>
public static class WorkspaceAssembler
{
    /// <summary>Assembles native C++ projects for a workspace.</summary>
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
            .GroupBy(pair => pair.module.Id, StringComparer.Ordinal)
            .Select(group => new WorkspaceProject(
                group.Key,
                [
                    ..group.Select(pair => new WorkspaceProjectVariant(
                            pair.graph.Target.Id,
                            pair.graph.Configuration,
                            pair.module))
                        .Distinct()
                        .OrderBy(variant => variant.Target, StringComparer.Ordinal)
                        .ThenBy(variant => variant.Configuration)
                ]))
            .OrderBy(project => project.Id, StringComparer.Ordinal)
            .ToImmutableArray();

        return new WorkspaceModel(definition.DisplayName, definition.StartupTarget, projects, graphs, actions);
    }
}
