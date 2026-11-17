# Superseded board log — trimmed 2026-11-17

Comment entries the board removed from open tasks under the rolling-window rule (decision-20). Each entry was
superseded argument or history, not a standing rule: the rules that survive are in the task Description,
Acceptance Criteria, Notes or SPEC. Nothing here is current guidance; it is the record of what was decided and
why, kept so a later reader can tell a decision from an oversight. Disposition of each round's findings is in
SPEC §15 and the commit that closed it.


**Entry counts, recomputed from the blocks on 2026-11-17 at 17:2xZ** by counting `created:` lines per section
rather than trusting the numbers written when each entry was moved: TASK-11 4, TASK-14 3, TASK-16 1, TASK-21 4, TASK-28 7, TASK-29 6, TASK-30 3, TASK-32 6, TASK-33 1, TASK-38 5, TASK-42 5, TASK-45 2, TASK-46 2, TASK-49 1 — 50 entries in 23
sections (a task whose second-pass entries sat under another heading is counted once, and each section header
now states its own count). The counts this file originally claimed were wrong; the blocks below are the record.

## TASK-11


# TASK-11: 3 superseded comment(s), removed from the board body
Nothing was deleted: this is the text that came out, and git history plus the cited commits and
SPEC 15 rows keep the record. Newest two kept in the task.

created: 2026-11-17T10:14:00Z

---

AC #2 rewritten: it used to list an explicit stream gap event as if settled, which pre-decided a wire question that TASK-21 - now a Blocked owner task - exists to answer. The task still depends on TASK-21 in front matter, so the gate is visible; the contradiction was in the criterion, not the dependency. TASK-6 re-checked for the stale macOS wording and none is present (no TASK-22 reference in the file).

---

created: 2026-11-17T10:58:00Z

---

Wake, named: the three posture answers on [TASK-21](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-21%20-%20Decision-what-the-live-view-shows-who-may-open-it-and-whether-the-wire-gains-loss-reporting.md) are adopted (operator-first; `/admin` unauthenticated and loopback-only with operator-managed tunnels; no new feed event kind — continuity computed client-side from sequence holes plus the exclusive cursor). This task starts when SPEC carries that wording, because AC #5 is the event-stream half of it.

---

created: 2026-11-17T13:56:00Z

---

**Stale park, found by the board audit at b214ce6.** This task sat Waiting on posture answers from a predecessor that is now closed; the wake already happened, so the Waiting column was hiding ready work. Moved to To Do with its assignee unchanged. Nothing about its scope or criteria changed.

## TASK-14


# TASK-14: 3 superseded comment(s), removed from the board body
Nothing was deleted: this is the text that came out, and git history plus the cited commits and
SPEC 15 rows keep the record. Newest two kept in the task.

created: 2026-11-17T03:44:00Z

---

AC #5 now holds, and it is checkable rather than a vibe: the only measured claims in the README are the benchmark paragraph, and every figure in it traces to the published run — routing +58.0/+90.6/+91.4 µs p50 and ~15 KB (README: ~58–91 µs, ~15 KB), full HTTP path +217.1/+320.7/+995.8 µs and ~26–32 KB (README: ~217–996 µs, ~26–32 KB, with the top of the range labelled as HTTP framing rather than routing), SSE relay +58.1/+97.5/+102.3 µs and ~24–25 KB (README: ~58–102 µs, ~25 KB) — with docs/benchmarks.md and SPEC 9.1 linked for the caveats. The compatibility claim is scoped to the two proxy routes in the works-today/not-built-yet table, and no other sentence in the README asserts performance.

---

created: 2026-11-17T05:26:00Z

---

Assignee reduced to one name. Two were listed (the owner and the agent): the owner's move on the README itself lives on TASK-23, so the next actor on this task is the agent who restructures it when TASK-23 lands. A task with two assignees reads as nobody.

---

created: 2026-11-17T07:55:00Z

---

AC #4 is two badges and only one of them exists today: README lines 10-12 carry CI, license and .NET badges, and the CI badge resolves (checked the URL form against the workflow name). The release badge cannot resolve until a release exists, which is TASK-32 (owner go-ahead) then TASK-28 (the tag run). Dependency set to TASK-28 so the board shows the real wake instead of a prose promise. Assignee stays the main session: a README line during the tag run, not a separate change.

## TASK-16


# TASK-16: 1 superseded comment(s), removed from the board body
Nothing was deleted: this is the text that came out, and git history plus the cited commits and
SPEC 15 rows keep the record. Newest two kept in the task.

created: 2026-11-17T05:26:00Z

---

Assignee changed from the external research service to the agent. Nothing has been dispatched: AC #1 is the dispatch itself, and it is sequenced behind TASK-14, so the next move is whoever submits the request, not the service that will answer it. The service owns a request only once it exists — the board has already used that convention on the tasks whose research actually ran. Dependency on TASK-14 recorded so the ordering is structural rather than a sentence in the description.

## TASK-21


# TASK-21: 3 superseded comment(s), removed from the board body
Nothing was deleted: this is the text that came out, and git history plus the cited commits and
SPEC 15 rows keep the record. Newest two kept in the task.

created: 2026-11-17T07:54:00Z

---

Assignee and title changed on purpose. This is not an owner decision being handed to a machine: it is a design question whose answer is a recommendation, so the external review service owns the analysis and the board session owns what happens to it - review against how this repo's admin surface and event feed actually work, then file one owner task with the approval lines written out. Wake condition: the recommendation lands. Until then no person is waiting on this and no person is being asked to write a design. Retitled from Decision: to Design: so the title matches who acts.

---

created: 2026-11-17T08:14:00Z

---

Design phase closed: dispatched to the external research and review service at 08:07 UTC and answered at 08:1x UTC (transcript in the board's untracked review transcripts, not in the repository). The board reviewed the answer against SPEC 6/10/12.5 and the feed semantics in code before putting any of it here - in particular the claim that loss is subscriber-relative, which is true of this ring: eviction is global, consumption is per subscriber, and an eviction event would itself consume ring capacity to describe something that may affect nobody. Retitled Design: back to Decision: because what remains is ratification, not analysis. Status Blocked: no event wakes this, a person answers three one-line approvals (about 3 minutes).

---

created: 2026-11-17T10:58:00Z

---

Reassigned under decision-16 and **adopted as recommended** by the external design answer, which the board checked against this code before recording it: (1) the live view is operator-first, not multi-tenant; (2) `/admin` stays unauthenticated and loopback-only, with operator-managed tunnels plus browser-request hardening — no new credential surface; (3) **no gap/loss event kind on the shared feed**: compute continuity client-side from sequence holes plus the exclusive cursor, and if a server-side notice is ever wanted it must be subscriber-relative and must not consume a global sequence id. Remaining work: write those three into SPEC §6, §10 and §12.5 as posture, with a revision-log line, then [TASK-11](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-11%20-%20Blazor-live-view-screens-plus-the-event-stream-additions-they-need.md) can start. Answer 3 changes nothing on the wire, which is the point of adopting it.

## TASK-28


# TASK-28: 5 superseded comment(s), removed from the board body
Nothing was deleted: this is the text that came out, and git history plus the cited commits and
SPEC 15 rows keep the record. Newest two kept in the task.

created: 2026-11-17T07:53:00Z

---

Wake condition rewritten, and a new step added from an external review of the release work, checked against the file: Actions runs the workflow from the commit the tag points at, so a tag on anything older than fbc48d6 runs the old four-leg workflow and the macOS legs queue again. Verified on the checked-out HEAD: matrix legs parse to [linux-x64, win-x64]; the gate names the same two in expected_rids (line 216); the archive-count refusal is at 225; prerelease handling at 238-241. The same ancestry check is now on TASK-32 for the case where the owner cuts the tag personally. Two external deep reviews of this area (713ffdd, fbc48d6) are recorded as in flight; per the contract that changes neither status nor assignee, and their request bookkeeping stays out of the board.

---

created: 2026-11-17T08:49:00Z

---

Dependency added: the tag should not be cut while the gate is under review. TASK-28 now waits on the owner go-ahead (TASK-32) and on the release-gate round closing (TASK-37, wake = the review of 6b91de4 plus the SPEC 15 disposition row). The first-run checklist is in Implementation Notes.

---

created: 2026-11-17T09:13:00Z

---

Dependency set widened deliberately: TASK-38 (who may move a release tag) and TASK-39 (whether to pay for download-and-hash or signing) are repository-settings and money calls in front of the owner, and TASK-40 is the gate round whose load-bearing claim is untested. Cutting the first release before those land would mean proving the gate in production with the settings still open. If you would rather cut v0.1.0 with the settings question open, say so on TASK-32 and I will record that as the accepted posture rather than argue it.

---

created: 2026-11-17T10:58:00Z

---

Board update after the round-8 review: this stays Blocked, and the blockers are all agent work now, not an owner's keystrokes (decision-16). Floor for the tag moves to the merge commit of [TASK-44](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-44%20-%20Close-the-open-release-gate-review-findings-SPEC-v0-12-15-onward.md); TASK-42 (canary) proves the gate first, TASK-45 turns on immutable releases before any release is published, TASK-38 puts a `v*` ruleset on the tag. No tag has run the gate: configured, not proven.

---

created: 2026-11-17T11:06:00Z

---

Board update: [TASK-39](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-39%20-%20Decision-verify-what-a-downloader-receives-or-keep-accepting-that-we-do-not.md) is answered (adopt posture), and the work it produced is [TASK-48](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-48%20-%20Prove-what-a-downloader-receives-post-publish-readback-and-free-build-attestation.md), which now also gates this task. The first real tag should therefore be the canary in [TASK-42](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-42%20-%20Run-the-canary-through-the-real-gate-knowing-the-version-name-is-burned.md), not v0.1.0: the canary is where the readback and the consumer-verification commands get tested on a release nobody will install.

## TASK-29


# TASK-29: 5 superseded comment(s), removed from the board body
Nothing was deleted: this is the text that came out, and git history plus the cited commits and
SPEC 15 rows keep the record. Newest two kept in the task.

created: 2026-11-17T06:56:00Z

---

Answer received: `card set, avatar set`. Checked AC #2 (avatar) and NOT AC #1 (card), because only one of the two is true on the public side, and this task is graded on what a visitor sees, not on what was pressed.

**Avatar — confirmed, from outside the form.** `https://github.com/ArborealML.png?size=512` returns 460×460 RGB whose colour histogram is 82.4% near-black field and 16.5% near-white mark. Our export `air-mark-512.png` is 82.4% #10241F and 16.7% #F3F7F2 — the same ratio to a tenth of a percent, with GitHub re-encoding the palette slightly (#101010 / #F3F3F3 instead of #10241F / #F3F7F2). That is the mark.

**Card — not live.** Two independent checks: the repository page still serves `<meta property="og:image" content="https://repository-images.githubusercontent.com/1460450305/…">`, which is GitHub's **auto-generated repository image**, not an upload; and that image's histogram is 87.6% white with 4.4% near-black — the inverse of our card, which is 93.6% #10241F field with 3.7% #F3F7F2 ink. A custom social preview would make the page point at `opengraph.githubassets.com/…` and serve the dark field. Re-checked after a pause, still the default.\n\nWhat to do: repo Settings → Options → Social preview → confirm the image is still shown in the box **and that Save was pressed** — the form silently keeps the previous state if Save is missed, and the removal/add state is easy to miss because the page shows a thumbnail either way. Then the check above should flip to the dark field; if you want the card ready to re-pick, the export is at `an untracked card render` (30,842 bytes, sha256 b02b6d32…97e9) and the mark at `an untracked avatar render` (9,935 bytes, sha256 2e1b5e02…820a). Stays Blocked: only a person can press that button.

---

created: 2026-11-17T07:45:00Z

---

Third check, 2026-11-17 07:33 UTC, and this one is decisive about what the settings page is holding. (a) The repository page still serves og:image = repository-images.githubusercontent.com/1460450305/..., and that object is 2269x1134 - GitHub's generated repository image. A repository that has a custom social preview serves og:image from opengraph.githubassets.com with a content hash in the path; checked against vitejs/vite as a positive control. (b) The object the link-preview endpoint serves is 1200x600 and 87.6 percent white with 3.9 percent purple (#7355DD, GitHub card furniture); our committed card is 93.6 percent #10241F field with 3.7 percent ink. Same answer from two surfaces, and re-fetching with different cache versions returns byte-identical bytes, so this is not a stale cache. Avatar by contrast is live: the org picture returns 460x460 at 82.4 percent #10241F and 16.5 percent #F3F7F2 against our export at 82.4/16.7 - that is the mark. What to look at: the card upload box in repo Settings -> Options, where an added image and an absent one both render as a thumbnail; the description answer has now said card set twice while the public surface says otherwise, which is the signature of pressing away from the form instead of Save. The PNG to pick is committed, so it is download-and-pick: docs/images/air-social-1280x640.png. AC #2 stays checked, AC #1 and #3 stay open, status stays Blocked: only a browser session can change that setting.

---

created: 2026-11-17T10:58:00Z

---

Retitled and re-trimmed under decision-16: this is the **only** task left on a person, because GitHub has no API, no `gh` endpoint and no committed file that stands in for the social-preview upload — an agent can prepare the PNG and verify the result afterwards, but cannot press Save. Everything that is not the button moved to [TASK-46](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-46%20-%20Render-the-final-social-preview-card-and-verify-it-from-outside-the-form.md) (final render, measurements, sha256 of the file to pick) and the after-the-fact public check. Status: avatar live and verified; card not live — re-checked 10:40Z, the page still serves `repository-images.githubusercontent.com/1460450305/...`, which is GitHub's generated image, and a custom card would make it serve `opengraph.githubassets.com/...`. The form shows a thumbnail whether an image is set or not, so the failure mode is pressing away from the form instead of Save; that has now happened twice against a 'card set' answer.

---

created: 2026-11-17T13:30:00Z

---

**Fourth check, measured by the board at 8f78a73 (13:2xZ), and the card is still not live.** The repository page serves `og:image` = `repository-images.githubusercontent.com/1460450305/...`, GitHub's generated image. Fetched the link-preview object directly: 1200x600, 88.7% light pixels — our committed card is 96.1% dark field, 3.9% light, so the card GitHub shows is not ours. The avatar **is** live and re-confirmed independently: 460x460, 82.6% dark / 16.7% light against our export's 82.4/16.7.

Moved to **Waiting** rather than Blocked because there is now an event that wakes it: [TASK-46](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-46%20-%20Render-the-final-social-preview-card-and-verify-it-from-outside-the-form.md) is re-rendering the card, because the committed one fails its own legibility bar — its smallest text line has ~19 px caps in the 1280x640 raster and a GitHub link preview shows the card at about 600 px wide, so that line lands near 9 px. Uploading today's PNG is still open to anyone who wants a card now: it is 30,842 bytes, under GitHub's 1 MB ceiling, and replacing it later is a second trip to the same form with nothing burned. What the board does not want is a third upload of an image that fails the measurement in TASK-46 AC #2.

---

created: 2026-11-17T13:44:00Z

---

**Heads-up on a number in this task's own text, so it is not trusted after it goes stale.** The byte size and sha256 quoted above (30,842 B, `b02b6d32...97e9`) belong to the card as committed today. [TASK-46] (https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-46%20-%20Render-the-final-social-preview-card-and-verify-it-from-outside-the-form.md) is replacing that image: the committed card's smallest text line measures 20 px cap height and a link preview shrinks it to about 9 px, which is unreadable. The replacement candidate is measured at 42 px on the same line and is already verified for determinism and geometry, but it is not committed yet. **Pick whatever `docs/images/air-social-1280x640.png` holds when the upload happens, and re-read its sha256 from the file rather than from this task.** Nothing here burns anything: replacing a social-preview image is a second trip to the same form.

## TASK-30


# TASK-30: 2 superseded comment(s), removed from the board body
Nothing was deleted: this is the text that came out, and git history plus the cited commits and
SPEC 15 rows keep the record. Newest two kept in the task.

created: 2026-11-17T08:14:00Z

---

Wire-shape question resolved rather than pre-decided, and the answer is smaller than it looked. Checked against the code and SPEC: the in-band ending for a stream whose 200 is already committed is shipped precedent, not a new surface - response_timeout_ms already ends a quiet engine mid-turn with one engine_timeout frame plus [DONE] (SPEC:696, SPEC:796, and the v0.12.7-era note at SPEC:222 that streaming ceilings are in-band by construction). So AC #3 now names SPEC 6 for the shape and TASK-34 for the numbers. TASK-34 asks you for three constants and one policy (idle bound default, cancel-or-let-finish, frames-or-bytes) and starts at @claire. Start the machinery now: relay bound, stall detection, tests against the fake - the constants are the only thing waiting on a person, and a two-minute answer should not stall a week of work.

---

created: 2026-11-17T08:48:00Z

---

Precedent claim verified independently, and the line anchors in my first wording had already drifted (SPEC moves constantly, so cite sections): SPEC 6 says a committed stream whose engine goes quiet fails in band with one engine_timeout frame plus [DONE] (row at line 735 today), SPEC 14.1 row 2 names the same ending and credits RequestPhaseTimeoutTests (line 1379), and the test AStreamThatNeverSendsAFrameEndsInsideTheStreamBecauseTheStatusIsAlreadySent pins it at RequestPhaseTimeoutTests.cs:196. So TASK-34 really is numbers-and-policy, not a wire change. If the coding session's independent check disagrees, this criterion moves back behind an owner wire decision and TASK-34 is retitled accordingly.

## TASK-32


# TASK-32: 6 superseded comment(s), removed from the board body
Nothing was deleted: this is the text that came out, and git history plus the cited commits and
SPEC 15 rows keep the record. Newest two kept in the task.

created: 2026-11-17T07:45:00Z

---

Kept over TASK-31: identical title, created two minutes later, and this copy carries the acceptance criterion and the board dependency. TASK-31 is archived rather than deleted so the ID and its links survive and a reader can see the duplicate was removed on purpose. Consequences, the one-line answer, the command and the minute cost are now in the description, which is what a person needs to close it without a follow-up question. The TASK-15 gate is gone from the prose because TASK-15 landed in 713ffdd and I re-verified the two-platform workflow myself.

---

created: 2026-11-17T07:53:00Z

---

Added the ancestry step after a review found the sequencing hazard: the workflow that runs is the one in the tagged commit, so a tag older than fbc48d6 would re-run the four-leg workflow and queue macOS jobs that no runner picks up. The check is git merge-base --is-ancestor fbc48d6 origin/main, and the tag is cut on the sha that check just cleared. This is the only reason the owner task now carries a command with three lines instead of one.

---

created: 2026-11-17T09:46:00Z

---

Rewritten to the owner-instruction standard: answer-first block at the top, one line per option, the exact commands, and links that resolve on specific files instead of the tasks folder - this task previously contained zero URLs for a decision about publishing a release. The floor is a command, not a sha, because it has moved five times today.

---

created: 2026-11-17T09:53:00Z

---

Now waits on the canary decision: cutting v0.1.0 on a gate that has never run for real makes the first release the test, which is the wrong order. The canary is the cheap falsifier; this stays a separate yes afterwards.

---

created: 2026-11-17T10:58:00Z

---

Reassigned under decision-16: the cut is an agent act, not an owner's keystroke. Wake: the canary in [TASK-42](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-42%20-%20Run-the-canary-through-the-real-gate-knowing-the-version-name-is-burned.md) green, [TASK-44](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-44%20-%20Close-the-open-release-gate-review-findings-SPEC-v0-12-15-onward.md) closed, [TASK-45](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-45%20-%20Enable-GitHub-immutable-releases-before-the-first-tag.md) done. The command stays as written in the description — check the floor first, never tag a hardcoded sha: `git log -1 --format=%H -- .github/workflows/release.yml`, `git merge-base --is-ancestor …`, then `git tag -a v0.1.0 -m v0.1.0 "$(git rev-parse origin/main)"` and `git push origin v0.1.0`. Then read the run, do not assume it.

---

created: 2026-11-17T14:02:00Z

---

Blocked, not Waiting: the chain it sits behind now waits on a person. Its stated wake was the canary green, TASK-44 closed and TASK-45 done; the canary and the two repository settings are now Blocked on TASK-49, so no event short of an owner answer wakes this. Nothing about its own criteria changed.

## TASK-33


# TASK-33: 1 superseded comment(s), removed from the board body
Nothing was deleted: this is the text that came out, and git history plus the cited commits and
SPEC 15 rows keep the record. Newest two kept in the task.

created: 2026-11-17T10:58:00Z

---

Reassigned under decision-16: the permission is no longer the owner's to type, so the task becomes a scoped act with a report. Answer recorded: **allow one lifecycle transaction on this host**, bounded to a named disposable engine that no other session is using, with the config committed as a sample-like file under `the board's untracked scratch directory`, and a report naming what was started, stopped and removed and how long each step took. That is a mutation of shared infrastructure on the owner's machine, so it stays reportable in the commit and in SPEC's verification wording, and it is reversible by `docker` inspection. If the run cannot be made bounded — no idle engine, or an unknown engine holding a slot — stop and put it back on the board rather than choosing which engine to disturb.

## TASK-38


# TASK-38: 3 superseded comment(s), removed from the board body
Nothing was deleted: this is the text that came out, and git history plus the cited commits and
SPEC 15 rows keep the record. Newest two kept in the task.

created: 2026-11-17T10:58:00Z

---

Reassigned under decision-16: this was parked as a browser settings act, and it is not one. Measured on this repository: `GET /repos/{owner}/{repo}/git/tag-protection/branches` answers 404, so the tag-protection endpoints are unavailable here; `GET /repos/{owner}/{repo}/rulesets` answers `[]`, so a **repository ruleset** matching `v*` is the scriptable way to get what the review asked for (block delete and non-fast-forward moves on release tags). The ruleset API is a real mutation of a public repository: state the ruleset created, and read it back after. 'Who may publish' stays a written sentence: admins plus the Actions token, least privilege, with the token's own permissions being settled in [TASK-44](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-44%20-%20Close-the-open-release-gate-review-findings-SPEC-v0-12-15-onward.md) AC #4, not here.

---

created: 2026-11-17T11:31:00Z

---

Ownership, settled: as with TASK-45, this is a setting rather than YAML, but it is reachable —
`GET .../git/tag-protection/branches` answers 404 on this repository while `GET .../rulesets` answers `[]`, so a
repository ruleset on `v*` is created over REST with the token this machine already holds (measured
`admin: true`). Agent work under decision-16, created and read back in the same report, and reversible. Not a
prerequisite the canary can fake: TASK-42 proves nothing about tag protection unless this lands first.

---

created: 2026-11-17T13:56:00Z

---

Blocked on TASK-49 (c): whether one ruleset or two, because a `creation` rule with no bypass list blocks the release path itself. Nothing else waits — the payload shape, the readback and the measurements are all in this task and agent-owned.

## TASK-42


# TASK-42: 4 superseded comment(s), removed from the board body
Nothing was deleted: this is the text that came out, and git history plus the cited commits and
SPEC 15 rows keep the record. Newest two kept in the task.

created: 2026-11-17T10:58:00Z

---

Reassigned under decision-16: an agent runs it. Wake: [TASK-44](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-44%20-%20Close-the-open-release-gate-review-findings-SPEC-v0-12-15-onward.md) merged and [TASK-45](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-45%20-%20Enable-GitHub-immutable-releases-before-the-first-tag.md) done, with CI green on the merge commit. Protocol, tightened by the round-8 review: tag current `main` as `v0.0.0-canary`, push, let the unmodified workflow run with its own token, capture the create and upload responses, the full asset inventory, the download SHA-256, the peeled tag SHA, the `PATCH` response, and `.immutable` on the published release; then try to replace an asset and to move the tag and **require both to be refused**; then delete the release with `--cleanup-tag`, because preflight refuses a tag that already carries a release, draft or published. Cost: public assets for a few minutes plus one runner leg. It is a mutation of a public repository and the report says so in those words.

---

created: 2026-11-17T11:06:00Z

---

Hardening from the TASK-39 answer: the canary is also the test bed for consumer verification. On the published canary, run the readback against the live URLs and try the documented user verification path; record the gh version used. Measured here: gh 2.46.0 has none of `gh release verify`, `gh release verify-asset`, `gh attestation` (each exits 1), so if the release README is to tell users those commands, the canary is where the version floor gets established — and if it tells them `sha256sum` plus a REST listing, the canary proves that works too.

---

created: 2026-11-17T11:31:00Z

---

Canary evidence items, from the seventh review's disposition
(aa7ad23, fd76021). Record these from the live run rather than reasoning about them afterwards:
(i) what `.target_commitish` says on the created release — this is the only way to see what the omitted parameter
actually resolved to, and it is the item that replaces the rejected `target_commitish` hardening on TASK-44;
(ii) whether the workflow-write guard fires at all for a tag cut at the head of main — by the endpoint note it
should not, because the resolved target is the default-branch head and that diff is empty by construction, but
our own tag commits touch `.github/workflows/release.yml`, so the answer matters and only a run gives it;
(iii) whether `contents: write` from `GITHUB_TOKEN` suffices for asset upload and the un-draft `PATCH` in practice.

Ownership caution: the canary cannot verify the two repository settings on TASK-45 (immutable releases) and
TASK-38 (a `v*` ruleset) unless those are done **first**. A canary run against an unprotected repository proves
nothing about protection that is not switched on, so the order is 45 and 38, then 42.

---

created: 2026-11-17T13:56:00Z

---

Blocked on TASK-49 (a) and (b): the canary decision and whether the destructive tests run in a throwaway repository. Not Waiting — no event will wake this; a person has to grant it.

## TASK-45


# TASK-45: 2 superseded comment(s), removed from the board body
Nothing was deleted: this is the text that came out, and git history plus the cited commits and
SPEC 15 rows keep the record. Newest two kept in the task.

created: 2026-11-17T11:06:00Z

---

TASK-39's answer makes this a prerequisite rather than an option, and sharpens what to write in SPEC: immutability locks published assets and the tag while the release exists, and it does not lock drafts, the pre-publish window, the title or notes, or the prerelease/latest flags — and it will freeze a malicious set just as faithfully as a good one. Its attestation binds the GitHub-published release set, not the trustworthiness of the source, the approval, or the build execution.

---

created: 2026-11-17T11:31:00Z

---

Ownership, settled: the coding session reads this as an owner Decision because it is a
repository setting and nothing in YAML. It is not an owner act here: it is a REST call from a machine whose
working token already answers `admin: true` for this repository (measured today), so under decision-16 it is
agent work — `gh api -X PUT repos/ArborealML/Inference-Router/immutable-releases`, then the same GET to prove the
state moved. What makes it reportable rather than private: it changes a public repository's supply-chain posture,
it reverses with `DELETE`, and it must be visible in SPEC and in the commit message before any release is published.
The owner keeps the veto, not the keystrokes.

## TASK-46


# TASK-46: 1 superseded comment(s), removed from the board body
Nothing was deleted: this is the text that came out, and git history plus the cited commits and
SPEC 15 rows keep the record. Newest two kept in the task.

created: 2026-11-17T13:43:00Z

---

**The image-prep half ran as a subagent in a scratch directory (no repo writes); it is done, and this board session re-measured its numbers rather than repeating them.** Artifacts are kept untracked under `the board's untracked card workspace`: `air-social-candidate-A.svg` + `-1280x640.png` (recommended), candidate B (tagline copy cut) and candidate C (full re-composition) with their rasters, a 640x320 preview raster of A, the measurement and downscale scripts, and the child's full report (`CARD-REPORT.md`).

**Verified by this session, not inherited.** Rendering `an untracked card candidate` twice with the documented pipeline (resvg 2.6.2 via `@resvg/resvg-js`, `defaultFontFamily`/`sansSerifFamily` pinned to DejaVu Sans, `fitTo:{mode:'original'}`) gives the same bytes both times and equals the shipped PNG: 1280x640, 38,150 bytes, sha256 `cee3237b7f980d12259d6aad082a9473f12214115b36e0239d85c8147ec98f09`. Measured from pixels with the corrected measurer: text bands at rows 191-299, 332-380, 408-460 with cap heights **109 / 48 / 42 px** against AC #2's 40 px floor; ring ink 0 on all four sides at both the light threshold and the off-field threshold that also sees the `#2B806C` accent; rightmost ink at x=1200, so a 79 px right margin; 3/3 bands still carry ink at 640x320. The committed card, measured the same way in the same text column, gives caps **109 / 29 / 20 px** — the 20 px tagline is the defect AC #2 exists to kill, and A takes it to 42 px.

**What the coding session owes before committing any of this** (a provenance claim is only as good as the check someone ran): (1) re-render inside the repo from the committed SVG and require the raster to match the artifact's sha256 byte-for-byte, so the PNG in `docs/images/` is provably the render of the SVG next to it; (2) re-measure dimensions and cap heights after that render rather than trusting this comment; (3) update the `docs/images/README.md` row with the renderer, the font that actually resolved, the size, the bytes and the sha256, and keep the note that the raster's lettering is **DejaVu Sans**, not the `system-ui` stack the SVG asks for — the child's honest limit, worth repeating: DejaVu Sans measures a cap ratio of about 0.73 em, and a face with a smaller cap ratio would pull A's font-size-56 tagline back under the 40 px floor, so the floor is proven for this box's face only.

**The remaining choice is copy, not layout, and it is cheap to change now and expensive to change after upload.** A keeps all three lines verbatim and raises the tagline to font-size 56 — which puts it close to the 64 px name line, so the hierarchy rests on colour. B deletes the tagline copy entirely and gets bigger type (min cap 47) and a smaller file (29,005 B). C re-composes the card (mark centred on top, horizontal accent rule) with min cap 44 and the widest safety margins but the smallest mark. Which one ships is a taste call; A is the recommendation because it changes the least and loses no copy.

## TASK-49


# TASK-49: 1 superseded comment(s), removed from the board body
Nothing was deleted: this is the text that came out, and git history plus the cited commits and
SPEC 15 rows keep the record. Newest two kept in the task.

created: 2026-11-17T14:10:00Z

---

**Rescoped, because the first version asked the owner for an agent's decision.** It previously carried a third answer about the scope of the `v*` ruleset — who may create a `v*` tag. That is a repository setting reachable over REST, which decision-16 assigns to the agent, so it now lives as a criterion on TASK-38 with its reasoning, and TASK-38 chooses between one ruleset and two layered rulesets itself. A fourth item, about swapping the social-preview card, moved to TASK-46, where a re-render is a one-line instruction to an agent. What stays here is exactly the two things decision-16 and decision-14 leave with a person: runner spend beyond what decision-10 already allows, and creating a new public repository under the organisation.


## TASK-38 (second pass, 16:1xZ)
# TASK-38: 2 superseded comment(s), removed from the board body
Nothing was deleted: this is the text that came out, and git history plus the cited commits and SPEC 15 rows keep the record.
created: 2026-11-17T14:10:00Z

---

Ownership corrected: the ruleset-scope question was parked on the owner by mistake and is now this task's own criterion. The measurement that makes it non-trivial stands — a `creation` rule with `bypass_actors: []` blocks the publishing account as well as a person, so one ruleset either blocks the release path or protects nothing, and two layered rulesets are the shape that does both. The only part that stayed with the owner is the runner spend and the second repository (TASK-49).

---

created: 2026-11-17T15:01:00Z

---

**Waiting, not Blocked:** the ruleset POST is an agent act (decision-16). **Wake:** TASK-49's answer about the throwaway repository, which is where the ruleset gets exercised against the destructive cases.

---


## TASK-42 (second pass, 16:1xZ)
# TASK-42: 1 superseded comment(s), removed from the board body
Nothing was deleted: this is the text that came out, and git history plus the cited commits and SPEC 15 rows keep the record.
created: 2026-11-17T14:25:00Z

---

Status corrected to match the body: agent-run, no owner keystroke. **Wake:** TASK-44 closed, TASK-38's ruleset read back, TASK-45 done, CI green on the merge commit. Recommendation, stated rather than asked: run the proving tag **after** the throwaway-repository rehearsal if TASK-49 answers yes, and treat the canary as a kept prerelease, because a published version name cannot be reused.

---


## TASK-46 (second pass, 16:1xZ)
# TASK-46: 1 superseded comment(s), removed from the board body
Nothing was deleted: this is the text that came out, and git history plus the cited commits and SPEC 15 rows keep the record.
created: 2026-11-17T13:56:00Z

---

**Render and verification are done and committed: b214ce6.** The coding session replaced `docs/images/air-social.svg` in place and re-rendered; this session re-checked the claim rather than recording it: the committed PNG is 1280x640, 38,150 bytes, sha256 `cee3237b7f980d12259d6aad082a9473f12214115b36e0239d85c8147ec98f09`, and rendering the committed SVG twice reproduces those bytes exactly. Measured with the corrected measurer (the one whose ring check also sees the `#2B806C` accent): text cap heights **109 / 48 / 42 px** against AC #2's 40 px floor — the card it replaces measured 109 / 29 / 20 — ring ink 0 on all four sides at both thresholds, rightmost ink x=1200 (a 79 px margin), 3/3 bands still carrying ink at 640x320, light 4.8%. The provenance row and the cap-floor note are in [docs/images/README.md](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/README.md), including the honest limit that the raster's lettering is DejaVu Sans at a cap ratio of about 0.73 em, so the 40 px floor is proven for this machine's face and not for one a browser would pick.

AC #1, #2, #3 and #4 are checked on that evidence; **#5 stays open**, which is why the task is Waiting and not Done. **Wake: the owner pressing Save on TASK-29** — #5 is the public readback that only a live setting can answer. If the owner takes TASK-49 item (d) and asks for a different composition, this reopens for one render and one re-measure.

---


## TASK-29 (second pass 16:2xZ)
# TASK-29: 1 superseded comment(s), removed from the board body
Nothing was deleted: this is the text that came out, and git history plus the cited commits and SPEC 15 rows keep the record.
created: 2026-11-17T13:56:00Z

---

**Nothing in the repository is outstanding for this task, so it is Blocked on a person again, not Waiting.** The card is committed and measured (b214ce6: 1280x640, 38,150 B, sha256 `cee3237b7f98…98f09`; smallest text line 42 px, four times the previous card's 20 px). What is missing is the upload: repo Settings -> Options -> Social preview, pick the committed `docs/images/air-social-1280x640.png`, then **press Save** — the form shows a thumbnail either way, which is how three 'card set' answers have produced no change. One optional answer can still change the image first: TASK-49 item (d).

---


## TASK-28 (second pass 16:2xZ)
# TASK-28: 2 superseded comment(s), removed from the board body
Nothing was deleted: this is the text that came out, and git history plus the cited commits and SPEC 15 rows keep the record.
created: 2026-11-17T13:30:00Z

---

Sequencing trap, from the plan review dispositioned in SPEC v0.12.20 and re-measured by the board at 8f78a73 (0 tags, 0 releases, `[]` rulesets): a `v*` repository ruleset with a `creation` rule and empty `bypass_actors` stops **anyone** — an admin included — from pushing a `v*` tag, so it cannot land before the tag this task cuts unless an actor is listed. Either the ruleset's `bypass_actors` names the tag pusher (TASK-38 AC #2) or this tag is cut first and the ruleset follows. Enforcement has no observe-only setting: `enforcement` is `disabled`, `active`, or Enterprise-only `evaluate`.

---

created: 2026-11-17T15:01:00Z

---

**Waiting, not Blocked:** every assignee here is an agent, so per the revised rule a task that waits on another task sits in Waiting and names it. **Wake:** TASK-50's `go` (or the owner's `ratify decision-21: no`, which removes the gate), plus the `v*` ruleset read back on TASK-38 and TASK-45 done — the ruleset's `creation` rule must be settled before a `v*` tag can be pushed at all.

---


## TASK-30 (second pass 16:2xZ)
# TASK-30: 1 superseded comment(s), removed from the board body
Nothing was deleted: this is the text that came out, and git history plus the cited commits and SPEC 15 rows keep the record.
created: 2026-11-17T11:08:00Z

---

Numbers and policy are decided (TASK-34 answered; the external answer was adopted because it contradicts nothing measured here). Ship defaults: `stream_idle_timeout_ms = 300000` **per engine**, `0` disables, measured on **upstream byte arrival** — reset by upstream bytes, never by keepalives or downstream writes, and bytes in hand beat an expiry race. `stream_buffer_limit_bytes = 262144` **global**, counted in bytes rather than frames, because frames vary in size. On idle expiry: cancel upstream, release the lease exactly once in a `finally`, and record `upstream_cancel_requested` always and `upstream_stop_confirmed` only when established — otherwise the feed says `backend_execution_unknown`, because cancelling an HTTP request does not guarantee GPU compute stopped. At high water with no drain: cancel upstream, close or reset downstream, release the lease, emit `slow_consumer`. Caller-visible shape stays exactly as SPEC §6 has it: one in-band error frame plus `[DONE]` on a stream that is still writable, close without ceremony when it is not, no new truncation format. A caller abort stays `cancelled`, never a timeout. The 30 s pending-write no-progress watchdog lives inside the relay as a safety rule, not a config key.

**Do not land the defaults as a bare config PR.** Two gates come first, and this task is not Done without them: per-engine gap replay (measured gap histograms; ~10,000 representative successful reasoning/tool/batched streams with zero false expiries; injections at 299999 and 300001 ms; timeout-versus-byte races; an upstream that ignores cancellation) and a slow-reader soak (stopped reader, one-byte blocked flush, frames larger than the cap, HTTP/1.1 and HTTP/2, abort and slot saturation) showing bounded backlog, bounded RSS, expiry at high water, and no leaks.

**Measure the premise before writing it into SPEC.** This task's Description says the router buffers a stalled reader without limit. That was written from reasoning, not measurement, and Kestrel plus TCP may already apply backpressure; AC #2's number and SPEC's wording should say what the code actually does, with the test that shows it.

---


## TASK-11 (second pass 16:2xZ)
# TASK-11: 1 superseded comment(s), removed from the board body
Nothing was deleted: this is the text that came out, and git history plus the cited commits and SPEC 15 rows keep the record.
created: 2026-11-17T14:02:00Z

---

**Correction, and the board was wrong.** A comment here a moment ago claimed this task's wake had already happened and moved it to To Do. It has not, and the claim came from reading the board listing instead of the predecessor task. The predecessor TASK-21 records its three postures as **adopted**, but its remaining act is still open: writing them into SPEC as a revision with the revision-log line in the same commit — checked here, `SPEC.md` carries no operator-first posture text at b214ce6. **Wake, named:** that SPEC revision landing, after which TASK-21 closes and this starts.

---


## TASK-14 pattern-findings block (second pass 16:3xZ)
# TASK-14: 0 superseded block(s), removed from the board body
Nothing was deleted: this is the text that came out, and git history plus the cited commits and SPEC 15 rows keep the record.
**Pattern findings (15 public .NET/C# READMEs, external research request completed)**
The report's own stated limits: a purposive high-visibility sample, not a star-ranked top 15; no AIR
binary was executed and no artwork was rendered. Treat each item as a recommendation to check against
this repo, not as a measured fact.

Recurring structure, in the order it recurs:

First screen: name, purpose, badges, current scope, one runnable command.

Anti-patterns the survey calls out: marketing adjectives and feature walls; unqualified "OpenAI
compatible"; unverified speed numbers; quick-start commands that no longer work; machine-specific TOML,
keys or model ids; promises about unreleased assets; duplicating the spec, config schema, API contract or
CI config into the README; badge spam and stale tables of contents.

**Links**
- The file: [README.md](https://github.com/ArborealML/Inference-Router/blob/main/README.md) — banner [line 1](https://github.com/ArborealML/Inference-Router/blob/main/README.md#L1), badges [10-12](https://github.com/ArborealML/Inference-Router/blob/main/README.md#L10-L12), platform copy [82-84](https://github.com/ArborealML/Inference-Router/blob/main/README.md#L82-L84), CI section [214](https://github.com/ArborealML/Inference-Router/blob/main/README.md#L214).
- Release badge cannot resolve until this is non-empty: [releases](https://github.com/ArborealML/Inference-Router/releases).
- Art and provenance: [docs/images/](https://github.com/ArborealML/Inference-Router/tree/main/docs/images/), [docs/images/README.md](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/README.md).
- Numbers any claim must trace to: [docs/benchmarks.md](https://github.com/ArborealML/Inference-Router/blob/main/docs/benchmarks.md).


## TASK-21 (second pass 16:4xZ)
# TASK-21: 1 superseded comment(s), removed from the board body
Nothing was deleted: this is the text that came out, and git history plus the cited commits and SPEC 15 rows keep the record.
created: 2026-11-17T14:02:00Z

---

Wake named explicitly for the task that parks behind this one (TASK-11, Waiting): **the SPEC revision that writes the three adopted postures into SPEC §6, §10 and §12.5**, with its revision-log line in the same commit. Measured at b214ce6: `SPEC.md` has no operator-first or `/admin`-tunnel posture text, so the act is not done and TASK-11 must not start. The adoption itself is recorded in the comment above; AC #1 and #2 stay unchecked until the SPEC revision exists, because this task is graded on the record, not on the recommendation.

---

## TASK-2

# TASK-2: 11 superseded prose block(s), removed from the board body in c9a7dfc
Not comments: this is prose cut from the Description, Implementation Plan and Notes when TASK-2 came
inside the whole-body word budget (1,431 words -> 596). Nothing was rewritten out of existence; the text
below is what came out, verbatim. The task now carries the conclusions these paragraphs argued for, and
the code and SPEC rows carry the fixes.

**Status notes**
The replay slice's external review found six gaps, each verified against the code at that commit and all of them fixed in the next slice rather than left as notes: replay is snapshotted before the live subscription starts, so an event appended in that window reaches neither path, and the per-frame SSE flush widens the hole; replay caps at 1024 events while the ring holds 4096, so a reconnecting client can be silently missing a thousand events it is entitled to; the subscriber buffer drops with `DropWrite` and no counter, so a dropped terminal event can be invisible to a reader that is already filtering; a `since` cursor ahead of the ring makes the replay helper compute a negative count and throw after the SSE headers are already sent; the per-subscription channel is never completed and is not linked to shutdown, so a reader of an unknown ticket stays subscribed and the subscriber list is uncapped; and the tests open the feed only after requests finish, which proves replay isolation but never exercises the handover window. Two related items are decisions rather than fixes: a ticket-scoped reader cannot see the engine-switch events it is promised because switch events carry no ticket id, and the docs' promise that a gap in `Seq` always reveals loss is true only for an unfiltered feed. Both are recorded as acceptance gaps to settle with the fix.

Fix slice (event-feed correctness first, ticket failure paths second). Each item lands with a test.

An external review of the three board-only commits that recorded these findings is in flight; it re-checks the findings against the code and the priority order of the fix slice. An in-flight review does not change this task status. Nothing from it is adopted until verified against this code.

A second external pass over the fix slice found two more cursor holes, both now fixed: a `since` at the top of the number range overflowed checked arithmetic inside the replay helper and threw after the SSE headers were already flushed, and a cursor ahead of the ring (a reconnect after a restart) produced an empty replay and then filtered out every future event, so the feed was open but permanently silent — the cursor is now clamped to the ring head. It also found that a full feed was enforced by an exception thrown after the response had committed to SSE headers (fixed with a pre-flight 503 and a refusal that is a return value), and that the tests only ever exercised replay rather than the handover window: there is now a concurrent-producer ring test that asserts every sequence number arrives exactly once, and a feed test that subscribes before the flood. What it found that is NOT fixed stays open and is stated in SPEC rather than papered over: an ended feed says "reconnect", not "N events were lost"; an ended feed is indistinguishable from one that finished normally; a filtered reader cannot see that a terminal event was evicted from the ring; and the ring has no epoch, so a restart is not distinguishable from a quiet period. Those need a wire decision, and they are the shape of the remaining work.

Moved out of In Progress: the replay slice is merged, and the remaining ticket-failure-path work is gated by nothing external — it waits for attention, so it belongs in the queue with its plan attached rather than in the WIP column.

Prerequisite check on the two open criteria (board audit, no status change). AC #2 says it waits on the switch identity described as TASK-10 plan step 3 — immutable (target, epoch, completion) state that a completion can be validated against. TASK-10 is now Done, and the identity is not in the tree: the runtime holds one switch state per engine carrying model, profile and start time only, and nothing in the source names an epoch at all. SPEC 14.1 row 3 calls switch serialization and the error contract closed, and its "epoch" row (row 6) is the event-feed epoch awaiting the live-view decision on TASK-21 — a different thing. So the prerequisite AC #2 depends on is real, exists nowhere in the code, and is not owned by any open task. Flagging it, not deciding it: whether that step becomes a TASK-2 step or its own task is the code owner's call. Until someone owns it, AC #2 is not merely unstarted, it is un-buildable.

**Correction:** the note here previously claimed `GET /admin/tickets/{id}/events`,
`POST /admin/tickets/{id}/cancel` and a ticket-scoped replay test had landed. They had not: the surface
is `GET /admin/tickets/{id}` (read) and `DELETE /admin/tickets/{id}` (cancel), `RouterEventFilter` has
no ticket field, and `AgentSurfaceTests` pins the tool manifest and `X-Inference-Ticket`, not replay.
AC #1 is unchecked again. What is actually shipped: `X-Inference-Ticket` on every admitted response
(including handled failures), so a caller can reach the two ticket routes without scraping the stream.

AC #1 closed as shipped: `GET /admin/tickets/{id}/events?since=<seq>` plus `ticket=` on
`/admin/events`, both filtering replay **and** live tail. `EventSurfaceTests` proves isolation with two
concurrent tickets (a frame carrying the other ticket id fails the test) and pins the admin feed's
PascalCase field names. Fixing this surfaced two defects in the library's own subscriber:
`SubscribeEvents` replayed the ring unfiltered, and its single-slot waiter dropped any event that
arrived while the consumer was busy; subscribers now have a bounded 256-event buffer (drops are visible
as `Seq` gaps) and SSE headers flush before the first frame, so an unknown ticket is a quiet 200.

Still open: #2 needs the switch identity (`(target, epoch, completion)`, TASK-10 plan step 3);
`/v1/models` still lacks AIR model metadata; the `POST /admin/tickets/{id}/cancel` alias remains
SPEC §13.3 work. #3 is partly met — wire tests cover the replay additions and `ServerWireTests` keeps
the OpenAI-compatible surface pinned — and stays open for additions that do not exist yet.

- #2 needs the switch identity the validation is checked against — that is TASK-10 plan step 3
  (immutable `(target, epoch, completion)` switch state). Validating against today's mutable
  per-target flag would pass the test and still mis-attribute a switch.
- `/v1/models` does not yet carry AIR model metadata (context window, slots, availability), and the
  Pi-side half (session extension) is a different repo entirely.

**Links**
- Surface still owed: [SPEC §13.3](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L1157) — the delta list is the spec of this task.
- Admin routes as mapped today: [ServerApp.cs:51-57](https://github.com/ArborealML/Inference-Router/blob/main/src/Arboreal.InferenceRouter.Server/ServerApp.cs#L51-L57) — read, cancel, and both event feeds.
- Tests to extend: [EventSurfaceTests.cs](https://github.com/ArborealML/Inference-Router/blob/main/tests/Arboreal.InferenceRouter.Tests/EventSurfaceTests.cs) for ticket filtering, [ServerWireTests.cs](https://github.com/ArborealML/Inference-Router/blob/main/tests/Arboreal.InferenceRouter.Tests/ServerWireTests.cs) for the OpenAI-compatible surface.
- Protocol write-up to keep honest: [docs/ai-integration.md](https://github.com/ArborealML/Inference-Router/blob/main/docs/ai-integration.md).

## TASK-6

# TASK-6: 8 superseded prose block(s), removed from the board body in 6e98830
Prose cut from Implementation Notes when the task's Notes were reduced to current state plus the standing
tool limitation. The blocked/unblocked history below is the record of which predecessor moved when; the
tool limit that survives is in the task.

Blocked on TASK-19 — the next move is the map-model decision, which belongs to a human. Work here resumes when that task is closed.

Two things gate this: the map-model decision, and the lifecycle work it is meant to pin (TASK-1), which is now unblocked and mine to start.

The lifecycle work that was gating this has started, so the map will need pinning to the engine components once the provider seam settles.

The lifecycle provider seam and the Docker provider are in main, so the map can pin the new lifecycle components once the map-model decision lands. Still blocked on TASK-19.

Unblocked: the map-model decision is made and the library is a container (TASK-19). What is left here is the pinning the original task asked for — the new lifecycle seam (provider interface, coordinator, delegated-exec and Docker providers) now has components, and the map should say which files each owns; the duplicate air-cli / cli containers the earlier scan produced are also gone.

Map state at 18d7279 (checked by exporting the map and reading its zones): no unidentified group remains; every component has a container parent, all 20 src files are claimed by a component, and the only zones are the four groups under the library container. The residual is that the library container is a permanent ghost: groma matches a declared container to a scan only when the scanner produces a container fact, and the C# scanner produces those only from application entry points, so a library never gets one and 'groma accept' prints 'no scan match'. The box will render outlined for as long as the library is one container. Pinning work to components (this task) is unaffected.

Status check for the board: this task is not blocked. TASK-19's map-model decision landed and the library is one declared container (sixteen components under it, verified by counting the map tree), so the pinning named here is ordinary work now. It is also unaffected by TASK-27, the follow-on owner decision about that container rendering as an outline rather than a solid box: keeping the outline changes nothing here, and only a decision to split the library per executable would re-open this task's shape.

Decision needed: what is a container here. The C4 guidance treats a shared assembly as *not* a container,
which argues for keeping the shared components unfiled until we choose a simplification, or for filing
them under the process that actually hosts them — noting that `air serve` runs the HTTP host inside the
CLI process and `air config` loads configuration locally, so the CLI is not merely a remote client of the
library and the map cannot show one source file owned by both executables.

## Duplicate archived titles

[backlog/archive/tasks/task-31 - Decision-cut-the-v0.1.0-tag.md](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/tasks/task-31%20-%20Decision-cut-the-v0.1.0-tag.md)
and
[backlog/archive/tasks/task-32 - Decision-cut-the-v0.1.0-tag.md](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/tasks/task-32%20-%20Decision-cut-the-v0.1.0-tag.md)
are the **same decision filed twice**, created five minutes apart with identical titles. TASK-32 is the
duplicate that was kept as the live record (it carried the acceptance criterion and the board dependency)
and TASK-31 was archived beside it so the id and its inbound links survive. The question both asked —
whether to cut `v0.1.0` — is closed by [TASK-50](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-50%20-%20Decision-approve-the-first-public-version-number-v0.1.0-%E2%80%94-one-line.md)
and neither archived id is a live dependency. A reader who follows either link is reading superseded
history, not the current gate.

## TASK-44

# TASK-44: 1 superseded prose block, retired from Implementation Notes on 2026-11-17
The stderr census behind a criterion that retired to SPEC §15 with the v0.12.20 round. It is not open work: the
task keeps a pointer here, and the command below re-derives every number in it from the workflow on `main`.

**The census behind the stderr criterion, retired into SPEC 15 with the v0.12.20 round** (re-run: `git show main:.github/workflows/release.yml | grep -c '2>/dev/null'`): 9 sites discard stderr, 6 jq shape guards discard stdout only, 1 discards both, 1 API read captures stderr to a temp file removed by an EXIT trap and reports only the exit code plus status-like numbers, 1 merge is compiler output (`dotnet publish … 2>&1 | tee aot-<rid>.log`). jq prints a value only for typed operations, truncated to ten characters, and every typed operation in those six guards sits behind a `type` guard — pinned by `shape_jq_stderr`.

### TASK-38 (superseded payload wording, 18:2xZ)

The plan review's third correction as it was first written, before the act measured the shape and narrowed it. It is kept
because it explains why the board sequenced the first tag against the ruleset at all.

> `creation`, `update` and `deletion` are real rule types, and `bypass_actors: []` means **nobody** — not an admin, not the
> Actions app — may create, move or delete a `v*` tag, including whoever pushes the next release tag: the actor list is
> decided before the POST and TASK-28 is sequenced against it.

And the sentence the task's Notes no longer carries: "the release path publishes whatever the tag points at, under a
version number people trust."

### TASK-42 (superseded narrative, 19:2xZ)

**Order, as performed.** Ruleset and immutability first: a canary against an unprotected repository proves nothing about
protection. The live ruleset protects `update` and `deletion` and omits `creation`, so a new `v*` tag pushes fine —
measured twice; whether a `creation` rule with `bypass_actors: []` would have blocked the pusher is untested.

**Canary 1 is evidence the gate works, not that it failed.** Both legs built and smoke-tested, then publish refused: the
gate byte-compared a checksum record whole, and Windows writes it in binary mode where GNU coreutils writes text — equal
digests, unequal lines. Fixed at 9910c0f (v0.12.23), harness to 132 cases, row in
[SPEC.md](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md) §15.

**Proves only what the tagged commit's workflow can do**; the floor rule is on TASK-28 and the gh version floor (no `gh
release verify` or `gh attestation` in 2.46.0) on TASK-48.

### TASK-42 (superseded narrative, 19:3xZ)

**Order, as performed, and canary 1's near-miss: superseded narrative, kept in
[board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).**
Ruleset and immutability came first; canary 1 built both legs and was refused at publish by the gate's own checksum
comparison, fixed at 9910c0f (v0.12.23), 132 harness cases.

### TASK-42 (trimmed from the task body 19:4xZ)

**Immutability protects less than this task assumed — measured in the laboratory repository at 19:2xZ.** On a published
immutable release an asset was deleted (`204`) and a **different file uploaded under the same name** (`201`); the download
URL serves the new bytes and the old asset is gone. `PATCH` on the release and on the asset answer 200. With no ruleset,
`git push -f` **moved the tag of a published immutable release**. What the flag does hold: deleting the release leaves the
tag name burned (`422 tag_name was used by an immutable release`), and implicit tag creation is refused where creation is
restricted. So the tag ref's only measured protection is the ruleset (`update` + `deletion`), and asset integrity rests on
the gate's digest records plus the post-publish readback — TASK-48's job, now load-bearing rather than optional.
**The tag-identity guard is already in the gate, measured in the open** (publish re-resolves the tag and refuses unless
its commit equals what the legs built, release.yml:500-510; checkout pins the same commit at release.yml:191). Owed still:
the **post**-publication peel re-check, and a SPEC line that `target_commitish` is not a pin — the canary answers `main`
there while the peeled tag is 9910c0f.
