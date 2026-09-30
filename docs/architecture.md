# Story workspace architecture

One ASP.NET Core application owns campaign APIs, provider contracts, durable import jobs and EF persistence. React/Vite compiles to static assets served by Nginx, which proxies `/api` to the backend. PostgreSQL is internal to Compose. There are no extra worker deployments, Redis or vector databases.

## State and history

Messages and checkpoints are append-only through the API. Each branch points to its current checkpoint and has an optimistic concurrency revision. A turn captures the expected checkpoint, persists a generation record, obtains a fixture result, then saves both messages and the new checkpoint in one database transaction. Competing/stale turns fail without a partial commit.

Forks copy checkpoints and messages only through the chosen sequence. Approved facts are copied only when their effective sequence is at or before that checkpoint, with evidence preserved. Pending and rejected import candidates do not cross into a fork. This favors explicit isolation over storage optimization in the first release.

Import approval validates the state and all decisions before saving. Its branch revision and the job status protect against concurrent generation or duplicate approval. A source accepted before a branch advances cannot later overwrite that new state.

## Safety boundaries

All story/model/source text is rendered as escaped text with preserved whitespace. Markdown rendering is deferred, so raw HTML cannot execute. PostgreSQL contains original bytes; only `application/octet-stream` attachment downloads expose them. File names are sanitized, upload size and segment counts bounded, and archives unsupported. Story text never invokes operating-system commands.

An optional bearer token gates `/api`. Browser storage holds that token for the current tab only. Default Compose binding is loopback, with no database port published. External hosting requires configured access control and HTTPS. The three containers are not intended as an anonymous public multi-user service.

## Next milestones

The autonomous slice now uses checkpoint world JSON, persisted generation drafts, independent perception-filtered character contexts, structured director resolutions, server-generated d20 checks, scoped memory retrieval and an interactive graph. See [storyteller behavior](storyteller.md). OpenAI-compatible and Ollama adapters support final JSON responses; token streaming is deferred.

Remaining milestones include agent-assisted extraction/reconstruction, evaluation against the supplied transcript, reusable world definitions, full progression mechanics, edit/regenerate shortcuts, portable export/reimport and distributed worker leasing.

The root specification remains the product authority; this slice adds the autonomous storyteller while keeping transcript reconstruction and later features explicit.
