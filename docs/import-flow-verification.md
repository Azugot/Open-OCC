# Import flow verification — 2026-10-05

## Automated checks

- Full .NET suite: **180 passed**, zero failures or skipped tests.
- Frontend TypeScript and Vite production builds passed locally and in Docker.
- `tests/review-draft.mjs`: local edits retained, unrelated remote changes merged, overlapping edits flagged, arrays and removed fields covered.
- `tests/import-reliability-smoke.mjs`: malformed JSON (syntax, missing fields, wrong types), invalid DOCX, exact preserved source bytes, preview with no operational provider, atomic new timeline, fixture rejection, invalid portable exports, and all 45 legacy candidates through three pages.
- `tests/import-smoke.mjs`: 34 grouped source blocks consolidated into ten proposals, saved corrections, stale-revision rejection, PostgreSQL approval, clean story transcript, portable restore, original download, malformed-model pause and retry.
- Unit regressions cover all 180 proposal citations through nine evidence pages, foreign-job proposal rejection, versions 1–5, preparing-job restoration, unchanged-checkpoint/run blockers, player identity, structural corrections, and fresh fork reanalysis.

## Browser checks

The isolated stack at `localhost:8082` used synthetic sources and a recorded model. No live model calls were made.

- Library **Bring your story** created an empty campaign and opened import setup.
- Selected **New timeline**, preserved the file, inspected source preview, chose the recorded model, and started reconstruction.
- Reviewed final-scene inventory and skill rows, saved edits, and reloaded the same import from its URL.
- A second browser tab saved an inventory proposal with scene synchronization while the first tab had an unsaved condition edit. The first tab adopted the new quantity without erasing the entered condition. Approval preserved both in the resulting story.
- Save/Discard/Stay protection worked for guided view changes and legacy page changes. Explicitly saved legacy corrections survived reload and page navigation.
- Desktop review spacing, steps, tabs, readable statuses, and explicit approval were inspected. Approved imports no longer show checkpoint advancement as unfinished review work.

The browser viewport override did not change the actual 1280×720 viewport, even after reload and opening another tab. Mobile CSS is implemented and builds, but a true narrow-viewport visual check remains unverified.

The scripted model proves request/response and persistence behavior. It does not establish live-model interpretation quality. The NuGet vulnerability metadata feed was unavailable locally; this did not prevent compilation or test execution.

## Follow-up: entity validation repairs

- Full .NET suite: **188 passed**, including entity schema requirements, precise claim-index/key feedback, both missing entity fields, bounded rejected-result feedback at a 12,000-character limit, unsupported-kind rejection, and accurate accounting when no repair request fits.
- The schema and repair-feedback regression cases failed against the previous implementation before the fix.
- `tests/entity-repair-smoke.mjs` passed against isolated PostgreSQL and the recorded HTTP model. The first extract response deliberately omitted the entity name and kind; the repair request included both field errors and the rejected entity. The corrected draft reached review with complete coverage and preserved original bytes.
- The existing long-transcript `tests/import-smoke.mjs` also passed, including saved edits, stale-revision rejection, approval, portable restore, original source download, and pause/retry.
- No live model calls were made. Lower real-model failure frequency remains to be observed on subsequent imports; strict evidence and state validation remain in force.

## Follow-up: state dictionary type repairs

- Full .NET suite: **198 passed**. Regressions cover bounded exact-field feedback, object/numeric/null/array/boolean skill values, and invalid inventory types and ranges. The initial skill regression cases failed before the fix.
- `tests/skill-repair-smoke.mjs` passed against isolated PostgreSQL and the recorded HTTP model. The first extract response returned a rank/progress object for Magic Talent; the repair received the exact field, expected string shape, received object type, and rejected value. The complete draft reached review with the evidence-supported string, approval readiness, and unchanged source bytes.
- The entity repair HTTP regression and long-transcript import smoke also passed, including saved edits, stale-revision rejection, approval, portable restore, source preservation, and pause/retry. No live model calls were made; validation remains strict and unsupported values are never coerced or partially committed.
