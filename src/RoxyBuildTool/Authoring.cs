using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO.Enumeration;
using System.Reflection;
using System.Runtime.ExceptionServices;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Configuration;
using RoxyBuildTool.Graph;
using RoxyBuildTool.Model;

namespace RoxyBuildTool;

/// <summary>Registers rules explicitly when assembly scanning is not appropriate.</summary>
public interface IRulesModule
{
    /// <summary>Adds modules, targets, or workspaces to <paramref name="registry"/>.</summary>
    void Register(BuildRegistry registry);
}

/// <summary>Reports a user-correctable rules definition failure with a stable diagnostic.</summary>
public sealed class RuleDefinitionException(Diagnostic diagnostic) : Exception(diagnostic.Message)
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}

/// <summary>Base type for native C++ module definitions.</summary>
public abstract class CxxModule
{
}

/// <summary>Excludes a rule type from assembly discovery while still allowing explicit registration.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class BuildRuleIgnoreAttribute : Attribute;

/// <summary>Marks a method that contributes settings to a module, target, or workspace definition.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public class ConfigureAttribute : Attribute
{
    /// <summary>Creates an unconditional configuration method marker.</summary>
    public ConfigureAttribute()
    {
        Values = [];
    }

    /// <summary>Creates a marker that runs when a fragment matches one of the supplied values.</summary>
    public ConfigureAttribute(string fragmentId, params string[] values)
    {
        Fragment = new(fragmentId);
        Values = values.Select(value => new FragmentValue(Fragment.Value, value)).ToImmutableArray();
        if (Values.IsEmpty)
        {
            throw new ArgumentException("A filtered [Configure] attribute requires at least one value.",
                nameof(values));
        }
    }

    /// <summary>Gets the fragment used by the filter, or <see langword="null"/> for an unconditional method.</summary>
    public FragmentId? Fragment { get; }

    /// <summary>Gets the accepted fragment values.</summary>
    public ImmutableArray<FragmentValue> Values { get; }

    /// <summary>Gets or sets the method ordering priority. Lower values run first.</summary>
    public int Priority { get; set; }

    internal bool IsUnconditional => Fragment is null;

    internal Type? FragmentType { get; private protected set; }
}

/// <summary>Marks a configuration method filtered by values of enum fragment <typeparamref name="TFragment"/>.</summary>
public sealed class ConfigureAttribute<TFragment> : ConfigureAttribute where TFragment : struct, Enum
{
    /// <summary>Creates a filter from stable enum value names.</summary>
    public ConfigureAttribute(params string[] values)
        : base(GetFragmentId(), EncodeValues(values))
    {
        FragmentType = typeof(TFragment);
    }

    private static string GetFragmentId() =>
        typeof(TFragment).GetCustomAttribute<BuildFragmentAttribute>()?.Id
        ?? throw new InvalidOperationException($"Enum '{typeof(TFragment).FullName}' must have [BuildFragment].");

    private static string[] EncodeValues(string[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var registry = new FragmentRegistry();
        var metadata = registry.RegisterEnum<TFragment>();
        var fields = typeof(TFragment).GetFields(BindingFlags.Public | BindingFlags.Static);

        return values.Select(value =>
        {
            var field = fields.SingleOrDefault(candidate =>
                candidate.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (field is not null)
            {
                return registry.Encode((TFragment)field.GetValue(null)!).Value;
            }

            var normalized = new FragmentValue(metadata.Id, value).Value;
            return metadata.Values.Any(candidate => candidate.Value == normalized)
                ? normalized
                : throw new ArgumentException(
                    $"'{value}' is not a value name or stable value ID of fragment enum " +
                    $"'{typeof(TFragment).FullName}'.",
                    nameof(values));
        }).Distinct(StringComparer.Ordinal).ToArray();
    }
}

/// <summary>Base type for a build target that selects root modules and a configuration matrix.</summary>
public abstract class BuildTarget
{
}

/// <summary>Base type for a workspace that groups targets for generator output.</summary>
public abstract class BuildWorkspace
{
}

/// <summary>Specifies the artifact produced by a C++ module.</summary>
public enum CxxOutput
{
    HeaderOnly,
    ObjectLibrary,
    StaticLibrary,
    SharedLibrary,
    Executable,
}

/// <summary>Collects discovered rules and converts them into immutable definitions.</summary>
public sealed class BuildRegistry(string workspaceRoot)
{
    private readonly List<Type> _modules = [];
    private readonly SourceFileSnapshotCache _sourceFiles = new();
    private readonly List<Type> _targets = [];
    private readonly List<Type> _workspaces = [];

    /// <summary>Registers a module type explicitly.</summary>
    public void AddModule<T>() where T : class, new() => AddUnique(_modules, typeof(T), "module");

    /// <summary>Registers a target type explicitly.</summary>
    public void AddTarget<T>() where T : BuildTarget, new() => AddUnique(_targets, typeof(T), "target");

    /// <summary>Registers a workspace type explicitly.</summary>
    public void AddWorkspace<T>() where T : BuildWorkspace, new() => AddUnique(_workspaces, typeof(T), "workspace");

    /// <summary>Discovers concrete rule types from an assembly.</summary>
    public void ScanAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var type in assembly.GetTypes()
                     .Where(type => type is { IsAbstract: false, ContainsGenericParameters: false } &&
                                    !type.IsDefined(typeof(BuildRuleIgnoreAttribute), inherit: false))
                     .OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            if (typeof(CxxModule).IsAssignableFrom(type))
            {
                EnsureParameterlessConstructor(type);
                AddUnique(_modules, type, "module");
            }
            else if (typeof(BuildTarget).IsAssignableFrom(type))
            {
                EnsureParameterlessConstructor(type);
                AddUnique(_targets, type, "target");
            }
            else if (typeof(BuildWorkspace).IsAssignableFrom(type))
            {
                EnsureParameterlessConstructor(type);
                AddUnique(_workspaces, type, "workspace");
            }
        }
    }

    internal DefinitionGraph Build()
    {
        EnsureUniqueDefinitionIds(_modules, "module");
        EnsureUniqueDefinitionIds(_targets, "target");
        EnsureUniqueDefinitionIds(_workspaces, "workspace");
        var modules = _modules.Select(BuildModule).OrderBy(module => module.Id, StringComparer.Ordinal)
            .ToImmutableArray();
        var targets = _targets.Select(BuildTargetDefinition).OrderBy(target => target.Id, StringComparer.Ordinal)
            .ToImmutableArray();
        var workspaces = _workspaces.Select(BuildWorkspaceDefinition)
            .OrderBy(workspace => workspace.Id, StringComparer.Ordinal).ToImmutableArray();
        ValidateFragmentDefinitions(_modules, targets);
        ValidateConfigureFilters(_modules, targets);
        ValidateReferences(modules, targets, workspaces);
        return new DefinitionGraph(modules, targets, workspaces)
        {
            RuleAssemblyIdentities = RuleAssemblyIdentities()
        };
    }

    private ImmutableArray<string> RuleAssemblyIdentities()
    {
        const BindingFlags configureMethodFlags = BindingFlags.Instance | BindingFlags.Static |
                                                  BindingFlags.Public | BindingFlags.NonPublic |
                                                  BindingFlags.FlattenHierarchy;
        return
        [
            .._modules.Concat(_targets).Concat(_workspaces)
                .SelectMany(type => type.GetMethods(configureMethodFlags)
                    .Where(method => method.IsDefined(typeof(ConfigureAttribute), inherit: true))
                    .Select(method => method.Module.Assembly)
                    .Append(type.Assembly))
                .Distinct()
                .OrderBy(assembly => assembly.FullName, StringComparer.Ordinal)
                .Select(assembly => $"{assembly.FullName}|{assembly.ManifestModule.ModuleVersionId:N}")
        ];
    }

    /// <summary>Derives a stable definition ID from a rule type name.</summary>
    public static string DefinitionId(Type type)
    {
        var name = type.Name;
        foreach (var suffix in new[] { "Module", "Target", "Workspace" })
        {
            if (!name.EndsWith(suffix, StringComparison.Ordinal) || name.Length <= suffix.Length) continue;
            name = name[..^suffix.Length];
            break;
        }

        return FragmentRegistry.ToPascalCase(name);
    }

    private ModuleDefinition BuildModule(Type type)
    {
        var relevantFragments = ConfigureFragments(type);
        if (!typeof(CxxModule).IsAssignableFrom(type))
            throw new InvalidOperationException(
                $"Registered module '{type.FullName}' must derive from CxxModule.");

        {
            var cache = new ConcurrentDictionary<string, Lazy<ModuleDefinition>>(StringComparer.Ordinal);
            return BuildCxxDefinition((CxxModule)Activator.CreateInstance(type)!, type, configuration: null) with
            {
                ConfigureForConfiguration = configuration => cache.GetOrAdd(
                    ValidateAndGetRelevantConfigurationKey(type, configuration, relevantFragments),
                    _ => new Lazy<ModuleDefinition>(
                        () => BuildCxxDefinition(
                            (CxxModule)Activator.CreateInstance(type)!, type, configuration),
                        LazyThreadSafetyMode.ExecutionAndPublication)).Value,
            };
        }
    }

    private static ImmutableHashSet<FragmentId> ConfigureFragments(Type type)
    {
        return type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                               BindingFlags.NonPublic | BindingFlags.FlattenHierarchy)
            .SelectMany(method => method.GetCustomAttributes<ConfigureAttribute>(true))
            .Where(attribute => !attribute.IsUnconditional)
            .Select(attribute => attribute.Fragment!.Value)
            .ToImmutableHashSet();
    }

    private static string ValidateAndGetRelevantConfigurationKey(
        Type moduleType,
        ConfigurationKey configuration,
        ImmutableHashSet<FragmentId> relevantFragments)
    {
        var missing = relevantFragments.Where(fragment => !configuration.TryGet(fragment, out _))
            .Order()
            .ToImmutableArray();
        if (!missing.IsEmpty)
        {
            throw new RuleDefinitionException(new Diagnostic(
                "RBT2104",
                DiagnosticSeverity.Error,
                $"Module '{DefinitionId(moduleType)}' has filtered [Configure] methods for fragments that are " +
                $"missing from the target matrix: {string.Join(", ", missing)}."));
        }

        return string.Join(';', configuration.Values
            .Where(value => relevantFragments.Contains(value.Fragment))
            .Select(value => $"{value.Fragment.Value}={value.Value}"));
    }

    private TargetDefinition BuildTargetDefinition(Type type)
    {
        var rules = new TargetRules();
        var target = (BuildTarget)Activator.CreateInstance(type)!;
        ApplyConfigureMethods(target, rules, configuration: null, allowFiltered: false);
        return rules.Build(type);
    }

    private WorkspaceDefinition BuildWorkspaceDefinition(Type type)
    {
        var rules = new WorkspaceRules();
        var workspace = (BuildWorkspace)Activator.CreateInstance(type)!;
        ApplyConfigureMethods(workspace, rules, configuration: null, allowFiltered: false);
        return rules.Build(type);
    }

    private ModuleDefinition BuildCxxDefinition(CxxModule module, Type type, ConfigurationKey? configuration)
    {
        var rules = new ModuleRules(workspaceRoot, _sourceFiles);
        ApplyConfigureMethods(module, rules, configuration, allowFiltered: true);
        return rules.Build(type);
    }

    private static void ApplyConfigureMethods(
        object instance,
        object rules,
        ConfigurationKey? configuration,
        bool allowFiltered)
    {
        var methods = instance.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.FlattenHierarchy)
            .Select(method => (Method: method,
                Attributes: method.GetCustomAttributes<ConfigureAttribute>(inherit: true).ToImmutableArray()))
            .Where(item => !item.Attributes.IsEmpty)
            .OrderBy(item => item.Attributes.Min(attribute => attribute.Priority))
            .ThenBy(item => item.Attributes.All(attribute => attribute.IsUnconditional) ? 0 : 1)
            .ThenBy(item => item.Method.Name, StringComparer.Ordinal)
            .ToImmutableArray();

        foreach (var item in methods)
        {
            ValidateConfigureMethod(item.Method, rules.GetType());
            var filtered = item.Attributes.Any(attribute => !attribute.IsUnconditional);
            if (filtered && !allowFiltered)
            {
                throw new InvalidOperationException(
                    $"Filtered [Configure] is not supported on '{instance.GetType().Name}.{item.Method.Name}'. " +
                    "Put configuration axes on TargetRules.Matrix and filtered settings on modules.");
            }

            if (!Matches(item.Attributes, configuration))
            {
                continue;
            }

            try
            {
                item.Method.Invoke(item.Method.IsStatic ? null : instance, [rules]);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            }
        }
    }

    private static bool Matches(ImmutableArray<ConfigureAttribute> attributes, ConfigurationKey? configuration)
    {
        if (attributes.Any(attribute => attribute.IsUnconditional))
        {
            return true;
        }

        if (configuration is null)
        {
            return false;
        }

        return attributes.GroupBy(attribute => attribute.Fragment!.Value)
            .All(group => group.SelectMany(attribute => attribute.Values).Any(configuration.Is));
    }

    private static void ValidateConfigureMethod(MethodInfo method, Type rulesType)
    {
        if (method.ReturnType != typeof(void))
        {
            throw new InvalidOperationException(
                $"[Configure] method '{method.DeclaringType?.FullName}.{method.Name}' must return void.");
        }

        var parameters = method.GetParameters();
        if (parameters.Length != 1 || parameters[0].ParameterType != rulesType)
        {
            throw new InvalidOperationException(
                $"[Configure] method '{method.DeclaringType?.FullName}.{method.Name}' must accept exactly one '{rulesType.Name}' parameter.");
        }
    }

    private static void AddUnique(List<Type> types, Type type, string kind)
    {
        if (types.Contains(type))
        {
            throw new InvalidOperationException($"The {kind} '{type.FullName}' is already registered.");
        }

        types.Add(type);
    }

    private static void EnsureParameterlessConstructor(Type type)
    {
        if (type.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new InvalidOperationException(
                $"Discovered rules type '{type.FullName}' must have a public parameterless constructor.");
        }
    }

    private static void EnsureUniqueDefinitionIds(IEnumerable<Type> types, string kind)
    {
        var duplicate = types.GroupBy(DefinitionId, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new RuleDefinitionException(new Diagnostic(
                "RBT2101",
                DiagnosticSeverity.Error,
                $"Multiple {kind} rule types produce definition ID '{duplicate.Key}': " +
                $"{string.Join(", ", duplicate.Select(type => type.FullName).Order(StringComparer.Ordinal))}."));
        }
    }

    private static void ValidateFragmentDefinitions(
        IEnumerable<Type> moduleTypes,
        ImmutableArray<TargetDefinition> targets)
    {
        var registry = new FragmentRegistry();
        foreach (var enumType in targets.SelectMany(target => target.Matrix.Axes)
                     .Select(axis => axis.EnumType)
                     .Concat(moduleTypes.SelectMany(type => type.GetMethods(
                             BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                             BindingFlags.NonPublic | BindingFlags.FlattenHierarchy)
                         .SelectMany(method => method.GetCustomAttributes<ConfigureAttribute>(inherit: true))
                         .Select(attribute => attribute.FragmentType)))
                     .Where(type => type is not null)
                     .Distinct())
        {
            registry.RegisterEnum(enumType!);
        }
    }

    private static void ValidateConfigureFilters(
        IEnumerable<Type> moduleTypes,
        ImmutableArray<TargetDefinition> targets)
    {
        var axes = targets.SelectMany(target => target.Matrix.Axes).ToImmutableArray();
        foreach (var moduleType in moduleTypes)
        foreach (var attribute in moduleType.GetMethods(
                         BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                         BindingFlags.NonPublic | BindingFlags.FlattenHierarchy)
                     .SelectMany(method => method.GetCustomAttributes<ConfigureAttribute>(inherit: true))
                     .Where(attribute => !attribute.IsUnconditional))
        {
            var matchingAxes = axes.Where(axis => axis.Fragment == attribute.Fragment!.Value).ToImmutableArray();
            if (matchingAxes.IsEmpty)
            {
                throw new RuleDefinitionException(new Diagnostic(
                    "RBT2104",
                    DiagnosticSeverity.Error,
                    $"Module '{DefinitionId(moduleType)}' filters on fragment '{attribute.Fragment}', " +
                    "but no target matrix declares that axis."));
            }

            var invalidValues = attribute.Values
                .Where(value => matchingAxes.All(axis => !axis.Values.Contains(value)))
                .Select(value => value.Value)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToImmutableArray();
            if (!invalidValues.IsEmpty)
            {
                throw new RuleDefinitionException(new Diagnostic(
                    "RBT2105",
                    DiagnosticSeverity.Error,
                    $"Module '{DefinitionId(moduleType)}' filters fragment '{attribute.Fragment}' by values " +
                    $"that no target matrix allows: {string.Join(", ", invalidValues)}."));
            }
        }
    }

    private static void ValidateReferences(
        ImmutableArray<ModuleDefinition> modules,
        ImmutableArray<TargetDefinition> targets,
        ImmutableArray<WorkspaceDefinition> workspaces)
    {
        var moduleIds = modules.Select(module => module.Id).ToHashSet(StringComparer.Ordinal);
        var targetIds = targets.Select(target => target.Id).ToHashSet(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var module in modules)
        {
            errors.AddRange(module.Dependencies
                .Where(dependency => !moduleIds.Contains(dependency.Module))
                .Select(dependency => $"Module '{module.Id}' references unregistered module '{dependency.Module}'."));
        }

        foreach (var target in targets)
        {
            errors.AddRange(target.RootModules
                .Where(module => !moduleIds.Contains(module))
                .Select(module => $"Target '{target.Id}' references unregistered root module '{module}'."));
            foreach (var requiredFragment in new[]
                     {
                         FragmentIds.Platform,
                         FragmentIds.Architecture,
                         FragmentIds.Profile,
                         FragmentIds.Toolchain,
                         FragmentIds.LinkModel,
                     })
            {
                if (!target.Matrix.Axes.Any(axis => axis.Fragment == requiredFragment))
                {
                    errors.Add($"Target '{target.Id}' matrix is missing required axis '{requiredFragment}'.");
                }
            }
        }

        foreach (var workspace in workspaces)
        {
            errors.AddRange(workspace.Targets
                .Where(target => !targetIds.Contains(target))
                .Select(target => $"Workspace '{workspace.Id}' references unregistered target '{target}'."));
            if (!workspace.Targets.Contains(workspace.StartupTarget, StringComparer.Ordinal))
            {
                errors.Add(
                    $"Workspace '{workspace.Id}' startup target '{workspace.StartupTarget}' is not in its target list.");
            }
        }

        if (errors.Count > 0)
            throw new RuleDefinitionException(new Diagnostic(
                "RBT2102",
                DiagnosticSeverity.Error,
                string.Join(Environment.NewLine, errors.Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal))));
    }
}

internal sealed class SourceFileSnapshotCache
{
    private readonly ConcurrentDictionary<string, Lazy<ImmutableArray<string>>> _snapshots =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public ImmutableArray<string> GetFiles(string absoluteRoot) => _snapshots.GetOrAdd(
        absoluteRoot,
        root => new Lazy<ImmutableArray<string>>(
            () =>
            [
                .. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Order(StringComparer.Ordinal)
            ],
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
}

/// <summary>Defines sources, output, usage requirements, dependencies, and conditional settings for a C++ module.</summary>
public class ModuleRules
{
    private readonly List<ConditionalModuleRule> _conditionalRules = [];
    private readonly List<DependencySpec> _dependencies = [];
    private readonly List<string> _sourceExclusions = [];
    private readonly SourceFileSnapshotCache _sourceFiles;
    private readonly List<(string Root, string Pattern)> _sourcePatterns = [];
    private readonly string _workspaceRoot;

    internal ModuleRules(string workspaceRoot, SourceFileSnapshotCache sourceFiles)
    {
        _workspaceRoot = workspaceRoot;
        _sourceFiles = sourceFiles;
        Sources = new SourceRules(_sourcePatterns, _sourceExclusions);
        Public = new UsageRules();
        Private = new UsageRules();
        Dependencies = new DependencyRules(_dependencies);
        Cxx = new CxxSettingsRules();
    }

    /// <summary>Gets or sets the native output kind.</summary>
    public CxxOutput Output { get; set; } = CxxOutput.StaticLibrary;

    /// <summary>Gets the module source rules.</summary>
    public SourceRules Sources { get; }

    /// <summary>Gets usage requirements exported to consumers.</summary>
    public UsageRules Public { get; }

    /// <summary>Gets usage requirements used only by this module.</summary>
    public UsageRules Private { get; }

    /// <summary>Gets the typed dependency rules.</summary>
    public DependencyRules Dependencies { get; }

    /// <summary>Gets native compiler, linker, output naming, forced-include, and PCH settings.</summary>
    public CxxSettingsRules Cxx { get; }

    /// <summary>Creates conditional mutations applied when <paramref name="value"/> is selected.</summary>
    public ConditionalModuleRules When<T>(T value) where T : struct, Enum =>
        new(FragmentEncoding.Encode(value), _conditionalRules);

    internal virtual ModuleDefinition Build(Type type) => new(
        BuildRegistry.DefinitionId(type),
        type.Name,
        Output switch
        {
            CxxOutput.HeaderOnly => ModuleKind.HeaderOnly,
            CxxOutput.ObjectLibrary => ModuleKind.ObjectLibrary,
            CxxOutput.StaticLibrary => ModuleKind.StaticLibrary,
            CxxOutput.SharedLibrary => ModuleKind.SharedLibrary,
            CxxOutput.Executable => ModuleKind.Executable,
            _ => throw new InvalidOperationException($"Unsupported C++ output kind '{Output}'."),
        },
        ExpandSources(),
        Public.Build(BuildRegistry.DefinitionId(type), "public"),
        Private.Build(BuildRegistry.DefinitionId(type), "private"),
        _dependencies.Select(dependency =>
                new DependencyEdge(BuildRegistry.DefinitionId(dependency.Type), dependency.Visibility))
            .OrderBy(edge => edge.Module, StringComparer.Ordinal).ThenBy(edge => edge.Visibility).ToImmutableArray(),
        _conditionalRules.ToImmutableArray(),
        CxxSettings: Cxx.Build());

    protected ImmutableArray<LogicalPath> ExpandSources()
    {
        var result = new HashSet<string>(LogicalPath.FileSystemComparer);
        foreach (var (root, pattern) in _sourcePatterns)
        {
            var absoluteRoot = Path.GetFullPath(root, _workspaceRoot);
            if (!Directory.Exists(absoluteRoot))
            {
                throw new DirectoryNotFoundException($"Source root '{root}' does not exist under '{_workspaceRoot}'.");
            }

            foreach (var path in _sourceFiles.GetFiles(absoluteRoot))
            {
                var relativeWithinRoot = Path.GetRelativePath(absoluteRoot, path).Replace('\\', '/');
                if (Matches(pattern, relativeWithinRoot) &&
                    !_sourceExclusions.Any(exclusion => Matches(exclusion, relativeWithinRoot)))
                {
                    result.Add(Path.GetRelativePath(_workspaceRoot, path).Replace('\\', '/'));
                }
            }
        }

        return result.Order(StringComparer.Ordinal).Select(path => new LogicalPath(path)).ToImmutableArray();
    }

    private static bool Matches(string pattern, string relativePath)
    {
        var patternSegments = pattern.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pathSegments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var memo = new Dictionary<(int Pattern, int Path), bool>();
        return Match(0, 0);

        bool Match(int patternIndex, int pathIndex)
        {
            if (memo.TryGetValue((patternIndex, pathIndex), out var cached))
                return cached;

            var result = patternIndex == patternSegments.Length
                ? pathIndex == pathSegments.Length
                : patternSegments[patternIndex] == "**"
                    ? Match(patternIndex + 1, pathIndex) ||
                      pathIndex < pathSegments.Length && Match(patternIndex, pathIndex + 1)
                    : pathIndex < pathSegments.Length &&
                      FileSystemName.MatchesSimpleExpression(
                          patternSegments[patternIndex],
                          pathSegments[pathIndex],
                          ignoreCase: OperatingSystem.IsWindows()) &&
                      Match(patternIndex + 1, pathIndex + 1);
            memo[(patternIndex, pathIndex)] = result;
            return result;
        }
    }
}

/// <summary>Collects source include patterns and exclusions relative to the workspace root.</summary>
public sealed class SourceRules(
    List<(string Root, string Pattern)> patterns,
    List<string> exclusions)
{
    /// <summary>Adds files matching <paramref name="pattern"/> below <paramref name="root"/>.</summary>
    public void From(string root, string pattern) => patterns.Add((root, pattern));

    /// <summary>Excludes source paths matching <paramref name="pattern"/>.</summary>
    public void Exclude(string pattern) => exclusions.Add(pattern);
}

/// <summary>Collects include, define, link, and runtime requirements for one visibility scope.</summary>
public sealed class UsageRules
{
    private readonly List<string> _defines = [];
    private readonly List<string> _includes = [];
    private readonly List<string> _links = [];
    private readonly List<string> _runtime = [];
    private readonly List<string> _systemIncludes = [];

    public StringRules IncludeDirectories => new(_includes);
    public StringRules Defines => new(_defines);
    public StringRules LinkInputs => new(_links);
    public StringRules RuntimeFiles => new(_runtime);
    public StringRules SystemIncludeDirectories => new(_systemIncludes);

    internal UsageRequirements Build(string module, string visibility) => new(
        Values(_includes, module, visibility, LogicalPath.FileSystemComparer, true),
        Values(_defines, module, visibility, StringComparer.Ordinal, false),
        Values(_links, module, visibility, LogicalPath.FileSystemComparer, true),
        Values(_runtime, module, visibility, LogicalPath.FileSystemComparer, true),
        Values(_systemIncludes, module, visibility, LogicalPath.FileSystemComparer, true));

    private static ImmutableArray<UsageValue> Values(
        IEnumerable<string> values,
        string module,
        string visibility,
        StringComparer comparer,
        bool normalizePath)
    {
        return values
            .Select(value => normalizePath ? value.Replace('\\', '/') : value)
            .Distinct(comparer).Order(StringComparer.Ordinal)
            .Select(value => new UsageValue(value, $"{module}:{visibility}"))
            .ToImmutableArray();
    }
}

/// <summary>Collects settings that affect native compilation and linking but are not propagated as usage.</summary>
public sealed class CxxSettingsRules
{
    private readonly List<string> _compilerArguments = [];
    private readonly List<string> _forcedIncludes = [];
    private readonly List<string> _librarianArguments = [];
    private readonly List<string> _linkerArguments = [];

    /// <summary>Gets additional structured compiler arguments.</summary>
    public StringRules CompilerArguments => new(_compilerArguments);

    /// <summary>Gets additional structured linker arguments.</summary>
    public StringRules LinkerArguments => new(_linkerArguments);

    /// <summary>Gets additional structured librarian arguments.</summary>
    public StringRules LibrarianArguments => new(_librarianArguments);

    /// <summary>Gets workspace-relative headers forcibly included by every translation unit.</summary>
    public StringRules ForcedIncludes => new(_forcedIncludes);

    /// <summary>Gets or sets the workspace-relative precompiled-header path.</summary>
    public string? PrecompiledHeader { get; set; }

    /// <summary>Gets or sets the workspace-relative source that creates the precompiled header.</summary>
    public string? PrecompiledSource { get; set; }

    /// <summary>Gets or sets the extensionless binary output name. The module ID is used by default.</summary>
    public string? OutputName { get; set; }

    internal bool IsEmpty => _compilerArguments.Count == 0 && _linkerArguments.Count == 0 &&
                             _librarianArguments.Count == 0 && _forcedIncludes.Count == 0 &&
                             PrecompiledHeader is null && PrecompiledSource is null && OutputName is null;

    internal CxxModuleSettings Build()
    {
        if (PrecompiledHeader is null != PrecompiledSource is null)
            throw InvalidNativeSettings(
                "PrecompiledHeader and PrecompiledSource must either both be set or both be null.");

        if (OutputName is not null &&
            (OutputName.Length == 0 || OutputName.IndexOfAny(['/', '\\']) >= 0 ||
             OutputName is "." or ".."))
            throw InvalidNativeSettings(
                $"Native output name '{OutputName}' must be a non-empty file stem without directory separators.");

        return new CxxModuleSettings(
            Normalize(_compilerArguments),
            Normalize(_linkerArguments),
            Normalize(_librarianArguments),
            [.. _forcedIncludes.Distinct(LogicalPath.FileSystemComparer).Select(path => new LogicalPath(path))],
            PrecompiledHeader is null ? null : new LogicalPath(PrecompiledHeader),
            PrecompiledSource is null ? null : new LogicalPath(PrecompiledSource),
            OutputName);
    }

    private static ImmutableArray<string> Normalize(IEnumerable<string> values)
    {
        return [.. values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal)];
    }

    private static RuleDefinitionException InvalidNativeSettings(string message)
    {
        return new RuleDefinitionException(new Diagnostic("RBT2103", DiagnosticSeverity.Error, message));
    }
}

/// <summary>Provides an append-only collection surface for string settings.</summary>
public sealed class StringRules(List<string> values)
{
    /// <summary>Adds a value.</summary>
    public void Add(string value) => values.Add(value);
}

internal sealed record DependencySpec(Type Type, DependencyVisibility Visibility);

/// <summary>Collects typed module dependencies and their propagation semantics.</summary>
public sealed class DependencyRules
{
    private readonly List<DependencySpec> _dependencies;

    internal DependencyRules(List<DependencySpec> dependencies) => _dependencies = dependencies;

    /// <summary>Adds a dependency used by the current module but not exported.</summary>
    public void Private<T>() where T : class => _dependencies.Add(new(typeof(T), DependencyVisibility.Private));

    /// <summary>Adds a dependency used by the current module and exported to consumers.</summary>
    public void Public<T>() where T : class => _dependencies.Add(new(typeof(T), DependencyVisibility.Public));

    /// <summary>Adds a dependency exported to consumers but not used to compile the current module.</summary>
    public void Interface<T>() where T : class => _dependencies.Add(new(typeof(T), DependencyVisibility.Interface));

    /// <summary>Adds an action-ordering dependency without usage propagation.</summary>
    public void BuildOrderOnly<T>() where T : class =>
        _dependencies.Add(new(typeof(T), DependencyVisibility.BuildOrderOnly));

    /// <summary>Adds a dependency whose runtime files are staged for the current module.</summary>
    public void Runtime<T>() where T : class => _dependencies.Add(new(typeof(T), DependencyVisibility.Runtime));
}

/// <summary>Builds module mutations applied for one selected fragment value.</summary>
public sealed class ConditionalModuleRules
{
    private readonly List<string> _defines = [];
    private readonly FragmentValue _match;
    private readonly List<string> _removeDependencies = [];
    private readonly List<ConditionalModuleRule> _rules;
    private bool _disable;

    internal ConditionalModuleRules(FragmentValue match, List<ConditionalModuleRule> rules)
    {
        _match = match;
        _rules = rules;
        _rules.Add(Snapshot());
    }

    /// <summary>Disables the module for the matching configuration.</summary>
    public ConditionalModuleRules Disable()
    {
        _disable = true;
        Update();
        return this;
    }

    /// <summary>Adds a private preprocessor definition for the matching configuration.</summary>
    public ConditionalModuleRules AddDefine(string value)
    {
        _defines.Add(value);
        Update();
        return this;
    }

    /// <summary>Removes a dependency for the matching configuration.</summary>
    public ConditionalModuleRules RemoveDependency<T>() where T : class
    {
        _removeDependencies.Add(BuildRegistry.DefinitionId(typeof(T)));
        Update();
        return this;
    }

    private void Update() => _rules[^1] = Snapshot();

    private ConditionalModuleRule Snapshot() => new(_match, _disable, _defines.ToImmutableArray(),
        _removeDependencies.ToImmutableArray());
}

/// <summary>Defines a target's root modules and configuration matrix.</summary>
public sealed class TargetRules
{
    private readonly List<Type> _roots = [];

    /// <summary>Gets the target's root module collection.</summary>
    public TypeRules RootModules => new(_roots);

    /// <summary>Gets the target's configuration matrix builder.</summary>
    public MatrixBuilder Matrix { get; } = new();

    /// <summary>Adds an entry module to the target.</summary>
    public void EntryModule<T>() where T : class => _roots.Add(typeof(T));

    internal TargetDefinition Build(Type type) => new(
        BuildRegistry.DefinitionId(type),
        type.Name,
        _roots.Select(BuildRegistry.DefinitionId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .ToImmutableArray(),
        Matrix.Build());
}

/// <summary>Provides a typed append-only collection for rule types.</summary>
public sealed class TypeRules(List<Type> types)
{
    /// <summary>Adds rule type <typeparamref name="T"/>.</summary>
    public void Add<T>() where T : class => types.Add(typeof(T));
}

/// <summary>Defines the targets and startup behavior of a generated workspace.</summary>
public sealed class WorkspaceRules
{
    private readonly List<Type> _targets = [];
    private Type? _startup;

    /// <summary>Gets the workspace target collection.</summary>
    public TypeRules Targets => new(_targets);

    /// <summary>Selects the startup target.</summary>
    public void StartupTarget<T>() where T : BuildTarget => _startup = typeof(T);

    internal WorkspaceDefinition Build(Type type)
    {
        if (_targets.Count == 0)
        {
            throw new InvalidOperationException($"Workspace '{type.Name}' has no targets.");
        }

        var startup = _startup ?? _targets[0];
        return new(
            BuildRegistry.DefinitionId(type),
            type.Name,
            _targets.Select(BuildRegistry.DefinitionId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .ToImmutableArray(),
            BuildRegistry.DefinitionId(startup));
    }
}