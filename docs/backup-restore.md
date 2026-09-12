# Backup and restore

PostgreSQL holds campaigns, branches, checkpoints, messages, import jobs, evidence, provider profiles, and the original imported file bytes. **One database dump backs up both records and uploaded files.** Keep your `.env` separately in a secure location; it contains credentials and is not part of the database backup.

These commands use the service's configured database/user and work from the repo root in PowerShell or a POSIX shell. Use unique filenames for each backup. Do not pipe binary dumps through Windows PowerShell redirection.

## Create a backup

```text
docker compose exec -T postgres sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc -f /tmp/open-occ-backup.dump'
docker compose cp postgres:/tmp/open-occ-backup.dump ./open-occ-backup.dump
```

`pg_dump` takes a consistent snapshot while the app is running. Store the dump outside the repository and retain multiple versions. Treat it as private campaign data.

## Restore into a fresh isolated instance

Restore into an empty instance to avoid destroying existing data. Use a new Compose project name (which creates a separate named volume), and a different frontend port:

```powershell
$env:APP_PORT = '8081'
docker compose -p open-occ-restored up -d postgres
docker compose -p open-occ-restored cp ./open-occ-backup.dump postgres:/tmp/open-occ-backup.dump
docker compose -p open-occ-restored exec -T postgres sh -c 'pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --no-owner --no-privileges --exit-on-error /tmp/open-occ-backup.dump'
docker compose -p open-occ-restored up -d
```

In a POSIX shell, use `export APP_PORT=8081` instead of the PowerShell assignment. Wait for the new PostgreSQL service to be healthy before restoring. Do not use `--clean` against an existing database. If the target already contains application tables, choose a new project name and volume instead.

Open http://localhost:8081 and confirm campaign names, branch histories, last checkpoints, approved facts, and import jobs. Download at least one original file and compare its SHA-256 with the original/source metadata. A successful dump alone is not proof that restore works.

For a stack started with a different environment file or project name, include the same `--env-file` and `-p` options consistently in every command. Remove the temporary host `APP_PORT` override when finished (`Remove-Item Env:APP_PORT` in PowerShell).
