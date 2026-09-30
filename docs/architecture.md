# Foundation architecture

One ASP.NET Core application owns campaign APIs, provider contracts, durable import jobs and EF persistence. React/Vite compiles to static assets served by Nginx, which proxies `/api` to the backend. PostgreSQL is internal to Compose. There are no extra worker deployments, Redis or vector databases.

## State and history

Messages and checkpoints are append-only through the API. Each branch points to its current checkpoint and has an optimistic concurrency revision. A turn captures the expected checkpoint and assembled context, streams narration, obtains a structured transition from the memory route, validates mechanics, then saves messages, complete state, new facts, thread updates, and checkpoint in one transaction. Competing/stale turns fail without a partial commit.

World definitions are reusable and versioned independently of campaigns; a campaign pins one world version. Characters are campaign-level records, while relationships and per-character knowledge are branch-scoped and sequence-aware. Forks copy checkpoints, messages, accepted facts, threads, relationships, knowledge, mechanics entries, events, and the latest summary through the chosen sequence. Pending and rejected import candidates do not cross into a fork.

Import approval validates the state and all decisions before saving. Its branch revision and the job status protect against concurrent generation or duplicate approval. A source accepted before a branch advances cannot later overwrite that new state.

New imports keep normalized evidence, chronological sections, versioned proposals, issues, resume drafts and stage results separate from canon. The reconstruction worker performs extraction, reconciliation, ending reconstruction, an independent audit and one targeted repair. Calls and token usage are durable; renewable leases and concurrency tokens prevent competing workers from publishing results. Invalid output receives one schema repair, then pauses without generating fallback facts or a default scene. Review edits persist before approval; approval checks source citations, unresolved blockers, scene usability and the captured branch head before atomically publishing the reviewed state and entities. Portable exports include draft progress and decisions; restored active jobs pause until explicitly resumed.

## Safety boundaries

All story/model/source text is rendered as escaped text with preserved whitespace. Markdown rendering is deferred, so raw HTML cannot execute. PostgreSQL contains original bytes; only `application/octet-stream` attachment downloads expose them. File names are sanitized, upload size and segment counts bounded, and archives unsupported. Story text never invokes operating-system commands.

An optional bearer token gates `/api`. Browser storage holds that token for the current tab only. Default Compose binding is loopback, with no database port published. External hosting requires configured access control and HTTPS. The three containers are not intended as an anonymous public multi-user service.

## Continuity boundary

Context assembly includes the current complete state, 16 recent messages, up to 35 relevant accepted facts, character knowledge scopes, and 20 active threads ordered by importance. The narrator receives private canon because it must preserve secrets, but its prompt forbids an NPC from revealing a secret unless that NPC is in `knownBy`. Rumors/beliefs remain typed uncertainty. Promises and deadlines use durable narrative threads.

The memory route must return a complete state or `null` for unchanged, plus bounded fact/thread updates. Inventory and skill mutations are rejected unless their key appears in the player action or generated narrative. Schema/semantic failure gets one repair attempt; failure leaves the branch unchanged.

Future work includes embedding/vector retrieval and richer authoring controls. The current foundation includes deterministic inventory/skill mechanics with a ledger, event history and rolling summaries, checkpoint state diffs, audited fact corrections, bounded historical retrieval with search diagnostics, portable JSON campaign export/reimport, and expiring import-job leases for safer recovery.

The root specification remains the product authority. This implementation deliberately satisfies its first foundation objective and documents later-phase features as unfinished.
