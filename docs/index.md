---
title: RoxyBuildTool documentation
description: Documentation for the RoxyBuildTool typed C++ build description system.
---

# RoxyBuildTool documentation

RoxyBuildTool is a typed, in-process build description system for C++ projects. Build definitions are ordinary C# code compiled by a small project-local host. The same resolved native model can generate a C++-only Visual Studio workspace and `compile_commands.json`.

> [!IMPORTANT]
> RoxyBuildTool is under active development. The implemented scope is Windows x64, MSVC, .NET 10, Visual Studio workspace generation, and compilation database generation. Public APIs may change before 1.0.

## Start here

- [Getting started](guides/getting-started.md) sets up and runs the included Windows sample.
- [Authoring build rules](guides/authoring-rules.md) defines modules, targets, workspaces, fragments, and dependencies.
- [Command-line reference](guides/command-line.md) documents the implemented commands and selectors.
- [Architecture](architecture.md) describes the current pipeline, invariants, and extension boundaries.
- [API reference](xref:RoxyBuildTool) is generated from the source and XML documentation comments.

## Current capabilities

- Typed C# rules with assembly discovery and inherited configuration methods.
- C++ header-only, object, static library, shared library, and executable modules.
- Typed configuration matrices with selectors, exclusions, requirements, and stable keys.
- Public, private, interface, build-order-only, and runtime dependency semantics.
- Immutable configured, action, and workspace models.
- C++-only Visual Studio solutions and `compile_commands.json`.
- Deterministic ordering, semantic action hashes, manifests, and compare-before-write output.
