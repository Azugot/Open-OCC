# Import and agent review

The original file is stored unchanged with its SHA-256 before reconstruction. Model conclusions remain in separate, versioned drafts until approval. Original downloads return the exact preserved bytes.

## Formats and limits

- UTF-8 `.txt`, `.md`, and `.markdown`, split on blank lines; obvious speaker prefixes are recorded, ambiguous speakers remain unknown.
- `.html`/`.htm`, with scripts/styles removed and visible block text converted to passages.
- `.docx` Word documents; paragraphs, table paragraphs, headings, tabs, and line breaks are extracted in document order with a streaming XML reader.
- UTF-8 `.json` with a root `messages` array of string `role` and `content` fields and optional string `timestamp`.
- Maximum 32 MiB per upload, 16 MiB of extracted text characters, 100 characters per speaker, and 200 filename characters. DOCX XML is bounded to 128 MiB. Long paragraphs split at natural boundaries without breaking Unicode surrogate pairs. Archives and images are not accepted.

Symbol-only fragments are marked as possible artifacts and remain visible as evidence. Heading and speaker labels are hints, not proof of a message boundary. A paragraph never automatically becomes a campaign fact.

## Guided import flow

The four steps are **Source → Reconstruct → Review → Continue**. From the library, **Bring your story** creates an empty campaign and opens Source directly. Within an existing campaign, Source defaults to a **New timeline** forked from the selected checkpoint; **Current timeline** is an explicit alternative. The campaign and destination stay visible throughout review.

Source validates the filename, extension, and size before uploading. Preparation preserves the original without needing credentials or an operational model. Expected JSON, document, and decoding failures stop the saved job with a useful error; the original remains downloadable. Fix the file and upload a new source, or retry preparation if appropriate. No incomplete normalized evidence is retained.

Reconstruct shows one effective provider/model choice and excludes fixture adapters. Budget controls remain under **Advanced**, with existing defaults. Start validates the model before inference. Reanalysis first prepares a fresh job from the preserved source and uses the same selection and budget controls. A changed destination offers a fork from the original checkpoint with fresh reconstruction; the previous draft remains available.

Review opens with remaining blockers and the final scene. **Needs attention**, **All proposals**, and **Final scene** provide focused views. Inventory quantities, skill ranks, and other categories have specific controls; stable keys and full JSON remain under Advanced. Approval is explicit, and clear proposals are included by default.

Editors keep local corrections when saved server data refreshes. Saves are serialized, obsolete reads are cancelled or ignored, and overlapping edits require comparison before resubmission. Changing views, filters, pages, or workspace prompts **Save / Discard / Stay** when edits are pending. Reloading warns through the browser. Legacy review edits use explicit browser-session Save and remain local until approval.

## Reconstruction stages

New jobs use `agent-v3` and stages `normalize → extract → reconcile → resume → audit → repair → complete`. The UI preserves and prepares the file before any model call, shows its structure and grouped source preview, then offers **Start reconstruction**. Status includes `preparing` and `prepared` as well as `queued`, `processing`, `paused`, `review`, `approved`, and `cancelled`.

The original file and one immutable normalized document are stored separately from conclusions. Citation rows contain offsets into that document rather than repeated text. Nearby Word paragraphs, headings, forms and lists form blocks around 3,500 characters, bounded by the captured reading budget and a 6,000-character maximum. Explicit JSON message turns remain distinct; unknown speaker identity stays unknown. The repository DOCX has 447 paragraphs and forms 13 blocks at the default budget. Reading blocks are evidence units, not individual story messages or automatic canon.

- Extraction reads chronologically and maintains a concise summary, evolving scene, and stable-key ledger of facts, characters, relationships, knowledge, world rules, events, inventory, skills, conditions, and threads.
- Reading windows combine consecutive blocks using the actual serialized input size, with preceding prose and the current heading when available. New extraction windows cap source evidence at 10,000 serialized characters, reserving larger budgets for continuity context rather than overwhelming a bounded response. Existing v2 drafts retain their evidence IDs; reanalysis uses the new document structure.
- Reconciliation reviews ledger pages with related entries. Explicit corrections and duplicates supersede prior versions; previous versions and evidence remain inspectable.
- Reconciliation and audit persist bounded evidence windows, paging even a single claim across windows when its accumulated citations are large. Supporting citations are retained without a 64-citation history cutoff; each model call sees up to 64 citations from its window. The model is told that citations outside the current window are not contradictions. These plans survive restart and portable export/import.
- Resume reconstruction uses the evolving state and ending passages to recover the final scene, latest action, and whether a narrator response is pending.
- A separate audit pass checks claims and the resume against supplied evidence. One targeted repair pass can propose changes; blocking issues still require human resolution or acknowledgment.

All passes use the captured reconstruction provider/model. Structured output and evidence ordinals are validated locally, including for Lemonade. Output instructions include a concrete entity JSON example and enums. Entity claims require an established nonblank subject and a supported nonnull `entityKind` in both the generation schema and local validation; other categories retain optional entity metadata for older results. Literal newlines, tabs and carriage returns inside JSON strings are escaped without changing their text; truncated objects, wrong field types and unsupported claims still fail validation.

Repairs receive claim indexes, stable keys, specific field failures, and the rejected response when it fits. Skill values must be plain strings (for example, `"Magic Talent": "F · 2/5"`); inventory values must be integer counts. State-map errors identify the exact field, expected shape, and received type before typed deserialization. For larger results, exact rejected claims or state fields are supplied instead; malformed JSON can use a clearly labeled bounded excerpt. Source evidence is retained, and repeated contextual ledger details can be reduced to stable keys to fit the captured input limit. Missing entity names or kinds must become explicit uncertainty, never guessed data. If exact feedback cannot fit, processing stops before sending or counting a repair request. After one sent repair attempt, invalid output pauses the stage and reports the stage and reason. Raw passages never become fallback facts. Fixture mode preserves and normalizes the source, then pauses for a configured model.

Each validated stage result, draft revision, coverage checkpoint, and usage saves together. Worker claims use concurrency checks; leases renew every 20 seconds and expire after two minutes. Cancellation or lost ownership prevents late results from committing. Retry continues at the incomplete stage. A remote call interrupted before persistence may be repeated and billed again.

Budgets default to 24,000 input characters including instructions/schema/context, 3,000 output tokens, and 100 calls. Limits are adjustable before upload and on retry. Calls include repair attempts. Budget exhaustion pauses processing. Reported tokens are shown where available; monetary costs are not invented.

## Human approval boundary

The review page shows readiness, coverage, conflicts, missing information, category filters, search, 20 proposals per page, and an evidence drawer. Clear proposals are included by default. Users can edit/exclude proposals, save issue resolutions, and edit resume fields or advanced JSON. Saved decisions survive reload; proposal revisions reject stale writes. Legacy candidates are also paginated in the interface.

Approval requires complete coverage and agent review, resolution/acknowledgment of blocking issues, a supported player character name, a usable location and objective, valid state/evidence, and agreement between inventory/skill claims and the resume. Participants mean people physically present at the ending. The default empty state cannot be approved as a reconstructed ending. Unknown information can remain explicitly unknown after acknowledgment. Unresolved character references must be corrected or excluded.

Approval atomically saves the resume checkpoint, a branch-scoped transcript binding, evidence-backed canon, character registrations, graph entities/connections, occurrences, scoped short/long memories, relationships, knowledge and threads. Objects, beings and places become stable entities where supported. Every imported occurrence links to original block/range evidence. The player is excluded from autonomous NPC sessions. Historical states stay historical; beliefs/rumors do not become canonical relationships; secrets reach only established knowers. Missing motives/locations are not invented. Imported world data is checkpoint scoped rather than changing shared world templates.

The Story page shows one clean resume message and a paginated, searchable transcript reader instead of hundreds of source chat messages. Live world occurrences and graph relationships open supporting source evidence. Source history, graph and memories follow the selected checkpoint and fork; future approvals are absent from earlier checkpoints. A paused turn is visible on the Story page with its error and Retry/Cancel controls.

Readiness and transactional approval share one evaluator for coverage, completed review, usable scene, player identity, evidence, state consistency, world references, unchanged checkpoint, and active/paused story runs. Structural warnings are reevaluated against edited data; uncertainty can be acknowledged with a note, while invalid data must be corrected. Approval cannot overwrite a branch that advanced after upload. Fork the original checkpoint and import there instead. The original source remains downloadable regardless of parse, provider, review, or approval outcome.

## APIs and compatibility

- Upload: `POST /api/branches/{id}/imports` with multipart `file`, `inputCharacterLimit`, `outputTokenLimit`, `maxCalls`, and optional `provider`/`model` for this import. `prepareOnly=true` prepares without inference; `resume-processing` starts it. Optional `destination=new|current`, `timelineName`, and `checkpointId` select a destination; omission retains the previous current-timeline behavior. Fork/source/job creation is transactional. Omitted profile/model use reconstruction settings; overrides do not change those settings.
- Document summary: `GET /api/sources/{id}/structure`; source reader: `GET .../blocks?jobId=...&page=0&q=...&ordinals=0,1` (20 blocks per page).
- Approved transcript history: `GET /api/branches/{id}/transcripts?checkpointId=...&page=0`.
- Draft: `GET /api/imports/{id}/draft` includes `approval: { canApprove, blockers }` with proposal/issue/field references; proposals: `GET .../proposals?page=0&category=fact&q=key&history=false`.
- Evidence: `GET .../evidence?page=0&proposalId=...` pages all saved citations, 20 blocks at a time. Explicit `ordinals=0,1` remains supported up to 64 ordinals. API ordinals start at zero; UI passage numbers start at one.
- Legacy candidates: `GET .../candidates?page=0`, 20 per page. Job metadata accepts `includeCandidates=false`.
- Saved edits: `PUT .../proposals/{proposalId}` with `{ revision, claim, excluded }`; `PUT .../issues/{issueId}` with `{ revision, resolution }`; `PUT .../resume` with `{ revision, resume }`.
- Retry: `POST .../resume-processing` with optional provider/model and budgets. Subsequent stage results record the selected model.
- Preparation retry: `POST .../prepare` for stopped normalization jobs, without inference.
- Approval: `POST .../approval` with `{ expectedCheckpointId, revision }`.
- Reanalysis: `POST .../reanalyze` with processing budgets, optional provider/model, `prepareOnly`, `forkOriginal`, and `timelineName` creates a new draft from the same source. Omitted `prepareOnly` retains previous API behavior.

Legacy imports remain inspectable with their original evidence IDs and a grouped transcript display. The migration backfills approved history bindings, including inherited evidence on forks. Reanalysis creates a fresh draft without changing approved campaigns. Portable export version 5 includes campaign settings, normalized documents, offset citations and history bindings alongside sections, drafts, issues, results and decisions; versions 1–4 remain readable. Required collections, duplicate IDs, references, and embedded JSON are validated before persistence; invalid exports create no partial campaign. Restored source/job/evidence IDs are remapped in graph snapshots. Restored preparing, queued, and processing jobs pause and do not copy leases.

## Local regression checks

Run `dotnet test tests/Story.Tests.csproj`, `npm --prefix frontend run build`, and `node tests/review-draft.mjs` (Node 22.18+ with native TypeScript support). For isolated PostgreSQL and recorded inference, start `docker compose -p occ-import-review -f docker-compose.yml -f tests/import-compose.yml up -d --build`; then run `node tests/import-reliability-smoke.mjs`, `node tests/entity-repair-smoke.mjs`, `node tests/skill-repair-smoke.mjs`, and `node tests/import-smoke.mjs http://localhost:8082`. This separate stack uses synthetic files, its own database, and a local scripted model; it does not call paid providers. Stop it with the same Compose files and `down` when finished. These checks validate transport, state, and review behavior; they do not measure live-model interpretation quality.

Model context and transcript pages are bounded, but checkpoint world JSON still stores a complete snapshot. Very large occurrence/memory histories will increase checkpoint storage and load costs. Live turns may add entities up to a 5,000-entity total; existing larger imports can continue when a turn creates no additional entities. A normalized event store and database-backed graph paging remain future scaling work. Text coverage proves that every source block was read, not that every detail was correctly reconstructed; author review and capacity issues remain necessary.

The model can still misread narrative meaning. Evidence, chronology, explicit uncertainty, and human approval remain necessary.

Provider timeouts pause the saved stage with an explicit error; they are not mistaken for cancellation and retried repeatedly. Provider/connection errors do not trigger a JSON repair call. A validation repair does count toward the call budget, and budget exhaustion retains the last validation reason when available.

Structured calls through the DeepSeek connector set `thinking.type=disabled` so the bounded output budget is available for the final JSON. The current API defaults to thinking mode; see [DeepSeek thinking mode](https://api-docs.deepseek.com/guides/thinking_mode/). Only final response content is consumed.
