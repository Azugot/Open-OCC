# Provider profiles

This foundation includes a provider-neutral narration request, capability reporting, persisted profiles, a deterministic fixture adapter, and explicit unavailable adapters for OpenAI, Anthropic, OpenAI-compatible services, and Ollama.

No live API calls are made. Enabling a stub profile does not enable inference. A selected unavailable adapter produces a corrective error and never silently falls back to a different provider.

The fixture simulates a next turn, records the player action, returns an explicitly labeled response, and copies the approved state unchanged. It does not use model reasoning, invent mechanics, advance time or alter inventory. Failed/cancelled requests do not save a partial turn. A generation interrupted by an application restart is marked `interrupted` at startup.

## Configuration

`.env.example` documents server-side credentials/endpoints, model defaults, and separate `NARRATION_*`, `RECONSTRUCTION_*`, and `MEMORY_*` task selections. Compose passes these to the backend only. The settings UI shows whether configuration is present, not its secret value. API keys must never be put in Vite variables, frontend code, or logs.

Profiles are seeded on first startup. Model and enabled state edited in the UI persist across restarts; environment default-model variables seed new profiles only. `NARRATION_MODEL` overrides the saved narration model when present. Reconstruction and memory task selections are displayed for future use; imports currently always use `deterministic-v1`. The settings page states this limitation.

| Profile | Adapter | Current status |
| --- | --- | --- |
| fixture | fixture | Local simulation; no streaming, tools, images or structured AI output |
| openai | openai | Stub |
| anthropic | anthropic | Stub |
| deepseek, kimi | openai-compatible | Stub |
| ollama | ollama | Stub |

## Verification and next work

Run `dotnet test tests/Story.Tests.csproj`: adapter contract tests confirm unavailable adapters fail explicitly, and fixture cancellation produces no narration. Live connectivity smoke tests cannot run yet because live adapters are not implemented.

Next implement OpenAI-compatible and Ollama adapters with bounded requests, timeouts, capability-specific fields and response validation. Add native OpenAI/Anthropic after that. Include provider/model provenance, output limits, usage where available, and no remote fallback. Do not add AI reconstruction until review and atomic approval tests cover its proposed updates.
