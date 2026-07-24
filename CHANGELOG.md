# Changelog

All notable changes to RoxyBuildTool are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.3] - 2026-07-23

### Changed

- Added module-relative Visual Studio filters and automatic inclusion of convention-named `*.Module.cs` rule files at each project root.

## [0.1.2] - 2026-07-23

### Changed

- Changed generated Visual Studio workspaces to emit one project per module, with target/configuration variants represented inside that project instead of duplicating shared modules per target.
- Removed internal short hashes from user-facing configuration names and added readable fragment qualifiers when names would otherwise collide.

## [0.1.1] - 2026-07-23

### Changed

- Changed generated build directories to PascalCase names, including the `Binaries` and `Intermediate` roots.
- Changed release publishing from GitHub Packages to NuGet.org using Trusted Publishing.

## [0.1.0] - 2026-07-21

### Added

- Initial Windows MVP for describing C++ builds with typed C# rules and generating Visual Studio 2022 and `compile_commands.json` workspaces.

[Unreleased]: https://github.com/EvanLuo42/RoxyBuildTool/compare/v0.1.3...HEAD
[0.1.3]: https://github.com/EvanLuo42/RoxyBuildTool/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/EvanLuo42/RoxyBuildTool/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/EvanLuo42/RoxyBuildTool/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/EvanLuo42/RoxyBuildTool/releases/tag/v0.1.0
