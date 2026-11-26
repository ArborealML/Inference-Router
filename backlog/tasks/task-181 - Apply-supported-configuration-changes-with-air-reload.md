---
id: TASK-181
title: Apply supported configuration changes with air reload
status: Preparing
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T00:25:00Z'
updated_date: '2026-11-26T03:41:00Z'
labels: []
dependencies: []
priority: high
ordinal: 173000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Expose the existing authenticated reload operation in the CLI for the running service configuration.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Apply a conditional reload with the current generation and protect the credential.
- [ ] #2 Report the resulting generation on success while admitted work continues.
- [ ] #3 Explain disabled reload, authentication failure, invalid changes, busy or stale generation, and restart-required changes. Leave restart explicit.
- [ ] #4 The reload credential is read from a file or the environment and never appears in argv, output or logs.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
One branch, CLI plus tests and docs. (1) Add a reload verb to the verb switch in the CLI entry point, taking the running service's config path with -c/--config and an admin base URL option following the pattern the status and switch verbs already use. (2) Read the reload bearer from a file or the environment, never as a positional argument, and never print it. GET /admin/config/reload to read the current strong ETag, then POST the same route with that tag in If-Match and bodyless HTTP framing, because SPEC 6 refuses chunked or body-capable framing on that route even with zero decoded bytes. (3) Map every failure to a sentence naming the cause and the next action: 404 means reload is disabled because the service started without a reload credential, so one restart is needed to enable it; 401 bad credential; 409 busy, retry after work drains; 412 stale generation, re-read and re-apply; 422 invalid config, nothing applied; 422 restart-required, name the changed setting class and keep the restart explicit. (4) On success print the resulting generation and state that admitted work continued. Tests: a live ServerApp fixture pins the request framing (ETag in If-Match, no body), pins that the credential never reaches output, and pins each status to its message; the existing reload wire tests stay untouched. Docs: the README feature line, docs/ai-integration.md, and the SPEC line that lists the CLI verbs.
<!-- SECTION:PLAN:END -->
