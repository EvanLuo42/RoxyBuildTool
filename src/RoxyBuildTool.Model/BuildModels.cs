using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using RoxyBuildTool.Abstractions;

namespace RoxyBuildTool.Model;

/// <summary>Associates a stable fragment ID with one normalized value.</summary>
public readonly partial record struct FragmentValue : IComparable<FragmentValue>
{
    /// <summary>Creates a fragment assignment.</summary>
    public FragmentValue(FragmentId fragment, string value)
    {
        Fragment = fragment;
        Value = ValidateValue(value);
    }

    public FragmentId Fragment { get; }
    public string Value { get; }

    private static string ValidateValue(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = ConfigurationlessPascalCase.Normalize(value);
        if (!StableIdPattern().IsMatch(normalized))
        {
            throw new ArgumentException($"'{value}' is not a stable fragment value ID.", nameof(value));
        }

        return normalized;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Z][A-Za-z0-9]*(?:\\.[A-Z0-9][A-Za-z0-9]*)*$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex StableIdPattern();

    public int CompareTo(FragmentValue other)
    {
        var fragmentComparison = Fragment.CompareTo(other.Fragment);
        return fragmentComparison != 0
            ? fragmentComparison
            : StringComparer.Ordinal.Compare(Value, other.Value);
    }

    public static bool operator <(FragmentValue left, FragmentValue right) => left.CompareTo(right) < 0;
    public static bool operator <=(FragmentValue left, FragmentValue right) => left.CompareTo(right) <= 0;
    public static bool operator >(FragmentValue left, FragmentValue right) => left.CompareTo(right) > 0;
    public static bool operator >=(FragmentValue left, FragmentValue right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Fragment}={Value}";
}

internal static class ConfigurationlessPascalCase
{
    public static string Normalize(string value)
    {
        var result = new StringBuilder(value.Length);
        var capitalizeNext = true;
        foreach (var character in value)
        {
            if (character == '.')
            {
                result.Append(character);
                capitalizeNext = true;
            }
            else if (character is '-' or '_')
            {
                capitalizeNext = true;
            }
            else
            {
                result.Append(capitalizeNext ? char.ToUpperInvariant(character) : character);
                capitalizeNext = false;
            }
        }

        return result.ToString();
    }
}

/// <summary>Represents a complete, canonical set of fragment assignments.</summary>
public sealed record ConfigurationKey : IComparable<ConfigurationKey>
{
    /// <summary>Creates a key, rejecting duplicate fragments and sorting values deterministically.</summary>
    public ConfigurationKey(IEnumerable<FragmentValue> values)
    {
        var ordered = values.Order().ToImmutableArray();
        var duplicate = ordered.GroupBy(value => value.Fragment).FirstOrDefault(group => group.Count() != 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Configuration contains multiple values for fragment '{duplicate.Key}'.", nameof(values));
        }

        Values = ordered;
        Canonical = string.Join(';', ordered);
    }

    public ImmutableArray<FragmentValue> Values { get; }
    public string Canonical { get; }
    public string ShortHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical)))[..12].ToLowerInvariant();

    /// <summary>Attempts to get the assignment for <paramref name="fragment"/>.</summary>
    public bool TryGet(FragmentId fragment, out FragmentValue value)
    {
        foreach (var candidate in Values)
        {
            if (candidate.Fragment == fragment)
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>Returns whether this configuration contains <paramref name="value"/>.</summary>
    public bool Is(FragmentValue value) => TryGet(value.Fragment, out var actual) && actual == value;
    public int CompareTo(ConfigurationKey? other) => other is null ? 1 : StringComparer.Ordinal.Compare(Canonical, other.Canonical);
    public static bool operator <(ConfigurationKey? left, ConfigurationKey? right) => Comparer<ConfigurationKey>.Default.Compare(left, right) < 0;
    public static bool operator <=(ConfigurationKey? left, ConfigurationKey? right) => Comparer<ConfigurationKey>.Default.Compare(left, right) <= 0;
    public static bool operator >(ConfigurationKey? left, ConfigurationKey? right) => Comparer<ConfigurationKey>.Default.Compare(left, right) > 0;
    public static bool operator >=(ConfigurationKey? left, ConfigurationKey? right) => Comparer<ConfigurationKey>.Default.Compare(left, right) >= 0;
    public override string ToString() => Canonical;
}

/// <summary>Represents a normalized, workspace-relative path.</summary>
public readonly record struct LogicalPath
{
    /// <summary>Creates a logical path and rejects rooted paths and parent traversal.</summary>
    public LogicalPath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value == ".")
        {
            Value = ".";
            return;
        }
        var normalized = value.Replace('\\', '/').TrimStart('/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }
        if (Path.IsPathRooted(value) || normalized.Split('/').Contains("..", StringComparer.Ordinal))
        {
            throw new ArgumentException($"Logical path '{value}' must be workspace-relative.", nameof(value));
        }

        Value = normalized;
    }

    public string Value { get; }
    public string FileName => Path.GetFileName(Value);
    public override string ToString() => Value;
}

/// <summary>Identifies the implementation language of a configured module.</summary>
public enum ModuleLanguage
{
    Cxx,
    CSharp,
}

/// <summary>Identifies the artifact shape of a configured module.</summary>
public enum ModuleKind
{
    HeaderOnly,
    ObjectLibrary,
    StaticLibrary,
    SharedLibrary,
    Executable,
    CSharpClassLibrary,
    CSharpConsoleApplication,
}

/// <summary>Defines how a module dependency affects compilation, consumers, runtime files, and ordering.</summary>
public enum DependencyVisibility
{
    Private,
    Public,
    Interface,
    BuildOrderOnly,
    Runtime,
}

/// <summary>Connects a module to a dependency with explicit propagation semantics.</summary>
public sealed record DependencyEdge(string Module, DependencyVisibility Visibility);

/// <summary>Describes a NuGet package reference emitted into a generated managed project.</summary>
public sealed record PackageReferenceModel(string Id, string Version, bool PrivateAssets = false);

/// <summary>Associates a propagated usage value with the definition that introduced it.</summary>
public sealed record UsageValue(string Value, string Origin);

/// <summary>Contains include, define, link, and runtime requirements with origin tracking.</summary>
public sealed record UsageRequirements(
    ImmutableArray<UsageValue> IncludeDirectories,
    ImmutableArray<UsageValue> Defines,
    ImmutableArray<UsageValue> LinkInputs,
    ImmutableArray<UsageValue> RuntimeFiles)
{
    public static UsageRequirements Empty { get; } = new([], [], [], []);

    /// <summary>Combines requirements by value using deterministic origin selection.</summary>
    public UsageRequirements Union(UsageRequirements other) => new(
        Normalize(IncludeDirectories.AddRange(other.IncludeDirectories)),
        Normalize(Defines.AddRange(other.Defines)),
        Normalize(LinkInputs.AddRange(other.LinkInputs)),
        Normalize(RuntimeFiles.AddRange(other.RuntimeFiles)));

    private static ImmutableArray<UsageValue> Normalize(IEnumerable<UsageValue> values) => values
        .GroupBy(value => value.Value, StringComparer.Ordinal)
        .Select(group => group.OrderBy(value => value.Origin, StringComparer.Ordinal).First())
        .OrderBy(value => value.Value, StringComparer.Ordinal)
        .ThenBy(value => value.Origin, StringComparer.Ordinal)
        .ToImmutableArray();
}

/// <summary>Represents one enabled module after configuration and dependency propagation.</summary>
public sealed record ConfiguredModule(
    string Id,
    string DisplayName,
    ModuleLanguage Language,
    ModuleKind Kind,
    ImmutableArray<LogicalPath> Sources,
    UsageRequirements PublicUsage,
    UsageRequirements PrivateUsage,
    UsageRequirements CompileUsage,
    UsageRequirements ConsumerUsage,
    ImmutableArray<DependencyEdge> Dependencies,
    ImmutableArray<string> TargetFrameworks,
    ImmutableArray<PackageReferenceModel> Packages,
    string? RootNamespace = null);

/// <summary>Identifies the target and root modules of a configured graph.</summary>
public sealed record ConfiguredTarget(
    string Id,
    string DisplayName,
    ImmutableArray<string> RootModules);

/// <summary>Contains the resolved module graph for one target configuration.</summary>
public sealed record ConfiguredGraph(
    ConfigurationKey Configuration,
    ConfiguredTarget Target,
    ImmutableArray<ConfiguredModule> Modules,
    ImmutableArray<Diagnostic> Diagnostics)
{
    /// <summary>Gets the module with stable ID <paramref name="id"/>.</summary>
    public ConfiguredModule GetModule(string id) => Modules.Single(module => module.Id == id);
}

/// <summary>Identifies the semantic kind of a produced build artifact.</summary>
public enum ArtifactKind
{
    ObjectFile,
    StaticLibrary,
    SharedLibrary,
    Executable,
    GeneratedProject,
    ManagedAssembly,
    RuntimeFile,
}

/// <summary>Describes a typed artifact and the action that produces it.</summary>
public sealed record BuildArtifact(string Id, ArtifactKind Kind, LogicalPath Path, string ProducerAction);

/// <summary>Identifies the operation performed by a build action.</summary>
public enum BuildActionKind
{
    Compile,
    Archive,
    Link,
    Copy,
    DotNetRestore,
    DotNetBuild,
}

/// <summary>Describes one structured, dependency-ordered build operation.</summary>
public sealed record BuildAction(
    string Id,
    BuildActionKind Kind,
    string Command,
    ImmutableArray<string> Arguments,
    LogicalPath WorkingDirectory,
    ImmutableArray<string> Inputs,
    ImmutableArray<string> Outputs,
    ImmutableArray<string> Dependencies,
    ImmutableArray<string> EnvironmentWhitelist,
    bool Cacheable,
    bool RemoteExecutable,
    ImmutableArray<string> SensitiveArguments)
{
    /// <summary>Gets the SHA-256 hash of fields that determine action semantics.</summary>
    public string SemanticHash
    {
        get
        {
            var semantic = string.Join('\n',
                Id,
                Kind.ToString(),
                Command,
                string.Join('\0', Arguments),
                WorkingDirectory.Value,
                string.Join('\0', Inputs),
                string.Join('\0', Outputs),
                string.Join('\0', Dependencies));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(semantic))).ToLowerInvariant();
        }
    }
}

/// <summary>Contains ordered actions and artifacts for one target configuration.</summary>
public sealed record ActionGraph(
    ConfigurationKey Configuration,
    string Target,
    ImmutableArray<BuildAction> Actions,
    ImmutableArray<BuildArtifact> Artifacts)
{
    /// <summary>Validates output ownership and action dependency references.</summary>
    public ImmutableArray<Diagnostic> Validate()
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var ids = Actions.Select(action => action.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var duplicate in Actions.SelectMany(action => action.Outputs.Select(output => (output, action.Id)))
                     .GroupBy(pair => pair.output, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            diagnostics.Add(new("RBT3002", DiagnosticSeverity.Error,
                $"Output '{duplicate.Key}' has multiple producers: {string.Join(", ", duplicate.Select(pair => pair.Id))}."));
        }

        foreach (var action in Actions)
        {
            foreach (var dependency in action.Dependencies.Where(dependency => !ids.Contains(dependency)))
            {
                diagnostics.Add(new("RBT3003", DiagnosticSeverity.Error,
                    $"Action '{action.Id}' depends on missing action '{dependency}'."));
            }
        }

        return diagnostics.ToImmutable();
    }
}

/// <summary>Connects a workspace project to one target configuration and module.</summary>
public sealed record WorkspaceProjectVariant(
    string Target,
    ConfigurationKey Configuration,
    ConfiguredModule Module);

/// <summary>Represents one generated or imported project in a workspace.</summary>
public sealed record WorkspaceProject(
    string Id,
    string Name,
    ModuleLanguage Language,
    ImmutableArray<WorkspaceProjectVariant> Variants,
    ImmutableArray<string> ProjectDependencies,
    bool IsBuildHost = false,
    LogicalPath? ImportedProject = null);

/// <summary>Contains the generator-neutral representation of a complete workspace.</summary>
public sealed record WorkspaceModel(
    string Name,
    string StartupTarget,
    ImmutableArray<WorkspaceProject> Projects,
    ImmutableArray<ConfiguredGraph> ConfiguredGraphs,
    ImmutableArray<ActionGraph> ActionGraphs);

/// <summary>Contains one generator-relative output file.</summary>
public sealed record GeneratedFile(LogicalPath Path, string Content);

/// <summary>Provides invocation-specific paths to a workspace generator.</summary>
public sealed record GenerationContext(string WorkspaceRoot, LogicalPath OutputDirectory);

/// <summary>Contains files and diagnostics returned by a workspace generator.</summary>
public sealed record GenerationResult(
    WorkspaceGeneratorId Generator,
    ImmutableArray<GeneratedFile> Files,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>Projects an immutable workspace model into IDE or tooling files.</summary>
public interface IWorkspaceGenerator
{
    /// <summary>Gets the stable generator ID.</summary>
    WorkspaceGeneratorId Id { get; }
    /// <summary>Gets the generator capabilities.</summary>
    CapabilitySet Capabilities { get; }
    /// <summary>Generates files for <paramref name="workspace"/>.</summary>
    GenerationResult Generate(WorkspaceModel workspace, GenerationContext context);
}
