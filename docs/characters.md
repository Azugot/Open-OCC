# Characters

Open **Live world → Characters**, or **View all characters** in the story sidebar. The roster has no eight-character limit. Search names and aliases, then filter by all known, present, player, or NPCs and beings. Unknown whereabouts are shown explicitly.

Profiles contain Overview, Relationships, Capabilities, and History. Enable Author view to create characters and edit their description, appearance, personality, speaking style, aliases, location, private guidance, and motives. Changes require **Save** and create a checkpoint on the selected timeline. Leaving an unsaved editor offers Save, Discard, or Stay. Historical checkpoints are read-only. Running and paused story drafts block conflicting edits; a stale save retains entered values for comparison.

## Identity and timelines

World entity IDs own profiles, capabilities, memories, and relationship endpoints. Optional campaign-character links connect legacy knowledge records. Existing links are resolved only when both the world name and campaign record match uniquely. Ambiguous identities can be selected explicitly under Advanced. Renaming keeps the entity ID and retains the previous name as an alias.

Profile details belong to world snapshots, rather than the shared campaign registration. Forks copy the chosen snapshot and diverge independently. Provider and director configuration stays separate from profile editing.

## Relationships

Connections remain directed and retain distinct types. Author saves append changes rather than overwrite history. Ending a relationship preserves previous labels, scores, and reasons. Imported connections and recorded checkpoint differences supply earlier history when available; a current legacy value without evidence is marked **Earlier history unavailable**. Interactions appear separately and do not imply score changes. Supporting events link to the immutable source viewer when a citation exists.

## Capabilities

Abilities, powers, and proficiencies have stable IDs, owners, descriptions, optional string ranks, limitations, visibility, and active status. Inactive entries remain editable and can be reactivated. Existing player skills become proficiencies with their original ranks. Author edits and the existing manual mechanics API synchronize the player skill display.

Both story modes receive applicable capabilities and limitations. Character prompts receive their own profile and visible capabilities; the director receives author context. Story generation preserves existing proficiency ranks and cannot persist new powers or capability progression. This feature adds no automatic costs, cooldowns, or roll bonuses. Portraits are excluded.

Reviewed skill proposals may specify an owner and an ability/power/proficiency category. Missing values retain the legacy player-proficiency meaning. Unknown owners block approval. Private NPC capability evidence stays outside player memories and public profile history.

## APIs and portability

- `GET /api/branches/{id}/characters?checkpointId=…&author=true` assembles a checkpoint-aware roster.
- `GET /api/branches/{id}/characters/{entityId}` returns a profile in the same scope.
- `POST /api/branches/{id}/characters` creates a profile and timeline entity.
- `PUT /api/branches/{id}/characters/{entityId}` edits a profile.
- `PUT /api/branches/{id}/characters/{entityId}/capabilities` saves a capability.
- `PUT /api/branches/{id}/character-relationships` appends a relationship change.

Mutations require `expectedCheckpointId`. Existing campaign character and relationship routes use the shared service. As with other author tools, `author=true` selects the author workspace; it is not a separate authentication role.

Portable exports remain version **5**. New world fields are optional and older snapshots default safely. Import validates owners, campaign links, relationship references, and citation references before persistence, remaps campaign-character links, and preserves atomic restore.

## Verification

Character unit and HTTP regressions cover stable renames, legacy string ranks, directed history, endings, fork isolation, private visibility, ambiguous names, checkpoint recovery, stale/paused-run guards, narrator and character contexts, manual skill synchronization, reviewed NPC owners, and version-5 restore.

Run `dotnet test tests/Story.Tests.csproj`, `npm --prefix frontend run build`, and `node tests/review-draft.mjs`. For PostgreSQL HTTP checks use the isolated stack described in `import-flow-verification.md`, then run `node tests/character-smoke.mjs http://localhost:8082`. The script refuses other ports to protect normal campaigns. Existing import and skill-repair smoke checks exercise recorded providers without live model calls.
