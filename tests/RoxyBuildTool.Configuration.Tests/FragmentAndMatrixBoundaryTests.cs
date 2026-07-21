using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Configuration;
using RoxyBuildTool.Model;
using Xunit;

namespace RoxyBuildTool.Configuration.Tests;

[BuildFragment("Custom.Mode")]
public enum CustomMode
{
    [FragmentValueAttribute("ExplicitValue")]
    Explicit,
    XML2Parser,
}

public enum MissingFragmentMetadata
{
    Value,
}

public sealed class FragmentAndMatrixBoundaryTests
{
    [Fact]
    public void RegistryStartsWithSortedBuiltIns()
    {
        var registry = new FragmentRegistry();

        Assert.Equal(
            ["Architecture", "LinkModel", "Platform", "Profile", "Toolchain"],
            registry.All.Select(metadata => metadata.Id.Value));
        Assert.All(registry.All, metadata => Assert.Null(metadata.ClrType));
        Assert.Equal(BuildProfiles.All, registry.All.Single(metadata => metadata.Id == FragmentIds.Profile).Values);
    }

    [Fact]
    public void EnumRegistrationHonorsExplicitValueAndIsIdempotent()
    {
        var registry = new FragmentRegistry();

        var first = registry.RegisterEnum<CustomMode>();
#pragma warning disable CA2263 // The Type overload is part of the public contract and needs direct coverage.
        var second = registry.RegisterEnum(typeof(CustomMode));
#pragma warning restore CA2263

        Assert.Same(first, second);
        Assert.Equal(typeof(CustomMode), first.ClrType);
        Assert.Equal(["ExplicitValue", "XML2Parser"], first.Values.Select(value => value.Value));
        Assert.Equal("ExplicitValue", registry.Encode(CustomMode.Explicit).Value);
        Assert.Equal("XML2Parser", FragmentEncoding.Encode(CustomMode.XML2Parser).Value);
    }

    [Fact]
    public void EnumRegistrationDiagnosesInvalidTypesAndMetadata()
    {
#pragma warning disable CA2263 // The Type overload is required to validate non-enum input.
        Assert.Throws<ArgumentException>(() => new FragmentRegistry().RegisterEnum(typeof(string)));
#pragma warning restore CA2263

        var missing = Assert.Throws<FragmentException>(() => new FragmentRegistry().RegisterEnum<MissingFragmentMetadata>());
        Assert.Equal("RBT1001", missing.Diagnostic.Code);

        var registry = new FragmentRegistry();
        registry.RegisterEnum<GameFlavor>();
        var conflict = Assert.Throws<FragmentException>(() => registry.RegisterEnum<ConflictingFlavor>());
        Assert.Equal("RBT1002", conflict.Diagnostic.Code);

        var undefined = Assert.Throws<FragmentException>(() => new FragmentRegistry().Encode((CustomMode)999));
        Assert.Equal("RBT1003", undefined.Diagnostic.Code);
    }

    [Theory]
    [InlineData("pascal-case", "PascalCase")]
    [InlineData("XML2Parser", "XML2Parser")]
    [InlineData("already-kebab", "AlreadyKebab")]
    [InlineData("abc.def", "Abc.Def")]
    [InlineData("a", "A")]
    [InlineData("", "")]
    public void PascalCaseEncodingIsDeterministic(string input, string expected)
    {
        Assert.Equal(expected, FragmentRegistry.ToPascalCase(input));
    }

    [Fact]
    public void MatrixAxisValidatesShapeAndNormalizesValues()
    {
        var builder = new MatrixBuilder();
        Assert.Throws<ArgumentException>(() => builder.Axis([]));
        Assert.Throws<ArgumentException>(() => builder.Axis(Platforms.Windows, Architectures.X64));

        builder.Axis(BuildProfiles.Debug, BuildProfiles.Debug, BuildProfiles.Release);
        var matrix = builder.Build();
        Assert.Equal([BuildProfiles.Debug, BuildProfiles.Release], Assert.Single(matrix.Axes).Values);

        var duplicate = Assert.Throws<FragmentException>(() => builder.Axis(BuildProfiles.Shipping));
        Assert.Equal("RBT1101", duplicate.Diagnostic.Code);
    }

    [Fact]
    public void GenericAxisRetainsEnumMetadata()
    {
        var axis = Assert.Single(new MatrixBuilder().Axis(CustomMode.Explicit, CustomMode.XML2Parser).Build().Axes);

        Assert.Equal(typeof(CustomMode), axis.EnumType);
        Assert.Equal(new FragmentId("Custom.Mode"), axis.Fragment);
    }

    [Fact]
    public void EmptyMatrixResolvesToTheEmptyConfiguration()
    {
        var result = new MatrixResolver(new()).Resolve(new MatrixBuilder().Build());

        Assert.Equal(0, result.CandidateCount);
        Assert.Empty(Assert.Single(result.Configurations).Values);
        Assert.Empty(result.Excluded);
    }

    [Fact]
    public void SelectorsDiagnoseUnknownAxesAndUnknownValues()
    {
        var matrix = new MatrixBuilder().Axis(BuildProfiles.Debug, BuildProfiles.Release).Build();

        var missingValue = Assert.Throws<FragmentException>(() => new MatrixResolver(new()).Resolve(matrix,
            new Dictionary<FragmentId, string> { [FragmentIds.Profile] = "Shipping" }));
        Assert.Equal("RBT1102", missingValue.Diagnostic.Code);

        var missingAxis = Assert.Throws<FragmentException>(() => new MatrixResolver(new()).Resolve(matrix,
            new Dictionary<FragmentId, string> { [FragmentIds.Platform] = "Windows" }));
        Assert.Equal("RBT1103", missingAxis.Diagnostic.Code);
    }

    [Fact]
    public void ConstraintsCoverFalseConditionsAndDeferredAxisReads()
    {
        var matrix = new MatrixBuilder()
            .Axis(BuildProfiles.Debug, BuildProfiles.Release)
            .Axis(LinkModels.Modular, LinkModels.Monolithic)
            .Exclude(view => view.Is(BuildProfiles.Shipping), "never")
            .Require(view => view.Is(BuildProfiles.Release), view => view.Is(LinkModels.Monolithic), "release is monolithic")
            .Build();

        var result = new MatrixResolver(new()).Resolve(matrix);

        Assert.Equal(3, result.Configurations.Length);
        Assert.Single(result.Excluded);
        Assert.Contains(result.Configurations, key => key.Is(BuildProfiles.Debug) && key.Is(LinkModels.Modular));
    }

    [Fact]
    public void ConfigurationViewRequiresTheReferencedFragment()
    {
        var view = new ConfigurationView(new Dictionary<FragmentId, FragmentValue>());
        Assert.ThrowsAny<Exception>(() => view.Is(BuildProfiles.Debug));
        Assert.False(true.Not());
        Assert.True(false.Not());
    }
}
