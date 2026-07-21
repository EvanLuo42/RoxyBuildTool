using System.Collections;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

    public int CompareTo(FragmentValue other)
    {
        var fragmentComparison = Fragment.CompareTo(other.Fragment);
        return fragmentComparison != 0
            ? fragmentComparison
            : StringComparer.Ordinal.Compare(Value, other.Value);
    }

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

    [GeneratedRegex("^[A-Z][A-Za-z0-9]*(?:\\.[A-Z0-9][A-Za-z0-9]*)*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex StableIdPattern();

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
            throw new ArgumentException($"Configuration contains multiple values for fragment '{duplicate.Key}'.",
                nameof(values));
        }

        Values = ordered;
        Canonical = string.Join(';', ordered);
        ShortHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical)))[..12]
            .ToLowerInvariant();
    }

    public ImmutableArray<FragmentValue> Values { get; }
    public string Canonical { get; }

    public string ShortHash { get; }

    public int CompareTo(ConfigurationKey? other)
    {
        return other is null ? 1 : StringComparer.Ordinal.Compare(Canonical, other.Canonical);
    }

    public bool Equals(ConfigurationKey? other)
    {
        return other is not null && Canonical.Equals(other.Canonical, StringComparison.Ordinal);
    }

    public override int GetHashCode()
    {
        return StringComparer.Ordinal.GetHashCode(Canonical);
    }

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

    public static bool operator <(ConfigurationKey? left, ConfigurationKey? right)
    {
        return Comparer<ConfigurationKey>.Default.Compare(left, right) < 0;
    }

    public static bool operator <=(ConfigurationKey? left, ConfigurationKey? right)
    {
        return Comparer<ConfigurationKey>.Default.Compare(left, right) <= 0;
    }

    public static bool operator >(ConfigurationKey? left, ConfigurationKey? right)
    {
        return Comparer<ConfigurationKey>.Default.Compare(left, right) > 0;
    }

    public static bool operator >=(ConfigurationKey? left, ConfigurationKey? right)
    {
        return Comparer<ConfigurationKey>.Default.Compare(left, right) >= 0;
    }

    public override string ToString() => Canonical;
}

/// <summary>Builds configuration-isolated logical paths shared by graph lowering and generators.</summary>
public static class BuildPathLayout
{
    public static string OutputRoot(ConfigurationKey configuration, string target)
    {
        var platform = Fragment(configuration, "Platform").ToLowerInvariant();
        var architecture = Fragment(configuration, "Architecture").ToLowerInvariant();
        var profile = Fragment(configuration, "Profile").ToLowerInvariant();
        return $"out/{platform}/{architecture}/{profile}/{configuration.ShortHash}/{target}";
    }

    public static string IntermediateRoot(ConfigurationKey configuration, string target)
    {
        return $"intermediate/{configuration.ShortHash}/{target}";
    }

    public static string ObjectFile(
        ConfigurationKey configuration,
        string target,
        string module,
        LogicalPath source)
    {
        return ObjectFile(configuration, target, module, source, StableToken(source.Value));
    }

    /// <summary>Builds an object path using a stable token already computed for <paramref name="source" />.</summary>
    public static string ObjectFile(
        ConfigurationKey configuration,
        string target,
        string module,
        LogicalPath source,
        string sourceToken)
    {
        return $"{IntermediateRoot(configuration, target)}/{module}/{SanitizedStem(source)}-" +
               $"{sourceToken}.obj";
    }

    public static string ResourceFile(
        ConfigurationKey configuration,
        string target,
        string module,
        LogicalPath source)
    {
        return ResourceFile(configuration, target, module, source, StableToken(source.Value));
    }

    /// <summary>Builds a resource path using a stable token already computed for <paramref name="source" />.</summary>
    public static string ResourceFile(
        ConfigurationKey configuration,
        string target,
        string module,
        LogicalPath source,
        string sourceToken)
    {
        return $"{IntermediateRoot(configuration, target)}/{module}/{SanitizedStem(source)}-" +
               $"{sourceToken}.res";
    }

    public static string PrecompiledHeaderFile(
        ConfigurationKey configuration,
        string target,
        string module)
    {
        return $"{IntermediateRoot(configuration, target)}/{module}/{module}.pch";
    }

    public static string StableToken(string value, int length = 12)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, SHA256.HashSizeInBytes * 2);

        var byteCount = Encoding.UTF8.GetByteCount(value);
        var encoded = byteCount <= 256 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(value, encoded);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(encoded, hash);
        var hashedBytes = (length + 1) / 2;
        Span<char> token = stackalloc char[hashedBytes * 2];
        Convert.TryToHexString(hash[..hashedBytes], token, out _);
        foreach (ref var character in token)
            character = char.ToLowerInvariant(character);
        return new string(token[..length]);
    }

    private static string Fragment(ConfigurationKey configuration, string fragment)
    {
        return configuration.Values.Single(value => value.Fragment.Value == fragment).Value;
    }

    private static string SanitizedStem(LogicalPath source)
    {
        var stem = Path.GetFileNameWithoutExtension(source.Value);
        var validCharacters = stem.Count(character => char.IsAsciiLetterOrDigit(character) || character == '_');
        if (validCharacters == 0) return "source";
        if (validCharacters == stem.Length) return stem;

        return string.Create(validCharacters, stem, static (destination, value) =>
        {
            var index = 0;
            foreach (var character in value)
                if (char.IsAsciiLetterOrDigit(character) || character == '_')
                    destination[index++] = character;
        });
    }
}

/// <summary>Creates stable, readable configuration names for workspaces and build invocations.</summary>
public static class BuildConfigurationNames
{
    public static string DisplayName(ConfigurationKey configuration)
    {
        var profile = configuration.Values.Single(value => value.Fragment.Value == "Profile").Value;
        var custom = configuration.Values
            .Where(value => value.Fragment.Value is not
                ("Platform" or "Architecture" or "Profile" or "Toolchain" or "LinkModel"))
            .Select(value => ToPascalCase(value.Value));
        return $"{string.Join(' ', new[] { ToPascalCase(profile) }.Concat(custom))}-{configuration.ShortHash}";
    }

    private static string ToPascalCase(string value)
    {
        return string.Concat(value
            .Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => char.ToUpperInvariant(segment[0]) + segment[1..]));
    }
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

        var normalized = value.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (Path.IsPathRooted(value) || segments.Contains("..", StringComparer.Ordinal))
        {
            throw new ArgumentException($"Logical path '{value}' must be workspace-relative.", nameof(value));
        }

        Value = string.Join('/', segments.Where(segment => segment != "."));
        if (Value.Length == 0)
            Value = ".";
    }

    /// <summary>Compares paths using the identity rules of the host file system.</summary>
    public static StringComparer FileSystemComparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public string Value { get; }
    public string FileName => Path.GetFileName(Value);
    public override string ToString() => Value;
}

/// <summary>Classifies source files consistently across action and workspace generators.</summary>
public static class BuildFileKinds
{
    public static bool IsCxxSource(string path)
    {
        var extension = Path.GetExtension(path.AsSpan());
        return extension.Equals(".c", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cc", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cpp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cxx", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHeader(string path)
    {
        var extension = Path.GetExtension(path.AsSpan());
        return extension.Equals(".h", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".hh", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".hpp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".hxx", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".inl", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".inc", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns true for intentionally unsupported MASM/NASM/GAS-style source extensions.</summary>
    public static bool IsAssemblySource(string path)
    {
        var extension = Path.GetExtension(path.AsSpan());
        return extension.Equals(".asm", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".s", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".nasm", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsResource(string path)
    {
        return Path.GetExtension(path.AsSpan()).Equals(".rc", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Identifies the artifact shape of a configured module.</summary>
public enum ModuleKind
{
    HeaderOnly,
    ObjectLibrary,
    StaticLibrary,
    SharedLibrary,
    Executable,
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

/// <summary>Associates a propagated usage value with the definition that introduced it.</summary>
public sealed record UsageValue(string Value, string Origin);

/// <summary>Contains ordinary/system includes, defines, link inputs, and runtime requirements with origin tracking.</summary>
public sealed record UsageRequirements(
    ImmutableArray<UsageValue> IncludeDirectories,
    ImmutableArray<UsageValue> Defines,
    ImmutableArray<UsageValue> LinkInputs,
    ImmutableArray<UsageValue> RuntimeFiles,
    ImmutableArray<UsageValue> SystemIncludeDirectories = default)
{
    public static UsageRequirements Empty { get; } = new([], [], [], [], []);

    /// <summary>Combines requirements by value using deterministic origin selection.</summary>
    public UsageRequirements Union(UsageRequirements other) => new(
        Normalize(IncludeDirectories.AddRange(other.IncludeDirectories), LogicalPath.FileSystemComparer),
        Normalize(Defines.AddRange(other.Defines), StringComparer.Ordinal),
        Normalize(LinkInputs.AddRange(other.LinkInputs), LogicalPath.FileSystemComparer),
        Normalize(Values(RuntimeFiles).AddRange(Values(other.RuntimeFiles)), LogicalPath.FileSystemComparer),
        Normalize(Values(SystemIncludeDirectories).AddRange(Values(other.SystemIncludeDirectories)),
            LogicalPath.FileSystemComparer));

    private static ImmutableArray<UsageValue> Values(ImmutableArray<UsageValue> values)
    {
        return values.IsDefault ? [] : values;
    }

    private static ImmutableArray<UsageValue> Normalize(
        IEnumerable<UsageValue> values,
        StringComparer comparer)
    {
        return values
            .GroupBy(value => value.Value, comparer)
            .Select(group => group.OrderBy(value => value.Origin, StringComparer.Ordinal).First())
            .OrderBy(value => value.Value, StringComparer.Ordinal)
            .ThenBy(value => value.Origin, StringComparer.Ordinal)
            .ToImmutableArray();
    }
}

/// <summary>Contains native settings that are not dependency usage requirements.</summary>
public sealed record CxxModuleSettings(
    ImmutableArray<string> CompilerArguments,
    ImmutableArray<string> LinkerArguments,
    ImmutableArray<string> LibrarianArguments,
    ImmutableArray<LogicalPath> ForcedIncludes,
    LogicalPath? PrecompiledHeader = null,
    LogicalPath? PrecompiledSource = null,
    string? OutputName = null)
{
    public static CxxModuleSettings Empty { get; } = new([], [], [], []);
}

/// <summary>Represents one enabled module after configuration and dependency propagation.</summary>
public sealed record ConfiguredModule(
    string Id,
    string DisplayName,
    ModuleKind Kind,
    ImmutableArray<LogicalPath> Sources,
    UsageRequirements PublicUsage,
    UsageRequirements PrivateUsage,
    UsageRequirements CompileUsage,
    UsageRequirements ConsumerUsage,
    ImmutableArray<DependencyEdge> Dependencies,
    CxxModuleSettings? CxxSettings = null);

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
    ResourceFile,
    StaticLibrary,
    ImportLibrary,
    SharedLibrary,
    Executable,
    GeneratedProject,
    RuntimeFile,
    PrecompiledHeader
}

/// <summary>Describes a typed artifact and the action that produces it.</summary>
public sealed record BuildArtifact(string Id, ArtifactKind Kind, LogicalPath Path, string ProducerAction);

/// <summary>Identifies the operation performed by a build action.</summary>
public enum BuildActionKind
{
    Compile,
    ResourceCompile,
    Archive,
    Link,
    Copy,
}

/// <summary>Provides allocation-free traversal over shared and action-specific argument segments.</summary>
public readonly struct ActionArgumentSequence : IReadOnlyList<string>
{
    private readonly ImmutableArray<string> _prefix;
    private readonly ImmutableArray<string> _suffix;

    public ActionArgumentSequence(ImmutableArray<string> arguments) : this(arguments, [])
    {
    }

    public ActionArgumentSequence(ImmutableArray<string> prefix, ImmutableArray<string> suffix)
    {
        _prefix = prefix.IsDefault ? [] : prefix;
        _suffix = suffix.IsDefault ? [] : suffix;
    }

    public int Count => _prefix.Length + _suffix.Length;

    /// <summary>Gets the segment shared by actions with identical compiler or linker settings.</summary>
    public ImmutableArray<string> SharedPrefix => _prefix;

    /// <summary>Gets the segment containing action-specific source, output, or input arguments.</summary>
    public ImmutableArray<string> SpecificSuffix => _suffix;

    public string this[int index] => index < _prefix.Length
        ? _prefix[index]
        : _suffix[index - _prefix.Length];

    public Enumerator GetEnumerator()
    {
        return new Enumerator(_prefix, _suffix);
    }

    IEnumerator<string> IEnumerable<string>.GetEnumerator()
    {
        return GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public ImmutableArray<string> ToImmutableArray()
    {
        if (_suffix.IsEmpty) return _prefix;
        if (_prefix.IsEmpty) return _suffix;

        var builder = ImmutableArray.CreateBuilder<string>(Count);
        builder.AddRange(_prefix);
        builder.AddRange(_suffix);
        return builder.MoveToImmutable();
    }

    public struct Enumerator : IEnumerator<string>
    {
        private readonly ImmutableArray<string> _prefix;
        private readonly ImmutableArray<string> _suffix;
        private int _index;

        internal Enumerator(ImmutableArray<string> prefix, ImmutableArray<string> suffix)
        {
            _prefix = prefix;
            _suffix = suffix;
            _index = -1;
        }

        public string Current => _index < _prefix.Length
            ? _prefix[_index]
            : _suffix[_index - _prefix.Length];

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            return ++_index < _prefix.Length + _suffix.Length;
        }

        public void Reset()
        {
            _index = -1;
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>Describes one structured, dependency-ordered build operation.</summary>
public sealed record BuildAction
{
    private readonly ActionArgumentSequence _argumentValues;

    [JsonConstructor]
    public BuildAction(
        string id,
        BuildActionKind kind,
        string command,
        ImmutableArray<string> arguments,
        LogicalPath workingDirectory,
        ImmutableArray<string> inputs,
        ImmutableArray<string> outputs,
        ImmutableArray<string> dependencies,
        ImmutableArray<string> environmentWhitelist,
        bool cacheable,
        bool remoteExecutable,
        ImmutableArray<string> sensitiveArguments)
        : this(id, kind, command, new ActionArgumentSequence(arguments), workingDirectory, inputs, outputs,
            dependencies, environmentWhitelist, cacheable, remoteExecutable, sensitiveArguments)
    {
    }

    public BuildAction(
        string id,
        BuildActionKind kind,
        string command,
        ActionArgumentSequence arguments,
        LogicalPath workingDirectory,
        ImmutableArray<string> inputs,
        ImmutableArray<string> outputs,
        ImmutableArray<string> dependencies,
        ImmutableArray<string> environmentWhitelist,
        bool cacheable,
        bool remoteExecutable,
        ImmutableArray<string> sensitiveArguments)
    {
        Id = id;
        Kind = kind;
        Command = command;
        _argumentValues = arguments;
        WorkingDirectory = workingDirectory;
        Inputs = inputs;
        Outputs = outputs;
        Dependencies = dependencies;
        EnvironmentWhitelist = environmentWhitelist;
        Cacheable = cacheable;
        RemoteExecutable = remoteExecutable;
        SensitiveArguments = sensitiveArguments;
    }

    public string Id { get; init; }
    public BuildActionKind Kind { get; init; }
    public string Command { get; init; }

    /// <summary>Gets a contiguous snapshot of the complete command-line argument list.</summary>
    public ImmutableArray<string> Arguments
    {
        get => _argumentValues.ToImmutableArray();
        init => _argumentValues = new ActionArgumentSequence(value);
    }

    /// <summary>Gets the shared, allocation-free argument view used by generators and executors.</summary>
    [JsonIgnore]
    public ActionArgumentSequence ArgumentValues => _argumentValues;

    public LogicalPath WorkingDirectory { get; init; }
    public ImmutableArray<string> Inputs { get; init; }
    public ImmutableArray<string> Outputs { get; init; }
    public ImmutableArray<string> Dependencies { get; init; }
    public ImmutableArray<string> EnvironmentWhitelist { get; init; }
    public bool Cacheable { get; init; }
    public bool RemoteExecutable { get; init; }
    public ImmutableArray<string> SensitiveArguments { get; init; }

    /// <summary>Gets the SHA-256 hash of fields that determine action semantics.</summary>
    public string SemanticHash
    {
        get
        {
            var semantic = string.Join('\n',
                Id,
                Kind.ToString(),
                Command,
                string.Join('\0', ArgumentValues),
                WorkingDirectory.Value,
                string.Join('\0', Inputs),
                string.Join('\0', Outputs),
                string.Join('\0', Dependencies));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(semantic))).ToLowerInvariant();
        }
    }
}

/// <summary>Contains normalized toolchain settings needed by workspace backends.</summary>
public sealed record ToolchainBuildSettings(
    string Id,
    string VisualStudioPlatformToolset,
    ImmutableArray<string> CompileArguments,
    ImmutableArray<string> LinkArguments);

/// <summary>Contains ordered actions and artifacts for one target configuration.</summary>
public sealed record ActionGraph(
    ConfigurationKey Configuration,
    string Target,
    ImmutableArray<BuildAction> Actions,
    ImmutableArray<BuildArtifact> Artifacts,
    ToolchainBuildSettings? Toolchain = null)
{
    /// <summary>Validates output ownership and action dependency references.</summary>
    public ImmutableArray<Diagnostic> Validate()
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var ids = Actions.Select(action => action.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var duplicate in Actions.GroupBy(action => action.Id, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
            diagnostics.Add(new Diagnostic("RBT3001", DiagnosticSeverity.Error,
                $"Action ID '{duplicate.Key}' is declared {duplicate.Count()} times."));

        foreach (var duplicate in Actions.SelectMany(action => action.Outputs.Select(output => (output, action.Id)))
                     .GroupBy(pair => pair.output, LogicalPath.FileSystemComparer).Where(group => group.Count() > 1))
        {
            diagnostics.Add(new Diagnostic("RBT3002", DiagnosticSeverity.Error,
                $"Output '{duplicate.Key}' has multiple producers: {string.Join(", ", duplicate.Select(pair => pair.Id))}."));
        }

        foreach (var action in Actions)
        {
            foreach (var output in action.Outputs)
                try
                {
                    var logical = new LogicalPath(output);
                    if (logical.Value != output)
                        diagnostics.Add(new Diagnostic("RBT3009", DiagnosticSeverity.Error,
                            $"Action '{action.Id}' output '{output}' is not a canonical logical path; use '{logical.Value}'."));
                }
                catch (ArgumentException)
                {
                    diagnostics.Add(new Diagnostic("RBT3009", DiagnosticSeverity.Error,
                        $"Action '{action.Id}' output '{output}' is not a workspace-relative logical path."));
                }

            foreach (var dependency in action.Dependencies.Where(dependency => !ids.Contains(dependency)))
            {
                diagnostics.Add(new Diagnostic("RBT3003", DiagnosticSeverity.Error,
                    $"Action '{action.Id}' depends on missing action '{dependency}'."));
            }
        }

        ValidateCycles();

        var actionsById = Actions.GroupBy(action => action.Id, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        foreach (var artifact in Artifacts)
            if (!actionsById.TryGetValue(artifact.ProducerAction, out var producer))
                diagnostics.Add(new Diagnostic("RBT3004", DiagnosticSeverity.Error,
                    $"Artifact '{artifact.Id}' references missing producer '{artifact.ProducerAction}'."));
            else if (!producer.Outputs.Contains(artifact.Path.Value, LogicalPath.FileSystemComparer))
                diagnostics.Add(new Diagnostic("RBT3005", DiagnosticSeverity.Error,
                    $"Artifact '{artifact.Id}' path '{artifact.Path}' is not declared by producer '{producer.Id}'."));

        foreach (var duplicate in Artifacts.GroupBy(artifact => artifact.Id, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
            diagnostics.Add(new Diagnostic("RBT3008", DiagnosticSeverity.Error,
                $"Artifact ID '{duplicate.Key}' is declared {duplicate.Count()} times."));

        return diagnostics.ToImmutable();

        void ValidateCycles()
        {
            var states = new Dictionary<string, int>(StringComparer.Ordinal);
            var stack = new List<string>();
            var actionsById = Actions.GroupBy(action => action.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            foreach (var action in Actions.OrderBy(action => action.Id, StringComparer.Ordinal)) Visit(action.Id);

            void Visit(string id)
            {
                if (!ids.Contains(id) || states.GetValueOrDefault(id) == 2) return;

                if (states.GetValueOrDefault(id) == 1)
                {
                    var start = stack.IndexOf(id);
                    diagnostics.Add(new Diagnostic("RBT3006", DiagnosticSeverity.Error,
                        $"Action dependency cycle detected: {string.Join(" -> ", stack.Skip(start).Append(id))}."));
                    return;
                }

                states[id] = 1;
                stack.Add(id);
                var action = actionsById[id];
                foreach (var dependency in action.Dependencies.Order(StringComparer.Ordinal)) Visit(dependency);

                stack.RemoveAt(stack.Count - 1);
                states[id] = 2;
            }
        }
    }
}

/// <summary>Connects a workspace project to one target configuration and module.</summary>
public sealed record WorkspaceProjectVariant(
    string Target,
    ConfigurationKey Configuration,
    ConfiguredModule Module);

/// <summary>Records that a project reference exists for one consumer variant.</summary>
public sealed record WorkspaceProjectDependencyVariant(
    string Target,
    ConfigurationKey Configuration,
    string ProjectId);

/// <summary>Represents one generated or imported project in a workspace.</summary>
public sealed record WorkspaceProject(
    string Id,
    string Name,
    ImmutableArray<WorkspaceProjectVariant> Variants,
    ImmutableArray<string> ProjectDependencies,
    ImmutableArray<WorkspaceProjectDependencyVariant> DependencyVariants = default);

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

/// <summary>
///     Declares additional external input identity required before a workspace generator may use a generation snapshot.
/// </summary>
public interface IWorkspaceGeneratorFingerprintProvider
{
    /// <summary>
    ///     Returns a deterministic identity for inputs not already represented by the workspace model, context, or
    ///     generator assembly. Pure generators should return an empty string.
    /// </summary>
    string GetAdditionalFingerprint(WorkspaceModel workspace, GenerationContext context);
}