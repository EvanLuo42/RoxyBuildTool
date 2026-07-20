using System.Collections.Immutable;
using RoxyBuildTool.Configuration;
using RoxyBuildTool.Model;

namespace RoxyBuildTool.Graph;

public sealed record ConditionalModuleRule(
    FragmentValue Match,
    bool Disable,
    ImmutableArray<string> AddDefines,
    ImmutableArray<string> RemoveDependencies);

public sealed record ModuleDefinition(
    string Id,
    string DisplayName,
    ModuleLanguage Language,
    ModuleKind Kind,
    ImmutableArray<LogicalPath> Sources,
    UsageRequirements PublicUsage,
    UsageRequirements PrivateUsage,
    ImmutableArray<DependencyEdge> Dependencies,
    ImmutableArray<string> TargetFrameworks,
    ImmutableArray<PackageReferenceModel> Packages,
    ImmutableArray<ConditionalModuleRule> ConditionalRules,
    string? RootNamespace = null,
    Func<ConfigurationKey, ModuleDefinition>? ConfigureForConfiguration = null);

public sealed record TargetDefinition(
    string Id,
    string DisplayName,
    ImmutableArray<string> RootModules,
    MatrixDefinition Matrix);

public sealed record WorkspaceDefinition(
    string Id,
    string DisplayName,
    ImmutableArray<string> Targets,
    string StartupTarget,
    bool IncludeBuildHost,
    LogicalPath? BuildHostProject);

public sealed record DefinitionGraph(
    ImmutableArray<ModuleDefinition> Modules,
    ImmutableArray<TargetDefinition> Targets,
    ImmutableArray<WorkspaceDefinition> Workspaces)
{
    public ModuleDefinition GetModule(string id) => Modules.Single(module => module.Id == id);
    public TargetDefinition GetTarget(string id) => Targets.Single(target => target.Id == id);
    public WorkspaceDefinition GetWorkspace(string id) => Workspaces.Single(workspace => workspace.Id == id);
}
