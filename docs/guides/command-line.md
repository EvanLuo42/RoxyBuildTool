---
title: Command-line reference
description: Implemented commands, selectors, output formats, and exit codes.
---

# Command-line reference

The build host receives RoxyBuildTool arguments after the `dotnet run` separator:

```text
dotnet run -- <command> [subject] [options]
```

No arguments execute the request configured by `DefaultGenerate<TWorkspace>`.

## Commands

### `generate`

Generate one or more workspace representations.

```powershell
dotnet run -- generate GameWorkspace --workspace Vs2022,CompileDb
```

When `--workspace` is omitted, the default request's generator list is used.

### `build`

Generate a target-scoped Visual Studio solution and invoke MSBuild.

```powershell
dotnet run -- build GameTarget `
  --platform Windows `
  --arch X64 `
  --profile Development `
  --fragment Game.Flavor=Client
```

Selectors must resolve to exactly one configuration. The MSBuild process exit code is returned by the host.

### `query matrix`

List canonical configuration keys for a target:

```powershell
dotnet run -- query matrix EditorTarget --why-excluded
```

`--why-excluded` also prints rejected partial candidates and their constraint reasons.

### `query graph`

Resolve one configuration and write its module dependency graph:

```powershell
dotnet run -- query graph GameTarget --profile Development --fragment Game.Flavor=Client --format dot
dotnet run -- query graph GameTarget --profile Development --fragment Game.Flavor=Client --format json
```

The default format is DOT.
Both graph formats require selectors that resolve to exactly one configuration. `json` writes a
single JSON document to stdout; diagnostics are written to stderr. Formats other than `dot` and
`json` are rejected.

### `explain`

Show resolved usage requirements and their origins:

```powershell
dotnet run -- explain GameTarget `
  --profile Development `
  --fragment Game.Flavor=Client `
  --setting usage
```

`--setting Compiler.Optimization` reports the profile-derived optimization policy. Other values currently produce the usage-requirement view.
Like `query graph`, `explain` requires selectors that resolve to exactly one configuration.

### `--help` and `--version`

`--help`, `-h`, and `help` print command help. `--version` and `version` print the host version.
These commands do not require rule or plugin registration.

## Options

| Option | Value | Effect |
|---|---|---|
| `--workspace` | Comma-separated IDs | Select workspace generators for `generate`. |
| `--platform` | ID | Select the `Platform` fragment. |
| `--arch` | ID | Select the `Architecture` fragment. |
| `--profile` | ID | Select the `Profile` fragment. |
| `--toolchain` | ID | Select the `Toolchain` fragment. |
| `--fragment` | `Id=Value` | Select a custom fragment. Repeat for multiple fragments. |
| `--why-excluded` | None | Include matrix exclusion reasons. |
| `--format` | `dot` or `json` | Select graph output format. |
| `--setting` | Setting ID | Select the setting explained by `explain`. |
| `--executor` | ID | Reserved. Parsed but does not change Phase 1 output or execution. |

IDs and values are normalized to PascalCase. Prefer canonical spelling in scripts because it is the serialized form used in manifests and configuration keys.

## Exit codes

| Code | Meaning |
|---:|---|
| `0` | Command completed successfully. |
| `1` | Unexpected host or infrastructure failure. |
| `2` | Invalid command, invalid configuration, or graph diagnostic error. |

The `build` command returns the underlying MSBuild exit code after successful generation.
