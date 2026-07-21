using System.Buffers;
using System.Text;
using System.Text.Json;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Model;

namespace RoxyBuildTool.Generators.CompilationDatabase;

/// <summary>Generates a deterministic Clang-compatible compilation database from compile actions.</summary>
public sealed class CompilationDatabaseGenerator : IWorkspaceGenerator, IWorkspaceGeneratorFingerprintProvider
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true, NewLine = "\n" };

    public WorkspaceGeneratorId Id { get; } = new("CompileDb");
    public CapabilitySet Capabilities { get; } = new(["CompileCommands", "ArgumentsArray"]);

    /// <inheritdoc />
    public string GetAdditionalFingerprint(WorkspaceModel workspace, GenerationContext context)
    {
        return string.Empty;
    }

    /// <inheritdoc />
    public GenerationResult Generate(WorkspaceModel workspace, GenerationContext context)
    {
        var actions = workspace.ActionGraphs
            .SelectMany(graph => graph.Actions)
            .Where(action => action.Kind == BuildActionKind.Compile)
            .OrderBy(action => action.Id, StringComparer.Ordinal)
            .ToArray();
        var directory = Path.GetFullPath(context.WorkspaceRoot);
        using var buffer = new PooledByteBufferWriter(EstimateCapacity(actions, directory));
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartArray();
            foreach (var action in actions)
            {
                writer.WriteStartObject();
                writer.WriteString("directory", directory);
                writer.WriteString("file", action.Inputs.Single(BuildFileKinds.IsCxxSource));
                writer.WriteStartArray("arguments");
                writer.WriteStringValue(action.Command);
                foreach (var argument in action.ArgumentValues)
                    writer.WriteStringValue(argument);
                writer.WriteEndArray();
                writer.WriteString("output", action.Outputs.Single());
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        var content = Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
        return new GenerationResult(Id, [new GeneratedFile(new LogicalPath("compile_commands.json"), content)], []);
    }

    private static int EstimateCapacity(BuildAction[] actions, string directory)
    {
        long capacity = 4;
        foreach (var action in actions)
        {
            capacity += 96 + directory.Length + action.Command.Length;
            foreach (var argument in action.ArgumentValues) capacity += argument.Length + 8;
            foreach (var input in action.Inputs) capacity += input.Length;
            foreach (var output in action.Outputs) capacity += output.Length;
        }

        return (int)Math.Min(capacity, Array.MaxLength);
    }

    private sealed class PooledByteBufferWriter(int initialCapacity) : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        private int _written;

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

        public void Advance(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count > _buffer.Length - _written)
                throw new InvalidOperationException("Cannot advance past the end of the JSON buffer.");
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_written);
        }

        public void Dispose()
        {
            var buffer = _buffer;
            _buffer = [];
            _written = 0;
            ArrayPool<byte>.Shared.Return(buffer);
        }

        private void EnsureCapacity(int sizeHint)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            if (sizeHint == 0) sizeHint = 1;
            if (sizeHint <= _buffer.Length - _written) return;

            var required = checked(_written + sizeHint);
            var newSize = Math.Max(required, _buffer.Length <= Array.MaxLength / 2
                ? _buffer.Length * 2
                : Array.MaxLength);
            var replacement = ArrayPool<byte>.Shared.Rent(newSize);
            _buffer.AsSpan(0, _written).CopyTo(replacement);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = replacement;
        }
    }
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
