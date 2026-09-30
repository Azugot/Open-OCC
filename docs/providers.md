# Provider profiles

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
