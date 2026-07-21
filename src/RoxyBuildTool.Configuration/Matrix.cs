using System.Collections.Immutable;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Model;

namespace RoxyBuildTool.Configuration;

public sealed class MatrixBuilder
{
    private readonly List<MatrixAxis> _axes = [];
    private readonly List<MatrixConstraint> _constraints = [];

    public MatrixBuilder Axis(params FragmentValue[] values)
    {
        AddAxis(values, null);
        return this;
    }

    public MatrixBuilder Axis<T>(params T[] values) where T : struct, Enum
    {
        var registry = new FragmentRegistry();
        AddAxis(values.Select(registry.Encode).ToArray(), typeof(T));
        return this;
    }

    public MatrixBuilder Exclude(Func<ConfigurationView, bool> predicate, string reason)
    {
        _constraints.Add(MatrixConstraint.Exclude(predicate, reason));
        return this;
    }

    public MatrixBuilder Require(
        Func<ConfigurationView, bool> when,
        Func<ConfigurationView, bool> requirement,
        string reason)
    {
        _constraints.Add(MatrixConstraint.Require(when, requirement, reason));
        return this;
    }

    public MatrixDefinition Build() => new(_axes.ToImmutableArray(), _constraints.ToImmutableArray());

    private void AddAxis(FragmentValue[] values, Type? enumType)
    {
        if (values.Length == 0)
        {
            throw new ArgumentException("A matrix axis must contain at least one value.", nameof(values));
        }

        var fragment = values.First().Fragment;
        if (values.Any(value => value.Fragment != fragment))
        {
            throw new ArgumentException("Every value in a matrix axis must belong to the same fragment.", nameof(values));
        }

        if (_axes.Any(axis => axis.Fragment == fragment))
        {
            throw new FragmentException(new("RBT1101", DiagnosticSeverity.Error,
                $"Matrix contains duplicate axis '{fragment}'."));
        }

        _axes.Add(new(fragment, values.Distinct().Order().ToImmutableArray(), enumType));
    }
}

public sealed record MatrixAxis(FragmentId Fragment, ImmutableArray<FragmentValue> Values, Type? EnumType);

public sealed record MatrixDefinition(
    ImmutableArray<MatrixAxis> Axes,
    ImmutableArray<MatrixConstraint> Constraints);

public sealed class ConfigurationView(IReadOnlyDictionary<FragmentId, FragmentValue> values)
{
    public bool Is(FragmentValue value)
    {
        if (!values.TryGetValue(value.Fragment, out var actual))
        {
            throw new IncompleteConfigurationException(value.Fragment);
        }

        return actual == value;
    }

    public bool Is<T>(T value) where T : struct, Enum => Is(FragmentEncoding.Encode(value));
}

internal sealed class IncompleteConfigurationException(FragmentId fragment) : Exception
{
    public FragmentId Fragment { get; } = fragment;
}

public sealed record ExcludedConfiguration(string AssignedPrefix, string Reason);

public sealed record MatrixResolution(
    ImmutableArray<ConfigurationKey> Configurations,
    ImmutableArray<ExcludedConfiguration> Excluded,
    int CandidateCount);

public sealed class MatrixResolver(FragmentRegistry registry)
{
    public MatrixResolution Resolve(
        MatrixDefinition matrix,
        IReadOnlyDictionary<FragmentId, string>? selectors = null)
    {
        selectors ??= ImmutableDictionary<FragmentId, string>.Empty;
        var selectedAxes = new List<MatrixAxis>(matrix.Axes.Length);

        foreach (var axis in matrix.Axes)
        {
            if (axis.EnumType is not null)
            {
                registry.RegisterEnum(axis.EnumType);
            }

            var values = axis.Values;
            if (selectors.TryGetValue(axis.Fragment, out var selected))
            {
                selected = FragmentRegistry.ToPascalCase(selected);
                values = values.Where(value => value.Value == selected).ToImmutableArray();
                if (values.IsEmpty)
                {
                    throw new FragmentException(new("RBT1102", DiagnosticSeverity.Error,
                        $"Selector '{axis.Fragment}={selected}' does not match this matrix."));
                }
            }

            selectedAxes.Add(axis with { Values = values });
        }

        foreach (var selector in selectors.Keys.Where(selector => matrix.Axes.All(axis => axis.Fragment != selector)))
        {
            throw new FragmentException(new("RBT1103", DiagnosticSeverity.Error,
                $"Selector references fragment '{selector}', which is not an axis of this matrix."));
        }

        var completed = ImmutableArray.CreateBuilder<ConfigurationKey>();
        var excluded = ImmutableArray.CreateBuilder<ExcludedConfiguration>();
        var candidates = 0;
        Expand(0, new Dictionary<FragmentId, FragmentValue>());
        return new(completed.Order().ToImmutableArray(), excluded.ToImmutable(), candidates);

        void Expand(int axisIndex, Dictionary<FragmentId, FragmentValue> assigned)
        {
            if (axisIndex == selectedAxes.Count)
            {
                completed.Add(new ConfigurationKey(assigned.Values));
                return;
            }

            var axis = selectedAxes[axisIndex];
            foreach (var value in axis.Values)
            {
                candidates++;
                assigned.Add(axis.Fragment, value);
                var rejection = EvaluateConstraints(matrix.Constraints, assigned);
                if (rejection is null)
                {
                    Expand(axisIndex + 1, assigned);
                }
                else
                {
                    excluded.Add(new(
                        string.Join(';', assigned.Values.Order()),
                        rejection));
                }

                assigned.Remove(axis.Fragment);
            }
        }
    }

    private static string? EvaluateConstraints(
        ImmutableArray<MatrixConstraint> constraints,
        IReadOnlyDictionary<FragmentId, FragmentValue> assigned)
    {
        var view = new ConfigurationView(assigned);
        foreach (var constraint in constraints)
        {
            try
            {
                if (constraint.IsRejected(view))
                {
                    return constraint.Reason;
                }
            }
            catch (IncompleteConfigurationException)
            {
                // This constraint will be retried after another axis has been assigned.
            }
        }

        return null;
    }
}

public sealed record MatrixConstraint
{
    private MatrixConstraint(
        Func<ConfigurationView, bool> condition,
        Func<ConfigurationView, bool>? requirement,
        string reason)
    {
        Condition = condition;
        Requirement = requirement;
        Reason = reason;
    }

    private Func<ConfigurationView, bool> Condition { get; }
    private Func<ConfigurationView, bool>? Requirement { get; }
    public string Reason { get; }

    public static MatrixConstraint Exclude(Func<ConfigurationView, bool> predicate, string reason) =>
        new(predicate, null, reason);

    public static MatrixConstraint Require(
        Func<ConfigurationView, bool> when,
        Func<ConfigurationView, bool> requirement,
        string reason) => new(when, requirement, reason);

    public bool IsRejected(ConfigurationView view) =>
        Requirement is null ? Condition(view) : Condition(view) && !Requirement(view);
}
