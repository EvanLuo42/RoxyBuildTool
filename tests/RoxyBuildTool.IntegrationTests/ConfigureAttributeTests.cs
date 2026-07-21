using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Configuration;
using RoxyBuildTool.Model;
using Xunit;

namespace RoxyBuildTool.IntegrationTests;

[BuildFragment("Test.ConfigureFlavor")]
public enum ConfigureFlavor
{
    [FragmentValue("StableEditor")] Editor,
    Client
}

[BuildFragment("Test.Conflicting")]
public enum FirstConflictingFragment
{
    First
}

[BuildFragment("Test.Conflicting")]
public enum SecondConflictingFragment
{
    Second
}

[BuildFragment("Test.Conflicting")]
public enum FilterConflictingFragment
{
    Filter
}

public sealed class ConfigureAttributeTests
{
    [Fact]
    public void GenericFilterMapsMemberNamesAndStableIdsToCanonicalFragmentValues()
    {
        var attribute = new ConfigureAttribute<ConfigureFlavor>("editor", "StableEditor", "client");

        Assert.Equal(new FragmentId("Test.ConfigureFlavor"), attribute.Fragment);
        Assert.Equal(["StableEditor", "Client"], attribute.Values.Select(value => value.Value));
    }

    [Fact]
    public void GenericFilterRejectsUnknownEnumValuesAtDefinitionLoadTime()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ConfigureAttribute<ConfigureFlavor>("typo"));

        Assert.Contains("typo", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistryValidatesFragmentIdentityAcrossEveryTargetWithoutUsingTheCache()
    {
        var registry = new BuildRegistry(Path.GetTempPath());
        registry.AddTarget<FirstConflictingTarget>();
        registry.AddTarget<SecondConflictingTarget>();

        var exception = Assert.Throws<FragmentException>(() => registry.Build());

        Assert.Equal("RBT1002", exception.Diagnostic.Code);
    }

    [Fact]
    public void RegistryValidatesGenericFilterFragmentIdentityAgainstTargetAxes()
    {
        var registry = new BuildRegistry(Path.GetTempPath());
        registry.AddModule<ConflictingFilterModule>();
        registry.AddTarget<FirstConflictingTarget>();

        var exception = Assert.Throws<FragmentException>(() => registry.Build());

        Assert.Equal("RBT1002", exception.Diagnostic.Code);
    }

    [Fact]
    public void ConfiguringAVisitedModuleDiagnosesFilteredFragmentsMissingFromTheTargetMatrix()
    {
        var registry = new BuildRegistry(Path.GetTempPath());
        registry.AddModule<MissingFilteredFragmentModule>();
        registry.AddTarget<ConfigureFlavorTarget>();
        var module = Assert.Single(registry.Build().Modules);

        var exception = Assert.Throws<RuleDefinitionException>(() =>
            module.ConfigureForConfiguration!(new ConfigurationKey([BuildProfiles.Debug])));

        Assert.Equal("RBT2104", exception.Diagnostic.Code);
        Assert.Contains("Test.ConfigureFlavor", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistryDiagnosesUntypedFilterValuesAbsentFromEveryTargetMatrix()
    {
        var registry = new BuildRegistry(Path.GetTempPath());
        registry.AddModule<InvalidUntypedFilterModule>();
        registry.AddTarget<ConfigureFlavorTarget>();

        var exception = Assert.Throws<RuleDefinitionException>(() => registry.Build());

        Assert.Equal("RBT2105", exception.Diagnostic.Code);
        Assert.Contains("Typo", exception.Message, StringComparison.Ordinal);
    }
}

[BuildRuleIgnore]
public sealed class MissingFilteredFragmentModule : CxxModule
{
    [Configure<ConfigureFlavor>("client")]
    private static void ConfigureClient(ModuleRules rules)
    {
        rules.Private.Defines.Add("CLIENT=1");
    }
}

[BuildRuleIgnore]
public sealed class InvalidUntypedFilterModule : CxxModule
{
    [Configure("Test.ConfigureFlavor", "typo")]
    private static void ConfigureTypo(ModuleRules rules)
    {
        rules.Private.Defines.Add("TYPO=1");
    }
}

[BuildRuleIgnore]
public sealed class ConflictingFilterModule : CxxModule
{
    [Configure<FilterConflictingFragment>("filter")]
    private static void ConfigureFilter(ModuleRules rules)
    {
        rules.Private.Defines.Add("FILTER=1");
    }
}

[BuildRuleIgnore]
public sealed class ConfigureFlavorTarget : BuildTarget
{
    [Configure]
    private static void ConfigureTarget(TargetRules rules)
    {
        FirstConflictingTarget.ConfigureMatrix(rules)
            .Axis(ConfigureFlavor.Client, ConfigureFlavor.Editor);
    }
}

[BuildRuleIgnore]
public sealed class FirstConflictingTarget : BuildTarget
{
    [Configure]
    private static void ConfigureTarget(TargetRules rules)
    {
        ConfigureMatrix(rules)
            .Axis(FirstConflictingFragment.First);
    }

    internal static MatrixBuilder ConfigureMatrix(TargetRules rules)
    {
        return rules.Matrix
            .Axis(Configuration.Platforms.Windows)
            .Axis(Architectures.X64)
            .Axis(BuildProfiles.Debug)
            .Axis(Configuration.Toolchains.Msvc)
            .Axis(LinkModels.Modular);
    }
}

[BuildRuleIgnore]
public sealed class SecondConflictingTarget : BuildTarget
{
    [Configure]
    private static void ConfigureTarget(TargetRules rules)
    {
        FirstConflictingTarget.ConfigureMatrix(rules)
            .Axis(SecondConflictingFragment.Second);
    }
}