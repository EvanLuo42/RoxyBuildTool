using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

namespace RoxyBuildTool.Abstractions;

internal static partial class StableIdSyntax
{
    [GeneratedRegex("^[A-Z][A-Za-z0-9]*(?:\\.[A-Z0-9][A-Za-z0-9]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static string Validate(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!char.IsAsciiLetterOrDigit(value[0]))
        {
            throw new ArgumentException($"'{value}' is not a stable ID.", parameterName);
        }

        var normalized = Normalize(value);
        if (!Pattern().IsMatch(normalized))
        {
            throw new ArgumentException(
                $"'{value}' is not a stable ID. Use dot-separated PascalCase ASCII segments.",
                parameterName);
        }

        return normalized;
    }

    private static string Normalize(string value)
    {
        var result = new StringBuilder(value.Length);
        var capitalizeNext = true;
        foreach (var character in value)
        {
            switch (character)
            {
                case '.':
                    result.Append(character);
                    capitalizeNext = true;
                    break;
                case '-' or '_':
                    capitalizeNext = true;
                    break;
                default:
                    result.Append(capitalizeNext ? char.ToUpperInvariant(character) : character);
                    capitalizeNext = false;
                    break;
            }
        }

        return result.ToString();
    }
}

/// <summary>Identifies a configuration fragment using a stable, dot-separated PascalCase value.</summary>
public readonly record struct FragmentId : IComparable<FragmentId>
{
    /// <summary>Creates a fragment ID after validating and normalizing <paramref name="value"/>.</summary>
    public FragmentId(string value) => Value = StableIdSyntax.Validate(value, nameof(value));

    public string Value { get; }
    public int CompareTo(FragmentId other) => StringComparer.Ordinal.Compare(Value, other.Value);
    public static bool operator <(FragmentId left, FragmentId right) => left.CompareTo(right) < 0;
    public static bool operator <=(FragmentId left, FragmentId right) => left.CompareTo(right) <= 0;
    public static bool operator >(FragmentId left, FragmentId right) => left.CompareTo(right) > 0;
    public static bool operator >=(FragmentId left, FragmentId right) => left.CompareTo(right) >= 0;
    public override string ToString() => Value;
}

/// <summary>Identifies a build-tool plugin using a stable ID.</summary>
public readonly record struct PluginId : IComparable<PluginId>
{
    /// <summary>Creates a plugin ID after validating and normalizing <paramref name="value"/>.</summary>
    public PluginId(string value) => Value = StableIdSyntax.Validate(value, nameof(value));

    public string Value { get; }
    public int CompareTo(PluginId other) => StringComparer.Ordinal.Compare(Value, other.Value);
    public static bool operator <(PluginId left, PluginId right) => left.CompareTo(right) < 0;
    public static bool operator <=(PluginId left, PluginId right) => left.CompareTo(right) <= 0;
    public static bool operator >(PluginId left, PluginId right) => left.CompareTo(right) > 0;
    public static bool operator >=(PluginId left, PluginId right) => left.CompareTo(right) >= 0;
    public override string ToString() => Value;
}

/// <summary>Identifies a target platform using a stable ID.</summary>
public readonly record struct PlatformId : IComparable<PlatformId>
{
    /// <summary>Creates a platform ID after validating and normalizing <paramref name="value"/>.</summary>
    public PlatformId(string value) => Value = StableIdSyntax.Validate(value, nameof(value));

    public string Value { get; }
    public int CompareTo(PlatformId other) => StringComparer.Ordinal.Compare(Value, other.Value);
    public static bool operator <(PlatformId left, PlatformId right) => left.CompareTo(right) < 0;
    public static bool operator <=(PlatformId left, PlatformId right) => left.CompareTo(right) <= 0;
    public static bool operator >(PlatformId left, PlatformId right) => left.CompareTo(right) > 0;
    public static bool operator >=(PlatformId left, PlatformId right) => left.CompareTo(right) >= 0;
    public override string ToString() => Value;
}

/// <summary>Identifies a compiler toolchain using a stable ID.</summary>
public readonly record struct ToolchainId : IComparable<ToolchainId>
{
    /// <summary>Creates a toolchain ID after validating and normalizing <paramref name="value"/>.</summary>
    public ToolchainId(string value) => Value = StableIdSyntax.Validate(value, nameof(value));

    public string Value { get; }
    public int CompareTo(ToolchainId other) => StringComparer.Ordinal.Compare(Value, other.Value);
    public static bool operator <(ToolchainId left, ToolchainId right) => left.CompareTo(right) < 0;
    public static bool operator <=(ToolchainId left, ToolchainId right) => left.CompareTo(right) <= 0;
    public static bool operator >(ToolchainId left, ToolchainId right) => left.CompareTo(right) > 0;
    public static bool operator >=(ToolchainId left, ToolchainId right) => left.CompareTo(right) >= 0;
    public override string ToString() => Value;
}

/// <summary>Identifies a workspace generator using a stable ID.</summary>
public readonly record struct WorkspaceGeneratorId : IComparable<WorkspaceGeneratorId>
{
    /// <summary>Creates a generator ID after validating and normalizing <paramref name="value"/>.</summary>
    public WorkspaceGeneratorId(string value) => Value = StableIdSyntax.Validate(value, nameof(value));

    public string Value { get; }
    public int CompareTo(WorkspaceGeneratorId other) => StringComparer.Ordinal.Compare(Value, other.Value);
    public static bool operator <(WorkspaceGeneratorId left, WorkspaceGeneratorId right) => left.CompareTo(right) < 0;
    public static bool operator <=(WorkspaceGeneratorId left, WorkspaceGeneratorId right) => left.CompareTo(right) <= 0;
    public static bool operator >(WorkspaceGeneratorId left, WorkspaceGeneratorId right) => left.CompareTo(right) > 0;
    public static bool operator >=(WorkspaceGeneratorId left, WorkspaceGeneratorId right) => left.CompareTo(right) >= 0;
    public override string ToString() => Value;
}

/// <summary>Specifies the effect of a diagnostic on the current operation.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Informational output that does not affect success.</summary>
    Info,

    /// <summary>A recoverable condition that should be reviewed.</summary>
    Warning,

    /// <summary>A condition that prevents a valid result.</summary>
    Error,
}

/// <summary>Identifies a source position associated with a diagnostic.</summary>
public sealed record SourceLocation(string File, int Line = 0, int Column = 0);

/// <summary>Describes a stable, structured problem reported by the build pipeline.</summary>
public sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    string? Definition = null,
    string? Configuration = null,
    SourceLocation? Location = null,
    string? Help = null);

/// <summary>Declares an enum as a typed configuration fragment.</summary>
[AttributeUsage(AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
public sealed class BuildFragmentAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}

/// <summary>Overrides the stable serialized ID of an enum fragment value.</summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class FragmentValueAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}

/// <summary>Stores normalized capability IDs exposed by a plugin, platform, toolchain, or generator.</summary>
public sealed record CapabilitySet
{
    /// <summary>Creates a set from stable capability IDs.</summary>
    public CapabilitySet(IEnumerable<string> capabilities)
    {
        Values = capabilities
            .Select(value => StableIdSyntax.Validate(value, nameof(capabilities)))
            .ToImmutableSortedSet(StringComparer.Ordinal);
    }

    public static CapabilitySet Empty { get; } = new([]);
    public ImmutableSortedSet<string> Values { get; }
    public bool Contains(string capability) => Values.Contains(capability);
}

/// <summary>Receives services contributed by a plugin during application startup.</summary>
public interface IPluginRegistry
{
    /// <summary>Registers a service under its declared contract type.</summary>
    void AddService<T>(T service) where T : class;
}

/// <summary>Provides the plugin composition surface shared by build-tool builders.</summary>
public interface IBuildToolBuilder
{
    /// <summary>Adds a plugin to the current application.</summary>
    void AddPlugin(IPlugin plugin);
}

/// <summary>Defines an in-process extension that registers platform or generator services.</summary>
public interface IPlugin
{
    /// <summary>Gets the stable plugin ID.</summary>
    PluginId Id { get; }

    /// <summary>Gets the plugin implementation version.</summary>
    Version Version { get; }

    /// <summary>Gets the oldest host contract version accepted by this plugin.</summary>
    Version MinimumHostApiVersion => new(0, 1, 0);

    /// <summary>Gets the newest host contract version accepted by this plugin, or null for no upper bound.</summary>
    Version? MaximumHostApiVersion => null;

    /// <summary>Gets the capabilities exposed by the plugin.</summary>
    CapabilitySet Capabilities { get; }

    /// <summary>Gets capabilities that must be supplied by the composed plugin set.</summary>
    CapabilitySet RequiredCapabilities => CapabilitySet.Empty;

    /// <summary>Registers plugin services for the current invocation.</summary>
    void Register(IPluginRegistry registry);
}