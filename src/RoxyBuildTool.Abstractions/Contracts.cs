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

public readonly record struct FragmentId : IComparable<FragmentId>
{
    public FragmentId(string value) => Value = StableIdSyntax.Validate(value, nameof(value));
    public string Value { get; }
    public int CompareTo(FragmentId other) => StringComparer.Ordinal.Compare(Value, other.Value);
    public static bool operator <(FragmentId left, FragmentId right) => left.CompareTo(right) < 0;
    public static bool operator <=(FragmentId left, FragmentId right) => left.CompareTo(right) <= 0;
    public static bool operator >(FragmentId left, FragmentId right) => left.CompareTo(right) > 0;
    public static bool operator >=(FragmentId left, FragmentId right) => left.CompareTo(right) >= 0;
    public override string ToString() => Value;
}

public readonly record struct PluginId : IComparable<PluginId>
{
    public PluginId(string value) => Value = StableIdSyntax.Validate(value, nameof(value));
    public string Value { get; }
    public int CompareTo(PluginId other) => StringComparer.Ordinal.Compare(Value, other.Value);
    public static bool operator <(PluginId left, PluginId right) => left.CompareTo(right) < 0;
    public static bool operator <=(PluginId left, PluginId right) => left.CompareTo(right) <= 0;
    public static bool operator >(PluginId left, PluginId right) => left.CompareTo(right) > 0;
    public static bool operator >=(PluginId left, PluginId right) => left.CompareTo(right) >= 0;
    public override string ToString() => Value;
}

public readonly record struct PlatformId : IComparable<PlatformId>
{
    public PlatformId(string value) => Value = StableIdSyntax.Validate(value, nameof(value));
    public string Value { get; }
    public int CompareTo(PlatformId other) => StringComparer.Ordinal.Compare(Value, other.Value);
    public static bool operator <(PlatformId left, PlatformId right) => left.CompareTo(right) < 0;
    public static bool operator <=(PlatformId left, PlatformId right) => left.CompareTo(right) <= 0;
    public static bool operator >(PlatformId left, PlatformId right) => left.CompareTo(right) > 0;
    public static bool operator >=(PlatformId left, PlatformId right) => left.CompareTo(right) >= 0;
    public override string ToString() => Value;
}

public readonly record struct ToolchainId : IComparable<ToolchainId>
{
    public ToolchainId(string value) => Value = StableIdSyntax.Validate(value, nameof(value));
    public string Value { get; }
    public int CompareTo(ToolchainId other) => StringComparer.Ordinal.Compare(Value, other.Value);
    public static bool operator <(ToolchainId left, ToolchainId right) => left.CompareTo(right) < 0;
    public static bool operator <=(ToolchainId left, ToolchainId right) => left.CompareTo(right) <= 0;
    public static bool operator >(ToolchainId left, ToolchainId right) => left.CompareTo(right) > 0;
    public static bool operator >=(ToolchainId left, ToolchainId right) => left.CompareTo(right) >= 0;
    public override string ToString() => Value;
}

public readonly record struct WorkspaceGeneratorId : IComparable<WorkspaceGeneratorId>
{
    public WorkspaceGeneratorId(string value) => Value = StableIdSyntax.Validate(value, nameof(value));
    public string Value { get; }
    public int CompareTo(WorkspaceGeneratorId other) => StringComparer.Ordinal.Compare(Value, other.Value);
    public static bool operator <(WorkspaceGeneratorId left, WorkspaceGeneratorId right) => left.CompareTo(right) < 0;
    public static bool operator <=(WorkspaceGeneratorId left, WorkspaceGeneratorId right) => left.CompareTo(right) <= 0;
    public static bool operator >(WorkspaceGeneratorId left, WorkspaceGeneratorId right) => left.CompareTo(right) > 0;
    public static bool operator >=(WorkspaceGeneratorId left, WorkspaceGeneratorId right) => left.CompareTo(right) >= 0;
    public override string ToString() => Value;
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record SourceLocation(string File, int Line = 0, int Column = 0);

public sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    string? Definition = null,
    string? Configuration = null,
    SourceLocation? Location = null,
    string? Help = null);

[AttributeUsage(AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
public sealed class BuildFragmentAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class FragmentValueAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}

public sealed record CapabilitySet
{
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

public interface IPluginRegistry
{
    void AddService<T>(T service) where T : class;
}

public interface IBuildToolBuilder
{
    void AddPlugin(IPlugin plugin);
}

public interface IPlugin
{
    PluginId Id { get; }
    Version Version { get; }
    CapabilitySet Capabilities { get; }
    void Register(IPluginRegistry registry);
}
