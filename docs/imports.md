# Import and agent review

The original file is stored unchanged with its SHA-256 before reconstruction. Model conclusions remain in separate, versioned drafts until approval. Original downloads return the exact preserved bytes.

## Formats and limits

- UTF-8 `.txt`, `.md`, and `.markdown`, split on blank lines; obvious speaker prefixes are recorded, ambiguous speakers remain unknown.
- `.html`/`.htm`, with scripts/styles removed and visible block text converted to passages.
- `.docx` Word documents up to 2 MiB; paragraphs, table paragraphs, headings, tabs, and line breaks are extracted in document order.
- UTF-8 `.json` with a root `messages` array of string `role` and `content` fields and optional string `timestamp`.
- Maximum 2 MiB per upload and extracted text, 4,000 initial/normalized passages, 100 characters per speaker, and 200 filename characters. Long turns are split at paragraph, line, sentence, or whitespace boundaries where possible, preserving their complete text and speaker metadata. Archives and images are not accepted.

Symbol-only fragments are marked as possible artifacts and remain visible as evidence. Heading and speaker labels are hints, not proof of a message boundary. A paragraph never automatically becomes a campaign fact.

## Durable reconstruction

New jobs use `agent-v2` and stages `normalize → extract → reconcile → resume → audit → repair → complete`. Status is `queued`, `processing`, `paused`, `review`, `approved`, or `cancelled`.

- Extraction reads chronologically and maintains a concise summary, evolving scene, and stable-key ledger of facts, characters, relationships, knowledge, world rules, events, inventory, skills, conditions, and threads.
- Citation passages are storage/evidence links, not separate model requests or scenes. Reading windows combine consecutive passages using the actual serialized input size, with preceding prose and the current heading when available. The default no longer divides the entire input budget by four. Existing stopped drafts combine adjacent saved sections on resume; a fresh reconstruction can be started from the original in the review screen.
- Reconciliation reviews ledger pages with related entries. Explicit corrections and duplicates supersede prior versions; previous versions and evidence remain inspectable.
- Reconciliation and audit persist bounded evidence windows, paging even a single claim across windows when its accumulated citations are large. All supporting citations are reviewed; the model is told that citations outside the current window are not contradictions. These plans survive restart and portable export/import.
- Resume reconstruction uses the evolving state and ending passages to recover the final scene, latest action, and whether a narrator response is pending.
- A separate audit pass checks claims and the resume against supplied evidence. One targeted repair pass can propose changes; blocking issues still require human resolution or acknowledgment.

All passes use the captured reconstruction provider/model. Structured output and evidence ordinals are validated locally, including for Lemonade. Output instructions include a concrete JSON example and enums. Literal newlines, tabs and carriage returns inside JSON strings are escaped without changing their text; truncated objects, wrong field types and unsupported claims still fail validation. A repair receives the specific validator error. After one repair attempt, malformed output pauses the stage and reports the stage and reason. Raw passages never become fallback facts. Fixture mode preserves and normalizes the source, then pauses for a configured model.

Each validated stage result, draft revision, coverage checkpoint, and usage saves together. Worker claims use concurrency checks; leases renew every 20 seconds and expire after two minutes. Cancellation or lost ownership prevents late results from committing. Retry continues at the incomplete stage. A remote call interrupted before persistence may be repeated and billed again.

Budgets default to 24,000 input characters including instructions/schema/context, 3,000 output tokens, and 100 calls. Limits are adjustable before upload and on retry. Calls include repair attempts. Budget exhaustion pauses processing. Reported tokens are shown where available; monetary costs are not invented.

## Human approval boundary

The review page shows the overview, coverage, conflicts, missing information, category filters, search, 20 proposals per page, and an evidence drawer. Clear proposals are included by default. Users can edit/exclude proposals, save issue resolutions, and edit resume fields or advanced JSON. Saved decisions survive reload; proposal revisions reject stale writes.

Approval requires complete coverage and agent review, resolution/acknowledgment of blocking issues, a usable location and objective, valid state/evidence, and agreement between inventory/skill claims and the resume. The default empty state cannot be approved as a reconstructed ending. Unknown information can remain explicitly unknown after acknowledgment. Unresolved character references must be corrected or excluded.

Approval atomically saves source history, evidence-backed canon, characters, relationships, knowledge, world definitions, events, threads, the pending-action summary, and resume checkpoint. Imported messages remain available for continuity retrieval.

Approval cannot overwrite a branch that advanced after upload. Fork the original checkpoint and import there instead. The original source remains downloadable regardless of parse, provider, review, or approval outcome.

## APIs and compatibility

- Upload: `POST /api/branches/{id}/imports` with multipart `file`, `inputCharacterLimit`, `outputTokenLimit`, `maxCalls`, and optional `provider`/`model` for this import. Omitted profile/model use reconstruction settings; overrides do not change those settings.
- Draft: `GET /api/imports/{id}/draft`; proposals: `GET .../proposals?page=0&category=fact&q=key&history=false`.
- Evidence: `GET .../evidence?page=0&ordinals=0,1`. API ordinals start at zero; UI passage numbers start at one.
- Saved edits: `PUT .../proposals/{proposalId}` with `{ revision, claim, excluded }`; `PUT .../issues/{issueId}` with `{ revision, resolution }`; `PUT .../resume` with `{ revision, resume }`.
- Retry: `POST .../resume-processing` with optional provider/model and budgets. Subsequent stage results record the selected model.
- Approval: `POST .../approval` with `{ expectedCheckpointId, revision }`.
- Reanalysis: `POST .../reanalyze` with processing budgets creates a new draft from the same source.

Legacy imports remain inspectable. Active legacy jobs pause during migration. Reanalysis creates a fresh draft without changing approved campaigns. Portable export version 3 includes sections, draft versions, issues, stage results, and saved decisions; versions 1 and 2 remain readable. Restored active jobs pause and do not copy leases.

The model can still misread narrative meaning. Evidence, chronology, explicit uncertainty, and human approval remain necessary.

Provider timeouts pause the saved stage with an explicit error; they are not mistaken for cancellation and retried repeatedly. Provider/connection errors do not trigger a JSON repair call. A validation repair does count toward the call budget, and budget exhaustion retains the last validation reason when available.
