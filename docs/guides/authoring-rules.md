---
title: Authoring build rules
description: Define modules, targets, workspaces, fragments, matrices, and dependencies.
---

# Authoring build rules

Rules are compiled C# types. RoxyBuildTool discovers concrete modules, targets, and workspaces from the assemblies selected by the build host.

## Naming and discovery

A rules type must be non-abstract, non-generic, and have a public parameterless constructor. Stable IDs are derived from type names by removing `Module`, `Target`, or `Workspace` and normalizing the remainder to PascalCase.

| Type | Stable ID |
|---|---|
| `EngineCoreModule` | `EngineCore` |
| `GameTarget` | `Game` |
| `GameWorkspace` | `Game` |

Stable fragment and plugin IDs use dot-separated PascalCase ASCII segments, for example `Game.Flavor` and `Roxy.Generator.Vs2022`.

## Configuration methods

Mark rule methods with `[Configure]`. A configuration method returns `void` and accepts exactly one rules object matching its owner:

```csharp
public sealed class EngineCoreModule : CxxModule
{
    [Configure]
    private static void ConfigureAll(ModuleRules rules)
    {
        rules.Output = CxxOutput.StaticLibrary;
        rules.Sources.From("Engine/Core", "**/*.cpp");
        rules.Public.IncludeDirectories.Add("Engine/Core/Public");
        rules.Private.IncludeDirectories.Add("Engine/Core/Private");
    }
}
```

Methods may be static or instance methods and may be non-public. Configuration methods on an abstract base target are inherited. `Priority` controls ordering; lower values run first. Unconditional methods run before filtered methods at the same priority.

Filtered `[Configure]` methods are supported on modules:

```csharp
[Configure<GameFlavor>(nameof(GameFlavor.DedicatedServer), Priority = 100)]
private static void ConfigureServer(ModuleRules rules) =>
    rules.Private.Defines.Add("ROXY_DEDICATED_SERVER=1");
```

Targets and workspaces must use unfiltered methods. Configuration axes belong to the target matrix.

## C++ modules

Derive from `CxxModule` and select a `CxxOutput`:

| Output | Purpose |
|---|---|
| `HeaderOnly` | Usage requirements without a link artifact. |
| `ObjectLibrary` | Compiled objects without archive or link output. |
| `StaticLibrary` | Static archive. This is the default. |
| `SharedLibrary` | DLL and import library on Windows. |
| `Executable` | Native executable. |

Source roots are relative to the workspace root. `Sources.From` enumerates matching files recursively; `Sources.Exclude` removes matching paths.

```csharp
rules.Sources.From("Engine/Runtime", "**/*.cpp");
rules.Sources.Exclude("**/*Tests.cpp");
```

Usage requirements distinguish settings needed by the module from settings exported to consumers:

```csharp
rules.Public.IncludeDirectories.Add("Engine/Runtime/Public");
rules.Public.Defines.Add("ROXY_RUNTIME_API=1");
rules.Private.IncludeDirectories.Add("Engine/Runtime/Private");
rules.Private.LinkInputs.Add("user32.lib");
rules.Public.RuntimeFiles.Add("ThirdParty/bin/runtime.dll");
```

## C# modules

Derive from `CSharpModule` and configure `CSharpModuleRules`:

```csharp
public sealed class ManagedToolModule : CSharpModule
{
    [Configure]
    private static void ConfigureAll(CSharpModuleRules rules)
    {
        rules.ManagedOutput = CSharpOutput.ConsoleApplication;
        rules.Sources.From("Managed/Tool", "**/*.cs");
        rules.Sources.Exclude("**/*.Module.cs");
        rules.TargetFrameworks.Add("net10.0");
        rules.Packages.Add("System.CommandLine", "2.0.0");
        rules.RootNamespace = "Example.Managed.Tool";
    }
}
```

The default target framework is `net10.0`. Package references are emitted into generated SDK-style projects.

## Dependencies

Dependencies are strongly typed:

```csharp
rules.Dependencies.Public<EngineCoreModule>();
rules.Dependencies.Private<SerializationModule>();
rules.Dependencies.Runtime<EngineRuntimeModule>();
```

| Visibility | Used to compile the current module | Exported to consumers | Orders build actions | Runtime files propagated |
|---|:---:|:---:|:---:|:---:|
| `Private` | Yes | No | Yes | Through consumed usage |
| `Public` | Yes | Yes | Yes | Through consumed usage |
| `Interface` | No | Yes | Yes | Through exported usage |
| `BuildOrderOnly` | No | No | Yes | No |
| `Runtime` | Runtime files only | No | Yes | Yes |

Dependency cycles and references to missing or disabled modules produce diagnostics during graph resolution.

## Custom fragments

Declare a single-choice configuration dimension with an enum:

```csharp
[BuildFragment("Game.Flavor")]
public enum GameFlavor
{
    Client,
    DedicatedServer,
    Editor,
}
```

Enum field names become stable values by default. Use `[FragmentValue("StableName")]` when the serialized value must not follow the C# field name.

## Target matrices

A target selects root modules and defines the configuration matrix:

```csharp
public sealed class GameTarget : WindowsTarget
{
    [Configure]
    private static void ConfigureTarget(TargetRules rules)
    {
        rules.RootModules.Add<GameExecutableModule>();
        rules.Matrix
            .Axis(GameFlavor.Client, GameFlavor.DedicatedServer)
            .Exclude(
                configuration =>
                    configuration.Is(GameFlavor.DedicatedServer) &&
                    configuration.Is(BuildProfiles.Debug),
                "Dedicated servers do not use the Debug profile.");
    }
}
```

Common axes can be inherited from an abstract target:

```csharp
public abstract class WindowsTarget : BuildTarget
{
    [Configure(Priority = -100)]
    protected static void ConfigureWindows(TargetRules rules)
    {
        rules.Matrix
            .Axis(Platforms.Windows)
            .Axis(Architectures.X64)
            .Axis(BuildProfiles.All.ToArray())
            .Axis(Toolchains.Msvc)
            .Axis(LinkModels.Modular);
    }
}
```

Each fragment may appear in only one axis. `Exclude` removes matching candidates. `Require` rejects a candidate when its condition is true and its requirement is false. Both forms require a diagnostic reason.

## Conditional module rules

For simple fragment-dependent changes, use `When`:

```csharp
rules.When(GameFlavor.DedicatedServer)
    .AddDefine("ROXY_SERVER=1")
    .RemoveDependency<RendererModule>();

rules.When(GameFlavor.Editor).Disable();
```

The supported operations are `Disable`, `AddDefine`, and `RemoveDependency<T>`.

## Workspaces

A workspace groups targets and selects the startup target:

```csharp
public sealed class GameWorkspace : BuildWorkspace
{
    [Configure]
    private static void ConfigureWorkspace(WorkspaceRules rules)
    {
        rules.Targets.Add<GameTarget>();
        rules.Targets.Add<EditorTarget>();
        rules.StartupTarget<EditorTarget>();
        rules.IncludeBuildHost = true;
        rules.BuildHostProject = "Build/RoxyBuild.csproj";
    }
}
```

Including the build host adds it to the generated mixed solution as an imported C# project. Disable `IncludeBuildHost` when the host must remain outside the workspace.
