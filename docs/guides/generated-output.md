---
title: Generated output
description: Layout, ownership, determinism, and backend-specific files.
---

# Generated output

Generated IDE and compilation-database files are projections of the rules model. Do not edit them; regenerate after changing rules.

## Layout

```text
.roxy/
  cache/v1/
    actions/<content-hash>.bin
    generation/<request-content-hash>.json
  generated/
    Vs2022/<workspace>/
    CompileDb/<workspace>/
  manifests/<request-hash>.json
Binaries/<platform>/<architecture>/<profile>/<configuration-hash>/<target>/
Intermediate/<configuration-hash>/<target>/
```

All paths in the model are workspace-relative and use `/` as the logical separator. Generators convert them to the syntax required by their output format.
Generated build-directory names use PascalCase. The `.roxy` internal layout and opaque hash segments retain their existing canonical casing.

## Visual Studio

The Visual Studio generator writes:

- A solution containing one project per module, with target/configuration variants represented as project configurations.
- `.vcxproj` and `.vcxproj.filters` files for C++ modules.

The generated solution never contains C# projects, including the project-local rules host.

Human-readable solution configuration names are derived from the profile and custom fragments without exposing the internal hash. If two configurations would otherwise have the same name, readable fragment qualifiers distinguish them. Internal configuration identity remains the full canonical key.

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

## Incremental graph cache

Action graphs are stored as compact, content-addressed binary entries. Configured graphs are cheap
to re-resolve and are only deduplicated in memory during an invocation. Rules assembly
identities, source sets, canonical configurations, resolver/lowerer versions, toolchain settings,
and workspace identity participate in their keys. Invalid or truncated entries are ignored and
recomputed. Call `WithIncrementalCache(false)` while composing `BuildToolApp` to disable persistent
graph reuse; generated-output compare-before-write behavior is unaffected.

After a successful generation, the cache also records hashes for every owned output, ownership
record, and manifest. An identical request can skip model and generator work when those files still
match. Editing or deleting any tracked file causes a normal regeneration, so the cache never treats
generated files as authoritative source input.

## Compare-before-write

RoxyBuildTool normalizes generated text to LF and compares it with the existing file. An unchanged file is not replaced, preserving timestamps and reducing unnecessary IDE reloads.

Each generator output directory contains `.roxy-outputs.json`. On the next successful generation, files that were previously tracked but are no longer emitted are removed. Files not listed in that ownership record are never removed.

Action and project ordering uses ordinal stable IDs. Configuration keys sort fragments and reject duplicate fragment assignments. These rules keep generated output stable across repeated runs with equivalent inputs.
