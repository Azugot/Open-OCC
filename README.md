# Open OCC

A private story workspace built with React/TypeScript/Vite, ASP.NET Core 10, EF Core, and PostgreSQL.

The autonomous storyteller adds independent character sessions, a master director, an ordered Live room, server-generated d20 checks, historical world graphs, and editable short/long memories. Open **Live world** to inspect events, graph evidence, and spoiler-gated character state. Player turns use this engine by default; the optional narrator-only mode preserves streamed narration and continuity extraction.

Lemonade and DeepSeek model lists are detected automatically in provider settings. You can select different models for each task or for character/director roles while sharing one connector. Manual IDs and saved choices remain available when discovery is unavailable.

Story cards include **Delete story** with a confirmation modal. Deletion removes all of that story's timelines, imports, graph history and memories together; reusable world definitions and other stories remain available. Active generation/import processing must finish or be cancelled first.

Imports read connected sections with nearby context and a running summary, while preserving individual evidence links. Choose a model for each import, resume a saved stage, or start a fresh reconstruction from the unchanged original. Validation errors report the failing stage and reason. See [import and review](docs/imports.md).

## Start with Docker

Requires Docker Desktop (Linux containers) or Docker Engine with Compose. From the repository root:

```powershell
Copy-Item .env.example .env
# Edit .env and choose a strong POSTGRES_PASSWORD.
docker compose up
```

On macOS/Linux, use `cp .env.example .env` for the first step. Then open **http://localhost:8080**. The first run builds the images; subsequent runs reuse them. After source changes use `docker compose up --build`.

The backend applies the checked-in EF migration before becoming healthy. PostgreSQL is internal to Compose. The app listens on `127.0.0.1` only; `APP_PORT` changes the host port. Data survives `docker compose down` and container recreation through the `postgres-data` volume. Do not use `down -v` unless you intend to delete the database.

No provider key is needed for fixture mode. Live OpenAI, Anthropic, Lemonade, Ollama, DeepSeek, and Kimi-compatible profiles support streaming narration; Lemonade’s JSON tasks are validated locally because it does not document OpenAI JSON mode. Configure credentials server-side, enable the profile, test it, then route narration, reconstruction, and memory independently in Settings. There is never a silent fallback.

## Try the first milestone

1. Create a campaign using the synthetic **Crownspire Academy** starting point.
2. Open it, inspect the state panel, and submit an action. The reply streams to the page; the messages, validated state, facts, story threads, and checkpoint commit atomically after completion.
3. Open **Timelines**, choose an earlier checkpoint, and create another branch. Its history stops at that checkpoint; the original continuation remains available.
4. Create an empty campaign, open **Import & review**, and upload `tests/fixtures/crownspire.json`.
5. Fixture reconstruction preserves the source and pauses for a configured model. With a live reconstruction model, inspect the consolidated proposal after chronological analysis, reconciliation, resume reconstruction, and agent review. Resolve exceptions and confirm the final scene before approval.
6. Download the original from the review page; its bytes and SHA-256 remain unchanged.
7. Restart the containers and reopen the campaign to confirm its checkpoint remains available.

The original research, specification, handoff, ZIP, and Word transcript remain in the root. The importer accepts UTF-8 text/Markdown, HTML, DOCX, and documented JSON; the supplied Word transcript can now be uploaded directly.

## What works and what remains

| Area | Foundation behavior |
| --- | --- |
| Campaigns | Create empty/synthetic campaigns, list and reopen them |
| Turns | NDJSON streaming; provider/model/usage/context records; atomic message/state/fact/thread/checkpoint commit; cancellation and interrupted-run records |
| Branches | Fork any checkpoint, copying only earlier messages, state and accepted facts with their evidence |
| Imports | Original-byte preservation; staged chronological agent reconstruction with separate drafts, reconciliation, final-scene reconstruction and audit; pause/retry/restart recovery |
| Review | Exceptions first, searchable category pages, saved edits/exclusions, evidence drawer and editable resume fields; revision-checked atomic approval |
| Providers | Persisted OpenAI Responses, Anthropic Messages, Lemonade/OpenAI-compatible Chat Completions, Ollama chat, and fixture adapters; task routing and connectivity tests |
| Continuity | Relevant accepted facts, recent history, active promises/deadlines/threads, character knowledge, structured post-turn extraction, and mechanics validation |
| World model | Reusable/versioned world definitions, campaign characters, branch relationships, and per-character knowledge records |
| Determinism | Explicit inventory/skill mechanics endpoint, append-only mechanics ledger, event ledger, rolling summaries, and checkpoint state diffs |
| Privacy | Server-only credential configuration, optional bearer access token, loopback binding, escaped text rendering |

Still deferred: embedding/vector search, automated provider-driven progression, and richer scene authoring controls. Retrieval now includes bounded historical-message search and a diagnostic run-context endpoint. Corrections are audited, campaigns can be exported/restored as portable JSON (including world versions and ledgers), and DOCX/HTML imports are supported. AI-extracted import facts still require human review.

## Local development

Requires .NET 10 SDK, Node.js 24+, and PostgreSQL 17. With Docker available, start only the development database:

```powershell
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d postgres
$env:ConnectionStrings__DefaultConnection = 'Host=127.0.0.1;Port=5433;Database=storyapp;Username=storyapp;Password=YOUR_ENV_PASSWORD'
dotnet run --project backend --urls http://127.0.0.1:5080
```

Use your `.env` database name/user/password if you changed them. ASP.NET Core does not load `.env` automatically outside Compose: set any provider and access-token environment variables in that terminal too. The development override publishes PostgreSQL only on loopback port 5433.

In a second terminal:

```powershell
cd frontend
npm ci
npm run dev
```

Open the Vite URL printed in the terminal. Vite proxies `/api` to port 5080. Provider secrets must never use the `VITE_` prefix.

## Verification

```powershell
dotnet test tests/Story.Tests.csproj
npm --prefix frontend ci
npm --prefix frontend run build
dotnet tool restore
dotnet ef migrations has-pending-model-changes --project backend
docker compose config --quiet
```

Unit/integration tests use SQLite in memory for fast relational checks. They do not replace the PostgreSQL/Compose smoke test. See `docs/verification.md` for the exercised cases and current verification status.

## Operations and design

- [Import formats and review](docs/imports.md)
- [Provider setup and limits](docs/providers.md)
- [Autonomous storyteller and APIs](docs/storyteller.md)
- [Backup and restore](docs/backup-restore.md)
- [Architecture and next milestones](docs/architecture.md)
- [Original implementation handoff](IMPLEMENTATION-HANDOFF.md)
- [Full product specification](OOC-Story-App-Conversation-and-Specification.md)

The ASP.NET background worker owns PostgreSQL-backed jobs and records a short expiring lease plus attempt count for recovery if a worker stops. For access beyond localhost, configure `APP_ACCESS_TOKEN` and a trusted HTTPS reverse proxy; the default deployment is intended for one local user. The UI stores the token only in the current browser tab's session storage. Backups contain private transcript data and should be protected accordingly.
