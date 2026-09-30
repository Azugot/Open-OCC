# Verification

## September 29, 2026 model discovery

- **112 .NET tests pass** with discovery, authenticated model-list requests, cache/refresh, modality filtering, invalid/oversized catalogs, cancellation, distinct task models, preserved connector defaults and captured imports. Lemonade agents use instruction-constrained JSON without unsupported JSON mode.
- **Frontend production build and EF model consistency pass.** No database migration is required: task choices use existing settings and character/director model choices use checkpoint world settings.
- **Disposable PostgreSQL HTTP smoke passes** via `tests/model-discovery-smoke.mjs`: automatic Lemonade/DeepSeek discovery, cache/refresh, unchanged profile defaults, task model persistence, saved selections after catalog changes, and separate character/director models through one Lemonade connector. The local transport provider uses scripted responses, not actual inference.

## September 29, 2026 combined storyteller merge

- **102 .NET tests pass**, retaining provider/continuity and transcript reconstruction coverage alongside autonomous engine tests. Four merge regression tests verify graph/memory preservation across mechanics and canon corrections, narrator-only scene transitions, portable paused dice drafts, and imported fact ownership.
- **Frontend production build and EF pending-model check pass.** The autonomous JSON turn endpoint and explicit narrator-only streaming endpoint coexist; vendor and character/director profiles remain available.
- **PostgreSQL storyteller HTTP smoke passes** on a disposable database: graph history, memory correction/pinning/removal, perception boundaries, HTTP agent adapters, preserved steps/dice across retry, cancellation, and concurrent turn reservation.
- **Combined foundation and import HTTP smoke pass**: both turn protocols, stale writes, nine provider profiles, original source preservation, 453-passage reconstruction, saved review edits, approval, portable restore, malformed output pause and explicit retry.

HTTP agent/reconstruction providers in this validation are local scripted protocol fixtures. No actual model inference or paid provider calls are needed for merge validation. Main database backup precedes the Docker rebuild; existing data and environment configuration are preserved.

## September 29, 2026 import rework

- **35 .NET tests pass**, including 450-paragraph DOCX reconstruction, versioned consolidation and evidence, state/entity/secret approval, invalid JSON pause, restart/portable draft recovery, missing-scene rejection, stale revision and lease/cancellation conflicts.
- **Fresh PostgreSQL migration and HTTP smoke pass** on isolated Compose project `open-occ-import-verify`, port 8082. A 453-passage synthetic transcript becomes 10 proposals; approval preserves the ending, characters, relationships, knowledge, inventory and source history. Saved edits, stale-edit rejection, export/restore, exact source checksum, invalid-model pause and explicit model-switch retry are checked.
- **Frontend production/container builds and EF pending-model check pass.** The foundation HTTP smoke also passes, including fixture preservation without fallback facts or empty checkpoint approval.
- **Desktop browser verification passes** for category filtering, saved character edits, citation evidence and resume checkpoint saving. Source history stays collapsed in the main chat. Screenshot: ignored `.local/import-review.jpg`.
- Reconstruction responses in these tests are **scripted/recorded, not actual AI inference**. This verifies the pipeline and contracts, not reconstruction quality on the user's Story transcript. That transcript and real Lemonade reconstruction remain deferred.

To reproduce the long PostgreSQL check, start `node tests/import-model.mjs` in one terminal, then run in another:

```powershell
docker compose --env-file .env.example -p open-occ-import-verify -f docker-compose.yml -f tests/import-compose.yml up --build -d
node tests/import-smoke.mjs http://localhost:8082
node tests/smoke.mjs http://localhost:8082
docker compose --env-file .env.example -p open-occ-import-verify -f docker-compose.yml -f tests/import-compose.yml stop
```

This isolated stack uses only synthetic data and an explicitly enabled synthetic DeepSeek-compatible profile. No paid provider calls are made. Stop the synthetic model terminal afterward; the stopped test volume remains recoverable.

## September 12, 2026 provider/continuity update

- **27 .NET tests pass.** Added recorded wire-contract coverage for OpenAI Responses SSE/structured output, Anthropic Messages SSE/structured output, OpenAI-compatible Chat Completions SSE/JSON mode, Lemonade Chat Completions without undocumented JSON mode, and Ollama NDJSON/schema output. Continuity, DOCX/HTML/Markdown parsing, correction auditing, retrieval, portability, world/version linking, deterministic mechanics, and evidence-linked reconstruction are covered.
- Live Lemonade verification completed: a one-passage import reached `review` with one evidence-linked fallback candidate when the local model could not produce valid structured JSON; the job remained resumable and no authoritative state was changed.
- Backend builds with zero warnings/errors; the EF pending-model check passes after the world/mechanics/event and import-lease migrations; the frontend TypeScript/Vite production build passes; Compose configuration validation passes.
- The HTTP smoke script validates the NDJSON turn stream, all seven implemented adapter capabilities, stale-write rejection, import review, and evidence isolation. It temporarily routes the disposable smoke campaign through the fixture and restores the original provider routing afterward.
- Live PostgreSQL verification created a versioned world, linked a campaign to its version, applied deterministic inventory mechanics, and confirmed the branch response exposes the event ledger and new world model.

Verified locally on September 11, 2026 with .NET SDK 10.0.400, Node 26.7.0, Docker Desktop and Docker Compose 5.1.4. Container builds use .NET 10, Node 24, Nginx and PostgreSQL 17.

## Earlier foundation checks (September 11)

- The then-current **12-test foundation suite passed**, including relational persistence, checkpoint branching, stale-turn rejection, resumable deterministic imports, review validation, fixture cancellation, and cancellation/approval races. The current 26-test result is recorded above.
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

## Scope limits

Story deletion and the connected import reader are covered by 125 backend tests, including transaction rollback/isolation, active-work conflicts, growing context budgets, complete citation paging, literal-newline JSON repair, and portable review plans. The frontend production build and confirmation modal's focus trap, Escape cancellation, and focus restoration were checked.

`node tests/import-deletion-smoke.mjs http://127.0.0.1:5080` runs only against a disposable PostgreSQL backend with `LEMONADE_BASE_URL=http://127.0.0.1:11450/v1`. It supplies scripted HTTP responses and checks per-import model selection, original DOCX bytes, full processing/approval and complete deletion without affecting another story. It does not test model interpretation of the source or make paid requests.

The live provider protocols are verified against recorded contract responses; real paid-provider connectivity remains opt-in through the settings page and is not part of the deterministic test stack. Embedding/vector retrieval and mobile browser testing remain outside this foundation. Portable campaign export/reimport is covered by SQLite round-trip tests and a live PostgreSQL endpoint check. SQLite tests complement the PostgreSQL smoke; they do not claim to reproduce every PostgreSQL concurrency behavior.
