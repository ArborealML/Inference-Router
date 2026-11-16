# AGENTS.md — working conventions for this repository

Conventions for humans and coding agents working here. The specification and its
revision log live in [`SPEC.md`](SPEC.md); read the relevant section before changing
behaviour, and update it in the same commit as the change.

## Build, test, publish

```bash
dotnet build                       # solution: inference-router.slnx  (SDK pinned in global.json)
dotnet test                        # xUnit; must stay green
dotnet publish src/Arboreal.InferenceRouter.Server -c Release -r linux-x64 /p:PublishAot=true
```

The build runs with `TreatWarningsAsErrors` and an analyzer stack
(`Directory.Build.props`, `Directory.Packages.props`, `BannedSymbols.txt`,
`.editorconfig`). A warning is a build failure — fix it rather than suppressing it, and
add a suppression only with a comment explaining why.

## Non-negotiable constraints

- **Native AOT everywhere.** No runtime reflection: JSON and TOML go through
  System.Text.Json / Tomlyn **source-generated** contexts over explicit DTOs. If you
  need something that would normally use reflection, write a source generator or a
  hand-written mapping. AOT publish must produce **zero trim warnings**
  (`-p:TrimmerSingleWarn=false` when checking).
- **The request hot path stays allocation-light.** Config is parsed once at startup into
  immutable domain types; upstream bodies are forwarded as bytes; no UTF-16 round trips
  on token deltas; never keep a borrowed span across an `await`.
- **Don't break the wire contract casually.** `/v1/*` must stay OpenAI-compatible.
  `/admin/*` JSON is currently PascalCase and tests pin it; casing changes are a wire
  break (SPEC §6, §15.1).
- **TOML keys with dots must be quoted**: `[engine.strata.catalog."model.id"]`. Unquoted,
  the key silently nests into junk. A test asserts the shipped samples load as written —
  run `dotnet run --project src/Arboreal.InferenceRouter.Cli -- config -c samples/router.toml`
  after editing any sample (it validates without binding sockets or running lifecycle
  commands).

## Safe testing on a real machine

Lifecycle commands (`select = { exec = … }`) **start and stop inference backends**. Never
smoke-test with a config that has them against a machine you care about: use
`samples/router.safe-smoke.toml`, whose engines are `remote = true` with no lifecycle
commands. If you do run a real lifecycle program, treat it as a mutation of shared
infrastructure and say so in your report.

## Repo hygiene

- `.local/` is gitignored and holds working notes, handoffs and anything specific to a
  person's machine or to internal services. Keep it out of commits and out of published
  files; do not put absolute home-directory paths, key-file locations, or internal
  service names in tracked files — use placeholders like `/etc/arboreal/keys/…`.
- Commit spec + code + tests together when they form one feature, and add a line to the
  SPEC revision log.
- New dependencies go in `Directory.Packages.props` (central package management).
