---
id: TASK-83
title: Check that every config value the parser accepts has a runtime handler
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T04:10:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels:
  - config
  - tests
milestone: m-6
dependencies:
  - TASK-77
ordinal: 83000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Config is parsed once into immutable domain types through source-generated DTOs, so a key that **binds without error and is then ignored** is invisible until a request depends on it. Two shapes matter: an enum-valued or string-valued setting whose accepted values are not the set the runtime actually handles, and a DTO field that is read into the config tree with no mapping into the domain type at all. The external evaluation of an open-source gateway (2026-11-18) borrowed a test for exactly this: their schema check asserts every provider name the configuration permits deserialises to a runtime variant. We have no equivalent, and TASK-77 is about to add a second engine flag that will be wrong in precisely this way if nothing checks it.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 One test per enum-valued config surface asserts the accepted set equals the set the runtime switches on, so a value that parses but matches no runtime branch fails the build.
- [ ] #2 A field present in the config DTO but absent from the domain mapping is reported by a test or a build-time check, not discovered at request time.
- [ ] #3 The shipped samples are the fixture: every engine block in `samples/` is asserted against the same rules, and the `config` dry-run still loads all of them unchanged.
<!-- AC:END -->
