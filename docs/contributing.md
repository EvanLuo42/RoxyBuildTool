---
title: Contributing
description: Build, test, and documentation requirements for RoxyBuildTool changes.
---

# Contributing

RoxyBuildTool is in early development. Changes should preserve deterministic output, typed configuration identity, immutable intermediate models, and explicit plugin boundaries.

## Build and test

```powershell
dotnet restore RoxyBuildTool.slnx
dotnet build RoxyBuildTool.slnx --configuration Release --no-restore
dotnet test RoxyBuildTool.slnx --configuration Release --no-build
```

To verify the package-consumer boundary:

```powershell
dotnet pack RoxyBuildTool.slnx --configuration Release --no-build --output artifacts/packages
dotnet restore samples/WindowsMvp/Build/RoxyBuild.csproj --force-evaluate
dotnet run --project samples/WindowsMvp/Build/RoxyBuild.csproj --no-restore
```

## Change requirements

- Add focused tests for behavior changes and boundary cases.
- Review semantic changes before updating golden files.
- Keep non-semantic collections and generated files ordinally sorted.
- Use stable diagnostic codes for user-correctable failures.
- Keep model paths workspace-relative.
- Do not make generators mutate definition, configured, or action graphs.
- Update guides and XML documentation when public behavior changes.

See the repository `CONTRIBUTING.md` for the complete contributor workflow.
