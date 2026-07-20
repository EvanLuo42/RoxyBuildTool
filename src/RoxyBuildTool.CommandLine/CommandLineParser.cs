using System.Collections.Immutable;
using RoxyBuildTool.Abstractions;

namespace RoxyBuildTool.CommandLine;

public enum CommandKind
{
    Generate,
    Build,
    QueryMatrix,
    QueryGraph,
    Explain,
}

public sealed record CommandRequest(
    CommandKind Kind,
    string? Subject,
    ImmutableArray<string> WorkspaceGenerators,
    ImmutableDictionary<FragmentId, string> Selectors,
    string? Setting,
    bool WhyExcluded,
    string Format);

public static class CommandLineParser
{
    public static CommandRequest Parse(string[] args, CommandRequest defaultRequest)
    {
        if (args.Length == 0)
        {
            return defaultRequest;
        }

        var index = 0;
        var command = args[index++].ToLowerInvariant();
        var kind = command switch
        {
            "generate" => CommandKind.Generate,
            "build" => CommandKind.Build,
            "explain" => CommandKind.Explain,
            "query" when index < args.Length && args[index].Equals("matrix", StringComparison.OrdinalIgnoreCase) =>
                ConsumeQueryKind(CommandKind.QueryMatrix, ref index),
            "query" when index < args.Length && args[index].Equals("graph", StringComparison.OrdinalIgnoreCase) =>
                ConsumeQueryKind(CommandKind.QueryGraph, ref index),
            _ => throw new CommandLineException($"Unknown command '{command}'."),
        };

        string? subject = null;
        if (index < args.Length && !args[index].StartsWith("--", StringComparison.Ordinal))
        {
            subject = args[index++];
        }

        var generators = ImmutableArray.CreateBuilder<string>();
        var selectors = ImmutableDictionary.CreateBuilder<FragmentId, string>();
        string? setting = null;
        var whyExcluded = false;
        var format = "dot";

        while (index < args.Length)
        {
            var option = args[index++];
            switch (option)
            {
                case "--workspace":
                    foreach (var generator in NextValue(option).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        generators.Add(generator);
                    }
                    break;
                case "--platform":
                    selectors[new("platform")] = NextValue(option);
                    break;
                case "--arch":
                    selectors[new("architecture")] = NextValue(option);
                    break;
                case "--profile":
                    selectors[new("profile")] = NextValue(option);
                    break;
                case "--toolchain":
                    selectors[new("toolchain")] = NextValue(option);
                    break;
                case "--fragment":
                    var assignment = NextValue(option).Split('=', 2);
                    if (assignment.Length != 2)
                    {
                        throw new CommandLineException("--fragment expects <id>=<value>.");
                    }
                    selectors[new(assignment[0])] = assignment[1];
                    break;
                case "--setting":
                    setting = NextValue(option);
                    break;
                case "--why-excluded":
                    whyExcluded = true;
                    break;
                case "--format":
                    format = NextValue(option);
                    break;
                case "--executor":
                    _ = NextValue(option); // executor is orthogonal to Phase 1 binary identity.
                    break;
                default:
                    throw new CommandLineException($"Unknown option '{option}'.");
            }
        }

        return new(kind, subject,
            generators.Count == 0 ? defaultRequest.WorkspaceGenerators : generators.ToImmutable(),
            selectors.ToImmutable(), setting, whyExcluded, format);

        string NextValue(string option)
        {
            if (index >= args.Length)
            {
                throw new CommandLineException($"{option} expects a value.");
            }

            return args[index++];
        }
    }

    private static CommandKind ConsumeQueryKind(CommandKind kind, ref int index)
    {
        index++;
        return kind;
    }
}

public sealed class CommandLineException(string message) : Exception(message);
