using System.Collections.Immutable;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Model;
using Xunit;

namespace RoxyBuildTool.Configuration.Tests;

public sealed class ValueObjectTests
{
    [Theory]
    [InlineData("Id")]
    [InlineData("A0")]
    [InlineData("Zero")]
    public void StableIdentifiersAcceptCanonicalValues(string value)
    {
        AssertIdentifier(new FragmentId(value), value);
        AssertIdentifier(new PluginId(value), value);
        AssertIdentifier(new PlatformId(value), value);
        AssertIdentifier(new ToolchainId(value), value);
        AssertIdentifier(new WorkspaceGeneratorId(value), value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("A..B")]
    [InlineData("---")]
    [InlineData("contains/slash")]
    [InlineData("café")]
    public void StableIdentifiersRejectNonCanonicalValues(string? value)
    {
        Assert.ThrowsAny<ArgumentException>(() => new FragmentId(value!));
        Assert.ThrowsAny<ArgumentException>(() => new PluginId(value!));
        Assert.ThrowsAny<ArgumentException>(() => new PlatformId(value!));
        Assert.ThrowsAny<ArgumentException>(() => new ToolchainId(value!));
        Assert.ThrowsAny<ArgumentException>(() => new WorkspaceGeneratorId(value!));
    }

    [Fact]
    public void StableIdentifiersNormalizeLegacyKebabSnakeAndDottedInput()
    {
        Assert.Equal("LegacyId", new FragmentId("legacy-id").Value);
        Assert.Equal("LegacyId", new PluginId("legacy_id").Value);
        Assert.Equal("Game.Flavor", new PlatformId("game.flavor").Value);
    }

    [Fact]
    public void IdentifierComparisonOperatorsUseOrdinalOrdering()
    {
        AssertOrdering(new FragmentId("A"), new FragmentId("B"));
        AssertOrdering(new PluginId("A"), new PluginId("B"));
        AssertOrdering(new PlatformId("A"), new PlatformId("B"));
        AssertOrdering(new ToolchainId("A"), new ToolchainId("B"));
        AssertOrdering(new WorkspaceGeneratorId("A"), new WorkspaceGeneratorId("B"));
    }

    [Fact]
    public void CapabilitiesAreValidatedDeduplicatedAndSorted()
    {
        var capabilities = new CapabilitySet(["ZLast", "AFirst", "ZLast"]);

        Assert.Equal(["AFirst", "ZLast"], capabilities.Values);
        Assert.True(capabilities.Contains("ZLast"));
        Assert.False(capabilities.Contains("Missing"));
        Assert.Empty(CapabilitySet.Empty.Values);
        Assert.Throws<ArgumentException>(() => new CapabilitySet(["not valid"]));
    }

    [Fact]
    public void DiagnosticAndAttributeContractsRetainTheirValues()
    {
        var location = new SourceLocation("Rules.cs", 7, 9);
        var diagnostic = new Diagnostic("RBT1", DiagnosticSeverity.Warning, "message", "module", "Debug", location,
            "help");
        var fragment = new BuildFragmentAttribute("Game.Flavor");
        var value = new FragmentValueAttribute("DedicatedServer");

        Assert.Equal("RBT1", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Same(location, diagnostic.Location);
        Assert.Equal("Game.Flavor", fragment.Id);
        Assert.Equal("DedicatedServer", value.Id);
        Assert.Equal(0, new SourceLocation("file").Line);
    }

    [Theory]
    [InlineData("Value")]
    [InlineData("Version.4")]
    public void FragmentValuesAcceptStableValues(string value)
    {
        var fragment = new FragmentId("Fragment");
        var actual = new FragmentValue(fragment, value);

        Assert.Equal(fragment, actual.Fragment);
        Assert.Equal(value, actual.Value);
        Assert.Equal($"Fragment={value}", actual.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("A..B")]
    [InlineData("with/slash")]
    public void FragmentValuesRejectInvalidValues(string? value)
    {
        Assert.ThrowsAny<ArgumentException>(() => new FragmentValue(new("Fragment"), value!));
    }

    [Fact]
    public void FragmentValuesNormalizeLegacyInput()
    {
        Assert.Equal("LegacyValue", new FragmentValue(new("Fragment"), "legacy-value").Value);
    }

    [Fact]
    public void FragmentValuesOrderByFragmentThenValue()
    {
        var first = new FragmentValue(new("A"), "Z");
        var second = new FragmentValue(new("B"), "A");
        var third = new FragmentValue(new("B"), "B");

        Assert.True(first < second);
        Assert.True(first <= second);
        Assert.True(third > second);
        Assert.True(third >= second);
        Assert.True(second.CompareTo(third) < 0);
    }

    [Fact]
    public void ConfigurationKeySupportsLookupOrderingAndStableHashing()
    {
        var a = new FragmentValue(new("A"), "One");
        var b = new FragmentValue(new("B"), "Two");
        var key = new ConfigurationKey([b, a]);
        var same = new ConfigurationKey([a, b]);
        var later = new ConfigurationKey([a, new(new("B"), "Z")]);

        Assert.Equal("A=One;B=Two", key.Canonical);
        Assert.Equal(key.ShortHash, same.ShortHash);
        Assert.Equal(key, same);
        Assert.Equal(key.GetHashCode(), same.GetHashCode());
        Assert.Equal(12, key.ShortHash.Length);
        Assert.True(key.TryGet(a.Fragment, out var found));
        Assert.Equal(a, found);
        Assert.False(key.TryGet(new("Missing"), out _));
        Assert.True(key.Is(b));
        Assert.False(key.Is(new(new("B"), "Other")));
        Assert.Equal(key.Canonical, key.ToString());
        Assert.True(key < later);
        Assert.True(key <= later);
        Assert.True(later > key);
        Assert.True(later >= key);
        Assert.Equal(1, key.CompareTo(null));
        Assert.True((ConfigurationKey?)null < key);
    }

    [Fact]
    public void ConfigurationKeyRejectsDuplicateFragments()
    {
        var exception = Assert.Throws<ArgumentException>(() => new ConfigurationKey([
            new(new("Duplicate"), "One"),
            new(new("Duplicate"), "Two"),
        ]));

        Assert.Contains("Duplicate", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".", ".")]
    [InlineData("./src\\main.cpp", "src/main.cpp")]
    [InlineData("././relative/path", "relative/path")]
    [InlineData("relative//./path/", "relative/path")]
    [InlineData("./", ".")]
    public void LogicalPathsNormalizeWorkspaceRelativeInput(string input, string expected)
    {
        var path = new LogicalPath(input);

        Assert.Equal(expected, path.Value);
        Assert.Equal(Path.GetFileName(expected), path.FileName);
        Assert.Equal(expected, path.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../outside")]
    [InlineData("dir/../outside")]
    [InlineData("C:\\absolute")]
    public void LogicalPathsRejectInvalidOrEscapingInput(string? input)
    {
        Assert.ThrowsAny<ArgumentException>(() => new LogicalPath(input!));
    }

    [Fact]
    public void UsageUnionIsDeterministicAndKeepsTheEarliestOrigin()
    {
        var left = new UsageRequirements(
            [new("z/include", "ZOrigin"), new("shared", "ZOrigin")],
            [new("LEFT", "left")],
            [new("left.lib", "left")],
            []);
        var right = new UsageRequirements(
            [new("a/include", "right"), new("shared", "AOrigin")],
            [new("RIGHT", "right")],
            [],
            [new("runtime.dll", "right")]);

        var union = left.Union(right);

        Assert.Equal(["a/include", "shared", "z/include"], union.IncludeDirectories.Select(item => item.Value));
        Assert.Equal("AOrigin", union.IncludeDirectories.Single(item => item.Value == "shared").Origin);
        Assert.Equal(["LEFT", "RIGHT"], union.Defines.Select(item => item.Value));
        Assert.Equal("left.lib", Assert.Single(union.LinkInputs).Value);
        Assert.Equal("runtime.dll", Assert.Single(union.RuntimeFiles).Value);
        Assert.Same(UsageRequirements.Empty, UsageRequirements.Empty);
    }

    [Fact]
    public void ConfiguredGraphRequiresExactlyOneMatchingModule()
    {
        var module = Module("One");
        var graph = new ConfiguredGraph(new([]), new("target", "Target", ["One"]), [module], []);

        Assert.Same(module, graph.GetModule("One"));
        Assert.Throws<InvalidOperationException>(() => graph.GetModule("Missing"));
    }

    [Fact]
    public void BuildActionSemanticHashIncludesBuildIdentityButNotExecutionPolicy()
    {
        var original = Action("action", ["/c", "source.cpp"]);
        var policyOnlyChange = original with
        {
            EnvironmentWhitelist = ["PATH"],
            Cacheable = false,
            RemoteExecutable = false,
            SensitiveArguments = ["secret"],
        };
        var argumentChange = original with { Arguments = ["/c", "other.cpp"] };

        Assert.Equal(original.SemanticHash, policyOnlyChange.SemanticHash);
        Assert.NotEqual(original.SemanticHash, argumentChange.SemanticHash);
        Assert.Equal(64, original.SemanticHash.Length);
    }

    [Fact]
    public void ActionGraphValidationReportsDuplicateOutputsAndMissingDependencies()
    {
        var first = Action("first", ["One"]);
        var second = Action("second", ["Two"]) with { Outputs = first.Outputs, Dependencies = ["Missing"] };
        var graph = new ActionGraph(new([]), "target", [first, second], []);

        var diagnostics = graph.Validate();

        Assert.Contains(diagnostics,
            item => item.Code == "RBT3002" && item.Message.Contains("first, second", StringComparison.Ordinal));
        Assert.Contains(diagnostics,
            item => item.Code == "RBT3003" && item.Message.Contains("Missing", StringComparison.Ordinal));
        Assert.Empty(new ActionGraph(new([]), "target", [first], []).Validate());
    }

    [Fact]
    public void ActionGraphValidationRejectsNonCanonicalAndEscapingOutputs()
    {
        var alias = Action("alias", ["One"]) with { Outputs = ["out\\./file.obj"] };
        var escaping = Action("escaping", ["Two"]) with { Outputs = ["../file.obj"] };

        var diagnostics = new ActionGraph(new ConfigurationKey([]), "target", [alias, escaping], []).Validate();

        Assert.Equal(2, diagnostics.Count(item => item.Code == "RBT3009"));
    }

    [Fact]
    public void ActionGraphValidationReportsDuplicateIdsCyclesAndInvalidArtifacts()
    {
        var first = Action("first", ["One"]) with { Outputs = ["first.obj"], Dependencies = ["second"] };
        var duplicateFirst = Action("first", ["Duplicate"]) with { Outputs = ["duplicate.obj"] };
        var second = Action("second", ["Two"]) with { Outputs = ["second.obj"], Dependencies = ["first"] };
        var graph = new ActionGraph(new ConfigurationKey([]), "target", [first, duplicateFirst, second],
        [
            new BuildArtifact("missing", ArtifactKind.ObjectFile, new LogicalPath("missing.obj"), "absent"),
            new BuildArtifact("mismatch", ArtifactKind.ObjectFile, new LogicalPath("other.obj"), "second"),
            new BuildArtifact("mismatch", ArtifactKind.ObjectFile, new LogicalPath("second.obj"), "second")
        ]);

        var diagnostics = graph.Validate();

        Assert.Contains(diagnostics,
            item => item.Code == "RBT3001" && item.Message.Contains("first", StringComparison.Ordinal));
        Assert.Contains(diagnostics,
            item => item.Code == "RBT3004" && item.Message.Contains("absent", StringComparison.Ordinal));
        Assert.Contains(diagnostics,
            item => item.Code == "RBT3005" && item.Message.Contains("other.obj", StringComparison.Ordinal));
        Assert.Contains(diagnostics,
            item => item.Code == "RBT3006" &&
                    item.Message.Contains("first -> second -> first", StringComparison.Ordinal));
        Assert.Contains(diagnostics,
            item => item.Code == "RBT3008" && item.Message.Contains("mismatch", StringComparison.Ordinal));
    }

    private static ConfiguredModule Module(string id) => new(
        id, id, ModuleLanguage.Cxx, ModuleKind.HeaderOnly, [], UsageRequirements.Empty, UsageRequirements.Empty,
        UsageRequirements.Empty, UsageRequirements.Empty, [], [], []);

    private static BuildAction Action(string id, ImmutableArray<string> arguments) => new(
        id, BuildActionKind.Compile, "compiler", arguments, new LogicalPath("."), ["source.cpp"], ["output.obj"], [],
        [], true,
        true, []);

    private static void AssertIdentifier<T>(T id, string expected) where T : struct
    {
        Assert.Equal(expected, id.ToString());
    }

    private static void AssertOrdering(FragmentId left, FragmentId right)
    {
        Assert.True(left < right);
        Assert.True(left <= right);
        Assert.True(right > left);
        Assert.True(right >= left);
        Assert.True(left.CompareTo(right) < 0);
    }

    private static void AssertOrdering(PluginId left, PluginId right)
    {
        Assert.True(left < right);
        Assert.True(left <= right);
        Assert.True(right > left);
        Assert.True(right >= left);
        Assert.True(left.CompareTo(right) < 0);
    }

    private static void AssertOrdering(PlatformId left, PlatformId right)
    {
        Assert.True(left < right);
        Assert.True(left <= right);
        Assert.True(right > left);
        Assert.True(right >= left);
        Assert.True(left.CompareTo(right) < 0);
    }

    private static void AssertOrdering(ToolchainId left, ToolchainId right)
    {
        Assert.True(left < right);
        Assert.True(left <= right);
        Assert.True(right > left);
        Assert.True(right >= left);
        Assert.True(left.CompareTo(right) < 0);
    }

    private static void AssertOrdering(WorkspaceGeneratorId left, WorkspaceGeneratorId right)
    {
        Assert.True(left < right);
        Assert.True(left <= right);
        Assert.True(right > left);
        Assert.True(right >= left);
        Assert.True(left.CompareTo(right) < 0);
    }
}