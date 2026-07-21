# RoxyBuildTool

RoxyBuildTool 是一个通用的、通过 NuGet 使用的强类型 C++/.NET 构建描述系统。当前 Phase 1 支持 Windows x64、MSVC、.NET 10、Visual Studio workspace 和 `compile_commands.json`。

## 规则入口

Build host 显式选择 rules assembly，Module、Target 和 Workspace 由反射自动发现，无需逐个注册：

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

每个 module 的 `*.Module.cs` 放在 module 自己的源码目录中。规则基类只是 marker，配置方法统一使用 `[Configure]`：

```csharp
public sealed class EngineCoreModule : CxxModule
{
    [Configure]
    private static void ConfigureAll(ModuleRules rules) { }

    [Configure<GameFlavor>(nameof(GameFlavor.DedicatedServer), Priority = 100)]
    private static void ConfigureServer(ModuleRules rules) { }
}
```

抽象 Target 基类上的 `[Configure]` 会被继承，适合复用公共平台矩阵。稳定内部 ID 使用 PascalCase，分段 ID 的每一段也使用 PascalCase（例如 `Game.Flavor`）；生成的工程名使用可读名称；solution configuration 例如 `Development Client | Win64`，内部 architecture ID 为 `X64`。

## 示例

```powershell
cd samples/WindowsMvp/Build
dotnet restore
dotnet run
dotnet run -- query matrix GameTarget
dotnet run -- build GameTarget --platform Windows --arch X64 --profile Development --fragment Game.Flavor=Client
```

完整 workspace 位于 `samples/WindowsMvp/.roxy/generated/Vs2022/Game/GameWorkspace.sln`。单 target build 使用独立的内部 solution，不会覆盖 Rider 正在打开的完整 workspace。

Roxy 不在生成工程中加入 MSBuild fallback。Rider 的 `Toolset and Build → MSBuild version` 是整个混合 solution 的全局选择；CLI 可通过 `WithMsBuild(path)` 或 `MSBUILD_EXE_PATH` 选择同一个 toolset。`.NET SDK 10.0.3xx` 与 `net10.0` 混合 `.vcxproj` workspace 需要同时带 .NET 10 和 VC targets 的 MSBuild 18.x（Visual Studio 2026），不能使用只含 Core MSBuild 的 SDK 目录替代 C++ targets。

更多设计细节见 [docs/architecture.md](docs/architecture.md)。
