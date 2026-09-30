using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Story;

public record ProviderCapabilities(bool Available, bool Streaming, bool StructuredOutput, bool Tools, bool ImageInput, string Note);
public record ProviderPrompt(string Instructions, string Input, int MaxOutputTokens = 1800);
public record ProviderEvent(string Type, string? Text = null, int? InputTokens = null, int? OutputTokens = null);
public record ProviderCompletion(string Text, int? InputTokens = null, int? OutputTokens = null);

public interface IStoryProvider
{
    ProviderCapabilities Capabilities { get; }
    IAsyncEnumerable<ProviderEvent> Stream(ProviderPrompt request, CancellationToken cancellation);
    Task<ProviderCompletion> Complete(ProviderPrompt request, JsonElement? schema, CancellationToken cancellation);
}

public sealed class FixtureProvider : IStoryProvider
{
    public ProviderCapabilities Capabilities => new(true, true, true, false, false, "Local deterministic fixture. No AI or remote request.");
    public async IAsyncEnumerable<ProviderEvent> Stream(ProviderPrompt request, [EnumeratorCancellation] CancellationToken cancellation)
    {
        var action = request.Input.Split("PLAYER ACTION:\n").LastOrDefault()?.Trim() ?? "Continue";
        var text = $"[SIMULATED TURN — no AI provider used]\n\nYour action has been recorded: \"{action}\"\n\nThe fixture keeps the approved state unchanged. Select and configure a live narration provider for narrative consequences.";
        foreach (var part in text.Split(' '))
        {
            cancellation.ThrowIfCancellationRequested();
            await Task.Delay(15, cancellation);
            yield return new("delta", part + " ");
        }
        yield return new("done", InputTokens: 0, OutputTokens: 0);
    }
    public Task<ProviderCompletion> Complete(ProviderPrompt request, JsonElement? schema, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var text = request.Instructions.Contains("reconstruction", StringComparison.OrdinalIgnoreCase)
            ? "{\"facts\":[],\"resumeState\":null,\"threads\":[],\"summary\":\"Deterministic review only.\"}"
            : "{\"state\":null,\"facts\":[],\"threads\":[]}";
        return Task.FromResult(new ProviderCompletion(text, 0, 0));
    }
}

public abstract class HttpStoryProvider(HttpClient http, string model) : IStoryProvider
{
    protected readonly HttpClient Http = http;
    protected readonly string Model = string.IsNullOrWhiteSpace(model) ? throw new InvalidOperationException("Configure a model for this provider profile.") : model;
    public abstract ProviderCapabilities Capabilities { get; }
    public abstract IAsyncEnumerable<ProviderEvent> Stream(ProviderPrompt request, CancellationToken cancellation);
    public abstract Task<ProviderCompletion> Complete(ProviderPrompt request, JsonElement? schema, CancellationToken cancellation);
    protected static async Task<HttpResponseMessage> Send(HttpClient http, HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct)
    {
        var response = await http.SendAsync(request, option, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            if (detail.Length > 500) detail = detail[..500];
            throw new InvalidOperationException($"Provider returned {(int)response.StatusCode}: {detail}");
        }
        return response;
    }
    protected static StringContent Body(object value) => new(JsonSerializer.Serialize(value, Json.Options), Encoding.UTF8, "application/json");
    protected static async Task<string> Read(HttpContent content, CancellationToken ct)
    {
        var text = await content.ReadAsStringAsync(ct);
        if (text.Length > 500_000) throw new InvalidOperationException("Provider response exceeded the 500,000 character safety limit.");
        return text;
    }
}

public sealed class OpenAIResponsesProvider(HttpClient http, string model) : HttpStoryProvider(http, model)
{
    public override ProviderCapabilities Capabilities => new(true, true, true, true, true, "OpenAI Responses API with SSE streaming and JSON Schema output.");
    public override async IAsyncEnumerable<ProviderEvent> Stream(ProviderPrompt p, [EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "responses") { Content = Body(new { model = Model, instructions = p.Instructions, input = p.Input, max_output_tokens = p.MaxOutputTokens, stream = true, store = false }) };
        using var res = await Send(Http, req, HttpCompletionOption.ResponseHeadersRead, ct);
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
        int? input = null, output = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ") || line[6..] == "[DONE]") continue;
            using var doc = JsonDocument.Parse(line[6..]); var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "response.output_text.delta" && root.TryGetProperty("delta", out var d)) yield return new("delta", d.GetString());
            if (type == "response.completed" && root.TryGetProperty("response", out var response) && response.TryGetProperty("usage", out var u))
            { input = u.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : null; output = u.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : null; }
            if (type == "error") throw new InvalidOperationException(root.TryGetProperty("message", out var m) ? m.GetString() : "OpenAI stream failed.");
        }
        yield return new("done", InputTokens: input, OutputTokens: output);
    }
    public override async Task<ProviderCompletion> Complete(ProviderPrompt p, JsonElement? schema, CancellationToken ct)
    {
        object format = schema is null ? new { type = "text" } : new { type = "json_schema", name = "open_occ_result", strict = false, schema };
        using var req = new HttpRequestMessage(HttpMethod.Post, "responses") { Content = Body(new { model = Model, instructions = p.Instructions, input = p.Input, max_output_tokens = p.MaxOutputTokens, store = false, text = new { format } }) };
        using var res = await Send(Http, req, HttpCompletionOption.ResponseContentRead, ct);
        using var doc = JsonDocument.Parse(await Read(res.Content, ct));
        string? text = null;
        foreach (var item in doc.RootElement.GetProperty("output").EnumerateArray())
            if (item.TryGetProperty("content", out var content)) foreach (var block in content.EnumerateArray())
                if (block.TryGetProperty("type", out var type) && type.GetString() == "output_text") text = block.GetProperty("text").GetString();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("OpenAI returned no output text.");
        var usage = doc.RootElement.TryGetProperty("usage", out var u) ? u : default;
        return new(text, usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : null, usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : null);
    }
}

public sealed class OpenAICompatibleProvider(HttpClient http, string model) : HttpStoryProvider(http, model)
{
    public override ProviderCapabilities Capabilities => new(true, true, true, true, false, "OpenAI-compatible Chat Completions with SSE and JSON mode.");
    public override async IAsyncEnumerable<ProviderEvent> Stream(ProviderPrompt p, [EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = Body(new { model = Model, messages = new[] { new { role = "system", content = p.Instructions }, new { role = "user", content = p.Input } }, max_tokens = p.MaxOutputTokens, stream = true, enable_thinking = false }) };
        using var res = await Send(Http, req, HttpCompletionOption.ResponseHeadersRead, ct); using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
        int? input = null, output = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ") || line[6..] == "[DONE]") continue;
            using var doc = JsonDocument.Parse(line[6..]); var root = doc.RootElement;
            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 && choices[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String) yield return new("delta", content.GetString());
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            { input = usage.TryGetProperty("prompt_tokens", out var i) ? i.GetInt32() : null; output = usage.TryGetProperty("completion_tokens", out var o) ? o.GetInt32() : null; }
        }
        yield return new("done", InputTokens: input, OutputTokens: output);
    }
    public override async Task<ProviderCompletion> Complete(ProviderPrompt p, JsonElement? schema, CancellationToken ct)
    {
        var schemaText = schema?.GetRawText(); var instructions = schemaText is null ? p.Instructions : p.Instructions + "\nReturn JSON matching this schema exactly:\n" + schemaText;
        var payload = new Dictionary<string, object> { ["model"] = Model, ["messages"] = new[] { new { role = "system", content = instructions }, new { role = "user", content = p.Input } }, ["max_tokens"] = p.MaxOutputTokens, ["stream"] = false };
        if (schema is not null) payload["response_format"] = new { type = "json_object" };
        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = Body(payload) };
        using var res = await Send(Http, req, HttpCompletionOption.ResponseContentRead, ct); using var doc = JsonDocument.Parse(await Read(res.Content, ct));
        var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Provider returned no output text.");
        var usage = doc.RootElement.TryGetProperty("usage", out var u) ? u : default;
        return new(text, usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("prompt_tokens", out var i) ? i.GetInt32() : null, usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("completion_tokens", out var o) ? o.GetInt32() : null);
    }
}

// Lemonade documents OpenAI Chat Completions and SSE, but not response_format JSON mode.
// Structured tasks therefore use the schema in the instruction and the common validator/repair boundary.
public sealed class LemonadeProvider(HttpClient http, string model) : HttpStoryProvider(http, model)
{
    public override ProviderCapabilities Capabilities => new(true, true, false, true, true, "Lemonade OpenAI-compatible Chat Completions with SSE; JSON is instruction-constrained and validated locally.");
    public override async IAsyncEnumerable<ProviderEvent> Stream(ProviderPrompt p, [EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = Body(new { model = Model, messages = new[] { new { role = "system", content = p.Instructions }, new { role = "user", content = p.Input } }, max_tokens = p.MaxOutputTokens, stream = true, enable_thinking = false }) };
        using var res = await Send(Http, req, HttpCompletionOption.ResponseHeadersRead, ct); using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
        int? input = null, output = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ") || line[6..] == "[DONE]") continue;
            using var doc = JsonDocument.Parse(line[6..]); var root = doc.RootElement;
            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 && choices[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String) yield return new("delta", content.GetString());
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            { input = usage.TryGetProperty("prompt_tokens", out var i) ? i.GetInt32() : null; output = usage.TryGetProperty("completion_tokens", out var o) ? o.GetInt32() : null; }
        }
        yield return new("done", InputTokens: input, OutputTokens: output);
    }
    public override async Task<ProviderCompletion> Complete(ProviderPrompt p, JsonElement? schema, CancellationToken ct)
    {
        var instructions = schema is null ? p.Instructions : p.Instructions + "\nReturn JSON matching this schema exactly. Do not add Markdown or commentary:\n" + schema.Value.GetRawText();
        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = Body(new { model = Model, messages = new[] { new { role = "system", content = instructions }, new { role = "user", content = p.Input } }, max_tokens = p.MaxOutputTokens, stream = false, enable_thinking = false }) };
        using var res = await Send(Http, req, HttpCompletionOption.ResponseContentRead, ct); using var doc = JsonDocument.Parse(await Read(res.Content, ct));
        var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Lemonade returned no output text.");
        var usage = doc.RootElement.TryGetProperty("usage", out var u) ? u : default;
        return new(text, usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("prompt_tokens", out var i) ? i.GetInt32() : null, usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("completion_tokens", out var o) ? o.GetInt32() : null);
    }
}

public sealed class AnthropicProvider(HttpClient http, string model) : HttpStoryProvider(http, model)
{
    public override ProviderCapabilities Capabilities => new(true, true, true, true, true, "Anthropic Messages API with SSE and structured outputs.");
    public override async IAsyncEnumerable<ProviderEvent> Stream(ProviderPrompt p, [EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "messages") { Content = Body(new { model = Model, system = p.Instructions, messages = new[] { new { role = "user", content = p.Input } }, max_tokens = p.MaxOutputTokens, stream = true }) };
        using var res = await Send(Http, req, HttpCompletionOption.ResponseHeadersRead, ct); using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
        int? input = null, output = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ")) continue; using var doc = JsonDocument.Parse(line[6..]); var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "content_block_delta" && root.TryGetProperty("delta", out var delta) && delta.TryGetProperty("text", out var text)) yield return new("delta", text.GetString());
            if (type == "message_start" && root.GetProperty("message").TryGetProperty("usage", out var start) && start.TryGetProperty("input_tokens", out var i)) input = i.GetInt32();
            if (type == "message_delta" && root.TryGetProperty("usage", out var end) && end.TryGetProperty("output_tokens", out var o)) output = o.GetInt32();
            if (type == "error") throw new InvalidOperationException("Anthropic stream failed.");
        }
        yield return new("done", InputTokens: input, OutputTokens: output);
    }
    public override async Task<ProviderCompletion> Complete(ProviderPrompt p, JsonElement? schema, CancellationToken ct)
    {
        var payload = new Dictionary<string, object> { ["model"] = Model, ["system"] = p.Instructions, ["messages"] = new[] { new { role = "user", content = p.Input } }, ["max_tokens"] = p.MaxOutputTokens };
        if (schema is not null) payload["output_config"] = new { format = new { type = "json_schema", schema } };
        using var req = new HttpRequestMessage(HttpMethod.Post, "messages") { Content = Body(payload) };
        using var res = await Send(Http, req, HttpCompletionOption.ResponseContentRead, ct); using var doc = JsonDocument.Parse(await Read(res.Content, ct));
        var text = doc.RootElement.GetProperty("content").EnumerateArray().First(x => x.GetProperty("type").GetString() == "text").GetProperty("text").GetString();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Anthropic returned no output text.");
        var usage = doc.RootElement.GetProperty("usage"); return new(text, usage.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : null, usage.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : null);
    }
}

public sealed class OllamaProvider(HttpClient http, string model) : HttpStoryProvider(http, model)
{
    public override ProviderCapabilities Capabilities => new(true, true, true, true, true, "Ollama chat API with NDJSON streaming and JSON Schema output.");
    public override async IAsyncEnumerable<ProviderEvent> Stream(ProviderPrompt p, [EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "chat") { Content = Body(new { model = Model, messages = new[] { new { role = "system", content = p.Instructions }, new { role = "user", content = p.Input } }, stream = true }) };
        using var res = await Send(Http, req, HttpCompletionOption.ResponseHeadersRead, ct); using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
        var emittedDone = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue; using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
            if (root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) && content.GetString() is { Length: > 0 } text) yield return new("delta", text);
            if (root.TryGetProperty("done", out var done) && done.GetBoolean()) { emittedDone = true; yield return new("done", InputTokens: root.TryGetProperty("prompt_eval_count", out var i) ? i.GetInt32() : null, OutputTokens: root.TryGetProperty("eval_count", out var o) ? o.GetInt32() : null); }
        }
        if (!emittedDone) yield return new("done");
    }
    public override async Task<ProviderCompletion> Complete(ProviderPrompt p, JsonElement? schema, CancellationToken ct)
    {
        var payload = new Dictionary<string, object> { ["model"] = Model, ["messages"] = new[] { new { role = "system", content = p.Instructions }, new { role = "user", content = p.Input } }, ["stream"] = false };
        if (schema is not null) payload["format"] = schema.Value;
        using var req = new HttpRequestMessage(HttpMethod.Post, "chat") { Content = Body(payload) };
        using var res = await Send(Http, req, HttpCompletionOption.ResponseContentRead, ct); using var doc = JsonDocument.Parse(await Read(res.Content, ct)); var root = doc.RootElement;
        var text = root.GetProperty("message").GetProperty("content").GetString(); if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Ollama returned no output text.");
        return new(text, root.TryGetProperty("prompt_eval_count", out var i) ? i.GetInt32() : null, root.TryGetProperty("eval_count", out var o) ? o.GetInt32() : null);
    }
}

public sealed class ProviderFactory(IHttpClientFactory clients, IConfiguration config)
{
    public IStoryProvider Create(ProviderProfile profile)
    {
        if (profile.Id == "fixture") return new FixtureProvider();
        var client = clients.CreateClient(); client.Timeout = TimeSpan.FromMinutes(3);
        string baseUrl; string? key;
        if (profile.Id == "openai") { baseUrl = config["OPENAI_BASE_URL"] ?? "https://api.openai.com/v1"; key = config["OPENAI_API_KEY"]; }
        else if (profile.Id == "anthropic") { baseUrl = config["ANTHROPIC_BASE_URL"] ?? "https://api.anthropic.com/v1"; key = config["ANTHROPIC_API_KEY"]; }
        else if (profile.Id == "ollama") { baseUrl = config["OLLAMA_BASE_URL"] ?? "http://host.docker.internal:11434"; if (!baseUrl.TrimEnd('/').EndsWith("/api", StringComparison.OrdinalIgnoreCase)) baseUrl = baseUrl.TrimEnd('/') + "/api"; key = "local"; }
        else if (profile.Id == "lemonade") { baseUrl = config["LEMONADE_BASE_URL"] ?? "http://host.docker.internal:13305/v1"; if (!baseUrl.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) baseUrl = baseUrl.TrimEnd('/') + "/v1"; key = config["LEMONADE_API_KEY"]; }
        else { var prefix = profile.Id.ToUpperInvariant(); baseUrl = config[$"{prefix}_BASE_URL"] ?? throw new InvalidOperationException($"Configure {prefix}_BASE_URL."); key = config[$"{prefix}_API_KEY"]; }
        if (profile.Id is not ("ollama" or "lemonade") && string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException($"Configure the server-side key for {profile.Name}.");
        if (profile.Id == "anthropic") { client.DefaultRequestHeaders.Add("x-api-key", key); client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01"); }
        else if (profile.Id != "ollama" && !string.IsNullOrWhiteSpace(key)) client.DefaultRequestHeaders.Authorization = new("Bearer", key);
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        return profile.Adapter switch { "openai" => new OpenAIResponsesProvider(client, profile.Model), "anthropic" => new AnthropicProvider(client, profile.Model), "ollama" => new OllamaProvider(client, profile.Model), "lemonade" => new LemonadeProvider(client, profile.Model), _ => new OpenAICompatibleProvider(client, profile.Model) };
    }
    public static ProviderCapabilities Capabilities(string adapter) => adapter switch
    { "fixture" => new FixtureProvider().Capabilities, "openai" => new(true, true, true, true, true, "OpenAI Responses API."), "anthropic" => new(true, true, true, true, true, "Anthropic Messages API."), "ollama" => new(true, true, true, true, true, "Ollama chat API."), "lemonade" => new(true, true, false, true, true, "Lemonade Chat Completions API; schema-free JSON validation."), _ => new(true, true, true, true, false, "OpenAI-compatible Chat Completions API.") };
    public static ProviderProfile[] Defaults(IConfiguration c) =>
    [
        new() { Id = "fixture", Name = "Development fixture", Adapter = "fixture", Model = "fixture-v1", Enabled = true },
        new() { Id = "openai", Name = "OpenAI", Adapter = "openai", Model = c["OPENAI_DEFAULT_MODEL"] ?? "" },
        new() { Id = "anthropic", Name = "Anthropic / Claude", Adapter = "anthropic", Model = c["ANTHROPIC_DEFAULT_MODEL"] ?? "" },
        new() { Id = "deepseek", Name = "DeepSeek", Adapter = "openai-compatible", Model = c["DEEPSEEK_DEFAULT_MODEL"] ?? "" },
        new() { Id = "kimi", Name = "Kimi", Adapter = "openai-compatible", Model = c["KIMI_DEFAULT_MODEL"] ?? "" },
        new() { Id = "lemonade", Name = "Lemonade", Adapter = "lemonade", Model = c["LEMONADE_DEFAULT_MODEL"] ?? "" },
        new() { Id = "ollama", Name = "Ollama", Adapter = "ollama", Model = c["OLLAMA_DEFAULT_MODEL"] ?? "" }
    ];
}
