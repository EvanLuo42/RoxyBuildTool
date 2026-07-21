using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Configuration;
using RoxyBuildTool.Model;
using Xunit;

namespace RoxyBuildTool.Configuration.Tests;

[BuildFragment("Game.Flavor")]
public enum GameFlavor
{
    Editor = 900,
    Client = -7,
    DedicatedServer = 42,
}

[BuildFragment("Game.Flavor")]
public enum ConflictingFlavor
{
    Other,
}

public sealed class MatrixTests
{
    [Fact]
    public void CanonicalKeyIsSortedAndIgnoresEnumIntegerValues()
    {
        var registry = new FragmentRegistry();
        var flavor = registry.Encode(GameFlavor.DedicatedServer);
        var key = new ConfigurationKey([
            BuildProfiles.Development,
            flavor,
            Architectures.X64,
            Platforms.Windows,
            Toolchains.Msvc,
            LinkModels.Modular,
        ]);

        Assert.Equal(
            "Architecture=X64;Game.Flavor=DedicatedServer;LinkModel=Modular;Platform=Windows;Profile=Development;Toolchain=Msvc14.4",
            key.Canonical);
    }

    [Fact]
    public void ResolverPrunesARejectedPrefixBeforeExpandingLaterAxes()
    {
        var matrix = new MatrixBuilder()
            .Axis(GameFlavor.Client, GameFlavor.DedicatedServer, GameFlavor.Editor)
            .Axis(BuildProfiles.All.ToArray())
            .Exclude(configuration => configuration.Is(GameFlavor.DedicatedServer), "Dedicated server disabled")
            .Build();

        var result = new MatrixResolver(new()).Resolve(matrix);

        Assert.Equal(8, result.Configurations.Length);
        Assert.Equal(11, result.CandidateCount);
        Assert.Contains(result.Excluded, excluded =>
            excluded.AssignedPrefix == "Game.Flavor=DedicatedServer" &&
            excluded.Reason == "Dedicated server disabled");
    }

    [Fact]
    public void RequireWaitsUntilEveryReferencedAxisIsAssigned()
    {
        var matrix = new MatrixBuilder()
            .Axis(GameFlavor.Client, GameFlavor.Editor)
            .Axis(BuildProfiles.Debug, BuildProfiles.Shipping)
            .Require(
                configuration => configuration.Is(GameFlavor.Editor),
                configuration => configuration.Is(BuildProfiles.Shipping).Not(),
                "Editor is never shipped")
            .Build();

        var result = new MatrixResolver(new()).Resolve(matrix);

        Assert.Equal(3, result.Configurations.Length);
        Assert.DoesNotContain(result.Configurations,
            configuration => configuration.Is(FragmentEncoding.Encode(GameFlavor.Editor)) && configuration.Is(BuildProfiles.Shipping));
    }

    [Fact]
    public void SelectorsAreAppliedBeforeExpansion()
    {
        var matrix = new MatrixBuilder()
            .Axis(GameFlavor.Client, GameFlavor.Editor)
            .Axis(BuildProfiles.All.ToArray())
            .Build();
        var selectors = new Dictionary<FragmentId, string>
        {
            [FragmentIds.Profile] = "Development",
            [new("Game.Flavor")] = "Editor",
        };

        var result = new MatrixResolver(new()).Resolve(matrix, selectors);

        var configuration = Assert.Single(result.Configurations);
        Assert.Equal("Game.Flavor=Editor;Profile=Development", configuration.Canonical);
    }

    [Fact]
    public void DuplicateFragmentIdsAreDiagnosedAcrossEnumTypes()
    {
        var registry = new FragmentRegistry();
        registry.RegisterEnum<GameFlavor>();

        var exception = Assert.Throws<FragmentException>(() => registry.RegisterEnum<ConflictingFlavor>());

        Assert.Equal("RBT1002", exception.Diagnostic.Code);
    }
}
