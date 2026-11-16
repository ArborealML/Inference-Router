---
type: C4 Container
title: air (command line)
status: stable
groma:
  id: air-cli
  parent: inference-router
description: 'The `air` executable: start the router, or drive a running one.'
---

`serve` hosts the HTTP server in-process; `chat`/`complete`, `models`, `status` and `events` talk to a running router over its own API; `config` validates a TOML file without binding sockets or running lifecycle commands. Published Native AOT per platform as the single binary `air`.
