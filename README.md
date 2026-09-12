# Open OCC

A private story workspace built with React/TypeScript/Vite, ASP.NET Core 10, EF Core, and PostgreSQL. This is the **first runnable foundation**, not the complete continuation engine in the original specification.

## Start with Docker

Requires Docker Desktop (Linux containers) or Docker Engine with Compose. From the repository root:

```powershell
Copy-Item .env.example .env
# Edit .env and choose a strong POSTGRES_PASSWORD.
docker compose up
```

On macOS/Linux, use `cp .env.example .env` for the first step. Then open **http://localhost:8080**. The first run builds the images; subsequent runs reuse them. After source changes use `docker compose up --build`.

The backend applies the checked-in EF migration before becoming healthy. PostgreSQL is internal to Compose. The app listens on `127.0.0.1` only; `APP_PORT` changes the host port. Data survives `docker compose down` and container recreation through the `postgres-data` volume. Do not use `down -v` unless you intend to delete the database.

No provider key is needed. Keep `NARRATION_PROVIDER=fixture` to try the foundation. All generated replies explicitly say **simulated turn** and preserve current state. If you configure a remote profile, it fails clearly because live adapters are not implemented yet.

## Try the first milestone

1. Create a campaign using the synthetic **Crownspire Academy** starting point.
2. Open it, inspect the state panel, and submit an action. The simulated reply and next checkpoint are saved atomically.
3. Open **Timelines**, choose an earlier checkpoint, and create another branch. Its history stops at that checkpoint; the original continuation remains available.
4. Create an empty campaign, open **Import & review**, and upload `tests/fixtures/crownspire.json`.
5. When its status becomes `review`, inspect it. Accept/edit the passages that establish facts, assign public or narrator-only visibility, and set the resume state JSON. Approve to save a checkpoint.
6. Download the original from the review page; its bytes and SHA-256 remain unchanged.
7. Restart the containers and reopen the campaign to confirm its checkpoint remains available.

The original research, specification, handoff, ZIP, and Word transcript remain in the root. The supplied Word transcript is **not** automatically imported or sent to any provider. Export it as UTF-8 plain text to try the initial importer.

## What works and what remains

| Area | Foundation behavior |
| --- | --- |
| Campaigns | Create empty/synthetic campaigns, list and reopen them |
| Turns | Clearly labeled deterministic fixture; atomic message/checkpoint commit; cancellation and interrupted-run records |
| Branches | Fork any checkpoint, copying only earlier messages, state and accepted facts with their evidence |
| Imports | Preserve original bytes in PostgreSQL; UTF-8 TXT/JSON parser; durable batched jobs; cancel/retry/restart recovery |
| Review | Edit and accept/reject source passages; view evidence; approve a manually specified resume checkpoint |
| Providers | Persisted profiles for fixture, OpenAI, Anthropic, DeepSeek, Kimi and Ollama; capability reporting; remote adapters are explicit stubs |
| Privacy | Server-only credential configuration, optional bearer access token, loopback binding, escaped text rendering |

Not implemented yet: live provider calls/streaming/connectivity tests; semantic AI reconstruction; per-character knowledge models beyond public/narrator scope; memory/search/retrieval; world versioning; automated progression; direct DOCX/HTML imports; edit/regenerate shortcuts; correction/supersession audit UI; campaign export/reimport. Full database backup/restore is available instead of portable campaign export. The first parser does **not** infer speaker boundaries in text or identify rumors, corrections, contradictions and final scene; the review UI makes that limitation explicit.

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
- [Backup and restore](docs/backup-restore.md)
- [Architecture and next milestones](docs/architecture.md)
- [Original implementation handoff](IMPLEMENTATION-HANDOFF.md)
- [Full product specification](OOC-Story-App-Conversation-and-Specification.md)

Run **one backend instance** in this foundation: its ASP.NET background worker owns PostgreSQL-backed jobs. Horizontal worker leasing is deferred. For access beyond localhost, configure `APP_ACCESS_TOKEN` and a trusted HTTPS reverse proxy; the default deployment is intended for one local user. The UI stores the token only in the current browser tab's session storage. Backups contain private transcript data and should be protected accordingly.
