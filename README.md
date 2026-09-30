# Open OCC

A private story workspace built with React/TypeScript/Vite, ASP.NET Core 10, EF Core, and PostgreSQL. The autonomous storyteller slice adds independent character sessions, a master director, a Live room, d20 checks, historical world graphs, and editable memories. Transcript reconstruction and the full continuation specification remain later milestones.

## Start with Docker

Requires Docker Desktop (Linux containers) or Docker Engine with Compose. From the repository root:

```powershell
Copy-Item .env.example .env
# Edit .env and choose a strong POSTGRES_PASSWORD.
docker compose up
```

On macOS/Linux, use `cp .env.example .env` for the first step. Then open **http://localhost:8080**. The first run builds the images; subsequent runs reuse them. After source changes use `docker compose up --build`.

The backend applies the checked-in EF migration before becoming healthy. PostgreSQL is internal to Compose. The app listens on `127.0.0.1` only; `APP_PORT` changes the host port. Data survives `docker compose down` and container recreation through the `postgres-data` volume. Do not use `down -v` unless you intend to delete the database.

No provider key is needed for the default fixture character/director profiles. Fixture replies explicitly say **simulated turn**; the demonstration records scene reactions and advances time for waiting. Configure OpenAI-compatible or Ollama role profiles for live generation. Character and director can use different models from the same vendor.

## Try the first milestone

1. Create a campaign using the synthetic **Crownspire Academy** starting point.
2. Open it and submit an action. Open **Live world** while characters respond; the final reply, world changes and checkpoint save atomically.
3. Open **Timelines**, choose an earlier checkpoint, and create another branch. Its history stops at that checkpoint; the original continuation remains available.
4. Create an empty campaign, open **Import & review**, and upload `tests/fixtures/crownspire.json`.
5. When its status becomes `review`, inspect it. Accept/edit the passages that establish facts, assign public or narrator-only visibility, and set the resume state JSON. Approve to save a checkpoint.
6. Download the original from the review page; its bytes and SHA-256 remain unchanged.
7. Restart the containers and reopen the campaign to confirm its checkpoint remains available.
8. In **Live world**, explore the graph and inspect an entity's supporting occurrences. Toggle **Author view** to inspect secrets, character workspaces, checks and director memories. Memory corrections, pinning and removal create new checkpoints; earlier checkpoints remain inspectable.
9. Try “Force the sealed door” for a d20 check or “Wait beside the notice board” for time advancement and off-screen activity. Fixture behavior is deterministic apart from server-generated dice; live models interpret arbitrary actions.

The original research, specification, handoff, ZIP, and Word transcript remain in the root. The supplied Word transcript is **not** automatically imported or sent to any provider. Export it as UTF-8 plain text to try the initial importer.

## What works and what remains

| Area | Foundation behavior |
| --- | --- |
| Campaigns | Create empty/synthetic campaigns, list and reopen them |
| Turns | Independent character proposals, director resolution/narration, bounded reaction beats, perception-filtered context, off-screen updates on time advances |
| Checks | Director discretion; server d20 plus recorded modifiers against difficulty; natural 1/20 do not override totals |
| Live & author views | Ordered events, active run polling, spoiler-gated goals/beliefs/emotions/decision summaries and checks |
| Graphs & memory | Entity/relationship/occurrence graph, evidence links, checkpoint history, per-owner short/long memory, versioned author edits |
| Recovery | Durable validated steps and dice; paused-run retry, cancellation, restart recovery, atomic final commit |
| Branches | Fork any checkpoint, copying only earlier messages, state and accepted facts with their evidence |
| Imports | Preserve original bytes in PostgreSQL; UTF-8 TXT/JSON parser; durable batched jobs; cancel/retry/restart recovery |
| Review | Edit and accept/reject source passages; view evidence; approve a manually specified resume checkpoint |
| Providers | Separate character/director profiles; OpenAI, compatible endpoints and Ollama JSON adapters; server credentials, limits and cancellation; Anthropic remains unsupported |
| Privacy | Server-only credential configuration, optional bearer access token, loopback binding, escaped text rendering |

Not implemented yet: token streaming, semantic transcript reconstruction, reusable world templates, full RPG progression, direct DOCX/HTML imports, edit/regenerate shortcuts, correction/supersession audit UI, and campaign export/reimport. Live adapters are implemented; actual model quality requires testing your chosen models. Graphs and memories are persisted as checkpoint JSON in PostgreSQL; there is no extra graph or vector service. The parser does **not** infer speaker boundaries or identify rumors/corrections/final scenes; review makes that limitation explicit.

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
- [Storyteller behavior and APIs](docs/storyteller.md)
- [Backup and restore](docs/backup-restore.md)
- [Architecture and next milestones](docs/architecture.md)
- [Original implementation handoff](IMPLEMENTATION-HANDOFF.md)
- [Full product specification](OOC-Story-App-Conversation-and-Specification.md)

Run **one backend instance** in this foundation: its ASP.NET background worker owns PostgreSQL-backed jobs. Horizontal worker leasing is deferred. For access beyond localhost, configure `APP_ACCESS_TOKEN` and a trusted HTTPS reverse proxy; the default deployment is intended for one local user. The UI stores the token only in the current browser tab's session storage. Backups contain private transcript data and should be protected accordingly.
