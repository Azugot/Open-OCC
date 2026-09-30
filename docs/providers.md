# Provider profiles

## Detected models and shared connectors

Lemonade and DeepSeek model pickers automatically query the server model list when opened. **Refresh models** bypasses the one-minute cache. Discovery requires only a reachable configured connector (and a DeepSeek key), not an enabled profile or a preselected model; it performs no inference, installation or model load. Lemonade uses `GET /v1/models` for locally available models and excludes known image/audio/embedding models from chat choices. DeepSeek uses authenticated `GET /models`; an omitted DeepSeek base URL defaults to `https://api.deepseek.com`. IDs come from the server rather than a hard-coded catalog. Sources: [Lemonade API](https://lemonade-server.ai/docs/api/openai/#get-v1models), [DeepSeek API](https://api-docs.deepseek.com/zh-cn/api/list-models/).

The connector's saved **default model** remains separate from each task's model choice. Narration, reconstruction and memory can share one connector with different model IDs, saved through task routing. Character and director settings likewise accept separate models from the same connector, including Lemonade. Blank task/role choices use the connector default. Refreshing never replaces saved selections; manual IDs remain editable when discovery fails or a saved model disappears. Changing the connector clears its local unsaved model override. Older API clients that omit model fields retain existing overrides when keeping the same connector; connector changes reset them unless a model is supplied. Imports retain the model captured when they started.

Lemonade agent calls use the existing instruction/example contract and local validation without OpenAI JSON mode. Responses that fail validation pause the turn. Model discovery is not proof of structured-output quality or model reachability during later inference.

Autonomous turns use independently selectable **character** and **director** profiles. Both are enabled fixtures on first startup; `CHARACTER_ADAPTER`/`CHARACTER_MODEL` and `DIRECTOR_ADAPTER`/`DIRECTOR_MODEL` seed their configuration. Saved UI edits take precedence over later environment changes. `CHARACTER_PROVIDER` and `DIRECTOR_PROVIDER` select defaults for new campaigns; existing branches select profiles in **Live world**.

Agent adapters support OpenAI Chat Completions, compatible Chat Completions and Ollama final JSON. OpenAI/Ollama roles can inherit their vendor key/base URL when role values are blank. Compatible roles require an explicit role base URL and key (a placeholder suffices for unauthenticated local servers). Credentials remain server-only. Requests use bounded contexts and final content; reasoning/thinking fields are discarded. Malformed agent output pauses for retry or cancellation. Author views display concise decision summaries.

Open OCC routes three tasks independently: **narration** streams prose to the browser, **reconstruction** extracts import candidates, and **memory** proposes post-turn state/fact/thread updates. The selected profile and model are recorded with each run or import. There is no automatic fallback to another provider.

## Adapters

| Profile | Protocol | Streaming | Structured output |
| --- | --- | --- | --- |
| fixture | Local deterministic fixture | Yes, simulated chunks | Deterministic empty updates |
| openai | OpenAI Responses API | SSE | `text.format` JSON Schema |
| anthropic | Anthropic Messages API | SSE | `output_config.format` JSON Schema |
| deepseek, kimi | OpenAI-compatible Chat Completions | SSE | JSON mode plus schema instruction |
| lemonade | Lemonade Chat Completions | SSE | Schema instruction plus local validation/repair |
| ollama | Ollama `/api/chat` | NDJSON | JSON Schema `format` |

Responses are bounded, requests time out after three minutes, cancellation reaches the outbound HTTP request, non-success responses are surfaced without logging credentials, and token usage is stored when the provider reports it. Structured results get one bounded repair attempt and are rejected if still malformed.

## Configuration

Set endpoint/key/default-model variables from `.env.example`; Compose passes them only to the backend. For Ollama, set the server root such as `http://host.docker.internal:11434`; the adapter adds `/api`. API keys must never be placed in `VITE_` variables, frontend code, or logs.

Lemonade defaults to `http://host.docker.internal:13305/v1` from the backend container. Set `LEMONADE_DEFAULT_MODEL` to an exact model ID returned by Lemonade’s `/v1/models`; set `LEMONADE_API_KEY` only if its server has authentication enabled. Its documented Chat Completions API does not list OpenAI JSON mode, so Open OCC deliberately omits `response_format` and rejects/repairs nonconforming JSON locally. The adapter sends `enable_thinking: false` so Qwen reasoning models return visible answer tokens rather than consuming the response budget in `reasoning_content`.

Profiles are seeded once. The UI persists enabled/model edits and task routing in PostgreSQL. Saving an environment default later does not overwrite a profile the user already edited. A live profile must have a model, be enabled, and have its required server configuration. Use **Test connection** before routing production work.

The test suite uses recorded in-memory HTTP responses to verify five wire protocols without spending tokens. A real connectivity test is intentionally user-triggered because it can contact a configured external service and incur provider usage.
