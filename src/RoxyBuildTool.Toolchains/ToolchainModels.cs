using System.Collections.Immutable;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Model;

namespace RoxyBuildTool.Toolchains;

/// <summary>Defines compiler and linker policy for one build profile.</summary>
public sealed record CxxProfilePolicy(
    ImmutableArray<string> CompileArguments,
    ImmutableArray<string> LinkArguments,
    bool Optimize,
    bool DebugInformation,
    bool Assertions,
    bool MinimalDiagnostics);

/// <summary>Describes compiler commands, profile policies, IDE metadata, and capabilities for a toolchain.</summary>
public sealed record ToolchainDescriptor(
    ToolchainId Id,
    PlatformId Platform,
    string Architecture,
    string Compiler,
    string Librarian,
    string Linker,
    string VisualStudioPlatformToolset,
    ImmutableDictionary<string, CxxProfilePolicy> Profiles,
    CapabilitySet Capabilities)
{
    /// <summary>Gets the policy selected by the configuration's profile fragment.</summary>
    public CxxProfilePolicy GetPolicy(ConfigurationKey configuration)
    {
        var profile = configuration.Values.Single(value => value.Fragment.Value == "Profile").Value;
        var match = Profiles.FirstOrDefault(pair => pair.Key.Equals(profile, StringComparison.OrdinalIgnoreCase));
        return match.Value
               ?? throw new InvalidOperationException($"Toolchain '{Id}' has no policy for profile '{profile}'.");
    }
}

/// <summary>Describes the architectures, toolchains, and capabilities supported by a platform.</summary>
public sealed record PlatformDescriptor(
    PlatformId Id,
    ImmutableArray<string> Architectures,
    ImmutableArray<ToolchainId> Toolchains,
    CapabilitySet Capabilities);