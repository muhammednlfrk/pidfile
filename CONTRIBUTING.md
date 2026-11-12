# Contributing

Thanks for your interest in PidFile.NET!

## Project status

PidFile.NET is published on NuGet. Public API changes follow semantic
versioning and are documented in the changelog.

## Setup

1. Install the .NET SDK version listed in `global.json` (newer feature bands are
accepted).
2. Run `dotnet restore`, `dotnet build`, and `dotnet test`.

## Code style

Style is defined in `.editorconfig` and enforced in CI. Key rules:

- K&R braces: the opening `{` stays on the same line.
- Explicit types instead of `var`.
- `const` fields in `UPPER_CASE`; private/internal fields `_camelCase`;
private/internal static fields `s_camelCase`.
- Library code must be compatible with `netstandard2.1` and C# 8.0
(`LangVersion` is pinned in `src/Directory.Build.props`).
- Keep public API documentation in sync with the implementation.

Before pushing, run:

```bash
dotnet format
```

CI runs `dotnet format --verify-no-changes`, and warnings are treated as errors
there.

## Public API changes

The public API surface is tracked in `src/PidFile.NET/PublicAPI.Shipped.txt` and
`PublicAPI.Unshipped.txt`.
Any new public member must be added to `PublicAPI.Unshipped.txt` (the IDE quick
fix for `RS0016` does this).
Changing or removing an entry in `PublicAPI.Shipped.txt` is a breaking change
and needs discussion first.

## Dependencies

Package versions are managed centrally in `Directory.Packages.props`. Do not put
`Version` attributes in `.csproj` files.

## Pull requests

- Keep PRs focused; add or update tests for behavior changes.
- Update `CHANGELOG.md` under "Unreleased".
- CI must pass (format check, build, tests).
