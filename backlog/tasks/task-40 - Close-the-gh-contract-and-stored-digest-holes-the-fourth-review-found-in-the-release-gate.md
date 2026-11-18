---
id: TASK-40
title: >-
  Close the gh-contract and stored-digest holes the fourth review found in the
  release gate
status: Done
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T09:11:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels:
  - ci
  - release
  - security
milestone: m-9
dependencies: []
priority: high
ordinal: 39000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Carrier for the fourth review round of the release gate. Rounds 1-3 are recorded on TASK-36 and TASK-37; this one
waits on the review of **cd4fcdd** (dispatched 09:05 UTC).

**What the round-4 review found, verified by the board rather than repeated.** The headline was not an artifact
forgery at all: `gh release create` has **no** `--json` or `--jq`, so the publish step that shipped in 6b91de4 would
have failed at argument parsing and could never have published anything. Confirmed locally on gh 2.46.0 — the
subcommand's own help mentions no JSON output flag at all, and the same help lists `--draft`, `--verify-tag` and
`--generate-notes`, which the workflow does use. The second half of the same finding: `gh release view` and
`gh release edit` read their operand as a **tag name**, not a release id, so the step believed it was addressing a
draft by id when it was addressing whatever tag matched. And the reason this survived two review rounds was the
coding session's own harness: its stub `gh` accepted `--json`/`--jq` and printed an id. A stub more permissive than
the real tool is exactly how a broken release path stays green — recorded here because that is the transferable
lesson, not just a footnote.

**What landed.** 0374fea: creation uses only flags the CLI actually has, the release id is read back over REST from
the tag, publishing is a REST PATCH by id, cleanup stays a REST DELETE by id, and a step output written *before* the
create attempt means a run that captured no id ends in a loud "Manual review required" annotation instead of a quiet
success. cd4fcdd: each remote asset's stored-bytes digest as GitHub reports it is compared against the staged file's
sha256, and a digest the remote does not report **fails** the gate rather than passing for unprovable; the archive
member test now checks the member's *type*, because a symlink member named `air.exe` passed the previous gate.

**Board verification (reading the checked-out workflow, not running it):** create at release.yml:348 with no JSON
flags; id read-back at 354; digest comparison at 379-388 with the fail-closed comment at 382; publish by id at 400;
the attempt-but-no-id annotation at 428 and the delete-nothing-without-an-id rule at 440; member-type check at 283-292.
CI for cd4fcdd on main: completed, success. The forgery cases are the coding session's harness (41 cases, zero
failures there, and it now runs 6b91de4's publish step against a strict stub and watches it fail) — their evidence,
credited, not repeated.

**The one claim this task is really waiting on.** Read-back-by-tag assumes GitHub refuses a second release for a tag
that already carries one. Nobody has tested that, because testing it means mutating a real release. It is
load-bearing: if GitHub ever allows two releases on one tag, "look up the id by tag" can address somebody else's
draft, which is precisely the failure the cleanup rewrite exists to prevent. It is also the kind of property only a
reviewer with the API contract, or a real tag run, can settle.

**Also deliberately not closed, and not this task's problem:** no workflow line excludes an external writer moving
the tag between the re-resolve and the create — `concurrency` serializes this repository's own runs only. That is a
repository-settings decision for the operator, and it now lives as its own owner task, alongside the accepted gap
about verifying what a downloader actually receives.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Release creation uses only flags the installed gh CLI documents, and the release id is read back over REST by tag
- [x] #2 Publishing and cleanup address the release by captured id over REST, and a run that attempted a create but captured no id ends in a loud manual-review annotation rather than deleting nothing quietly
- [x] #3 Each published asset's GitHub-reported stored-bytes digest is compared with the staged file's sha256, and a missing digest fails the gate
- [x] #4 An archive member's type is checked, so a symlink or hardlink named air.exe is refused
- [x] #5 The gate harness stub enforces the real gh flag set, so a contract error like an unsupported flag cannot pass green again
- [x] #6 The by-tag id read-back is retired as unsafe-by-design, not merely untested: GET /releases/tags/{tag} is documented as returning a PUBLISHED release, and drafts are listed only for push-capable users, so a draft this run created was never findable that way
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Round numbering for the record: TASK-40 is the round-5 carrier. After e4b4683 changed the lines it cites, boxes 2-4 were re-verified against the current file (create by REST answering id plus upload_url at 355-359; one-snapshot verification with exactly-once at 399-409; cleanup addressing only the captured id and re-checking id/tag/draft at 465-482). Box 5 (member type) was verified at 283-299. Box 1, the by-tag claim, needed no re-verification: the mechanism it condemns no longer exists.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T09:12:00Z

---

Boxes 2-6 checked by reading the checked-out workflow (release.yml:348 create with no JSON flags, 354 id read-back by tag, 379-388 digest comparison with fail-closed at 382, 400 PATCH by id, 428 and 440 the attempt-without-id annotation and the delete-nothing rule, 283-292 member type) and by checking the CLI contract myself: gh 2.46.0 here, and the release-create help names no JSON output flag at all - so the P0 is real and not a reviewer's guess. AC #1 stays open on purpose: the by-tag uniqueness property has not been tested by anyone, and testing it means mutating a real release. Wake: the external review of cd4fcdd (dispatched 09:05 UTC) plus, ideally, a tag run that proves it. CI for cd4fcdd: success.

---

created: 2026-11-17T09:41:00Z

---

Wake fired, and the answer to the open box is NO - my framing was wrong and is corrected here rather than quietly amended. The property was testable for free from the API description (Get a PUBLISHED release with the specified tag; draft listings need push access); I recorded it as untestable-without-a-real-release, which is how a wrong claim survived two of my own board passes. Consequence: the design was never risky, it was broken - the gate failed closed and could not publish. Disposition: replaced by create-over-REST returning the id, now on TASK-41. Line anchors for the five boxes I verified at cd4fcdd are now partly historical: create was 348 and is 357; the by-tag read-back at 354 is gone entirely; PATCH moved 400 to 446; the attempt-without-id annotation 428 to 469; digest compare 379-388 to the single-snapshot read at 399-409. The archive member-type check at 283-292 still stands.

---
<!-- COMMENTS:END -->
