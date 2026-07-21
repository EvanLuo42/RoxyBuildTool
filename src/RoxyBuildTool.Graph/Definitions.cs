using System.Collections.Immutable;
using RoxyBuildTool.Configuration;
using RoxyBuildTool.Model;

namespace RoxyBuildTool.Graph;

/// <summary>Describes module mutations applied when a fragment value is selected.</summary>
public sealed record ConditionalModuleRule(
    FragmentValue Match,
    bool Disable,
    ImmutableArray<string> AddDefines,
    ImmutableArray<string> RemoveDependencies);

/// <summary>Contains unresolved authoring data for one native C++ module.</summary>
public sealed record ModuleDefinition(
    string Id,
    string DisplayName,
    ModuleKind Kind,
    ImmutableArray<LogicalPath> Sources,
    UsageRequirements PublicUsage,
    UsageRequirements PrivateUsage,
    ImmutableArray<DependencyEdge> Dependencies,
    ImmutableArray<ConditionalModuleRule> ConditionalRules,
    Func<ConfigurationKey, ModuleDefinition>? ConfigureForConfiguration = null,
    CxxModuleSettings? CxxSettings = null);

/// <summary>Contains root modules and the configuration matrix of a target.</summary>
public sealed record TargetDefinition(
    string Id,
    string DisplayName,
    ImmutableArray<string> RootModules,
    MatrixDefinition Matrix);

/// <summary>Contains the target set and presentation settings of a workspace.</summary>
public sealed record WorkspaceDefinition(
    string Id,
    string DisplayName,
    ImmutableArray<string> Targets,
    string StartupTarget);

/// <summary>Contains all module, target, and workspace definitions discovered from the rules assemblies.</summary>
public sealed record DefinitionGraph(
    ImmutableArray<ModuleDefinition> Modules,
    ImmutableArray<TargetDefinition> Targets,
    ImmutableArray<WorkspaceDefinition> Workspaces)
{
    /// <summary>Gets rule binaries that participate in cross-invocation cache invalidation.</summary>
    public ImmutableArray<string> RuleAssemblyIdentities { get; init; }

    /// <summary>Gets a module by stable ID.</summary>
    public ModuleDefinition GetModule(string id) => Modules.Single(module => module.Id == id);

    /// <summary>Gets a target by stable ID.</summary>
    public TargetDefinition GetTarget(string id) => Targets.Single(target => target.Id == id);

    /// <summary>Gets a workspace by stable ID.</summary>
    public WorkspaceDefinition GetWorkspace(string id) => Workspaces.Single(workspace => workspace.Id == id);
}