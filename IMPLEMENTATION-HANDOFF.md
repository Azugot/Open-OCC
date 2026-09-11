# Implementation Handoff — Private AI Story Application

This archive is the authoritative handoff for implementing the project. Read `OOC-Story-App-Conversation-and-Specification.md` first. It contains the accepted requirements and acceptance tests. `Lumencia-Research-and-Clean-Room-Blueprint.md` contains research context and a clean-room story-engine template; it is reference material, not a requirement to copy Lumencia Academy.

## User intent

Build a private, self-hosted web app that allows the user to import an OOC campaign export, have an AI reconstruct its state and current story position, review uncertainty, and continue the story. It must also support original campaigns.

The app does **not** need to scrape OOC, access the user’s OOC account, recover private prompts, or reproduce OOC’s model output exactly.

## Non-negotiable decisions

- React + TypeScript + Vite frontend.
- C# / ASP.NET Core backend.
- PostgreSQL database.
- Docker Compose with `frontend`, `backend`, and `postgres` services.
- Fresh start through `docker compose up` after the user fills in `.env`.
- `.env.example` without secrets.
- API keys remain server-side. They must never be compiled into Vite output.
- Provider agnostic support: OpenAI, Anthropic/Claude, OpenAI-compatible endpoints such as DeepSeek/Kimi where compatible, and Ollama.
- Separate configured models for narration, migration/reconstruction, and memory.
- Agent-assisted migration layered on top of deterministic preservation/import.
- The original imported files are preserved unchanged and remain available as evidence.
- Branches, checkpoints, state validation, evidence-backed facts, and NPC knowledge boundaries are core features.
- This is a modular monolith; do not split it into microservices, Redis, vector databases, or separate worker deployments in the initial implementation.

## First implementation objective

Produce a runnable foundation with a synthetic campaign. Do not wait for a real OOC export.

The foundation must include:

1. Docker Compose, Dockerfiles, `.env.example`, and README.
2. ASP.NET Core API with EF Core migrations and PostgreSQL persistence.
3. React/Vite UI that can list/create a synthetic campaign and open a story view.
4. Provider profile persistence plus a provider-neutral abstraction and adapters/stubs.
5. A visible current-state panel and append-only messages/checkpoints.
6. A file import endpoint that preserves original source files and starts a persisted background import job, even if the first supported parser is plain text/JSON only.
7. A reviewable import/reconstruction model with facts linked to source segments.

Do not represent incomplete functionality as complete. When a live provider key is absent, expose configuration and use clearly labeled fixtures/mocks for development rather than silently calling an unavailable service.

## Recommended build sequence

1. Inspect repository state and existing instructions before changing files.
2. Scaffold the Compose project and document startup.
3. Implement database schema and migrations for campaigns, branches, messages, checkpoints, sources, import jobs, facts, and evidence.
4. Implement campaign API and synthetic fixture.
5. Implement the React working surface: dashboard, campaign story view, state panel, provider settings stub, and import entry point.
6. Add an OpenAI-compatible adapter and an Ollama adapter first, then native OpenAI and Anthropic adapters if their APIs are configured.
7. Add provider capability profiles and safe structured-output validation.
8. Implement resumable plain-text/JSON import, source segmentation, and a review screen.
9. Add agent-assisted chronological reconstruction only after source preservation, evidence links, and review surfaces exist.
10. Test restart persistence, branch isolation, migration behavior, cancellation, and compose startup.

## Continuity and migration rules

- Imported source remains immutable. Extracted conclusions are separate editable records.
- Tag facts as imported, extracted, user-confirmed, or generated.
- Preserve source evidence for material facts.
- Treat rumors, beliefs, secrets, contradictions, corrections, and branch-specific facts differently.
- Never allow NPCs to see narrator-only secrets.
- A cancelled or failed generation/import must not partially update authoritative state.
- Regeneration and edits create branches. Original continuations remain.
- Do not let a summary replace original chat history.

## Definition of first meaningful success

A synthetic or imported campaign can be reopened after restart at its last approved checkpoint, show relevant state, preserve branch history, and generate or simulate one next turn without uncontrolled state mutation.

## Required documentation before handoff

- `README.md`: exact development and Docker Compose startup instructions.
- `.env.example`: every required/optional setting explained.
- Migration notes: supported initial import formats and how evidence/review works.
- Provider notes: supported adapters, capability limits, and test procedure.
- Backup/restore procedure for PostgreSQL plus uploaded/imported files.

## Input still required from the user later

An OOC export or representative sample is required only to validate the real importer and campaign reconstruction. Ask for exported content, never credentials, browser cookies, session tokens, or passwords.

