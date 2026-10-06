# Processing indicators

Story and Live world show the same operational activity from submission through the saved-story refresh: a fixed stage label, an English phrase changing every eight seconds, and elapsed time. The catalogue contains 128 phrases in 16 groups. Narrator mode never uses cast or Director groups. Ordinary loading/saving uses direct copy.

The story spinner is indeterminate: no percentage or completion estimate is invented. Import reconstruction retains its existing counts, stage-weighted progress and timing-based completion range, and now adds stage-specific phrases, including response repair and saved-work recovery. Finishing source reading does not hide the remaining reconciliation, final-scene and evidence work.

## API and persistence

`GenerationRuns.ProgressJson` is optional operational metadata, defaulting to `{}`. It stores mode, phase, overall start, phase start and last update. It does not modify narrative state. Stages are saved before waiting for model calls; completion records are never used to infer the current call.

`GET /api/branches/{id}/generation-progress` returns either `null` or `{runId, status, mode, phase, startedAt, phaseStartedAt, updatedAt, asOf}`. Optional `runId` selects a run on that branch; `expectedCheckpointId` selects the latest run begun at that branch checkpoint. Foreign run/checkpoint selections return 404. Without a selector, only running/paused work at the current head is returned. No actors, private reasoning, hidden activity counts or draft data are returned.

Autonomous phases: `preparing`, `resuming`, `director-planning`, `cast-deciding`, `director-assessing`, `director-resolving`, `narrating`, `saving`. There can be multiple rounds of character assessment and resolution. Narrator phases: `preparing`, `narrating`, `checking-continuity`, `saving`.

Narrator NDJSON adds `{type:"progress", progress:{...}}` alongside the existing `start`, `delta`, `done`, and `error` events. Clients should ignore unrecognized event types. The interface also polls progress once per second after the previous request completes, aborts stale requests and rejects older updates. A newly submitted retry ignores the previous stopped status while reservation starts. The indicator remains visible during streaming and subsequent continuity analysis. Story/timeline and Live world URLs restore the selected branch after reload; active work is read from persisted runs.

Paused, failed, cancelled and interrupted runs are not animated. Paused Autonomous work retains its existing retry/cancel controls; retries reset the attempt clock and reuse validated steps. Stage changes are announced politely to screen readers; phrase/timer ticks are not live announcements. Spinner/pulse animations respect reduced motion.

Portable export remains version 5. Missing metadata uses generic preparation copy, and restored active work keeps the existing paused/interrupted restoration rules.

## Recorded verification

```powershell
dotnet test tests/Story.Tests.csproj
npm --prefix frontend run build
node tests/activity-messages.mjs
node tests/activity-components.mjs
dotnet ef migrations has-pending-model-changes --project backend
docker compose -p occ-progress-review -f docker-compose.yml -f tests/import-compose.yml -f tests/progress-compose.yml up -d --build
node tests/generation-progress-smoke.mjs http://localhost:8082
node tests/import-smoke.mjs http://localhost:8082
docker compose -p occ-progress-review -f docker-compose.yml -f tests/import-compose.yml -f tests/progress-compose.yml down
```

The separate stack uses a separate PostgreSQL volume and synthetic local HTTP fixtures. No real model calls are required. Its fixture control endpoint is local-only on port 23307 and exists only in the test override. Unit tests cover persistence before provider calls, clocks, pause/retry, cancellation, restart recovery, branch scoping/privacy, the migration and optional version-5 round trips. The catalogue script covers all 128 messages, rotation without adjacent repeats, Narrator filtering and unknown-stage fallback. Server-rendered component checks cover stopped states, labels, elapsed time and live regions; they do not establish browser timer behavior. The HTTP smoke is designed to observe actual in-flight stages, streamed progress before/after text, cancellation, failed narration, and saved checkpoints.
