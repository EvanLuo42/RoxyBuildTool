using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.Graph;
using RoxyBuildTool.Model;
using RoxyBuildTool.Toolchains;

namespace RoxyBuildTool;

/// <summary>Stores content-addressed action graphs and validated generation snapshots.</summary>
internal sealed class PipelineCache(string workspaceRoot)
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions CacheJson = CreateCacheJson();

    private readonly ConcurrentDictionary<string, Lazy<ActionGraph>> _actionGraphs = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, Lazy<ConfiguredGraph>>
        _configuredGraphs = new(StringComparer.Ordinal);

    private readonly string _root = Path.Combine(workspaceRoot, ".roxy", "cache", $"v{SchemaVersion}");
    private readonly string _workspaceRoot = Path.GetFullPath(workspaceRoot);

    public ConfiguredGraph GetOrAddConfiguredGraph(string fingerprint, Func<ConfiguredGraph> factory) =>
        _configuredGraphs.GetOrAdd(fingerprint, key => new Lazy<ConfiguredGraph>(
            factory, LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    public ActionGraph GetOrAddActionGraph(string fingerprint, Func<ActionGraph> factory) =>
        _actionGraphs.GetOrAdd(fingerprint, key => new Lazy<ActionGraph>(
            () => LoadActionGraph(key, factory), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    public static string DefinitionFingerprint(
        DefinitionGraph definitions,
        TargetDefinition target)
    {
        var payload = new
        {
            SchemaVersion,
            Target = new
            {
                target.Id,
                target.DisplayName,
                Roots = target.RootModules,
            },
            RuleAssemblies = definitions.RuleAssemblyIdentities.IsDefault
                ? []
                : definitions.RuleAssemblyIdentities,
            Modules = definitions.Modules.OrderBy(module => module.Id, StringComparer.Ordinal).Select(module => new
            {
                module.Id,
                module.DisplayName,
                module.Kind,
                Sources = module.Sources.Select(source => source.Value),
                module.PublicUsage,
                module.PrivateUsage,
                module.Dependencies,
                module.ConditionalRules,
                module.CxxSettings,
            }),
        };
        return Hash(payload);
    }

    public static string ConfiguredGraphFingerprint(
        string definitionFingerprint,
        ConfigurationKey configuration) => Hash(new
    {
        SchemaVersion,
        Resolver = typeof(DependencyResolver).Assembly.ManifestModule.ModuleVersionId,
        Definitions = definitionFingerprint,
        Configuration = configuration.Canonical,
    });

    public static string ResolvedGraphFingerprint(ConfiguredGraph graph) => Hash(new
    {
        SchemaVersion,
        Resolver = typeof(DependencyResolver).Assembly.ManifestModule.ModuleVersionId,
        Graph = graph,
    });

    public static string ActionGraphFingerprint(
        string configuredGraphFingerprint,
        ToolchainDescriptor toolchain,
        string workspaceName)
    {
        return Hash(new
        {
            SchemaVersion,
            Lowerer = typeof(ActionGraphLowerer).Assembly.ManifestModule.ModuleVersionId,
            ConfiguredGraph = configuredGraphFingerprint,
            Toolchain = toolchain,
            Workspace = workspaceName,
        });
    }

    public static string GenerationFingerprint(
        WorkspaceDefinition workspace,
        WorkspaceModel model,
        IReadOnlyDictionary<FragmentId, string> selectors,
        ImmutableArray<string> generators,
        IEnumerable<string> generatorIdentities,
        IEnumerable<string> generatorFingerprints,
        IEnumerable<string> pluginIdentities,
        IEnumerable<ToolchainDescriptor> toolchains)
    {
        return Hash(new
        {
            SchemaVersion,
            Host = typeof(BuildToolApp).Assembly.ManifestModule.ModuleVersionId,
            Assembler = typeof(WorkspaceAssembler).Assembly.ManifestModule.ModuleVersionId,
            Workspace = workspace,
            Model = model,
            Selectors = selectors.OrderBy(selector => selector.Key.Value, StringComparer.Ordinal)
                .Select(selector => $"{selector.Key.Value}={selector.Value}"),
            Generators = generators.Order(StringComparer.Ordinal),
            GeneratorIdentities = generatorIdentities.Order(StringComparer.Ordinal),
            GeneratorFingerprints = generatorFingerprints.Order(StringComparer.Ordinal),
            PluginIdentities = pluginIdentities.Order(StringComparer.Ordinal),
            Toolchains = toolchains.OrderBy(toolchain => toolchain.Id.Value, StringComparer.Ordinal)
        });
    }

    public bool TryLoadGenerationSnapshot(string fingerprint, out GenerationSnapshot snapshot)
    {
        var path = GenerationSnapshotPath(fingerprint);
        if (!File.Exists(path))
        {
            snapshot = null!;
            return false;
        }

        try
        {
            using var file = File.OpenRead(path);
            snapshot = JsonSerializer.Deserialize<GenerationSnapshot>(file, CacheJson)
                       ?? throw new JsonException("The generation snapshot was empty.");
            foreach (var cached in snapshot.Files)
            {
                var absolute = SafeWorkspacePath(cached.Path);
                if (!File.Exists(absolute) || !FileHash(absolute).Equals(cached.Hash, StringComparison.Ordinal))
                {
                    TryDelete(path);
                    snapshot = null!;
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or
                                              ArgumentException or UnauthorizedAccessException)
        {
            TryDelete(path);
            snapshot = null!;
            return false;
        }
    }

    public void StoreGenerationSnapshot(
        string fingerprint,
        IEnumerable<string> files,
        IEnumerable<string> reportedOutputs,
        string manifestPath)
    {
        var path = GenerationSnapshotPath(fingerprint);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var cachedFiles = files.Distinct(LogicalPath.FileSystemComparer)
                .Order(StringComparer.Ordinal)
                .Select(relative => new GenerationSnapshotFile(
                    relative.Replace('\\', '/'),
                    FileHash(SafeWorkspacePath(relative))))
                .ToImmutableArray();
            var snapshot = new GenerationSnapshot(
                cachedFiles,
                reportedOutputs.Select(output => output.Replace('\\', '/')).ToImmutableArray(),
                manifestPath.Replace('\\', '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, snapshot, CacheJson);
            }

            File.Move(temporary, path, true);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or
                                              ArgumentException or UnauthorizedAccessException)
        {
            // Snapshot reuse is opportunistic and must not affect successful generation.
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private ActionGraph LoadActionGraph(string fingerprint, Func<ActionGraph> factory)
    {
        if (TryRead("actions", fingerprint,
                PipelineCacheBinarySerializer.ReadActionGraph, out var cached))
            return cached;

        var graph = factory();
        TryWrite("actions", fingerprint, graph, PipelineCacheBinarySerializer.WriteActionGraph);
        return graph;
    }

    private bool TryRead<T>(string kind, string fingerprint, Func<Stream, T> deserialize, out T value)
        where T : class
    {
        var path = CachePath(kind, fingerprint);
        if (!File.Exists(path))
        {
            value = null!;
            return false;
        }

        try
        {
            using var file = File.OpenRead(path);
            value = deserialize(file);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                              InvalidOperationException or ArgumentException)
        {
            TryDelete(path);
            value = null!;
            return false;
        }
    }

    private void TryWrite<T>(
        string kind,
        string fingerprint,
        T value,
        Action<Stream, T> serialize)
    {
        var path = CachePath(kind, fingerprint);
        if (File.Exists(path)) return;

        var directory = Path.GetDirectoryName(path)!;
        var temporary = Path.Combine(directory, $".{fingerprint}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                serialize(file, value);
            File.Move(temporary, path, overwrite: false);
        }
        catch (IOException)
        {
            // Caching is opportunistic. Another process may have published the same content-addressed entry.
        }
        catch (UnauthorizedAccessException)
        {
            // A read-only workspace must still be able to generate in-memory models.
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // An unsupported model shape must not make the build depend on the optional cache.
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private string CachePath(string kind, string fingerprint) =>
        Path.Combine(_root, kind, $"{fingerprint}.bin");

    private string GenerationSnapshotPath(string fingerprint)
    {
        return Path.Combine(_root, "generation", $"{fingerprint}.json");
    }

    private string SafeWorkspacePath(string relative)
    {
        var absolute = Path.GetFullPath(relative, _workspaceRoot);
        var root = Path.TrimEndingDirectorySeparator(_workspaceRoot) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(root, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
            throw new ArgumentException("A generation snapshot path escaped the workspace.", nameof(relative));
        return absolute;
    }

    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Hash<T>(T value)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, CacheJson);
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private static JsonSerializerOptions CreateCacheJson()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new DefaultImmutableArrayJsonConverterFactory());
        options.Converters.Add(new ConfigurationKeyJsonConverter());
        options.Converters.Add(new LogicalPathJsonConverter());
        return options;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class ConfigurationKeyJsonConverter : JsonConverter<ConfigurationKey>
    {
        public override ConfigurationKey Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("A cached configuration key must be an object.");

            var values = ImmutableArray.CreateBuilder<FragmentValue>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new JsonException("A cached configuration fragment name was expected.");
                var fragment = reader.GetString()!;
                if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                    throw new JsonException("A cached configuration fragment value was expected.");
                values.Add(new FragmentValue(new FragmentId(fragment), reader.GetString()!));
            }

            return new ConfigurationKey(values);
        }

        public override void Write(
            Utf8JsonWriter writer,
            ConfigurationKey value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var fragment in value.Values)
                writer.WriteString(fragment.Fragment.Value, fragment.Value);
            writer.WriteEndObject();
        }
    }

    private sealed class DefaultImmutableArrayJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert)
        {
            return typeToConvert.IsGenericType &&
                   typeToConvert.GetGenericTypeDefinition() == typeof(ImmutableArray<>);
        }

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var itemType = typeToConvert.GetGenericArguments()[0];
            return (JsonConverter)Activator.CreateInstance(
                typeof(DefaultImmutableArrayJsonConverter<>).MakeGenericType(itemType))!;
        }

        private sealed class DefaultImmutableArrayJsonConverter<T> : JsonConverter<ImmutableArray<T>>
        {
            public override ImmutableArray<T> Read(
                ref Utf8JsonReader reader,
                Type typeToConvert,
                JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.Null) return [];
                if (reader.TokenType != JsonTokenType.StartArray)
                    throw new JsonException("A cached immutable array must be an array.");

                var values = ImmutableArray.CreateBuilder<T>();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray) return values.ToImmutable();
                    values.Add(JsonSerializer.Deserialize<T>(ref reader, options)!);
                }

                throw new JsonException("A cached immutable array was not terminated.");
            }

            public override void Write(
                Utf8JsonWriter writer,
                ImmutableArray<T> value,
                JsonSerializerOptions options)
            {
                writer.WriteStartArray();
                if (!value.IsDefault)
                    foreach (var item in value)
                        JsonSerializer.Serialize(writer, item, options);
                writer.WriteEndArray();
            }
        }
    }

    private sealed class LogicalPathJsonConverter : JsonConverter<LogicalPath>
    {
        public override LogicalPath Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            return reader.TokenType != JsonTokenType.String
                ? throw new JsonException("A cached logical path must be a string.")
                : new LogicalPath(reader.GetString()!);
        }

        public override void Write(
            Utf8JsonWriter writer,
            LogicalPath value,
            JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
    }
}

internal sealed record GenerationSnapshot(
    ImmutableArray<GenerationSnapshotFile> Files,
    ImmutableArray<string> ReportedOutputs,
    string ManifestPath);

internal sealed record GenerationSnapshotFile(string Path, string Hash);
