using RoxyBuildTool;
using RoxyBuildTool.Generators.CompilationDatabase;
using RoxyBuildTool.Generators.VisualStudio;
using RoxyBuildTool.Platforms.Windows;
using WindowsMvp.Build;

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
