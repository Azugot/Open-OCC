# Verification history

## Autonomous storyteller — September 29, 2026

- **72 passing .NET tests**: the original 12 foundation tests, 39 provider/configuration/transport cases, and 21 engine cases. Engine coverage includes perception isolation, privately moved/introduced entities, ordered reactions, NPC/beat limits, off-screen time advancement, d20 totals including natural 1/20, persisted rolls/steps across retries, invalid outcome regeneration, handoff after movement, cancellation, startup recovery, stale drafts, graph/fork evidence and memory ownership/author corrections.
- **TypeScript/Vite production build passed**. No new frontend dependency was required.
- **Migration consistency passed**. The new PostgreSQL migration applied on an isolated database; previous rows receive `{}` defaults for world/draft data and use the legacy-state fallback.
- **Real HTTP/PostgreSQL storyteller smoke passed**: graph history, cross-branch checkpoint rejection, memory edit/pin/removal, two independently configured role profiles, compatible HTTP generation, saved-step/dice retry, explicit cancellation and concurrent turn reservation. Its temporary loopback provider returns protocol fixtures, so it verifies transport/orchestration rather than actual inference.
- **Foundation HTTP smoke and restart persistence passed**: checkpoints, messages, state, original source checksum and bytes survived backend restart.
- **Protected access/client cancellation smoke passed**: missing token/custom write header rejection, aborted-request cancellation and unchanged preceding checkpoint.
- **Browser checks passed** for creating Crownspire, watching Live activity during generation, spoiler-gated character/dice panels, a saved memory correction, graph entity selection and evidence. The default compact viewport was inspected and checkbox layout corrected. Screenshot proof is saved as a task artifact.
- **Compose validation and whitespace checks passed**. NuGet vulnerability-feed retrieval emitted NU1900 in this network-restricted environment; package compilation/tests completed successfully.

The run used a disposable PostgreSQL 17 container, local .NET backend and Vite frontend. Existing application stacks and the user's transcript were left untouched. Temporary test services were stopped afterward.

To reproduce the engine smoke, run a disposable backend with `CHARACTER_BASE_URL` and `DIRECTOR_BASE_URL` set to `http://127.0.0.1:11449/v1`, both role API keys set to the local test placeholder `local-smoke-token`, then run:

```powershell
node tests/engine-smoke.mjs http://127.0.0.1:5080
```

The script owns a temporary protocol server on port 11449 and adds synthetic campaigns. Set `APP_ACCESS_TOKEN` in the test terminal when the backend is protected. Actual model inference/quality, token streaming, reusable worlds, full progression and automatic transcript reconstruction remain unverified or deferred. Configure and evaluate chosen live models separately.

## Original foundation — September 11, 2026

Verified locally on September 11, 2026 with .NET SDK 10.0.400, Node 26.7.0, Docker Desktop and Docker Compose 5.1.4. Container builds use .NET 10, Node 24, Nginx and PostgreSQL 17.

## Completed checks

- **12 passing .NET tests**, including relational persistence across DbContext lifetimes; checkpoint branching; exclusion of future turns/facts; stale-turn rejection; resumable 25-passage import batches; byte preservation; fact/evidence counts; review validation; public/private filtering; provider stub contracts; fixture cancellation; cancellation winning against an in-flight import transaction; and rejection of approval after a branch advances.
- **TypeScript and Vite production builds passed**, both locally and in the frontend image.
- **EF model consistency passed**: no pending model changes after generating the checked-in initial migration.
- **Fresh Compose build/start passed** with frontend, backend and PostgreSQL. The backend applied the migration and became healthy.
- **HTTP/PostgreSQL smoke passed**: campaign creation/listing, simulated turn, stale write conflict, fork history, original-source upload/checksum/download, background processing, evidence review, invalid-state rejection, atomic approval, fact isolation, provider capability profiles.
- **Restart persistence passed**: restarting backend and PostgreSQL preserved the exact checkpoint ID, message count, state, and source checksum.
- **Backup/restore passed**: `pg_dump -Fc`, copy, and `pg_restore` into a fresh separate Compose database; restored application returned the same checkpoint ID, messages, state and source bytes.
- **Access and cancellation passed**: the protected test stack rejected missing access tokens (401), rejected writes without the custom same-origin header (403), recorded client cancellation, and retained the preceding checkpoint/messages/state.
- **Browser verification passed** for library loading, creating a synthetic campaign, submitting a simulated action, viewing its state panel, and forking the starting checkpoint with later history excluded. Desktop layout was visually inspected.
- **Dependency remediation**: Vite updated to 7.3.6; the test-only SQLite bundle updated to 3.0.5. The subsequent npm install audit reported zero vulnerabilities and the .NET restore completed without the previous SQLite advisory.

The first browser pass exposed a campaign-list EF projection issue; it was fixed by materializing the nested branch list, and the HTTP smoke script now covers the listing endpoint.

## Reproduce

```powershell
dotnet test tests/Story.Tests.csproj
npm --prefix frontend ci
npm --prefix frontend run build
dotnet tool restore
dotnet ef migrations has-pending-model-changes --project backend
docker compose --env-file .env.example -p open-occ-verify up --build -d
node tests/smoke.mjs http://localhost:8080
docker compose --env-file .env.example -p open-occ-verify restart backend postgres
# Wait for the backend health check after restart.
node tests/smoke.mjs http://localhost:8080 --check-saved
```

The smoke test adds synthetic campaigns to the local verification database. Its reference checkpoint and checksum are saved in ignored `.local/smoke-result.json`. Run the same `--check-saved` command against a restored instance to compare its data. Follow `backup-restore.md`, keeping the environment file and Compose project options consistent.

For a protected, isolated test stack, set `APP_ACCESS_TOKEN` to that stack's token in the terminal and run:

```powershell
node tests/access-and-cancellation.mjs http://localhost:8081
```

This test creates a synthetic campaign and aborts a fixture turn. It requires a stack with token authentication enabled.

## Local test environment

The interactive verification app uses Compose project `open-occ-verify` and loopback port 8080. It contains only synthetic test campaigns. To stop it without removing data:

```powershell
docker compose --env-file .env.example -p open-occ-verify stop
```

The separate restore-check project can likewise be stopped with `-p open-occ-restore-check`. Volumes and the ignored `.local/open-occ-verify.dump` retain the synthetic verification data. Neither stack imports the user's Word transcript or makes remote AI requests.

## Original foundation scope limits

The September 11 checks validated the foundation only. See the September 29 results above for the implemented storyteller capabilities and current limits. SQLite tests complement real PostgreSQL smoke; they do not claim to reproduce every PostgreSQL concurrency behavior.
