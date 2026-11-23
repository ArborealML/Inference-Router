---
id: TASK-176
title: Show the selected AIR model’s supported thinking levels in Pi
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-23T07:27:00Z'
updated_date: '2026-11-23T22:01:00Z'
labels: []
milestone: m-7
dependencies: []
references:
  - tools/pi-air/catalog.mjs
ordinal: 170000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Expose distinct reasoning choices for each of the ten configured model IDs. Preserve Pi model switching and send the selected effort to the serving template.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The picker lists the supported modes for every configured model.
- [x] #2 Switching between Qwen 27B and a high-capable model maps high to xhigh and back. Requests carry the displayed effort.
- [x] #3 The verified change lands on main and passes hosted CI.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Merged in [PR 82](https://github.com/ArborealML/Inference-Router/pull/82) with green hosted checks. Pi RPC verified all ten lists, highest effort on the wire and nearest-level switching in both directions.
<!-- SECTION:NOTES:END -->
