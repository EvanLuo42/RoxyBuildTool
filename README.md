# RoxyBuildTool

RoxyBuildTool is a strongly typed, in-process build description system for C++. A project-local C# host compiles build rules, resolves an immutable native build model, and generates a C++-only Visual Studio workspace and `compile_commands.json` from the same configuration graph.

> RoxyBuildTool is under active development. Version 0.1 implements a Windows MVP; public APIs may change before 1.0.

## Features

- Ordinary C# build rules with IntelliSense, compile-time checking, and debugging.
- C++ header-only, object, static library, shared library, and executable modules.
- Native PCH, forced/system includes, output naming, and structured compiler/linker/librarian options.
- Typed configuration fragments, matrices, constraints, and canonical keys.
- Explicit public, private, interface, build-order, and runtime dependency semantics.
- C++-only Visual Studio solutions and compilation databases.
- Deterministic output, compare-before-write generation, manifests, and semantic action hashes.
- Bounded parallel configuration resolution and incremental generation/action graph caches.
- Versioned plugins with declared capabilities and compatibility validation before registration.

## Supported environment

The current implementation targets Windows x64, MSVC C/C++, .NET 10, and Visual Studio-compatible MSBuild. C++/CLI and assembly-language inputs (ASM/MASM/NASM/GAS) are intentionally unsupported. Linux, macOS, FASTBuild, packaging, deployment, and remote execution are design directions, not implemented features.

## Quick start

The packages are not yet published to a public feed. Build the repository-local packages and run the included consumer sample:

```powershell
dotnet restore RoxyBuildTool.slnx
dotnet build RoxyBuildTool.slnx --configuration Release --no-restore
dotnet pack RoxyBuildTool.slnx --configuration Release --no-build --output artifacts/packages

cd samples/WindowsMvp/Build
dotnet restore
dotnet run --no-restore
```

Generated files are written to:

```text
samples/WindowsMvp/.roxy/generated/Vs2022/Game/GameWorkspace.sln
samples/WindowsMvp/.roxy/generated/CompileDb/Game/compile_commands.json
```

## Build host

The host selects its rules assembly and plugins explicitly:

```csharp
return await BuildToolApp.Create(args)
    .WithWorkspaceRoot("..")
    .DiscoverRulesFromAssemblyContaining<WindowsMvpRules>()
    .UseWindowsPlatform()
    .UseVisualStudio()
    .UseCompilationDatabase()
    .DefaultGenerate<GameWorkspace>(request => request.Workspace(
        WorkspaceGenerators.VisualStudio2022,
        WorkspaceGenerators.CompilationDatabase))
    .RunAsync();
```

Rules are attached to modules, targets, and workspaces with `[Configure]`:

```csharp
public sealed class EngineCoreModule : CxxModule
{
    [Configure]
    private static void ConfigureAll(ModuleRules rules)
    {
        rules.Output = CxxOutput.StaticLibrary;
        rules.Sources.From("Engine/Core", "**/*.cpp");
        rules.Public.IncludeDirectories.Add("Engine/Core/Public");
    }
}
```

## Commands

```powershell
dotnet run -- query matrix GameTarget
dotnet run -- query graph GameTarget --profile Development --fragment Game.Flavor=Client --format dot
dotnet run -- explain GameTarget --profile Development --fragment Game.Flavor=Client
dotnet run -- build GameTarget --platform Windows --arch X64 --profile Development --fragment Game.Flavor=Client
```

`query graph`, `explain`, and `build` require selectors that resolve to exactly one
configuration. JSON graph output is machine-readable stdout without registration banners.

The `build` command requires a full MSBuild installation containing both .NET and Visual C++ targets. Use `WithMsBuild(path)` or `MSBUILD_EXE_PATH` to select it.

## Documentation

- [Getting started](docs/guides/getting-started.md)
- [Authoring build rules](docs/guides/authoring-rules.md)
- [Command-line reference](docs/guides/command-line.md)
- [Architecture](docs/architecture.md)
- [Contributing](CONTRIBUTING.md)
- [Security policy](SECURITY.md)

Build the Docfx site with:

```powershell
dotnet tool restore
dotnet docfx docs/docfx.json
```

## Performance benchmarks

The BenchmarkDotNet suite exercises configuration expansion, dependency and usage
propagation, action lowering, workspace assembly, and Visual Studio/compilation database
generation with production-shaped workloads, including 1000-module and 16-variant
game-engine-scale scenarios.

```powershell
dotnet run --project benchmarks/RoxyBuildTool.Benchmarks -c Release -- --filter "*"
```

See [the benchmark guide](benchmarks/RoxyBuildTool.Benchmarks/README.md) for focused and
short-run commands and a description of each workload.

Use `--filter "*IncrementalPipelineBenchmarks*"` to compare a full 16-variant graph rebuild with
cross-invocation action graph cache loading.
Use `--filter "*GenerationSnapshotBenchmarks*"` to compare a complete generation rebuild with a
validated output-snapshot hit.

## License

RoxyBuildTool is licensed under the [MIT License](LICENSE).
