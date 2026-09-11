# OOC Story App — Conversation Handoff and Implementation Specification

**Prepared:** 2026-09-11  
**Purpose:** Preserve the substantive discussion, decisions, research conclusions, and agreed implementation plan for the private AI story application.

---

## 1. Conversation record

### Initial request

The project began with a request to find the entire script and data for **Lumencia Academy** on OOC in order to understand its parameters, primary story structure, and prompts well enough to make an original version.

### Research outcome

The research established that Lumencia Academy is an OOC story marked as a creator custom prompt. Its public description presents it as an imperial academy of sword and sorcery, with a large media set and a romance-oriented audience. Public playthroughs show that it is generative: different players receive substantially different events, relationships, abilities, mysteries, and timelines. It is therefore not credibly describable as one fixed, complete screenplay.

Public evidence strongly supports these mechanics:

- Character setup asks for name, gender, background, department, appearance, and a notable trait or starting condition.
- Departments include Knight, Magic, and Theology. Hybrid study is possible but difficult.
- A persistent HUD commonly tracks tier, affiliation, title, status, skills, Body/Mana/INT/Divinity, talent, condition, inventory, and relationships.
- Skills use a grade scale around `F-` through `SSS` with subdivisions and visible progress.
- The recurring gameplay loops are academy life, progression, relationships, institutional politics, examinations, monsters/anomalies, and a hidden conspiracy/cult layer.
- Recurring names reported by players include Emily, Artemis, Elena, Lilia Valenheart, Serena Caldwin, Laris, Sia, Rena, Lucia, Elara, Isabel/Isobel, Chloe, Carne, and Anastasia.

The OOC community’s creator guides also describe an approximate prompt/memory stack: creator prompt and media descriptions; triggered keyword books; player avatar; recent chat history; host metadata; player input; temporary memories, relationships, and goals; three long-term memories; user notes; then keyword books again. This explains why permanent rules should live in the creator prompt, current state should be compact and explicit, and keyword books should reinforce a small number of critical rules.

### What could not be recovered or verified

The following were explicitly identified as unavailable in public evidence:

- Lumencia’s complete creator system prompt, word for word.
- OOC’s hidden system scaffolding surrounding the creator prompt.
- A definitive Lumencia model/version and its sampling settings: temperature, top-p, seed, penalties, token limits, retrieval thresholds, and summarizer configuration.
- The complete canonical character database, media pack, keyword books, media trigger rules, slash commands, editor configuration, stat formulas, and ending configuration.
- A canonical chapter sequence or fixed main script.
- The complete roster and exact rules for the Sin Bishops and Divine Apostles.
- A reliable way to separate official canon from facts organically invented within individual players’ sessions.
- Proof that fan recreations or partial copies on other platforms faithfully reproduce Lumencia.
- Any private creator notes, session memories, backend records, or inaccessible OOC account data.

The result was a **clean-room reconstruction**: a source-backed understanding of the architecture and an original academy template, not a claimed extraction or leak.

### Build feasibility

The question "If I ask you, could you build it for me?" was answered **yes**, with the qualification that the application can import content supplied or exported by the user, but cannot promise automatic OOC scraping, recovery of hidden prompts, or generation identical to OOC.

### Initial architecture discussion

An initially proposed broad architecture included React, a .NET backend, PostgreSQL, and Docker. The possibility of a browser-only React/Vite application was discussed. The conclusion was that React and Vite alone are viable for a simple local browser application, but they are the UI/build layer rather than a full secure backend. A small server is still valuable for API keys, import processing, structured state, reliable backups, and later private hosting.

The final chosen architecture is the first proposed architecture:

- React + TypeScript + Vite frontend.
- C# / ASP.NET Core backend.
- PostgreSQL database.
- Docker Compose deployment.
- A `.env` file for provider credentials and defaults.
- A single command startup: `docker compose up`.

This is a modular monolith. Frontend, backend, and database run as separate containers for deployment convenience, while the actual application logic remains one backend application rather than a microservice fleet.

### Migration decision

The preferred migration approach is **agent-assisted reconstruction**, not a simplistic file-to-table import.

The complete OOC export is preserved unchanged as source material. A deterministic parser preserves ordering and metadata; an LLM-assisted migration process then reconstructs the campaign chronologically. It extracts events, character relationships, world facts, inventory, abilities, promises, secrets, current location, and unresolved plots. It distinguishes facts, corrections, contradictory evidence, rumors, abandoned branches, and uncertainty. Important extracted facts retain links to source messages and are presented for review before becoming the resume checkpoint.

This is intentionally a hybrid design:

- Classic import preserves bytes, messages, order, and evidence.
- The agent understands narrative meaning and builds structured state.
- The user remains the authority for meaningful ambiguities.

### Provider decision

The application must be provider-agnostic and support online and local models. Initial support targets:

- OpenAI.
- Anthropic / Claude.
- OpenAI-compatible providers, including DeepSeek and Kimi when their configured endpoints conform sufficiently.
- Ollama, including a local or reachable remote instance.

Provider compatibility is capability-based. No provider receives a blind universal request. Each adapter declares streaming, structured-output, tool-calling, image-input, context, output, and sampling capabilities. Unsupported features are omitted or reported clearly. Provider/model choices are recorded per generation.

No silent fallback to another remote provider is allowed because that would affect cost, output behavior, and where campaign data is sent.

---

## 2. Product objective

Build a private, self-hosted AI story application that can:

1. Create original campaigns and reusable world definitions.
2. Import a user-supplied OOC export or other supported story source.
3. Preserve original source data and reconstruct an inspectable campaign model.
4. Resume the story from an approved checkpoint.
5. Generate story turns through a configurable AI provider.
6. Keep state, knowledge, continuity, relationships, and branches reliable over long sessions.
7. Allow the user to correct facts, retry scenes, create branches, export, back up, and restore campaigns.

The first meaningful success condition is: **the imported campaign opens at the correct final scene, remembers material facts, and produces a credible next turn without inventing contradictory history.**

---

## 3. Architecture

### Docker Compose services

| Service | Responsibility |
|---|---|
| `frontend` | React + TypeScript + Vite build, served as static assets through a web server |
| `backend` | ASP.NET Core API, provider adapters, story orchestration, import jobs, retrieval, and persistence logic |
| `postgres` | Campaigns, messages, world definitions, structured state, facts, evidence, jobs, and audit records |

The frontend server proxies `/api` to the backend so the browser sees one origin. PostgreSQL remains internal to Docker Compose unless a developer profile explicitly exposes it.

### Repository structure

```text
/frontend
/backend
/tests
/docs
docker-compose.yml
.env.example
README.md
```

### Backend modules

- Campaigns and branches.
- Conversations and generation runs.
- World definitions, characters, locations, factions, and rules.
- State, progression, inventory, relationships, and knowledge.
- Memory, summaries, retrieval, and diagnostics.
- Import, normalization, reconstruction, and review.
- Provider integration and model profiles.
- Background jobs and administration.

These are modules in one ASP.NET Core application. They are not independently deployed microservices.

### Deployment contract

The project includes Dockerfiles, `docker-compose.yml`, health checks, persistent volumes, automatic controlled EF Core migrations, `.env.example`, and backup/restore documentation.

Expected startup after configuration:

```bash
docker compose up
```

API keys exist only in backend configuration. They must never be passed through `VITE_*` variables or bundled into browser assets.

---

## 4. Provider integration specification

### Internal provider contract

The backend defines provider-neutral request and response models:

- Model profile and capabilities.
- System and user messages.
- Streaming output events.
- Usage data where returned by the provider.
- Optional structured response schema.
- Tool/function interface only where explicitly supported.
- Failure, retry, and cancellation states.

Adapters translate this contract to provider APIs.

### Initial adapters

| Adapter | Use |
|---|---|
| OpenAI | Native OpenAI API handling |
| Anthropic | Native Claude message API handling |
| OpenAI-compatible | DeepSeek, Kimi, and compatible hosted endpoints |
| Ollama | Local or remote Ollama APIs |

### `.env` settings

The eventual `.env.example` should include the following categories, without real secrets:

```dotenv
POSTGRES_DB=storyapp
POSTGRES_USER=storyapp
POSTGRES_PASSWORD=change-me
CONNECTIONSTRINGS__DEFAULTCONNECTION=Host=postgres;Port=5432;Database=storyapp;Username=storyapp;Password=change-me

OPENAI_API_KEY=
OPENAI_BASE_URL=https://api.openai.com/v1
OPENAI_DEFAULT_MODEL=

ANTHROPIC_API_KEY=
ANTHROPIC_DEFAULT_MODEL=

DEEPSEEK_API_KEY=
DEEPSEEK_BASE_URL=
DEEPSEEK_DEFAULT_MODEL=

KIMI_API_KEY=
KIMI_BASE_URL=
KIMI_DEFAULT_MODEL=

OLLAMA_BASE_URL=http://host.docker.internal:11434
OLLAMA_DEFAULT_MODEL=

NARRATION_PROVIDER=
NARRATION_MODEL=
RECONSTRUCTION_PROVIDER=
RECONSTRUCTION_MODEL=
MEMORY_PROVIDER=
MEMORY_MODEL=
```

The exact naming may evolve during implementation, but the separation between provider credentials, endpoint configuration, and task-specific model selection is intentional.

### Capability and validation behavior

- Streaming turns use streaming only when the selected model profile supports it.
- Structured outputs are requested only through supported mechanisms.
- If structured output is not supported, output is parsed and validated with bounded repair attempts.
- Invalid responses cannot silently commit state changes.
- Provider/model selection is stored with generated messages, extracted facts, summaries, and import jobs.
- Provider connectivity can be tested from the settings UI without exposing keys to the frontend.

---

## 5. Campaign data model

### Information layers

| Layer | Meaning |
|---|---|
| World definition | Reusable setting facts: rules, locations, NPC templates, factions, mechanics, author instructions |
| Campaign canon | Facts accepted as true for a particular playthrough |
| Event history | Ordered things that happened in the selected branch |
| Current state | Location, time, participants, conditions, inventory, objectives, relationships, and progression |
| Beliefs and secrets | Who knows, believes, suspects, or is ignorant of each fact |
| Narrative threads | Promises, mysteries, deadlines, conflicts, goals, and unresolved hooks |

World definitions are versioned. Editing a reusable setting must not silently rewrite an existing campaign.

### Provenance requirements

Important campaign facts retain:

- Source type: imported, extracted, user-confirmed, or generated.
- One or more supporting source message references.
- Confidence/review state.
- Validity period and supersession links.
- Branch identity.
- Visibility/knowledge scope when relevant.

### Core entity set

- `World`, `WorldVersion`.
- `Campaign`, `CampaignBranch`, `Checkpoint`.
- `Message`, `GenerationRun`.
- `Event`, `Fact`, `FactEvidence`, `KnowledgeRecord`.
- `Character`, `Relationship`, `Condition`, `Skill`, `InventoryItem`.
- `NarrativeThread`, `Goal`, `Promise`, `Deadline`.
- `Memory`, `Summary`, `RetrievalLog`.
- `ImportedSource`, `ImportJob`, `ImportSegment`, `ReviewItem`.
- `ProviderProfile`, `ModelProfile`, `UsageRecord`.

---

## 6. Agent-assisted migration specification

### Input types

The import format is determined only after inspecting an actual export. The initial supported target formats are:

- Plain-text transcript.
- Structured JSON matching a documented import schema.
- Separate notes, memories, and character sheets.
- HTML export when available.

Screenshots/OCR are a fallback because they can introduce message-order and recognition errors.

### Pipeline

1. **Preserve source** — store supplied files unchanged, with checksum and metadata.
2. **Normalize** — identify speakers, message order, timestamps, source sections, notes, visible state panels, and possible branches.
3. **Analyze chronologically** — process manageable sections while maintaining an evolving campaign ledger.
4. **Extract** — build candidate events, facts, characters, relationships, world rules, possessions, abilities, conditions, and narrative threads.
5. **Reconcile** — recognize corrections, changed state, contradictions, rumors, abandoned timelines, and unresolved uncertainty.
6. **Review** — show meaningful conflicts and uncertain facts to the user.
7. **Materialize** — create campaign entities and an approved checkpoint.
8. **Resume** — use the checkpoint, relevant source material, and current structured state to generate the next scene.

### Import job requirements

- Background execution via PostgreSQL-backed jobs and ASP.NET Core background workers.
- Progress tracking, cancellation, retry, and restart recovery.
- Segment checkpointing to avoid rerunning completed work.
- User-visible token/cost data where the provider reports it.
- Configurable practical budgets before expensive reconstruction calls.
- No duplicate extraction when a job resumes.

### Resume checkpoint

The checkpoint must identify:

- Latest exact scene/location/time, where recoverable.
- Latest player action and whether a reply was pending.
- Current participants and relevant relationships.
- Active conditions, inventory, skills, and recent changes.
- Immediate goals, promises, threats, and unresolved questions.
- What each active character knows or believes.
- Evidence supporting the summary.

---

## 7. Story generation and continuity

### Turn workflow

1. Receive the player action.
2. Load the current campaign branch and checkpoint.
3. Retrieve relevant canon, current state, recent turns, summaries, character knowledge, and open narrative threads.
4. Assemble a context package within the selected model’s budget.
5. Generate a compact, validated scene plan and proposed state changes.
6. Stream the narrative response.
7. Validate that narrative and state proposal do not conflict.
8. Commit message, events, fact changes, and checkpoint atomically.

If generation is cancelled or fails, the application retains an incomplete generation record but must not partially mutate authoritative campaign state.

### Agency model

The backend distinguishes:

- Player attempts.
- In-character dialogue/declarations.
- Explicit out-of-character direction.
- Corrections to canon.

The user can choose a simulation-oriented or author-directed mode. Neither mode may silently overwrite accepted facts.

### Mechanics

When formal rules exist, the application calculates them deterministically:

- Inventory quantities.
- Currency changes.
- Resource consumption.
- Rank thresholds.
- Validated progression.

Narrative judgment remains an AI task, while deterministic mechanics do not rely on the model’s arithmetic or fake randomness.

### Continuity rules

- NPCs have only their own filtered knowledge; narrator knowledge does not make a secret public.
- A rumor remains a rumor until evidence verifies it.
- Later corrections supersede prior state without deleting history.
- Active promises and deadlines remain retrieval candidates regardless of age.
- Memories and state are branch-specific.
- Manual corrections can be pinned so summarization cannot erase them.

---

## 8. Memory and retrieval

The first implementation uses PostgreSQL full-text search plus entity references, recency, importance, relationship relevance, and unresolved-thread priority. A vector database is deliberately deferred.

Each turn can retrieve:

- Recent conversation turns.
- Rolling campaign summary.
- Current authoritative state.
- Relevant historical events.
- NPC-specific interaction history.
- Active goals, promises, and unresolved threads.
- Relevant world entries and rules.

The UI includes a diagnostic view showing the context categories used for a turn. It should not expose hidden model reasoning; it exposes application-level data and retrieval evidence.

Embeddings may be added later only if retrieval tests show a real benefit.

---

## 9. Branches, checkpoints, and corrections

### Branching

- Regenerating a response creates an alternative branch.
- Editing an earlier action creates a new continuation.
- The original branch remains intact.
- Later events and memories cannot cross between branches.
- Checkpoint snapshots plus append-only events provide sufficient initial implementation complexity.
- Deleting a branch requires confirmation.

### Corrections

The user can correct extracted or generated facts, NPC profiles, relationships, inventory, ability state, timeline information, and current scene state.

Each correction records old value, new value, reason, editor, timestamp, and affected branch. Related summaries are marked stale and rebuilt or reviewed.

---

## 10. User interface

### Primary screens

| Screen | Purpose |
|---|---|
| Campaign dashboard | Create, import, resume, export, and organize campaigns |
| Story view | Streaming narrative, message input, stop/retry/regenerate controls |
| State panel | Character sheet, location, conditions, inventory, relationships, objectives |
| World reference | Characters, locations, factions, mechanics, and canon status |
| Timeline and memory | Search events and inspect evidence source messages |
| Import review | Review extracted facts, conflicts, confidence, and checkpoint proposal |
| Branch manager | Inspect checkpoints and choose alternative timelines |
| Provider settings | Configure provider/model profiles and test connectivity |

### Story controls

- Stop streaming generation.
- Retry a failed generation.
- Regenerate into a branch.
- Edit an earlier player message into a branch.
- Submit correction to canon/state.
- Inspect state changes caused by a turn.
- View provider/model and usage data where available.

The UI must render Markdown safely. Imported HTML and model output are data; they must not execute scripts.

---

## 11. Security, privacy, and reliability

The default audience is one private user.

- API keys remain server-side and never appear in frontend assets or logs.
- PostgreSQL is internal to Compose by default.
- Application access is protected when exposed beyond the local machine.
- File uploads have size, type, and archive extraction limits.
- Background jobs have timeouts, bounded retries, cancellation, and durable status.
- Story text never causes automatic operating-system commands or tool execution.
- The user can see which configured provider receives a generation/import request.
- Local Ollama provides a fully local inference path.
- Backups include database and uploaded/imported files; restore must be tested.

---

## 12. Scope phases

| Phase | Deliverable | Completion criterion |
|---|---|---|
| 0 | Export assessment | A sample establishes the actual input contract and data gaps |
| 1 | Foundation | Fresh checkout starts through Docker Compose with documented `.env` setup |
| 2 | Provider layer | Streaming narration and structured analysis work through configured adapters |
| 3 | Campaign core | Small synthetic campaign persists, resumes, and branches correctly |
| 4 | Migration proof | A sample export produces an approved resume checkpoint |
| 5 | Continuation engine | Retrieval and state validation maintain coherent continuation |
| 6 | Full migration | Complete export processes resumably while preserving original source |
| 7 | Hardening | Tests, diagnostics, backups, restore validation, and documentation complete |

No calendar estimates were committed because actual export size/format and provider access determine scope.

---

## 13. Acceptance criteria

### Deployment

- A fresh checkout starts with `docker compose up` after `.env` configuration.
- Data survives container recreation.
- Migrations run safely.
- Missing provider configuration produces clear corrective messages.

### Migration

- Original source material remains intact.
- Message order is preserved or uncertainty is flagged.
- Important extracted facts point to evidence.
- Explicit corrections supersede earlier facts.
- The resume checkpoint reflects the final scene.

### Continuity

- Old promises are retrieved when relevant.
- Inventory/progression do not reset.
- NPCs cannot know undisclosed secrets.
- Location and time stay coherent.
- Failed/cancelled generations do not commit state.

### Branching

- Regeneration retains the original continuation.
- Branch memories remain isolated.
- Restored checkpoints exactly restore state.

### Provider adapters

- Contract tests exercise every adapter.
- Live smoke tests run when credentials/endpoint are supplied.
- Unsupported capabilities fail clearly.
- Provider switching preserves campaign data.

### Recovery

- Export/reimport preserves campaign data.
- A backup restores into a fresh instance.

---

## 14. Current status and next implementation step

The architecture, migration approach, provider requirements, and acceptance criteria are agreed. Implementation had not started when an earlier workspace became unresponsive. The correct first implementation step in a working workspace is to scaffold the Docker Compose project, frontend, backend, database, configuration, and a synthetic campaign fixture. An actual OOC export is not required for that foundation; it becomes necessary for Phase 0/4 migration validation.

