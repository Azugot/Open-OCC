using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Story;
using Xunit;

namespace StoryTests;

public class ModelDiscoveryTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return send(request, ct); }
    }
    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, false); }
    private static IConfiguration Config(params (string, string)[] values) => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(x => x.Item1, x => (string?)x.Item2)).Build();
    private static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData("lemonade", "lemonade", "http://local.test/v1/models", null)]
    [InlineData("deepseek", "openai-compatible", "https://api.deepseek.com/models", "secret")]
    public async Task ListsModelsWithoutEnablingOrSelectingOne(string id, string adapter, string endpoint, string? key)
    {
        var profile = new ProviderProfile { Id = id, Adapter = adapter, Enabled = false, Model = "" };
        var handler = new Handler((req, _) =>
        {
            Assert.Equal(HttpMethod.Get, req.Method); Assert.Null(req.Content); Assert.Equal(endpoint, req.RequestUri!.AbsoluteUri);
            Assert.Equal(key, req.Headers.Authorization?.Parameter);
            return Task.FromResult(Reply("{\"data\":[{\"id\":\"small\",\"name\":\"Small model\"},{\"id\":\"large\"}]}"));
        });
        var config = id == "deepseek" ? Config(("DEEPSEEK_API_KEY", "secret")) : Config(("LEMONADE_BASE_URL", "http://local.test"));
        var clients = new Clients(handler); var discovery = new ModelDiscovery(new ProviderFactory(clients, config), clients);
        var catalog = await discovery.Read(profile, false, default);
        Assert.True(catalog.Supported); Assert.Null(catalog.Error); Assert.Equal(2, catalog.Models.Length);
        Assert.False(profile.Enabled); Assert.Equal("", profile.Model);
    }

    [Fact]
    public async Task CacheAndRefreshUseListRequestsOnly()
    {
        var handler = new Handler((_, _) => Task.FromResult(Reply("{\"data\":[{\"id\":\"model\"}]}")));
        var clients = new Clients(handler); var discovery = new ModelDiscovery(new ProviderFactory(clients, Config()), clients);
        var profile = new ProviderProfile { Id = "lemonade", Adapter = "lemonade" };
        var first = await discovery.Read(profile, false, default);
        Assert.Same(first, await discovery.Read(profile, false, default)); Assert.Equal(1, handler.Calls);
        await discovery.Read(profile, true, default); Assert.Equal(2, handler.Calls);
        var unsupported = await discovery.Read(new() { Id = "kimi", Adapter = "openai-compatible" }, false, default);
        Assert.False(unsupported.Supported); Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public void LemonadeCatalogDeduplicatesAndDistinguishesOtherModalities()
    {
        var models = ModelDiscovery.Parse(Encoding.UTF8.GetBytes("""
        {"data":[{"id":"chat-small","labels":["reasoning"],"recipe":"llamacpp","downloaded":true},
        {"id":"chat-small"},{"id":"chat-large","recipe":"cloud"},{"id":"image","labels":["image"]},
        {"id":"speech","recipe":"kokoro"},{"id":"not-installed","downloaded":false},{"id":"embedding","labels":["embeddings"]},
        {"id":""},{"id":"bad\nmodel"},{"name":"missing ID"}]}
        """), true);
        Assert.Equal(6, models.Length);
        Assert.Equal(new[] { "chat-large", "chat-small" }, models.Where(x => x.ChatCapable).Select(x => x.Id));
        Assert.False(models.Single(x => x.Id == "not-installed").ChatCapable);
    }

    [Theory]
    [InlineData("{}", 200)]
    [InlineData("{\"data\":\"not an array\"}", 200)]
    [InlineData("secret provider body", 401)]
    public async Task ErrorsRemainSeparateFromSelectedModel(string body, int status)
    {
        var handler = new Handler((_, _) => Task.FromResult(Reply(body, (HttpStatusCode)status)));
        var clients = new Clients(handler); var discovery = new ModelDiscovery(new ProviderFactory(clients, Config()), clients);
        var profile = new ProviderProfile { Id = "lemonade", Adapter = "lemonade", Model = "saved-model" };
        var catalog = await discovery.Read(profile, false, default);
        Assert.NotNull(catalog.Error); Assert.DoesNotContain("secret provider body", catalog.Error); Assert.Empty(catalog.Models);
        Assert.Equal("saved-model", profile.Model);
    }

    [Fact]
    public async Task CallerCancellationPropagatesAndOversizedCatalogIsRejected()
    {
        var handler = new Handler(async (_, ct) => { await Task.Delay(10000, ct); return Reply("{}"); });
        var clients = new Clients(handler); var discovery = new ModelDiscovery(new ProviderFactory(clients, Config()), clients);
        using var cancellation = new CancellationTokenSource(30);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery.Read(new() { Id = "lemonade", Adapter = "lemonade" }, false, cancellation.Token));
        var oversized = new Handler((_, _) => Task.FromResult(Reply(new string('x', 1024 * 1024 + 1))));
        clients = new Clients(oversized); discovery = new ModelDiscovery(new ProviderFactory(clients, Config()), clients);
        Assert.Contains("size limit", (await discovery.Read(new() { Id = "lemonade", Adapter = "lemonade" }, false, default)).Error);
    }

    [Fact]
    public async Task ThreeTasksShareConnectorButRetainDistinctModelsAndCapturedImports()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new StoryDb(new DbContextOptionsBuilder<StoryDb>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var profile = new ProviderProfile { Id = "lemonade", Adapter = "lemonade", Model = "", Enabled = true };
        db.Providers.Add(profile); await db.SaveChangesAsync();
        var clients = new Clients(new Handler((_, _) => Task.FromResult(Reply("{}"))));
        var routing = new ProviderRouting(new ProviderFactory(clients, Config()), Config());
        var routes = ProviderRouting.Tasks.ToDictionary(t => t, _ => "lemonade");
        var models = new Dictionary<string, string?> { ["narration"] = "large", ["reconstruction"] = "medium", ["memory"] = "small" };
        await routing.Save(db, routes, default, models);
        foreach (var task in ProviderRouting.Tasks) Assert.Equal(models[task], (await routing.Resolve(db, task, default)).Profile.Model);
        Assert.Equal("", profile.Model); Assert.Equal("", (await db.Providers.AsNoTracking().SingleAsync()).Model);
        Assert.Equal("old-import-model", (await routing.ResolveCaptured(db, "lemonade", "old-import-model", default)).Profile.Model);
        Assert.Equal("small", (await routing.ReadModels(db, default))["memory"]);
        await routing.Save(db, routes, default);
        Assert.Equal("small", (await routing.ReadModels(db, default))["memory"]);
        models["memory"] = "bad\nmodel";
        await Assert.ThrowsAsync<InvalidOperationException>(() => routing.Save(db, routes, default, models));
        Assert.Equal("small", (await routing.ReadModels(db, default))["memory"]);
    }

    [Fact]
    public async Task LemonadeAgentUsesSchemaInstructionWithoutUnsupportedJsonMode()
    {
        var handler = new Handler(async (req, ct) =>
        {
            Assert.Equal("http://local.test/v1/chat/completions", req.RequestUri!.AbsoluteUri);
            var body = await req.Content!.ReadAsStringAsync(ct);
            Assert.Contains("\"model\":\"small\"", body); Assert.DoesNotContain("response_format", body);
            Assert.Contains("\"enable_thinking\":false", body); Assert.Contains("outputExample", body);
            return Reply("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"{\\\"value\\\":1}\"}}]}");
        });
        using var client = new HttpClient(handler);
        var agent = AgentProviderFactory.Create(new() { Id = "lemonade", Adapter = "lemonade", Model = "small", Enabled = true }, Config(("LEMONADE_BASE_URL", "http://local.test")), client);
        var result = await agent.Generate<Dictionary<string, int>>(new("character", new { }, new { value = 1 }), default);
        Assert.Equal(1, result["value"]);
    }
}
