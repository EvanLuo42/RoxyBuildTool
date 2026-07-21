using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Model;

namespace RoxyBuildTool;

/// <summary>Reads and writes compact pipeline models without JSON metadata or repeated strings.</summary>
internal static class PipelineCacheBinarySerializer
{
    private const uint Magic = 0x59425852; // RXBY
    private const int FormatVersion = 2;
    private static readonly LogicalPath RootLogicalPath = new(".");

    public static void WriteActionGraph(Stream stream, ActionGraph graph)
    {
        using var writer = new CacheWriter(stream, EntryKind.ActionGraph);
        WriteConfiguration(writer, graph.Configuration);
        writer.String(graph.Target);

        var prefixIndexes = new Dictionary<ImmutableArray<string>, int>();
        var prefixes = ImmutableArray.CreateBuilder<ImmutableArray<string>>();
        var actionPrefixIndexes = new int[graph.Actions.Length];
        var environmentIndexes = new Dictionary<ImmutableArray<string>, int>();
        var environments = ImmutableArray.CreateBuilder<ImmutableArray<string>>();
        var actionEnvironmentIndexes = new int[graph.Actions.Length];
        for (var index = 0; index < graph.Actions.Length; index++)
        {
            var action = graph.Actions[index];
            var prefix = action.ArgumentValues.SharedPrefix;
            if (!prefixIndexes.TryGetValue(prefix, out var prefixIndex))
            {
                prefixIndex = prefixes.Count;
                prefixIndexes.Add(prefix, prefixIndex);
                prefixes.Add(prefix);
            }

            actionPrefixIndexes[index] = prefixIndex;
            if (!environmentIndexes.TryGetValue(action.EnvironmentWhitelist, out var environmentIndex))
            {
                environmentIndex = environments.Count;
                environmentIndexes.Add(action.EnvironmentWhitelist, environmentIndex);
                environments.Add(action.EnvironmentWhitelist);
            }

            actionEnvironmentIndexes[index] = environmentIndex;
        }

        writer.Count(prefixes.Count);
        foreach (var prefix in prefixes) writer.Strings(prefix);
        writer.Count(environments.Count);
        foreach (var environment in environments) writer.Strings(environment);
        writer.Count(graph.Actions.Length);
        for (var index = 0; index < graph.Actions.Length; index++)
            WriteBuildAction(
                writer,
                graph.Actions[index],
                actionPrefixIndexes[index],
                actionEnvironmentIndexes[index]);

        writer.Count(graph.Artifacts.Length);
        foreach (var artifact in graph.Artifacts)
        {
            writer.String(artifact.Id);
            writer.Enum(artifact.Kind);
            writer.LogicalPath(artifact.Path);
            writer.String(artifact.ProducerAction);
        }

        writer.Boolean(graph.Toolchain is not null);
        if (graph.Toolchain is not null)
        {
            writer.String(graph.Toolchain.Id);
            writer.String(graph.Toolchain.VisualStudioPlatformToolset);
            writer.Strings(graph.Toolchain.CompileArguments);
            writer.Strings(graph.Toolchain.LinkArguments);
        }

        writer.Complete();
    }

    public static ActionGraph ReadActionGraph(Stream stream)
    {
        using var reader = new CacheReader(stream, EntryKind.ActionGraph);
        var configuration = ReadConfiguration(reader);
        var target = reader.String();
        var prefixes = reader.Array(reader.Strings);
        var environments = reader.Array(reader.Strings);
        var actions = reader.Array(() => ReadBuildAction(reader, prefixes, environments));
        var artifacts = reader.Array(() => new BuildArtifact(
            reader.String(),
            reader.Enum<ArtifactKind>(),
            reader.LogicalPath(),
            reader.String()));
        ToolchainBuildSettings? toolchain = null;
        if (reader.Boolean())
            toolchain = new ToolchainBuildSettings(
                reader.String(),
                reader.String(),
                reader.Strings(),
                reader.Strings());
        reader.Complete();
        return new ActionGraph(configuration, target, actions, artifacts, toolchain);
    }

    private static void WriteConfiguration(CacheWriter writer, ConfigurationKey configuration)
    {
        writer.Count(configuration.Values.Length);
        foreach (var fragment in configuration.Values)
        {
            writer.String(fragment.Fragment.Value);
            writer.String(fragment.Value);
        }
    }

    private static ConfigurationKey ReadConfiguration(CacheReader reader)
    {
        return new ConfigurationKey(
            reader.Array(() => new FragmentValue(new FragmentId(reader.String()), reader.String())));
    }

    private static void WriteBuildAction(
        CacheWriter writer,
        BuildAction action,
        int prefixIndex,
        int environmentIndex)
    {
        writer.String(action.Id);
        writer.Enum(action.Kind);
        writer.String(action.Command);
        writer.Integer(prefixIndex);
        var suffix = action.ArgumentValues.SpecificSuffix;
        writer.Strings(suffix);
        writer.LogicalPath(action.WorkingDirectory);
        var inputsShareSuffix = action.Inputs == suffix;
        writer.Boolean(inputsShareSuffix);
        if (!inputsShareSuffix) writer.Strings(action.Inputs);
        writer.Strings(action.Outputs);
        writer.Strings(action.Dependencies);
        writer.Integer(environmentIndex);
        writer.Boolean(action.Cacheable);
        writer.Boolean(action.RemoteExecutable);
        writer.Strings(action.SensitiveArguments);
    }

    private static BuildAction ReadBuildAction(
        CacheReader reader,
        ImmutableArray<ImmutableArray<string>> prefixes,
        ImmutableArray<ImmutableArray<string>> environments)
    {
        var id = reader.String();
        var kind = reader.Enum<BuildActionKind>();
        var command = reader.String();
        var prefixIndex = reader.Integer();
        if ((uint)prefixIndex >= (uint)prefixes.Length)
            throw new InvalidDataException("The cached action argument prefix index is invalid.");
        var suffix = reader.Strings();
        var workingDirectory = reader.LogicalPath();
        var inputs = reader.Boolean() ? suffix : reader.Strings();
        var outputs = reader.Strings();
        var dependencies = reader.Strings();
        var environmentIndex = reader.Integer();
        if ((uint)environmentIndex >= (uint)environments.Length)
            throw new InvalidDataException("The cached action environment index is invalid.");
        return new BuildAction(
            id,
            kind,
            command,
            new ActionArgumentSequence(prefixes[prefixIndex], suffix),
            workingDirectory,
            inputs,
            outputs,
            dependencies,
            environments[environmentIndex],
            reader.Boolean(),
            reader.Boolean(),
            reader.Strings());
    }

    private enum EntryKind : byte
    {
        ActionGraph = 2
    }

    private sealed class CacheWriter : IDisposable
    {
        private readonly Dictionary<string, int> _stringIndexes = new(StringComparer.Ordinal);
        private readonly List<string> _strings = [];
        private readonly long _tableOffsetPosition;
        private readonly BinaryWriter _writer;
        private bool _completed;

        public CacheWriter(Stream stream, EntryKind kind)
        {
            if (!stream.CanSeek)
                throw new ArgumentException("The binary cache stream must be seekable.", nameof(stream));
            _writer = new BinaryWriter(stream, Encoding.UTF8, true);
            _writer.Write(Magic);
            _writer.Write(FormatVersion);
            _writer.Write((byte)kind);
            _tableOffsetPosition = stream.Position;
            _writer.Write(0L);
        }

        public void Dispose()
        {
            if (!_completed) _writer.Flush();
            _writer.Dispose();
        }

        public void Complete()
        {
            var tableOffset = _writer.BaseStream.Position;
            _writer.Write(_strings.Count);
            foreach (var value in _strings) _writer.Write(value);
            var end = _writer.BaseStream.Position;
            _writer.BaseStream.Position = _tableOffsetPosition;
            _writer.Write(tableOffset);
            _writer.BaseStream.Position = end;
            _writer.Flush();
            _completed = true;
        }

        public void Boolean(bool value)
        {
            _writer.Write(value);
        }

        public void Count(int value)
        {
            _writer.Write(value);
        }

        public void Integer(int value)
        {
            _writer.Write(value);
        }

        public void Enum<T>(T value) where T : struct, Enum
        {
            if (Unsafe.SizeOf<T>() != sizeof(int))
                throw new InvalidOperationException("Cached enum values must use a 32-bit underlying type.");
            _writer.Write(Unsafe.As<T, int>(ref value));
        }

        public void LogicalPath(LogicalPath value)
        {
            String(value.Value);
        }

        public void String(string value)
        {
            Integer(StringIndex(value));
        }

        public void Strings(ImmutableArray<string> values)
        {
            Count(values.IsDefault ? 0 : values.Length);
            if (values.IsDefault) return;
            foreach (var value in values) String(value);
        }

        private int StringIndex(string value)
        {
            if (_stringIndexes.TryGetValue(value, out var index)) return index;
            index = _strings.Count;
            _stringIndexes.Add(value, index);
            _strings.Add(value);
            return index;
        }
    }

    private sealed class CacheReader : IDisposable
    {
        private const int MaximumCollectionLength = 10_000_000;
        private readonly BinaryReader _reader;
        private readonly string[] _strings;
        private readonly long _tableOffset;

        public CacheReader(Stream stream, EntryKind expectedKind)
        {
            if (!stream.CanSeek)
                throw new ArgumentException("The binary cache stream must be seekable.", nameof(stream));
            _reader = new BinaryReader(stream, Encoding.UTF8, true);
            if (_reader.ReadUInt32() != Magic || _reader.ReadInt32() != FormatVersion ||
                _reader.ReadByte() != (byte)expectedKind)
                throw new InvalidDataException("The pipeline cache header is invalid or incompatible.");
            _tableOffset = _reader.ReadInt64();
            var dataOffset = stream.Position;
            if (_tableOffset < dataOffset || _tableOffset > stream.Length - sizeof(int))
                throw new InvalidDataException("The pipeline cache string table offset is invalid.");
            stream.Position = _tableOffset;
            var count = ReadCount();
            _strings = new string[count];
            for (var index = 0; index < count; index++)
                _strings[index] = _reader.ReadString();

            stream.Position = dataOffset;
        }

        public void Dispose()
        {
            _reader.Dispose();
        }

        public void Complete()
        {
            if (_reader.BaseStream.Position != _tableOffset)
                throw new InvalidDataException("The pipeline cache entry has an incompatible payload length.");
        }

        public ImmutableArray<T> Array<T>(Func<T> read)
        {
            var count = ReadCount();
            if (count == 0) return [];
            var values = ImmutableArray.CreateBuilder<T>(count);
            for (var index = 0; index < count; index++) values.Add(read());
            return values.MoveToImmutable();
        }

        public bool Boolean()
        {
            return _reader.ReadBoolean();
        }

        public int Integer()
        {
            return _reader.ReadInt32();
        }

        public T Enum<T>() where T : struct, Enum
        {
            if (Unsafe.SizeOf<T>() != sizeof(int))
                throw new InvalidDataException("A cached enum does not use a 32-bit underlying type.");
            var value = Integer();
            return Unsafe.As<int, T>(ref value);
        }

        public LogicalPath LogicalPath()
        {
            var value = String();
            return value == "." ? RootLogicalPath : new LogicalPath(value);
        }

        public string String()
        {
            return String(Integer());
        }

        public ImmutableArray<string> Strings()
        {
            var count = ReadCount();
            if (count == 0) return [];
            var values = ImmutableArray.CreateBuilder<string>(count);
            for (var index = 0; index < count; index++) values.Add(String());
            return values.MoveToImmutable();
        }

        private int ReadCount()
        {
            var count = _reader.ReadInt32();
            return (uint)count > MaximumCollectionLength
                ? throw new InvalidDataException("A pipeline cache collection length is invalid.")
                : count;
        }

        private string String(int index)
        {
            return (uint)index >= (uint)_strings.Length
                ? throw new InvalidDataException("A pipeline cache string index is invalid.")
                : _strings[index];
        }
    }
}