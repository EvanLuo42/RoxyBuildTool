using System.Collections.Immutable;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Toolchains;

namespace RoxyBuildTool.Platforms.Windows;

public sealed class WindowsPlatformPlugin : IPlugin
{
    public PluginId Id { get; } = new("roxy.windows");
    public Version Version { get; } = new(0, 1, 0);
    public CapabilitySet Capabilities { get; } = new([
        "platform.windows",
        "architecture.x64",
        "toolchain.msvc",
        "dynamic-library",
        "dotnet.net10",
        "cpp-cli",
    ]);

    public void Register(IPluginRegistry registry)
    {
        var toolchainId = new ToolchainId("msvc-14.4");
        registry.AddService(new PlatformDescriptor(
            new("windows"),
            ["x64"],
            [toolchainId],
            Capabilities));
        registry.AddService(new ToolchainDescriptor(
            toolchainId,
            new("windows"),
            "x64",
            "cl.exe",
            "lib.exe",
            "link.exe",
            "v143",
            CreatePolicies(),
            new(["cxx20", "cxx-latest", "shared-library", "response-file"])));
    }

    private static ImmutableDictionary<string, CxxProfilePolicy> CreatePolicies() =>
        new Dictionary<string, CxxProfilePolicy>(StringComparer.Ordinal)
        {
            ["debug"] = new(["/Od", "/Zi", "/RTC1", "/DROXY_DEBUG=1"], ["/DEBUG"], false, true, true, false),
            ["development"] = new(["/O2", "/Zi", "/DROXY_DEVELOPMENT=1"], ["/DEBUG", "/INCREMENTAL"], true, true, true, false),
            ["release"] = new(["/O2", "/Zi", "/DNDEBUG"], ["/DEBUG", "/OPT:REF", "/OPT:ICF"], true, true, false, false),
            ["shipping"] = new(["/O2", "/GL", "/DNDEBUG", "/DROXY_SHIPPING=1"], ["/LTCG", "/OPT:REF", "/OPT:ICF"], true, true, false, true),
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
