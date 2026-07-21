using System.Collections.Immutable;
using System.Text.Json;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Model;

namespace RoxyBuildTool.Generators.CompilationDatabase;

/// <summary>Generates a deterministic Clang-compatible compilation database from compile actions.</summary>
public sealed class CompilationDatabaseGenerator : IWorkspaceGenerator
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public WorkspaceGeneratorId Id { get; } = new("CompileDb");
    public CapabilitySet Capabilities { get; } = new(["CompileCommands", "ArgumentsArray"]);

    /// <inheritdoc />
    public GenerationResult Generate(WorkspaceModel workspace, GenerationContext context)
    {
        var entries = workspace.ActionGraphs
            .SelectMany(graph => graph.Actions)
            .Where(action => action.Kind == BuildActionKind.Compile)
            .OrderBy(action => action.Id, StringComparer.Ordinal)
            .Select(action => new CompileCommand(
                Path.GetFullPath(context.WorkspaceRoot),
                action.Inputs.Single(BuildFileKinds.IsCxxSource),
                [action.Command, .. action.Arguments],
                action.Outputs.Single()))
            .ToImmutableArray();
        var content = JsonSerializer.Serialize(entries, SerializerOptions)
            .Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        return new GenerationResult(Id, [new GeneratedFile(new LogicalPath("compile_commands.json"), content)], []);
    }

    private sealed record CompileCommand(
        string Directory,
        string File,
        ImmutableArray<string> Arguments,
        string Output);
}

/// <summary>Registers the compilation database workspace generator.</summary>
public sealed class CompilationDatabasePlugin : IPlugin
{
    public PluginId Id { get; } = new("Roxy.Generator.CompileDb");
    public Version Version { get; } = new(0, 1, 0);
    public CapabilitySet Capabilities { get; } = new(["Workspace.CompileDb"]);

    public void Register(IPluginRegistry registry)
    {
        registry.AddService<IWorkspaceGenerator>(new CompilationDatabaseGenerator());
    }
}

/// <summary>Provides compilation database composition extensions.</summary>
public static class CompilationDatabaseExtensions
{
    /// <summary>Adds the compilation database plugin and returns the same builder.</summary>
    public static T UseCompilationDatabase<T>(this T builder) where T : IBuildToolBuilder
    {
        builder.AddPlugin(new CompilationDatabasePlugin());
        return builder;
    }
}