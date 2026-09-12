namespace Story;

public record ProviderCapabilities(bool Available, bool Streaming, bool StructuredOutput, bool Tools, bool ImageInput, string Note);
public record NarrationRequest(string Action, StoryState State, string[] PublicFacts);
public interface IStoryProvider
{
    ProviderCapabilities Capabilities { get; }
    Task<string> Narrate(NarrationRequest request, CancellationToken cancellation);
}
public sealed class FixtureProvider : IStoryProvider
{
    public ProviderCapabilities Capabilities => new(true, false, false, false, false, "Local deterministic fixture. No AI or remote request.");
    public async Task<string> Narrate(NarrationRequest request, CancellationToken cancellation)
    {
        await Task.Delay(700, cancellation);
        return $"[SIMULATED TURN — no AI provider used]\n\nYour action has been recorded: \"{request.Action}\"\n\nThe scene remains at {request.State.Location}. Your next objective is still: {request.State.Objective}.\n\nThis development fixture preserves the approved state. Configure a live adapter in a later milestone for narrative consequences.";
    }
}
public sealed class UnimplementedProvider(string adapter) : IStoryProvider
{
    public ProviderCapabilities Capabilities => new(false, false, false, false, false,
        $"{adapter} adapter is a foundation stub. Live requests are not implemented; no automatic fallback.");
    public Task<string> Narrate(NarrationRequest request, CancellationToken cancellation) =>
        throw new InvalidOperationException(Capabilities.Note);
}
public static class ProviderRegistry
{
    public static IStoryProvider Resolve(string adapter) => adapter == "fixture" ? new FixtureProvider() : new UnimplementedProvider(adapter);
    public static ProviderProfile[] Defaults(IConfiguration c) =>
    [
        new() { Id = "fixture", Name = "Development fixture", Adapter = "fixture", Model = "fixture-v1", Enabled = true },
        new() { Id = "openai", Name = "OpenAI", Adapter = "openai", Model = c["OPENAI_DEFAULT_MODEL"] ?? "" },
        new() { Id = "anthropic", Name = "Anthropic / Claude", Adapter = "anthropic", Model = c["ANTHROPIC_DEFAULT_MODEL"] ?? "" },
        new() { Id = "deepseek", Name = "DeepSeek", Adapter = "openai-compatible", Model = c["DEEPSEEK_DEFAULT_MODEL"] ?? "" },
        new() { Id = "kimi", Name = "Kimi", Adapter = "openai-compatible", Model = c["KIMI_DEFAULT_MODEL"] ?? "" },
        new() { Id = "ollama", Name = "Ollama", Adapter = "ollama", Model = c["OLLAMA_DEFAULT_MODEL"] ?? "" }
    ];
}
