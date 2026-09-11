# Lumencia Academy Research and Clean-Room Blueprint

## Purpose

This document preserves the OOC/Lumencia research that led to the application requirements. It should guide feature design, especially campaign migration and narrative state, but must not be used to claim access to Lumencia’s private prompt or to copy a creator’s private work.

## Publicly supported observations

Lumencia Academy appears publicly on OOC as a creator custom-prompt story about an imperial academy of sword and sorcery. Public listings indicate a large media set and romance orientation. Player reports demonstrate divergent campaigns, making it an AI-directed sandbox rather than a fixed screenplay.

Repeated player reports support these visible mechanics:

| Area | Observed pattern |
|---|---|
| Player setup | Name, gender, background, department, appearance, notable trait/condition |
| Departments | Knight, Magic, Theology; difficult hybrid enrollment |
| HUD | Tier, affiliation, title, status, skills, core stats, talent, condition, inventory, relationships |
| Scale | Skill ranks around F through SSS, with subgrades/progress |
| Primary loops | Academy life, training, relationships, exams, politics, anomalies, conspiracy |
| State risk | Long sessions can drift, forget schedule/state, or reset visible stats |

Community creator guides report approximate OOC behavior:

- A custom prompt offers roughly 10,000 creator characters; the default option has less user space plus hidden scaffolding.
- A prologue is limited to roughly 2,000 characters.
- Keyword Books may allow many entries but only three reliably trigger in a turn, so critical rules must be compact and repeated.
- The platform-managed stats/endings features are not considered reliable enough to be authoritative.
- Context order gives priority to permanent prompt rules and late-injected keyword/memory data.

## What remains unknown

No public source proved any of the following:

- Exact Lumencia prompt and OOC hidden scaffolding.
- Exact model/version, sampling parameters, context limit, or retrieval configuration.
- Complete canonical NPC profiles, media, keyword books, slash commands, progression formulas, and editor setup.
- A single canonical chapter script.
- The boundary between official story facts and organic player-session invention.

Treat community discussions and fan recreations only as corroborating behavioral evidence.

## App design implications

The project should solve the problems a prompt-only story system has:

1. Store canonical state outside the model.
2. Maintain source evidence for imported facts.
3. Keep branches isolated.
4. Track what each NPC knows, suspects, and does not know.
5. Record promises, deadlines, open mysteries, and state changes in structured form.
6. Validate any AI-proposed state update before it becomes authoritative.
7. Keep the complete transcript searchable; summaries are aids, not replacement history.

## Original clean-room academy template

The following is an original design pattern suitable for an example campaign. It is intentionally distinct from Lumencia.

### Setting: Crownspire Academy

Imperial Year 731. Crownspire Academy, above the capital of Valedorn, trains adult students in:

- **Vanguard:** martial aura, weapons, tactics, conditioning.
- **Arcanum:** mana, spell circles, elemental/conceptual magic.
- **Covenant:** wards, healing, oaths, spirits, divinity.

Hybrid study is legal but creates schedule conflict, slower foundations, and social resistance. Beneath the academy sits an inaccessible pre-imperial Undercrypt. A masked group called the Hollow Court seeks seven lock-shards connected to it.

### Story structure

Use simultaneous fronts instead of a forced main script:

| Front | Function |
|---|---|
| Academy life | Classes, dorms, clubs, exams, mentors, festivals |
| Bonds | Friendship, rivalry, romance, promises, reconciliation |
| Status | Department standing, discipline, noble pressure, reputation |
| Breaches | Monsters, relic failures, spirit disturbances, sealed ruins |
| Hollow Court | Surveillance, recruitment, theft, infiltration, ritual |

The director advances at most one danger front in an ordinary scene, foreshadows before escalation, and keeps normal academy life active between crises.

### Clean-room state model

```text
Tier: Initiate | Year/Dept: 1 / Vanguard | Title: None
Status: light fatigue
Core: Body F ●●○○○ · Aether F ●○○○○ · Insight F ●●○○○ · Resolve F ●●○○○
Skills: sword practice F ●●○○○; aura circulation F ●○○○○
Gear: uniform, practice sword, 20 crowns
Bonds: Lyra Fen +8 / rival; Nessa Calder +3 / curious
Clocks: Term 1/5 · Breach 0/5 · Court 0/5 · Suspicion 0/5
Open: registration deadline; missing library key
```

Use five progress pips instead of unrestricted `0–100` arithmetic. Ordinary completed practice gains at most one pip; difficult supervised success earns one or two; costly breakthroughs can earn two or three. Five pips promote one rank. Power must have a limitation, cost, preparation requirement, counter, fatigue effect, or exposure risk.

### Clean-room prompt skeleton

```markdown
# CROWNSPIRE ACADEMY

Role: Act as game master, world-builder, and all NPCs in a persistent interactive fantasy academy simulation.

## Player agency
- Never write the player’s actions, dialogue, thoughts, feelings, decisions, or hidden history.
- NPCs know only observed facts or facts learned in play.
- Preserve accepted canon, current state, rumors, and secrets as separate categories.

## Prose
- Write 180–420 words in second person.
- Use italics for description/actions and bold names for NPC dialogue.
- End at a meaningful decision or reaction; free-form player action is always valid.

## Initialization
Ask for name/pronouns, adult age, background, department, appearance, and one talent/complication/secret. Balance exceptional concepts with a meaningful limitation. Start at registration rather than resolving enrollment immediately.

## Director rules
Maintain academy life, bonds, status, breaches, and Hollow Court fronts. Advance no more than two clocks in a turn. Foreshadow before escalation. Select hooks that follow from schedules, NPC goals, player choices, rumors, deadlines, and earlier evidence.

## Continuity
Treat rumors as unverified. Keep secrets scoped to characters who learned them. Advance time plausibly. New powers require instruction, experimentation, or meaningful practice.

## Output
Start with date/time/location/objective. End with a compact state block containing only changed/relevant data.
```

### Major NPC pattern

Every active NPC should hold an immediate goal, private pressure, incomplete knowledge, and a boundary. Example roles:

- Headmistress Mara Vale: politically shrewd hybrid scholar protecting academy independence.
- Captain Cassia Vorn: strict Vanguard leader who respects discipline and adaptation.
- Professor Selene Oris: mana anatomist studying ethical repair of damaged channels.
- Prior Aurelius Thane: compassionate Covenant leader who believes miracles create obligation.
- Lyra Fen: earnest noble student pursuing merit beyond family status.
- Nessa Calder: reserved researcher whose trust grows through evidence and honesty.
- Talia Ash: spirit-caller whose companion senses a danger in the academy.
- Kael Ren: roommate with a criminal-guild debt.
- Princess Isolde Aerwyn: politically constrained royal testing others’ motives.

## Suggested future import strategy for Lumencia playthroughs

When the user provides an export, preserve what actually happened in that campaign. Do not overwrite its specific setting, cast, mechanics, relationships, or narrative outcomes with this template. The template exists to test the app and to support original new campaigns.

## Sources consulted during research

- OOC story listing: https://ooc.ai/story/69f414d90abbab6f6a2c8131
- OOC app description: https://play.google.com/store/apps/details?id=com.newai.ooc
- OOC creator guide: https://www.reddit.com/r/OOC_official/comments/1waruee/gold_standard_story_creation_guide/
- OOC prompt/scaffolding tutorial: https://www.reddit.com/r/OOC_official/comments/1v5e5l4/basic_story_custom_prompt_tutorial/
- OOC player guide: https://www.reddit.com/r/OOC_official/comments/1vkw4in/comprehensive_player_guide/
- Lumencia player reports: https://www.reddit.com/r/OOC_official/comments/1v13ho1/thoughts_on_lumencia_academy/
- Lumencia department discussion: https://www.reddit.com/r/OOC_official/comments/1vubi0f/lumencia_academy_departments/

