using System.Collections.Immutable;
using RoxyBuildTool.Configuration;
using RoxyBuildTool.Model;

namespace RoxyBuildTool.Graph;

/// <summary>Groups configured module variants into a generator-neutral workspace model.</summary>
public static class WorkspaceAssembler
{
    /// <summary>Assembles projects and optionally imports the project-local build host.</summary>
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
            .GroupBy(pair => FragmentRegistry.ToPascalCase($"{pair.graph.Target.Id}.{pair.module.Id}"),
                StringComparer.Ordinal)
            .Select(group =>
            {
                var (graph, first) = group.First();
                var targetId = graph.Target.Id;
                var targetName = TrimSuffix(graph.Target.DisplayName, "Target");
                var moduleName = TrimSuffix(first.DisplayName, "Module");
                var presentationName = moduleName.Equals(targetName, StringComparison.Ordinal) ||
                                       moduleName.Equals(targetName + "Executable", StringComparison.Ordinal)
                    ? targetName
                    : $"{moduleName}.{targetName}";
                return new WorkspaceProject(
                    group.Key,
                    presentationName,
                    first.Language,
                    [
                        ..group.Select(pair => new WorkspaceProjectVariant(
                                pair.graph.Target.Id,
                                pair.graph.Configuration,
                                pair.module))
                            .Distinct()
                            .OrderBy(variant => variant.Target, StringComparer.Ordinal)
                            .ThenBy(variant => variant.Configuration)
                    ],
                    [
                        ..group.SelectMany(pair => pair.module.Dependencies)
                            .Select(dependency => FragmentRegistry.ToPascalCase($"{targetId}.{dependency.Module}"))
                            .Distinct(StringComparer.Ordinal)
                            .Order(StringComparer.Ordinal)
                    ]);
            })
            .OrderBy(project => project.Id, StringComparer.Ordinal)
            .ToImmutableArray();

        var projectIds = projects.Select(project => project.Id).ToImmutableHashSet(StringComparer.Ordinal);
        projects =
        [
            ..projects.Select(project => project with
            {
                ProjectDependencies = [..project.ProjectDependencies.Where(projectIds.Contains)]
            })
        ];

        if (definition.IncludeBuildHost && definition.BuildHostProject is not null)
        {
            projects = projects.Add(new WorkspaceProject(
                "BuildRules",
                "Build Rules",
                ModuleLanguage.CSharp,
                [],
                [],
                true,
                definition.BuildHostProject));
        }

        return new WorkspaceModel(definition.DisplayName, definition.StartupTarget, projects, graphs, actions);
    }

    private static string TrimSuffix(string value, string suffix) =>
        value.EndsWith(suffix, StringComparison.Ordinal) ? value[..^suffix.Length] : value;
}