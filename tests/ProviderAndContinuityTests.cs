using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Story;
using Xunit;

namespace StoryTests;

public class ProviderAndContinuityTests
{
    private sealed class Handler(Func<HttpRequestMessage, string, string> reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(reply(request, body), Encoding.UTF8, "application/json") };
        }
    }
    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    [Fact]
    public async Task OpenAiResponsesParsesSseAndStructuredCompletion()
    {
        var http = Client((_, body) => body.Contains("\"stream\":true")
            ? "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hello\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":4,\"output_tokens\":1}}}\n\n"
            : "{\"output\":[{\"content\":[{\"type\":\"output_text\",\"text\":\"{\\\"state\\\":null,\\\"facts\\\":[],\\\"threads\\\":[]}\"}]}],\"usage\":{\"input_tokens\":4,\"output_tokens\":8}}");
        var provider = new OpenAIResponsesProvider(http, "test");
        Assert.Equal("Hello", await StreamText(provider));
        Assert.Contains("\"facts\"", (await provider.Complete(new("i", "x"), ContinuityService.TransitionSchema, default)).Text);
    }

    [Fact]
    public async Task OpenAiCompatibleParsesSseAndJsonCompletion()
    {
        var http = Client((_, body) => body.Contains("\"stream\":true")
            ? "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\ndata: [DONE]\n\n"
            : "{\"choices\":[{\"message\":{\"content\":\"{}\"}}],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":1}}");
        var provider = new OpenAICompatibleProvider(http, "test");
        Assert.Equal("Hi", await StreamText(provider));
        Assert.Equal("{}", (await provider.Complete(new("i", "x"), null, default)).Text);
    }

    [Fact]
    public async Task AnthropicParsesSseAndStructuredCompletion()
    {
        var http = Client((_, body) => body.Contains("\"stream\":true")
            ? "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":2}}}\n\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Claude\"}}\n\ndata: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":1}}\n\n"
            : "{\"content\":[{\"type\":\"text\",\"text\":\"{}\"}],\"usage\":{\"input_tokens\":2,\"output_tokens\":1}}");
        var provider = new AnthropicProvider(http, "test");
        Assert.Equal("Claude", await StreamText(provider));
        Assert.Equal("{}", (await provider.Complete(new("i", "x"), ContinuityService.TransitionSchema, default)).Text);
    }

    [Fact]
    public async Task OllamaParsesNdjsonAndStructuredCompletion()
    {
        var http = Client((_, body) => body.Contains("\"stream\":true")
            ? "{\"message\":{\"content\":\"Local\"},\"done\":false}\n{\"message\":{\"content\":\"\"},\"done\":true,\"prompt_eval_count\":2,\"eval_count\":1}\n"
            : "{\"message\":{\"content\":\"{}\"},\"prompt_eval_count\":2,\"eval_count\":1}");
        var provider = new OllamaProvider(http, "test");
        Assert.Equal("Local", await StreamText(provider));
        Assert.Equal("{}", (await provider.Complete(new("i", "x"), ContinuityService.TransitionSchema, default)).Text);
    }

    [Fact]
    public async Task LemonadeUsesDocumentedChatCompletionShapeWithoutJsonMode()
    {
        var captured = new List<string>();
        var http = Client((_, body) =>
        {
            captured.Add(body);
            return body.Contains("\"stream\":true")
                ? "data: {\"choices\":[{\"delta\":{\"content\":\"Fresh\"}}]}\n\ndata: [DONE]\n\n"
                : "{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}";
        });
        var provider = new LemonadeProvider(http, "Qwen3-0.6B-GGUF");
        Assert.Equal("Fresh", await StreamText(provider));
        Assert.Equal("{}", (await provider.Complete(new("i", "x"), ContinuityService.TransitionSchema, default)).Text);
        Assert.All(captured, body => Assert.DoesNotContain("response_format", body));
        Assert.Contains(captured, body => body.Contains("Return JSON matching this schema"));
        Assert.All(captured, body => Assert.Contains("\"enable_thinking\":false", body));
    }

    [Fact]
    public void MechanicsRejectsUnsupportedInventoryMutation()
    {
        var after = StoryState.Synthetic with { Inventory = new(StoryState.Synthetic.Inventory) { ["Crowns"] = 999 } };
        Assert.False(ContinuityService.MechanicsSupported(StoryState.Synthetic, after, "I look around", "Nothing changes."));
        Assert.True(ContinuityService.MechanicsSupported(StoryState.Synthetic, after, "I count my Crowns", "The Crowns total changes to 999."));
    }

    [Fact]
    public async Task ContextIncludesKnowledgeScopeAndImportantThreads()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new StoryDb(new DbContextOptionsBuilder<StoryDb>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var branch = await CampaignService.Create(db, new("Continuity"), default);
        db.Facts.Add(new Fact { BranchId = branch.Id, Text = "The vault key is silver.", ReviewStatus = "accepted", Visibility = "narrator", KnownByJson = Json.Write(new[] { "Lyra" }), Kind = "secret" });
        db.NarrativeThreads.Add(new NarrativeThread { BranchId = branch.Id, Title = "Meet Lyra at noon", Details = "A promise with a deadline.", Kind = "promise", Importance = 5 });
        await db.SaveChangesAsync();
        var context = await new ContinuityService().Assemble(db, branch.Id, StoryState.Synthetic, "Ask Lyra about the key", default);
        Assert.Contains(context.RelevantFacts, x => x.KnownBy.SequenceEqual(new[] { "Lyra" }));
        Assert.Contains(context.ActiveThreads, x => x.Kind == "promise");
    }

    [Fact]
    public async Task AiImportPersistsOnlyEvidenceLinkedStructuredCandidates()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new StoryDb(new DbContextOptionsBuilder<StoryDb>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var branch = await CampaignService.Create(db, new("Reconstruction", false), default);
        var source = new ImportedSource { CampaignId = branch.CampaignId, FileName = "story.txt", Bytes = Encoding.UTF8.GetBytes("Lyra promises to return before noon.") };
        var job = new ImportJob { SourceId = source.Id, BranchId = branch.Id, ExpectedCheckpointId = branch.HeadCheckpointId, Provider = "deepseek", Model = "test", Method = "ai-reconstruction-v1" };
        db.Providers.Add(new ProviderProfile { Id = "deepseek", Name = "DeepSeek", Adapter = "openai-compatible", Model = "test", Enabled = true });
        db.AddRange(source, job); await db.SaveChangesAsync();
        var json = "{\"facts\":[{\"text\":\"Lyra promised to return before noon.\",\"kind\":\"fact\",\"visibility\":\"public\",\"knownBy\":[\"Lyra\"],\"confidence\":0.95,\"evidenceOrdinals\":[0]}],\"resumeState\":null,\"threads\":[{\"title\":\"Lyra returns before noon\",\"details\":\"Lyra made a time-bound promise.\",\"kind\":\"promise\",\"status\":\"active\",\"importance\":5}],\"summary\":\"A promise is pending.\"}";
        var handler = new Handler((_, _) => "{\"choices\":[{\"message\":{\"content\":" + System.Text.Json.JsonSerializer.Serialize(json) + "}}],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":20}}");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DEEPSEEK_BASE_URL"] = "https://example.test/v1", ["DEEPSEEK_API_KEY"] = "test" }).Build();
        var routing = new ProviderRouting(new ProviderFactory(new ClientFactory(handler), config), config);
        await ImportWorker.ProcessBatch(db, job, routing, default);
        Assert.Equal("review", job.Status);
        Assert.Single(await db.Facts.Where(x => x.JobId == job.Id).ToListAsync());
        Assert.Single(await db.Evidence.ToListAsync());
        Assert.Contains("promise", job.ProposedThreadsJson);
        Assert.Equal(12, job.InputTokens);
    }

    private static HttpClient Client(Func<HttpRequestMessage, string, string> reply) => new(new Handler(reply)) { BaseAddress = new Uri("https://example.test/v1/") };
    private static async Task<string> StreamText(IStoryProvider provider)
    {
        var text = new StringBuilder();
        await foreach (var item in provider.Stream(new("i", "x"), default)) if (item.Type == "delta") text.Append(item.Text);
        return text.ToString();
    }
}
