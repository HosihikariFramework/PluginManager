# AGENTS.md

Managed core of the Hosihikari Framework: plugin discovery, dependency ordering, isolated loading,
lifecycle, unload verification, the packer, and the author test packages.

## Repository map

- `src/` — manager, assembly loader, lifecycle, package model.
- `host/` — assertion host; runs without BDS, Shim, or a carrier.
- `tools/Pack/` — the packaging tool.
- `tools/Build/` — `Hosihikari.Plugin.Build` and the `PackPlugin` MSBuild target.
- `testing/` — `Hosihikari.Testing` and its smoke host.
- `test/` — fixture plugins used by the assertions and packer tests.
- `.github/workflows/verify.yml` — the checks CI runs.

## Build and verify

```bash
dotnet run --project host/Hosihikari.PluginManager.Host.csproj -c Release
bash tools/Build/smoke.sh
dotnet run --project testing/Hosihikari.Testing.Smoke/Hosihikari.Testing.Smoke.csproj -c Release
bash ./pack.sh 0.0.0-check
```

`-c Release` is required: a Debug build keeps local references alive and makes unload verification
fail. CI places `PluginAbstractions` and `Hosihikari.McMaster.NETCore.Plugins` beside this checkout;
do the same when changing the contract or native probing.

## Invariants

- Public names follow ADR-0040. 1.x changes are additive (ADR-0041); do not add a Reload verb or
  cascade unload.
- Unload timeout is a failed unload and leaves the slot `Detached` (ADR-0042).
- On release, keep the core `manifest.json`, `src/Hosihikari.PluginManager.csproj` version, and core
  dependency ranges in sync. `Hosihikari.Plugin.Build` and `Hosihikari.Testing` are versioned
  independently.
- Do not commit `artifacts/`, `bin/`, or `obj/`.

## Context

- Design decisions and terminology are kept by maintainers outside the public repositories; ask for
  the relevant decision when changing lifecycle or public API.
- [Documentation](https://hf.srcz.one/) — user-facing guides.
- Public default branches are protected. Work on a branch, open a pull request, and squash merge.
