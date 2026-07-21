# RoxyBuildTool

RoxyBuildTool is a strongly typed, in-process build description system for C++ and .NET. Build rules are compiled C# code, and workspace generators consume the same immutable configuration and action models.

Version 0.1 is under active development. The implemented scope is Windows x64, MSVC, .NET 10, mixed Visual Studio workspace generation, and `compile_commands.json` generation.

## Package roles

- `RoxyBuildTool` provides the authoring facade and command host.
- `RoxyBuildTool.Platforms.Windows` registers Windows and MSVC support.
- `RoxyBuildTool.Generators.VisualStudio` generates mixed C++/.NET Visual Studio workspaces.
- `RoxyBuildTool.Generators.CompilationDatabase` generates compilation databases.

## Host example

```csharp
return await BuildToolApp.Create(args)
    .WithWorkspaceRoot("..")
    .DiscoverRulesFromAssemblyContaining<MyBuildRules>()
    .UseWindowsPlatform()
    .UseVisualStudio()
    .UseCompilationDatabase()
    .DefaultGenerate<MyWorkspace>(request => request.Workspace(
        WorkspaceGenerators.VisualStudio2022,
        WorkspaceGenerators.CompilationDatabase))
    .RunAsync();
```

RoxyBuildTool packages are not yet published to a public feed.

Source: <https://github.com/EvanLuo42/RoxyBuildTool>

License: MIT
