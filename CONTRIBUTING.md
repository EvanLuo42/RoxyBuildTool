# Contributing to RoxyBuildTool

RoxyBuildTool is in early development. Changes should preserve deterministic output, typed configuration identity, immutable intermediate models, and explicit plugin boundaries.

## Prerequisites

- .NET SDK selected by `global.json`.
- Git.
- Windows, MSVC, and the Windows SDK for native integration tests.

## Build and test

From the repository root:

```powershell
dotnet restore RoxyBuildTool.slnx
dotnet build RoxyBuildTool.slnx --configuration Release --no-restore
dotnet test RoxyBuildTool.slnx --configuration Release --no-build
```

The integration suite covers the complete Windows game workspace, configuration matrix,
module graph, generators, and build invocation. Running a sample is not part of the test workflow.

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

## Releases

`Directory.Build.props` is the canonical product-version file. Releases use Semantic Versioning and
tags have the matching `v` prefix. Before 1.0, use a minor bump for a breaking public-API change;
after 1.0, use a major bump. Features use a minor bump and compatible fixes use a patch bump.

Prepare a release by updating `Directory.Build.props` and moving the handwritten notes from
`[Unreleased]` into a dated section for exactly the same version:

```markdown
## [0.2.0] - 2026-07-21

### Added

- Describe the user-visible change.
```

Keep releases in reverse chronological order, use ISO `YYYY-MM-DD` dates, and include only the
non-empty Keep a Changelog categories that apply: `Added`, `Changed`, `Deprecated`, `Removed`,
`Fixed`, and `Security`. Changelog entries are curated for users; do not paste a raw commit log.
Version headings and `[Unreleased]` should use Markdown reference links to the corresponding GitHub
tag comparison when the adjacent tags are known.

Commit the version bump and changelog together, create an annotated tag on that commit, and push it:

```powershell
git add Directory.Build.props CHANGELOG.md
git commit -m "chore(release): v0.2.0"
git tag -a v0.2.0 -m "RoxyBuildTool 0.2.0"
git push --atomic origin HEAD refs/tags/v0.2.0
```

The tag triggers `.github/workflows/release.yml`. The workflow requires the tag, version file, and
changelog section to agree, then builds, tests, packs NuGet and symbol packages, writes SHA-256 checksums,
and creates the GitHub Release. The handwritten matching changelog section becomes the release description,
and the full `CHANGELOG.md` is attached as an asset.

A production hotfix is the next patch version, such as `0.2.1`. Prerelease tags such as
`0.2.1-hotfix.1`, `0.3.0-beta.1`, and `1.0.0-rc.1` are also supported and create GitHub prereleases.
