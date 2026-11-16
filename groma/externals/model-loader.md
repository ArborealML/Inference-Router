---
type: C4 System
title: Model loader (select-model script)
status: stable
groma:
  id: model-loader
  technology: Operator shell command
---

The operator's select-model program, run as a lifecycle command when an engine must be drained and pointed at a different model. Today this is a shell-out; a native engine-lifecycle provider is specified (SPEC 5.3.1) but not implemented.
