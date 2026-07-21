using System.Collections.Immutable;
using RoxyBuildTool.Abstractions;
using RoxyBuildTool.CommandLine;
using Xunit;

namespace RoxyBuildTool.IntegrationTests;

public sealed class CommandLineParserTests
{
    private static readonly CommandRequest Default = new(
        CommandKind.Generate,
        "DefaultWorkspace",
        ["Vs2022"],
        ImmutableDictionary<FragmentId, string>.Empty.Add(new("Profile"), "Debug"),
        null,
        false,
        "dot");

    [Fact]
    public void EmptyArgumentsReturnTheConfiguredDefaultRequest()
    {
        Assert.Same(Default, CommandLineParser.Parse([], Default));
    }

    [Theory]
    [InlineData("generate", CommandKind.Generate)]
    [InlineData("BUILD", CommandKind.Build)]
    [InlineData("explain", CommandKind.Explain)]
    public void SimpleCommandsParseOptionalSubject(string command, CommandKind expected)
    {
        var withSubject = CommandLineParser.Parse([command, "Subject"], Default);
        var withoutSubject = CommandLineParser.Parse([command], Default);

        Assert.Equal(expected, withSubject.Kind);
        Assert.Equal("Subject", withSubject.Subject);
        Assert.Null(withoutSubject.Subject);
        Assert.Equal(Default.WorkspaceGenerators, withSubject.WorkspaceGenerators);
        Assert.Empty(withSubject.Selectors);
    }

    [Theory]
    [InlineData("matrix", CommandKind.QueryMatrix)]
    [InlineData("GRAPH", CommandKind.QueryGraph)]
    public void QuerySubcommandsAreCaseInsensitive(string query, CommandKind expected)
    {
        var request = CommandLineParser.Parse(["QUERY", query, "target"], Default);

        Assert.Equal(expected, request.Kind);
        Assert.Equal("target", request.Subject);
    }

    [Fact]
    public void EveryOptionIsParsedAndLaterSelectorsReplaceEarlierValues()
    {
        var request = CommandLineParser.Parse([
            "generate", "workspace",
            "--workspace", " vs2022, CompileDb ,,",
            "--platform", "windows",
            "--arch", "x64",
            "--profile", "debug",
            "--profile", "release",
            "--toolchain", "Msvc14.4",
            "--fragment", "Game.Flavor=editor",
            "--setting", "Compiler.Optimization",
            "--why-excluded",
            "--format", "json",
            "--executor", "ignored",
        ], Default);

        Assert.Equal(["Vs2022", "CompileDb"], request.WorkspaceGenerators);
        Assert.Equal("Windows", request.Selectors[new("Platform")]);
        Assert.Equal("X64", request.Selectors[new("Architecture")]);
        Assert.Equal("Release", request.Selectors[new("Profile")]);
        Assert.Equal("Msvc14.4", request.Selectors[new("Toolchain")]);
        Assert.Equal("Editor", request.Selectors[new("Game.Flavor")]);
        Assert.Equal("Compiler.Optimization", request.Setting);
        Assert.True(request.WhyExcluded);
        Assert.Equal("json", request.Format);
    }

    [Fact]
    public void EmptyWorkspaceListFallsBackToDefaultsAndOptionCanBeFirst()
    {
        var request = CommandLineParser.Parse(["generate", "--workspace", ",,,"], Default);

        Assert.Null(request.Subject);
        Assert.Equal(Default.WorkspaceGenerators, request.WorkspaceGenerators);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("query unknown")]
    [InlineData("unknown")]
    public void UnknownCommandsAreDiagnosed(string commandLine)
    {
        var args = commandLine.Split(' ');
        Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(args, Default));
    }

    [Theory]
    [InlineData("--workspace")]
    [InlineData("--platform")]
    [InlineData("--arch")]
    [InlineData("--profile")]
    [InlineData("--toolchain")]
    [InlineData("--fragment")]
    [InlineData("--setting")]
    [InlineData("--format")]
    [InlineData("--executor")]
    public void ValueOptionsDiagnoseMissingValues(string option)
    {
        var exception = Assert.Throws<CommandLineException>(() =>
            CommandLineParser.Parse(["generate", option], Default));

        Assert.Contains("expects a value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FragmentOptionRequiresAnAssignmentAndUnknownOptionsFail()
    {
        Assert.Throws<CommandLineException>(() =>
            CommandLineParser.Parse(["generate", "--fragment", "NotAnAssignment"], Default));
        Assert.Throws<CommandLineException>(() =>
            CommandLineParser.Parse(["generate", "--unknown"], Default));
    }
}
