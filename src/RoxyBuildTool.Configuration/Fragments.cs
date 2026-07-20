using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Model;

namespace RoxyBuildTool.Configuration;

public static class FragmentIds
{
    public static FragmentId Platform { get; } = new("platform");
    public static FragmentId Architecture { get; } = new("architecture");
    public static FragmentId Profile { get; } = new("profile");
    public static FragmentId Toolchain { get; } = new("toolchain");
    public static FragmentId LinkModel { get; } = new("link-model");
}

public static class Platforms
{
    public static FragmentValue Windows { get; } = new(FragmentIds.Platform, "windows");
}

public static class Architectures
{
    public static FragmentValue X64 { get; } = new(FragmentIds.Architecture, "x64");
}

public static class BuildProfiles
{
    public static FragmentValue Debug { get; } = new(FragmentIds.Profile, "debug");
    public static FragmentValue Development { get; } = new(FragmentIds.Profile, "development");
    public static FragmentValue Release { get; } = new(FragmentIds.Profile, "release");
    public static FragmentValue Shipping { get; } = new(FragmentIds.Profile, "shipping");
    public static ImmutableArray<FragmentValue> All { get; } = [Debug, Development, Release, Shipping];
}

public static class Toolchains
{
    public static FragmentValue Msvc { get; } = new(FragmentIds.Toolchain, "msvc-14.4");
}

public static class LinkModels
{
    public static FragmentValue Modular { get; } = new(FragmentIds.LinkModel, "modular");
    public static FragmentValue Monolithic { get; } = new(FragmentIds.LinkModel, "monolithic");
}

public sealed record FragmentMetadata(
    FragmentId Id,
    Type? ClrType,
    ImmutableArray<FragmentValue> Values);

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

    public FragmentMetadata RegisterEnum<T>() where T : struct, Enum => RegisterEnum(typeof(T));

    public FragmentMetadata RegisterEnum(Type type)
    {
        if (!type.IsEnum)
        {
            throw new ArgumentException($"'{type}' is not an enum.", nameof(type));
        }

        var attribute = type.GetCustomAttribute<BuildFragmentAttribute>()
            ?? throw new FragmentException(new("RBT1001", DiagnosticSeverity.Error,
                $"Enum '{type.FullName}' must have [BuildFragment]."));
        var id = new FragmentId(attribute.Id);
        var values = Enum.GetNames(type)
            .Select(name => new FragmentValue(id, GetValueId(type.GetField(name)!)))
            .Order()
            .ToImmutableArray();
        var metadata = new FragmentMetadata(id, type, values);

        if (_metadata.TryGetValue(id, out var existing))
        {
            if (existing.ClrType == type)
            {
                return existing;
            }

            throw new FragmentException(new("RBT1002", DiagnosticSeverity.Error,
                $"Fragment ID '{id}' is already registered by '{existing.ClrType?.FullName ?? "a built-in fragment"}'."));
        }

        _metadata.Add(id, metadata);
        return metadata;
    }

    public FragmentValue Encode<T>(T value) where T : struct, Enum
    {
        var metadata = RegisterEnum<T>();
        var name = Enum.GetName(value)
            ?? throw new FragmentException(new("RBT1003", DiagnosticSeverity.Error,
                $"'{value}' is not a named value of '{typeof(T).FullName}'."));
        return new(metadata.Id, GetValueId(typeof(T).GetField(name)!));
    }

    private void RegisterBuiltIn(FragmentId id, ImmutableArray<FragmentValue> values) =>
        _metadata.Add(id, new(id, null, values));

    private static string GetValueId(FieldInfo field) =>
        field.GetCustomAttribute<FragmentValueAttribute>()?.Id ?? ToKebabCase(field.Name);

    public static string ToKebabCase(string value)
    {
        var result = new StringBuilder(value.Length + 4);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) && index > 0 && (char.IsLower(value[index - 1]) || char.IsDigit(value[index - 1])))
            {
                result.Append('-');
            }

            result.Append(char.ToLowerInvariant(character));
        }

        return result.ToString();
    }
}

public sealed class FragmentException(Diagnostic diagnostic) : Exception(diagnostic.Message)
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}

public static class FragmentEncoding
{
    public static FragmentValue Encode<T>(T value) where T : struct, Enum => new FragmentRegistry().Encode(value);
}

public static class BooleanExtensions
{
    public static bool Not(this bool value) => !value;
}
