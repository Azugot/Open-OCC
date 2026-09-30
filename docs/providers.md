# Story agent providers

Configure independent `character` and `director` profiles in provider settings, then select them in the storyteller settings. Each role has its own adapter/model, so both can use the same vendor with a small character model and a larger director model. Characters share their role profile but receive separate, perception-filtered contexts. The deterministic `fixture` adapter requires no service or credentials.

Both role profiles are seeded as enabled fixtures on first startup. `CHARACTER_ADAPTER` / `CHARACTER_MODEL` and `DIRECTOR_ADAPTER` / `DIRECTOR_MODEL` seed their adapter/model; set both when enabling a live role. Changes saved in the UI persist and environment seed values do not overwrite saved profiles. Select role IDs with `CHARACTER_PROVIDER=character` and `DIRECTOR_PROVIDER=director` for new campaigns, or select them in an existing branch's engine settings. Keeping either selection at `fixture` uses simulation for that role.

Remote adapters send structured, non-streaming JSON requests. The engine validates the returned proposals and resolutions before any checkpoint commit. JSON mode guarantees neither correct story semantics nor a matching contract, so malformed responses pause the run for retry or cancellation. There is no automatic provider fallback.

Set credentials and endpoints on the **server**, never in frontend configuration, profile JSON, or a model prompt. The environment prefix is the profile ID uppercased with hyphens changed to underscores; `actor-small` uses `ACTOR_SMALL_API_KEY` and `ACTOR_SMALL_BASE_URL`.

The two role profiles first read `CHARACTER_API_KEY` / `CHARACTER_BASE_URL` or `DIRECTOR_API_KEY` / `DIRECTOR_BASE_URL`. For the `openai` adapter, unset or blank role values use `OPENAI_API_KEY` / `OPENAI_BASE_URL`; for `ollama`, they use `OLLAMA_API_KEY` / `OLLAMA_BASE_URL`. Nonblank role values override vendor values; invalid URLs produce an error. Unset or blank vendor base URLs use the adapter's default URL below. For `openai-compatible`, supply both role key and base URL explicitly; the application does not infer DeepSeek, Kimi, or another vendor. Ordinary profile IDs do not inherit another profile's configuration.

For example, set both role adapters to `openai`, set `CHARACTER_MODEL` to your chosen small model and `DIRECTOR_MODEL` to your chosen director model, and provide `OPENAI_API_KEY` once. You can instead use `CHARACTER_API_KEY` and `DIRECTOR_API_KEY` to separate credentials. Set these through your server environment; never paste keys into profile names or model fields.

| Adapter | Key | Base URL | Request |
| --- | --- | --- | --- |
| `openai` | Required | Defaults to `https://api.openai.com/v1` | `POST chat/completions`, JSON mode and `max_completion_tokens` |
| `openai-compatible` | Required; use a local placeholder for servers without auth | Required, including any API prefix such as `/v1` | `POST chat/completions`, JSON mode and `max_tokens` |
| `ollama` | Optional | Defaults to `http://host.docker.internal:11434` for Docker; native hosts can set `http://localhost:11434` | `POST api/chat`, `format: "json"`, `stream: false` |

Each profile must be enabled and have an explicit model name. OpenAI-compatible servers differ in support: select a model that supports the request format and configure its base URL accordingly. Ollama models must already be installed. Capability status indicates valid configuration; it does not probe reachability or model availability.

Requests contain the task, bounded context, and an example defining the expected result fields/types. Trusted task directives are appended to the system message, separately from world context. Each call has a two-minute deadline, a 1 MiB response envelope limit, and a 64 KiB final-content limit. Cancellation reaches the HTTP request. HTTP, connection, and parsing errors are sanitized; provider error bodies and credentials are never returned to the browser. The default client refuses redirects.

Only the assistant's final `content` field is deserialized. Reasoning or `thinking` fields are ignored and never persisted. Author panels display explicit concise decision summaries returned in the application contract.

The adapter tests include a real HTTP loopback transport exercise as well as mocked requests. This verifies the integration protocol without credentials or inference. An actual configured model still needs a turn smoke test in the deployed environment.

Implementation references: [OpenAI Chat Completions](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create), [OpenAI JSON mode](https://developers.openai.com/api/docs/guides/structured-outputs), [DeepSeek's compatible chat contract](https://api-docs.deepseek.com/api/create-chat-completion/), and [Ollama chat API](https://docs.ollama.com/api/chat).
