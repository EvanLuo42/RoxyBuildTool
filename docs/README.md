# Documentation development

The documentation site is built with Docfx 2.78.5. The repository-local tool manifest keeps the version reproducible.

## Build

From the repository root:

```powershell
dotnet tool restore
dotnet docfx docs/docfx.json
```

The generated site is written to `docs/_site`. To preview it locally:

```powershell
dotnet docfx docs/docfx.json --serve
```

Open `http://localhost:8080`.

## Structure

```text
docs/
  index.md                 # Site landing page
  guides/                  # Task-oriented user documentation
  architecture.md          # Design and invariants
  api/                     # Generated API metadata; not committed
  docfx.json               # Docfx configuration
  toc.yml                  # Site navigation
```

## Writing rules

- Document implemented behavior in the present tense.
- Mark planned behavior explicitly; do not present roadmap items as available features.
- Use canonical stable IDs in examples.
- Keep the root README concise and place detailed procedures in `docs/guides`.
- Add XML summaries to public APIs when their contract is not obvious from the signature.
- Build the site after changing links, navigation, API comments, or code samples.
