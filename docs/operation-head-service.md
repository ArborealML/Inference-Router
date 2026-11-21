# Supervised launch operation head

The standalone `air-operation-head` executable stores the committed head of one commissioned initial-launch journal. It serves a scoped read and compare-and-advance over a private TLS Unix socket. The supervised writer computes the digest from the exact serialized journal frame. The authority compares and stores that value. It has no path to the writer's journal and no Docker, readiness or ingress controls.

## Protected inputs

The installer supplies the external authority and installation proof. Give the service a dedicated OS principal, distinct from the producer UID, and an exclusive persistent state root beyond the producer and AIR backup, clone and restore boundary. Hold the root and its parent chain against producer path replacement. Use a local volume whose atomic same-directory rename, file flush and directory sync semantics are qualified for deployment. Independently exclude restored copies, alternate writable mounts and other service instances outside the stable lock domain. The lock applies to that inode. The service cannot establish past storage continuity from a snapshot alone.

The authority directory belongs to the service principal and has mode `0700`. Provision these regular, single-link authority files with mode `0600` before startup:

| File | Contents |
| --- | --- |
| `bootstrap.json` | Version 1 object with `Version`, `Scope` and `ProducerUid`. The scope is the commissioned `HostReservationScope` with `Slot`, GUID `Identity` and `Enrollment` containing `Group` and positive `Generation`. |
| `head.lock` | Stable, initially empty lock file. Preserve this inode through service restarts. |
| `head.json` | Version 1 snapshot with the exact same `Scope`. A newly commissioned scope starts with `Kind: "Absent"`, `Revision: null` and `Digest: null`. A `Current` snapshot always carries revision 0 through 5 and a 64-character uppercase SHA-256 hex digest. |
| `authority.pem`, `authority.key` | Service certificate and matching unencrypted PEM key, protected by the same authority. The producer receives the exact leaf SHA-256 pin separately. |

Provision the absent seed once for the new scope. The process requires a complete installed snapshot on every start and has no initialize, reset or repair command. Treat a missing state, uncertain durable commit or suspected rollback as an authority incident. Keep the journal's flushed tail intact.

Give the authority a separate runtime directory with mode `0710` for access through the producer's assigned group. Set the service process's primary group to that group. The service creates `operation-head.sock` with mode `0660` and verifies the exact permitted UID on each connection. Its state root remains owner-only. The process requires an unoccupied socket pathname at startup and can remove its own endpoint on orderly shutdown. Leave occupied or replaced names for the administrator to resolve.

The executable requires Linux x64 with glibc, procfs, kernel `openat2` and `statx` mount-ID support. Its two arguments are the authority directory and socket directory as absolute paths. Public AIR configuration and startup do not register this service or its adapter.

## Scoped results

The client sends one strict length-framed request, pinning the service TLS leaf. Each decoded reply binds the full request and its fresh nonce. Only an authenticated absent snapshot maps to a null read. Only a verified no-write refusal maps to false for an advance. An unavailable or ambiguous exchange raises an I/O error. The committed state becomes visible only after the authority flushes the new file, atomically replaces the old snapshot, and syncs the authority directory.

Focused Release verification exercises the real service on scratch Unix sockets, without daemon or host provisioning. The independent published-binary smoke uses two disposable process UIDs and scratch files inside an isolated qualification runner. Those checks establish implementation behavior within their test environment. Lead owns deployment, independent storage and real host authority.
