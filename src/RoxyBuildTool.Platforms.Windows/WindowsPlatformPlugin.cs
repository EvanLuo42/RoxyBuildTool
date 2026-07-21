using System.Collections.Immutable;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Toolchains;

namespace RoxyBuildTool.Platforms.Windows;

public sealed class WindowsPlatformPlugin : IPlugin
{
    public PluginId Id { get; } = new("Roxy.Windows");
    public Version Version { get; } = new(0, 1, 0);
    public CapabilitySet Capabilities { get; } = new([
        "Platform.Windows",
        "Architecture.X64",
        "Toolchain.Msvc",
        "DynamicLibrary",
        "Dotnet.Net10",
        "CppCli",
    ]);

    public void Register(IPluginRegistry registry)
    {
        var toolchainId = new ToolchainId("Msvc14.4");
        registry.AddService(new PlatformDescriptor(
            new("Windows"),
            ["X64"],
            [toolchainId],
            Capabilities));
        registry.AddService(new ToolchainDescriptor(
            toolchainId,
            new("Windows"),
            "X64",
            "cl.exe",
            "lib.exe",
            "link.exe",
            "v143",
            CreatePolicies(),
            new(["Cxx20", "CxxLatest", "SharedLibrary", "ResponseFile"])));
    }

    private static ImmutableDictionary<string, CxxProfilePolicy> CreatePolicies() =>
        new Dictionary<string, CxxProfilePolicy>(StringComparer.Ordinal)
        {
            ["Debug"] = new(["/Od", "/Zi", "/RTC1", "/DROXY_DEBUG=1"], ["/DEBUG"], false, true, true, false),
            ["Development"] = new(["/O2", "/Zi", "/DROXY_DEVELOPMENT=1"], ["/DEBUG", "/INCREMENTAL"], true, true, true, false),
            ["Release"] = new(["/O2", "/Zi", "/DNDEBUG"], ["/DEBUG", "/OPT:REF", "/OPT:ICF"], true, true, false, false),
            ["Shipping"] = new(["/O2", "/GL", "/DNDEBUG", "/DROXY_SHIPPING=1"], ["/LTCG", "/OPT:REF", "/OPT:ICF"], true, true, false, true),
        }.ToImmutableDictionary(StringComparer.Ordinal);
}

public static class WindowsPlatformExtensions
{
    public static T UseWindowsPlatform<T>(this T builder) where T : IBuildToolBuilder
    {
        builder.AddPlugin(new WindowsPlatformPlugin());
        return builder;
    }
}
