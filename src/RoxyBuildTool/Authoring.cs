using System.Collections.Immutable;
using System.IO.Enumeration;
using System.Reflection;
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

/// <summary>Base type for native C++ module definitions.</summary>
public abstract class CxxModule
{
}

/// <summary>Base type for managed C# module definitions.</summary>
public abstract class CSharpModule
{
}

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
            throw new ArgumentException("A filtered [Configure] attribute requires at least one value.", nameof(values));
        }
    }

    /// <summary>Gets the fragment used by the filter, or <see langword="null"/> for an unconditional method.</summary>
    public FragmentId? Fragment { get; }
    /// <summary>Gets the accepted fragment values.</summary>
    public ImmutableArray<FragmentValue> Values { get; }
    /// <summary>Gets or sets the method ordering priority. Lower values run first.</summary>
    public int Priority { get; set; }
    internal bool IsUnconditional => Fragment is null;
}

/// <summary>Marks a configuration method filtered by values of enum fragment <typeparamref name="TFragment"/>.</summary>
public sealed class ConfigureAttribute<TFragment> : ConfigureAttribute where TFragment : struct, Enum
{
    /// <summary>Creates a filter from stable enum value names.</summary>
    public ConfigureAttribute(params string[] values)
        : base(GetFragmentId(), values.Select(FragmentRegistry.ToPascalCase).ToArray())
    {
    }

    private static string GetFragmentId() =>
        typeof(TFragment).GetCustomAttribute<BuildFragmentAttribute>()?.Id
        ?? throw new InvalidOperationException($"Enum '{typeof(TFragment).FullName}' must have [BuildFragment].");
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

/// <summary>Specifies the artifact produced by a C# module.</summary>
public enum CSharpOutput
{
    ClassLibrary,
    ConsoleApplication,
}

/// <summary>Collects discovered rules and converts them into immutable definitions.</summary>
public sealed class BuildRegistry(string workspaceRoot)
{
    private readonly List<Type> _modules = [];
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
                     .Where(type => type is { IsAbstract: false, ContainsGenericParameters: false })
                     .OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            if (typeof(CxxModule).IsAssignableFrom(type) || typeof(CSharpModule).IsAssignableFrom(type))
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
        var modules = _modules.Select(BuildModule).OrderBy(module => module.Id, StringComparer.Ordinal).ToImmutableArray();
        var targets = _targets.Select(BuildTargetDefinition).OrderBy(target => target.Id, StringComparer.Ordinal).ToImmutableArray();
        var workspaces = _workspaces.Select(BuildWorkspaceDefinition).OrderBy(workspace => workspace.Id, StringComparer.Ordinal).ToImmutableArray();
        return new(modules, targets, workspaces);
    }

    /// <summary>Derives a stable definition ID from a rule type name.</summary>
    public static string DefinitionId(Type type)
    {
        var name = type.Name;
        foreach (var suffix in new[] { "Module", "Target", "Workspace" })
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        return FragmentRegistry.ToPascalCase(name);
    }

    private ModuleDefinition BuildModule(Type type)
    {
        if (typeof(CxxModule).IsAssignableFrom(type))
        {
            var module = (CxxModule)Activator.CreateInstance(type)!;
            return BuildCxxDefinition(module, type, configuration: null) with
            {
                ConfigureForConfiguration = configuration => BuildCxxDefinition(module, type, configuration),
            };
        }

        if (typeof(CSharpModule).IsAssignableFrom(type))
        {
            var module = (CSharpModule)Activator.CreateInstance(type)!;
            return BuildCSharpDefinition(module, type, configuration: null) with
            {
                ConfigureForConfiguration = configuration => BuildCSharpDefinition(module, type, configuration),
            };
        }

        throw new InvalidOperationException($"Registered module '{type.FullName}' must derive from CxxModule or CSharpModule.");
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
        var rules = new ModuleRules(workspaceRoot);
        ApplyConfigureMethods(module, rules, configuration, allowFiltered: true);
        return rules.Build(type);
    }

    private ModuleDefinition BuildCSharpDefinition(CSharpModule module, Type type, ConfigurationKey? configuration)
    {
        var rules = new CSharpModuleRules(workspaceRoot);
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
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy)
            .Select(method => (Method: method, Attributes: method.GetCustomAttributes<ConfigureAttribute>(inherit: true).ToImmutableArray()))
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

            item.Method.Invoke(item.Method.IsStatic ? null : instance, [rules]);
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
            throw new InvalidOperationException($"[Configure] method '{method.DeclaringType?.FullName}.{method.Name}' must return void.");
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
}

/// <summary>Defines sources, output, usage requirements, dependencies, and conditional settings for a C++ module.</summary>
public class ModuleRules
{
    private readonly string _workspaceRoot;
    private readonly List<(string Root, string Pattern)> _sourcePatterns = [];
    private readonly List<string> _sourceExclusions = [];
    private readonly List<DependencySpec> _dependencies = [];
    private readonly List<ConditionalModuleRule> _conditionalRules = [];

    internal ModuleRules(string workspaceRoot)
    {
        _workspaceRoot = workspaceRoot;
        Sources = new SourceRules(_sourcePatterns, _sourceExclusions);
        Public = new UsageRules();
        Private = new UsageRules();
        Dependencies = new DependencyRules(_dependencies);
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

    /// <summary>Creates conditional mutations applied when <paramref name="value"/> is selected.</summary>
    public ConditionalModuleRules When<T>(T value) where T : struct, Enum =>
        new(FragmentEncoding.Encode(value), _conditionalRules);

    internal virtual ModuleDefinition Build(Type type) => new(
        BuildRegistry.DefinitionId(type),
        type.Name,
        ModuleLanguage.Cxx,
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
        _dependencies.Select(dependency => new DependencyEdge(BuildRegistry.DefinitionId(dependency.Type), dependency.Visibility))
            .OrderBy(edge => edge.Module, StringComparer.Ordinal).ThenBy(edge => edge.Visibility).ToImmutableArray(),
        [],
        [],
        _conditionalRules.ToImmutableArray());

    protected ImmutableArray<LogicalPath> ExpandSources()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (root, pattern) in _sourcePatterns)
        {
            var absoluteRoot = Path.GetFullPath(root, _workspaceRoot);
            if (!Directory.Exists(absoluteRoot))
            {
                throw new DirectoryNotFoundException($"Source root '{root}' does not exist under '{_workspaceRoot}'.");
            }

            foreach (var path in Directory.EnumerateFiles(absoluteRoot, "*", SearchOption.AllDirectories))
            {
                var relativeWithinRoot = Path.GetRelativePath(absoluteRoot, path).Replace('\\', '/');
                if (Matches(pattern, relativeWithinRoot) && !_sourceExclusions.Any(exclusion => Matches(exclusion, relativeWithinRoot)))
                {
                    result.Add(Path.GetRelativePath(_workspaceRoot, path).Replace('\\', '/'));
                }
            }
        }

        return result.Order(StringComparer.Ordinal).Select(path => new LogicalPath(path)).ToImmutableArray();
    }

    private static bool Matches(string pattern, string relativePath)
    {
        var normalizedPattern = pattern.Replace('\\', '/');
        if (normalizedPattern.StartsWith("**/", StringComparison.Ordinal))
        {
            normalizedPattern = normalizedPattern[3..];
            return FileSystemName.MatchesSimpleExpression(normalizedPattern, Path.GetFileName(relativePath), ignoreCase: true);
        }
        return FileSystemName.MatchesSimpleExpression(normalizedPattern, relativePath, ignoreCase: true);
    }
}

/// <summary>Defines managed output, target frameworks, packages, and shared module settings for a C# module.</summary>
public sealed class CSharpModuleRules : ModuleRules
{
    private readonly List<string> _targetFrameworks = [];
    private readonly List<PackageReferenceModel> _packages = [];

    internal CSharpModuleRules(string workspaceRoot) : base(workspaceRoot)
    {
        TargetFrameworks = new StringRules(_targetFrameworks);
        Packages = new PackageRules(_packages);
    }

    /// <summary>Gets or sets the managed output kind.</summary>
    public CSharpOutput ManagedOutput { get; set; } = CSharpOutput.ClassLibrary;
    /// <summary>Gets the target framework collection.</summary>
    public StringRules TargetFrameworks { get; }
    /// <summary>Gets the package reference collection.</summary>
    public PackageRules Packages { get; }
    /// <summary>Gets or sets the root namespace emitted into the generated project.</summary>
    public string? RootNamespace { get; set; }

    internal override ModuleDefinition Build(Type type)
    {
        var native = base.Build(type);
        return native with
        {
            Language = ModuleLanguage.CSharp,
            Kind = ManagedOutput == CSharpOutput.ClassLibrary
                ? ModuleKind.CSharpClassLibrary
                : ModuleKind.CSharpConsoleApplication,
            TargetFrameworks = (_targetFrameworks.Count == 0 ? ["net10.0"] : _targetFrameworks)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray(),
            Packages = _packages.Distinct().OrderBy(package => package.Id, StringComparer.Ordinal).ToImmutableArray(),
            RootNamespace = RootNamespace,
        };
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
    private readonly List<string> _includes = [];
    private readonly List<string> _defines = [];
    private readonly List<string> _links = [];
    private readonly List<string> _runtime = [];

    public StringRules IncludeDirectories => new(_includes);
    public StringRules Defines => new(_defines);
    public StringRules LinkInputs => new(_links);
    public StringRules RuntimeFiles => new(_runtime);

    internal UsageRequirements Build(string module, string visibility) => new(
        Values(_includes, module, visibility),
        Values(_defines, module, visibility),
        Values(_links, module, visibility),
        Values(_runtime, module, visibility));

    private static ImmutableArray<UsageValue> Values(IEnumerable<string> values, string module, string visibility) => values
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
        .Select(value => new UsageValue(value.Replace('\\', '/'), $"{module}:{visibility}"))
        .ToImmutableArray();
}

/// <summary>Provides an append-only collection surface for string settings.</summary>
public sealed class StringRules(List<string> values)
{
    /// <summary>Adds a value.</summary>
    public void Add(string value) => values.Add(value);
}

/// <summary>Collects package references for a generated C# project.</summary>
public sealed class PackageRules(List<PackageReferenceModel> packages)
{
    /// <summary>Adds a package reference.</summary>
    public void Add(string id, string version, bool privateAssets = false) => packages.Add(new(id, version, privateAssets));
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
    public void BuildOrderOnly<T>() where T : class => _dependencies.Add(new(typeof(T), DependencyVisibility.BuildOrderOnly));
    /// <summary>Adds a dependency whose runtime files are staged for the current module.</summary>
    public void Runtime<T>() where T : class => _dependencies.Add(new(typeof(T), DependencyVisibility.Runtime));
}

/// <summary>Builds module mutations applied for one selected fragment value.</summary>
public sealed class ConditionalModuleRules
{
    private readonly FragmentValue _match;
    private readonly List<ConditionalModuleRule> _rules;
    private bool _disable;
    private readonly List<string> _defines = [];
    private readonly List<string> _removeDependencies = [];

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
    private ConditionalModuleRule Snapshot() => new(_match, _disable, _defines.ToImmutableArray(), _removeDependencies.ToImmutableArray());
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
        _roots.Select(BuildRegistry.DefinitionId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray(),
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
    /// <summary>Gets or sets whether the project-local build host is included in the workspace.</summary>
    public bool IncludeBuildHost { get; set; } = true;
    /// <summary>Gets or sets the workspace-relative build-host project path.</summary>
    public string BuildHostProject { get; set; } = "Build/RoxyBuild.csproj";
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
            _targets.Select(BuildRegistry.DefinitionId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray(),
            BuildRegistry.DefinitionId(startup),
            IncludeBuildHost,
            IncludeBuildHost ? new(BuildHostProject) : null);
    }
}
