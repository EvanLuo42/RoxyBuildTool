---
title: Getting started
description: Build RoxyBuildTool and generate the included Windows workspace.
---

# Getting started

The repository contains a complete mixed C++/.NET example in `samples/WindowsMvp`. The example consumes locally packed RoxyBuildTool packages, matching how a downstream repository uses the tool.

## Prerequisites

- Windows x64.
- The .NET SDK version selected by `global.json`.
- MSVC, the Windows SDK, and a full Visual Studio or Build Tools MSBuild installation.

The SDK-local MSBuild does not contain the Visual C++ targets. Generation works without a full MSBuild installation, but the `build` command requires one.

## Build the packages

From the repository root:

```powershell
dotnet restore RoxyBuildTool.slnx
dotnet build RoxyBuildTool.slnx --configuration Release --no-restore
dotnet pack RoxyBuildTool.slnx --configuration Release --no-build --output artifacts/packages
```

`NuGet.config` maps packages named `RoxyBuildTool*` to `artifacts/packages` and all other packages to NuGet.org.

## Run the sample

```powershell
cd samples/WindowsMvp/Build
dotnet restore
dotnet run --no-restore
```

The default request generates both backends:

```text
samples/WindowsMvp/.roxy/generated/Vs2022/Game/GameWorkspace.sln
samples/WindowsMvp/.roxy/generated/CompileDb/Game/compile_commands.json
```

Run the same command again to verify compare-before-write behavior. Existing files with identical content are reported as `unchanged`.

## Inspect the configuration model

```powershell
dotnet run --no-restore -- query matrix GameTarget
dotnet run --no-restore -- query matrix EditorTarget --why-excluded
dotnet run --no-restore -- query graph GameTarget --format dot
dotnet run --no-restore -- explain GameTarget --profile Development --fragment Game.Flavor=Client
```

## Build one target

The build command requires selectors that resolve to exactly one configuration:

```powershell
dotnet run --no-restore -- build GameTarget `
  --platform Windows `
  --arch X64 `
  --profile Development `
  --fragment Game.Flavor=Client
```

RoxyBuildTool generates a target-scoped solution before invoking global MSBuild. Configure a non-default installation in the build host:

```csharp
.WithMsBuild(@"C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe")
```

You can also set `MSBUILD_EXE_PATH` for command-line builds.

## Create a build host

A consuming repository uses a small SDK-style console project. Reference the facade plus the platform and generators required by that repository:

```xml
<ItemGroup>
  <PackageReference Include="RoxyBuildTool" Version="0.1.0" />
  <PackageReference Include="RoxyBuildTool.Platforms.Windows" Version="0.1.0" />
  <PackageReference Include="RoxyBuildTool.Generators.VisualStudio" Version="0.1.0" />
  <PackageReference Include="RoxyBuildTool.Generators.CompilationDatabase" Version="0.1.0" />
</ItemGroup>
```

The entry point selects the rules assembly and enabled plugins explicitly:

```csharp
using RoxyBuildTool;
using RoxyBuildTool.Generators.CompilationDatabase;
using RoxyBuildTool.Generators.VisualStudio;
using RoxyBuildTool.Platforms.Windows;

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

Continue with [Authoring build rules](authoring-rules.md).
