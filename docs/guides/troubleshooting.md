---
title: Troubleshooting
description: Diagnose restore, generation, configuration, and MSBuild failures.
---

# Troubleshooting

## `dotnet` is not found

Use the full path to the SDK host or add the installation directory to `PATH` for the current shell:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" --info
```

The SDK version must satisfy `global.json`.

## Local RoxyBuildTool packages cannot be restored

The sample uses `artifacts/packages` through the repository `NuGet.config`. Build the local feed before restoring the sample:

```powershell
dotnet restore RoxyBuildTool.slnx
dotnet build RoxyBuildTool.slnx --configuration Release --no-restore
dotnet pack RoxyBuildTool.slnx --configuration Release --no-build --output artifacts/packages
dotnet restore samples/WindowsMvp/Build/RoxyBuild.csproj --force-evaluate
```

During local development, repacking the same `0.1.0` version does not replace an already expanded package in `artifacts/sample-packages`. Use a fresh `RestorePackagesPath` or remove only that generated package cache before restoring again.

## Generation reports an unknown workspace generator

Register the matching plugin in the host. `Vs2022` requires `UseVisualStudio()` and `CompileDb` requires `UseCompilationDatabase()`.

## Build reports that multiple configurations matched

`build` accepts exactly one configuration. Add selectors for every axis with more than one value, commonly `--profile` and project-specific fragments:

```powershell
dotnet run -- build GameTarget --profile Development --fragment Game.Flavor=Client
```

Use `query matrix` first to inspect the available canonical keys.

## No global MSBuild was found

The build command needs an MSBuild installation with both .NET and Visual C++ targets. Select it with one of these mechanisms:

- Call `WithMsBuild(path)` in the build host.
- Set `MSBUILD_EXE_PATH`.
- Install a supported Visual Studio or Build Tools edition in a standard location.

Do not point `WithMsBuild` at the SDK-local `MSBuild.dll` for C++ builds. It does not contain the Visual C++ targets.

## A source root does not exist

`Sources.From(root, pattern)` resolves `root` against `WithWorkspaceRoot`. Verify both values and run the host from the expected directory. Prefer setting the workspace root explicitly rather than relying on the process working directory.

## A rules type is not discovered

Check that the type:

- Is in an assembly passed to `DiscoverRulesFromAssemblyContaining<T>()`.
- Derives from `CxxModule`, `CSharpModule`, `BuildTarget`, or `BuildWorkspace`.
- Is concrete, non-generic, and has a public parameterless constructor.

## A `[Configure]` method fails validation

The method must return `void` and accept exactly one rules parameter: `ModuleRules`, `CSharpModuleRules`, `TargetRules`, or `WorkspaceRules` as appropriate. Filtered configuration methods are valid only on modules.

## Diagnostic codes

| Range | Area |
|---|---|
| `RBT0000-RBT0999` | Host and command-line failures. |
| `RBT1000-RBT1999` | Fragment and matrix validation. |
| `RBT2000-RBT2999` | Definition and dependency resolution. |
| `RBT3000-RBT3999` | Action graph validation. |

Run `query matrix --why-excluded`, `query graph`, and `explain` before inspecting generated project files. They operate on the source model and usually identify the failing layer directly.
