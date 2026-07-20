using System.Collections.Immutable;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Model;

namespace RoxyBuildTool.Toolchains;

public sealed record CxxProfilePolicy(
    ImmutableArray<string> CompileArguments,
    ImmutableArray<string> LinkArguments,
    bool Optimize,
    bool DebugInformation,
    bool Assertions,
    bool MinimalDiagnostics);

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
    public CxxProfilePolicy GetPolicy(ConfigurationKey configuration)
    {
        var profile = configuration.Values.Single(value => value.Fragment.Value == "profile").Value;
        return Profiles.TryGetValue(profile, out var policy)
            ? policy
            : throw new InvalidOperationException($"Toolchain '{Id}' has no policy for profile '{profile}'.");
    }
}

public sealed record PlatformDescriptor(
    PlatformId Id,
    ImmutableArray<string> Architectures,
    ImmutableArray<ToolchainId> Toolchains,
    CapabilitySet Capabilities);
