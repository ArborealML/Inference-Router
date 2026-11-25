---
id: TASK-181
title: Apply supported configuration changes with air reload
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T00:25:00Z'
labels: []
dependencies: []
priority: medium
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
<!-- AC:END -->
