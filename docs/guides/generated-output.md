---
title: Generated output
description: Layout, ownership, determinism, and backend-specific files.
---

# Generated output

Generated IDE and compilation-database files are projections of the rules model. Do not edit them; regenerate after changing rules.

## Layout

```text
.roxy/
  generated/
    Vs2022/<workspace>/
    CompileDb/<workspace>/
  manifests/<request-hash>.json
out/<platform>/<architecture>/<profile>/<target>/
intermediate/<configuration-hash>/<target>/
```

All paths in the model are workspace-relative and use `/` as the logical separator. Generators convert them to the syntax required by their output format.

## Visual Studio

The Visual Studio generator writes:

- A solution containing one project per target/module pair.
- `.vcxproj` and `.vcxproj.filters` files for C++ modules.
- SDK-style `.csproj` files for C# modules.
- A generated `Directory.Build.props` that isolates intermediate output.
- The build-host project when `WorkspaceRules.IncludeBuildHost` is enabled.

Human-readable solution configuration names are derived from the profile and custom fragments. Internal configuration identity remains the full canonical key.

The `build` command uses a separate target-scoped solution under `.roxy/generated/Vs2022`. It does not replace the complete workspace solution an IDE may already have open.

## Compilation database

The compilation database generator writes `compile_commands.json` with an `arguments` array for every C++ compile action. Entries are sorted by action ID for deterministic output.

## Manifests

Every generation request writes a JSON manifest containing:

- Schema version and request hash.
- Workspace and generator IDs.
- Canonical configuration keys.
- Action IDs and semantic hashes.
- Plugin IDs and versions.

The request hash includes the workspace, selected generators, configurations, and plugin versions. It is an identity for the generation request, not a content-addressed cache of every source file.

## Compare-before-write

RoxyBuildTool normalizes generated text to LF and compares it with the existing file. An unchanged file is not replaced, preserving timestamps and reducing unnecessary IDE reloads.

Action and project ordering uses ordinal stable IDs. Configuration keys sort fragments and reject duplicate fragment assignments. These rules keep generated output stable across repeated runs with equivalent inputs.
