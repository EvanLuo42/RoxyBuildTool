# Contributing to RoxyBuildTool

RoxyBuildTool is in early development. Changes should preserve deterministic output, typed configuration identity, immutable intermediate models, and explicit plugin boundaries.

## Prerequisites

- .NET SDK selected by `global.json`.
- Git.
- Windows, MSVC, and the Windows SDK for the Windows sample and native integration tests.

## Build and test

From the repository root:

```powershell
dotnet restore RoxyBuildTool.slnx
dotnet build RoxyBuildTool.slnx --configuration Release --no-restore
dotnet test RoxyBuildTool.slnx --configuration Release --no-build
```

To test the package-consumer boundary:

```powershell
dotnet pack RoxyBuildTool.slnx --configuration Release --no-build --output artifacts/packages
dotnet restore samples/WindowsMvp/Build/RoxyBuild.csproj --force-evaluate
dotnet run --project samples/WindowsMvp/Build/RoxyBuild.csproj --no-restore
```

## Documentation

```powershell
dotnet tool restore
dotnet docfx docs/docfx.json
```

The build must complete without broken links or unresolved API references.

## Change requirements

- Add focused tests for behavior changes and boundary cases.
- Update golden files only after reviewing the semantic change in generated output.
- Keep collections and generated files ordinally sorted where order is not semantically meaningful.
- Use stable diagnostic codes for user-correctable failures.
- Keep filesystem paths workspace-relative in the model.
- Do not make generators mutate definition, configured, or action graphs.
- Update user guides and XML documentation when changing public behavior.

## Pull requests

Keep each pull request scoped to one coherent change. Include the motivation, observable behavior, tests, and any compatibility impact. Breaking API or serialization changes must identify the affected stable IDs and migration path.
