---
id: TASK-41
title: >-
  Close the create-by-REST and upload byte-fidelity questions from the fifth
  gate review
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T09:41:00Z'
updated_date: '2026-11-17T09:54:00Z'
labels:
  - ci
  - release
  - security
dependencies: []
priority: high
ordinal: 40000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Carrier for the fifth-and-sixth review rounds of the release gate. Rounds 1-4 are on TASK-36, TASK-37 and TASK-40.
This task waits on the deep review of **e4b4683** (SPEC v0.12.13), dispatched 09:14 UTC.

**Why round 5 existed at all: a claim this board repeated.** TASK-40 left one box open on the theory that the
by-tag id read-back might be unsafe and that testing it would cost a real release. It was testable for free, and the
answer is **no**: `GET /repos/{owner}/{repo}/releases/tags/{tag}` is documented as *"Get a **published** release with
the specified tag"*, and draft releases are visible only to users with push access. So the draft this run had just
created was never findable by tag. The step failed closed — nothing wrong ever shipped, and the gate refused to
publish — but the publish path as written in 6b91de4 and cd4fcdd could never have published anything. The board's
"untested, costs a real release" framing on TASK-40 was wrong and is corrected there.

**What the repair does** (e4b4683, verified by reading the step bodies at the current line numbers): creation is a
REST `POST /releases` carrying tag, draft, prerelease and release-notes flags, and the run keeps the **id and
`upload_url` from that response** (release.yml:355-359) — the id is what this run was handed, not an inference about
who owns the tag. Assets upload through that template by release id with octet-stream bodies (372-380), and a create
that answers without an `upload_url` stops rather than guessing (366-367). Verification reads **one snapshot** of the
asset list (399) instead of five per-name lookups at five different moments, requires every expected name to appear
exactly once (409), and re-runs the whole inspection plus a tag re-resolve immediately before the un-draft PATCH
(446). Cleanup addresses only the captured id and re-checks id, tag and draft state before deleting (465-482). The
archive member test also checks the member's type (283-299).

**What this task is actually waiting on.** Three questions a reviewer or a real tag run has to settle:

1. Is "create over REST, keep the id from the response" the **whole** repair, or does the tag still need re-checking
   at the moment of create in a way the current sequence cannot express?
2. Is the upload leg **byte-faithful**? `gh api --input` sends the file as-is and the only evidence is that GitHub's
   reported stored-bytes digest matches the staged sha256 — which is a strong signal, but it is GitHub grading the
   upload rather than an independent hash of the object.
3. Will GitHub refuse the create with **422** when the tagged commit edits `.github/workflows/release.yml`, because
   the endpoint notes that the token must be authorized to modify workflows in that case? The job deliberately keeps
   `contents: write` only, so the first tag run is what tells us.

Harness context, credited not claimed: 48 cases, zero failures, and the stub `gh` now refuses `releases/tags/`
outright, with cd4fcdd's publish step replayed from git and shown failing.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The create answers this run with a numeric release id and an upload_url, and every asset is uploaded to that id through the returned template - not addressed by tag
- [x] #2 Verification reads one snapshot of the asset list, requires each expected name exactly once, and re-inspects plus re-resolves the tag immediately before the un-draft PATCH
- [x] #3 Cleanup addresses only the captured id and re-checks id, tag and draft state before deleting anything
- [x] #4 The gate harness stub refuses the published-only by-tag endpoint, so a run cannot pass green against a lookup that would fail in production
- [ ] #5 A reviewer or a real tag run confirms that create-by-REST with a response-captured id is the whole repair, with no remaining window that needs the tag re-checked at create time
- [ ] #6 Byte fidelity of the upload leg is established beyond GitHub's own stored-bytes digest matching, or the residual trust is written as an accepted gap
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Naming for the owner's convenience: this is the round-6 carrier; the deep review of e4b4683 is what wakes it.

Dependency for the board's sake: TASK-28 waits on this round closing, so the tag is not cut against a gate under review.

Wake condition, final form: a canary release run (TASK-42) or a real tag run that produces an asset digest from the live API. If the canary is declined, this task moves to Blocked naming the owner decision, because then nothing can close AC #5 or #6.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T09:41:00Z

---

Boxes 1-4 checked by reading e4b4683 at current line numbers: create_attempted 355, POST create with tag/draft/prerelease/notes 357-358, id plus upload_url read from the response 359, no-upload_url stop 366-367, upload by id 380, tag re-resolve 393, single snapshot 399, exactly-once 409, PATCH by id 446, cleanup re-check 465-482, member type 283-299. Boxes 5-6 are the open questions: only a reviewer or a real tag run settles them, and box 6 partly merges into TASK-39. Wake: the deep review of e4b4683 (dispatched 09:14 UTC).

---

created: 2026-11-17T09:54:00Z

---

Wake restated, as the board rule wants the event rather than the artifact: the review of e4b4683 has LANDED and is now evidence in this task, not the thing that wakes it. What wakes it is AC #5 and AC #6 being settled - and both can only be settled by a run against the real API, which is exactly what the canary decision on TASK-42 asks for. Round 6's verdict is recorded here because it closes half the argument: the response-captured id is the documented identity of the draft, and gh 2.46.0 sends an --input body as a raw reader with no JSON re-encoding, so the upload path is byte-faithful by construction - but the only observation that a real upload matched bytes is the stored digest, so AC #6 stays open until a real run produces one. Four refinements round 6 found (draft tag_name re-check, pinned release title, exact upload_url shape, and the guard's real status codes being 404/403 rather than the 422 assumed) are on TASK-43, not here.

---
<!-- COMMENTS:END -->
