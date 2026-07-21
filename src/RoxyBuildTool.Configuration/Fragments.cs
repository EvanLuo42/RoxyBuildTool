using System.Collections.Immutable;
using System.Reflection;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Model;

namespace RoxyBuildTool.Configuration;

/// <summary>Provides the built-in fragment IDs used by the core pipeline.</summary>
public static class FragmentIds
{
    public static FragmentId Platform { get; } = new("Platform");
    public static FragmentId Architecture { get; } = new("Architecture");
    public static FragmentId Profile { get; } = new("Profile");
    public static FragmentId Toolchain { get; } = new("Toolchain");
    public static FragmentId LinkModel { get; } = new("LinkModel");
}

/// <summary>Provides built-in target platform values.</summary>
public static class Platforms
{
    public static FragmentValue Windows { get; } = new(FragmentIds.Platform, "Windows");
}

/// <summary>Provides built-in target architecture values.</summary>
public static class Architectures
{
    public static FragmentValue X64 { get; } = new(FragmentIds.Architecture, "X64");
}

/// <summary>Provides the built-in build profile values.</summary>
public static class BuildProfiles
{
    public static FragmentValue Debug { get; } = new(FragmentIds.Profile, "Debug");
    public static FragmentValue Development { get; } = new(FragmentIds.Profile, "Development");
    public static FragmentValue Release { get; } = new(FragmentIds.Profile, "Release");
    public static FragmentValue Shipping { get; } = new(FragmentIds.Profile, "Shipping");
    public static ImmutableArray<FragmentValue> All { get; } = [Debug, Development, Release, Shipping];
}

/// <summary>Provides built-in toolchain values.</summary>
public static class Toolchains
{
    public static FragmentValue Msvc { get; } = new(FragmentIds.Toolchain, "Msvc14.4");
}

/// <summary>Provides built-in link model values.</summary>
public static class LinkModels
{
    public static FragmentValue Modular { get; } = new(FragmentIds.LinkModel, "Modular");
    public static FragmentValue Monolithic { get; } = new(FragmentIds.LinkModel, "Monolithic");
}

/// <summary>Describes a fragment, its optional enum type, and its allowed values.</summary>
public sealed record FragmentMetadata(
    FragmentId Id,
    Type? ClrType,
    ImmutableArray<FragmentValue> Values);

/// <summary>Registers built-in and enum-backed fragments and encodes typed values.</summary>
public sealed class FragmentRegistry
{
    private readonly Dictionary<FragmentId, FragmentMetadata> _metadata = [];

    public FragmentRegistry()
    {
        RegisterBuiltIn(FragmentIds.Platform, [Platforms.Windows]);
        RegisterBuiltIn(FragmentIds.Architecture, [Architectures.X64]);
        RegisterBuiltIn(FragmentIds.Profile, BuildProfiles.All);
        RegisterBuiltIn(FragmentIds.Toolchain, [Toolchains.Msvc]);
        RegisterBuiltIn(FragmentIds.LinkModel, [LinkModels.Modular, LinkModels.Monolithic]);
    }

    public ImmutableArray<FragmentMetadata> All => _metadata.Values.OrderBy(item => item.Id).ToImmutableArray();

    /// <summary>Registers the fragment declared by enum <typeparamref name="T"/>.</summary>
    public FragmentMetadata RegisterEnum<T>() where T : struct, Enum => RegisterEnum(typeof(T));

    /// <summary>Registers a fragment enum and returns its stable metadata.</summary>
    public FragmentMetadata RegisterEnum(Type type)
    {
        if (!type.IsEnum)
        {
            throw new ArgumentException($"'{type}' is not an enum.", nameof(type));
        }

        var attribute = type.GetCustomAttribute<BuildFragmentAttribute>()
                        ?? throw new FragmentException(new Diagnostic("RBT1001", DiagnosticSeverity.Error,
                            $"Enum '{type.FullName}' must have [BuildFragment]."));
        var id = new FragmentId(attribute.Id);
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .OrderBy(field => field.MetadataToken)
            .ToImmutableArray();
        for (var left = 0; left < fields.Length; left++)
        for (var right = left + 1; right < fields.Length; right++)
        {
            if (Equals(fields[left].GetRawConstantValue(), fields[right].GetRawConstantValue()))
            {
                throw new FragmentException(new Diagnostic("RBT1004", DiagnosticSeverity.Error,
                    $"Fragment enum '{type.FullName}' contains aliases '{fields[left].Name}' and " +
                    $"'{fields[right].Name}'. Fragment values must be one-to-one."));
            }
        }

        var values = fields
            .Select(field => new FragmentValue(id, GetValueId(field)))
            .Order()
            .ToImmutableArray();
        var duplicateId = values.GroupBy(value => value.Value, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateId is not null)
        {
            throw new FragmentException(new Diagnostic("RBT1004", DiagnosticSeverity.Error,
                $"Fragment enum '{type.FullName}' maps multiple members to value ID '{duplicateId.Key}'."));
        }

        var metadata = new FragmentMetadata(id, type, values);

        if (_metadata.TryGetValue(id, out var existing))
        {
            if (existing.ClrType == type)
            {
                return existing;
            }

            throw new FragmentException(new Diagnostic("RBT1002", DiagnosticSeverity.Error,
                $"Fragment ID '{id}' is already registered by '{existing.ClrType?.FullName ?? "a built-in fragment"}'."));
        }

        _metadata.Add(id, metadata);
        return metadata;
    }

    /// <summary>Encodes an enum value as a stable fragment assignment.</summary>
    public FragmentValue Encode<T>(T value) where T : struct, Enum
    {
        var metadata = RegisterEnum<T>();
        var name = Enum.GetName(value)
                   ?? throw new FragmentException(new Diagnostic("RBT1003", DiagnosticSeverity.Error,
                       $"'{value}' is not a named value of '{typeof(T).FullName}'."));
        return new FragmentValue(metadata.Id, GetValueId(typeof(T).GetField(name)!));
    }

    private void RegisterBuiltIn(FragmentId id, ImmutableArray<FragmentValue> values) =>
        _metadata.Add(id, new FragmentMetadata(id, null, values));

    private static string GetValueId(FieldInfo field) =>
        field.GetCustomAttribute<FragmentValueAttribute>()?.Id ?? field.Name;

    /// <summary>Normalizes a stable ID or selector to dot-separated PascalCase.</summary>
    public static string ToPascalCase(string value)
    {
        var outputLength = value.Count(character => character is not ('-' or '_'));
        return string.Create(outputLength, value, static (result, source) =>
        {
            var index = 0;
            var capitalizeNext = true;
            foreach (var character in source)
                switch (character)
                {
                    case '.':
                        result[index++] = character;
                        capitalizeNext = true;
                        break;
                    case '-' or '_':
                        capitalizeNext = true;
                        break;
                    default:
                        result[index++] = capitalizeNext ? char.ToUpperInvariant(character) : character;
                        capitalizeNext = false;
                        break;
                }
        });
    }
}

/// <summary>Represents a fragment or matrix failure with a structured diagnostic.</summary>
public sealed class FragmentException(Diagnostic diagnostic) : Exception(diagnostic.Message)
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}

/// <summary>Provides stateless encoding for enum fragment values.</summary>
public static class FragmentEncoding
{
    public static FragmentValue Encode<T>(T value) where T : struct, Enum => new FragmentRegistry().Encode(value);
}

/// <summary>Provides predicate helpers for configuration expressions.</summary>
public static class BooleanExtensions
{
    public static bool Not(this bool value) => !value;
}