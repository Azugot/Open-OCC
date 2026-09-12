# Foundation verification

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

## Scope limits

These checks validate the foundation only. Live AI providers, streaming, semantic reconstruction, per-NPC knowledge, transcript search, portable campaign export/reimport and mobile browser testing remain unverified/unimplemented as described in the README. SQLite tests complement the real PostgreSQL smoke; they do not claim to reproduce every PostgreSQL concurrency behavior.
