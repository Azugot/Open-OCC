# Autonomous storyteller slice

Create a synthetic Crownspire campaign, submit an action, and open **Live world**. The fixture needs no credentials. **Author view** reveals private character workspaces, off-screen activity, all graph entities, dice and each owner's memories. It is a spoiler preference for this single-user app, not an access-control boundary.

## Turn execution

The director selects up to six relevant NPCs and assesses the player's attempt. Characters independently propose speech/actions from their own perceptions and memories. The director assesses and resolves each attempt, publishes ordered events, and narrates only the player's observed events. Subsequent characters read earlier published events they can perceive. Default limits are three beats/six NPCs, configurable within 1–5 beats and 1–10 NPCs. Player decisions stop further scene reactions. Selected off-screen actors update once when story time advances, even when scene control has already returned to the player.

Scene events reach present observers. Private events reach the actor; directed events reach explicitly named present recipients. Previously encountered remote entities do not reveal their current whereabouts. Privately introduced entities remain restricted until an observable event establishes discovery. Public descriptions should describe observable traits; secrets belong in scoped events/memories. Accepted import facts preserve their kind/confidence and knowledge scope: narrator-only facts reach the director and explicitly named knowers. Private character knowledge remains owner-scoped. Versioned world rules and active narrative threads inform the director.

Routine actions need no roll. For uncertain or contested attempts, the director fixes difficulty (1–40) and named modifiers (−20..20 each) using player skills/inventory, character stats and relevant precedents. The server persists that assessment before generating a cryptographic d20. Total >= difficulty succeeds; natural 1 and 20 cannot override totals. Failed checks cannot apply successful movement, entity creation or relationship effects. Models still require evaluation for coherent interpretations and narration.

## History, graph and memory

Each checkpoint contains a complete world version: stable entities, ordered occurrences, typed relationships with event evidence, character state, dice and scoped memories. Moving actors and newly encountered entities can change the graph. Historical graphs and memories are read-only. Forks copy versions only through the selected checkpoint. Import approval starts a neutral scene from the reviewed state instead of copying unrelated synthetic secrets.

Short-term memories retain the latest 24 unpinned observations per owner. Durable memories preserve observed episodes and actor beliefs/rumors. Retrieval first filters by owner, then includes up to 20 pins, 16 recent memories and 16 relevant long-term entries within a 32,000-character memory budget. Event and world context also have bounded windows. Editing memories changes future agent context without rewriting occurrences. Corrections/removal update an actor's concise belief summary when applicable. Every author edit creates a checkpoint and a content-free audit message. Original historical versions remain available. Removing a memory does not erase its source occurrence from history.

World and generation JSON are stored in PostgreSQL text columns to match the existing checkpoint foundation. No graph/vector database is needed. This slice supports up to 500 world entities. Reusable world definitions, reviewed transcript reconstruction, deterministic mechanics and narrator-only token streaming are integrated alongside the autonomous engine. Automated RPG progression and agent token streaming remain later work.

## Recovery and API additions

- `GET /api/branches/{id}/engine?checkpointId=...`: branch-validated world version and current drafts; author data is returned to the authenticated single-user client.
- `POST /api/branches/{id}/engine/settings`: `{expectedCheckpointId, settings: {characterProfile, directorProfile, maxBeats, maxNpcs}}`.
- `POST /api/branches/{id}/memories`: `{expectedCheckpointId, id, text, pinned, remove}` for an existing memory.
- `POST /api/runs/{id}/retry` and `/cancel`: resume validated work or discard the pending turn.
- Existing turn API now orchestrates agents. Provider role updates also accept `adapter` for `character`/`director`.
- `POST /api/branches/{id}/turns/stream` retains optional narrator-only streaming and structured continuity updates.

Validated model outputs, rolls and applied draft steps are saved before final commit. Failure pauses the run; retry reuses saved outputs and rolls, regenerating only unsuccessful calls. Restart converts autonomous runs to paused and legacy streams to interrupted. Cancellation signals the active agent provider request. Draft activity is labeled separately from committed history. Final narrative, world, memories and checkpoint commit in one EF transaction guarded by checkpoint/revision and generation status. Stale drafts must be cancelled. Finish or cancel a draft before changing campaign settings or memories. Provider configuration itself may be corrected while a draft is paused. Mechanics/canon correction checkpoints preserve graph and memory history; narrator-only scene updates preserve existing entities and memories. Portable exports retain world snapshots and saved agent drafts.

Run one backend instance. The in-process execution/cancellation registry and PostgreSQL optimistic concurrency guard overlapping requests; distributed leasing remains outside this slice. API keys and original transcripts are never browser credentials. Remote provider requests transmit the selected campaign context only after selecting a live profile.
