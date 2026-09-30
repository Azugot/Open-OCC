using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Story;
using Xunit;

namespace StoryTests;

public sealed class ProviderTests
{
    public record Answer(string Speech, string DecisionSummary);
    private static AgentRequest Request => new("character-proposal", new { perceivedEvents = new[] { "The bell rings." } }, new Answer("hello", "wait"));
    private static ProviderProfile Profile(string adapter = "openai-compatible") => new()
    { Id = "actor-small", Adapter = adapter, Model = "test-model", Enabled = true };
    private static IConfiguration Config(params (string, string?)[] values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.Select(x => new KeyValuePair<string, string?>(x.Item1, x.Item2))).Build();
    private static IConfiguration RemoteConfig => Config(("ACTOR_SMALL_API_KEY", "test-secret"), ("ACTOR_SMALL_BASE_URL", "https://example.test/v1"));
    private static string OpenAiEnvelope(string content) => Json.Write(new
    {
        choices = new[] { new { finish_reason = "stop", message = new { content, reasoning_content = "PRIVATE REASONING MUST NOT RETURN" } } }
    });
    private static HttpResponseMessage Response(string content, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("openai-compatible")]
    public async Task ChatRequestUsesProfileCredentialsAndOnlyFinalContent(string adapter)
    {
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal("https://example.test/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-secret", request.Headers.Authorization.Parameter);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("test-model", json.RootElement.GetProperty("model").GetString());
            Assert.False(json.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal("json_object", json.RootElement.GetProperty("response_format").GetProperty("type").GetString());
            Assert.Equal(4096, json.RootElement.GetProperty(adapter == "openai" ? "max_completion_tokens" : "max_tokens").GetInt32());
            var messages = json.RootElement.GetProperty("messages");
            Assert.Equal("system", messages[0].GetProperty("role").GetString());
            using var prompt = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
            Assert.Equal("character-proposal", prompt.RootElement.GetProperty("task").GetString());
            Assert.Equal("The bell rings.", prompt.RootElement.GetProperty("context").GetProperty("perceivedEvents")[0].GetString());
            Assert.Equal("hello", prompt.RootElement.GetProperty("outputExample").GetProperty("speech").GetString());
            return Response(OpenAiEnvelope(Json.Write(new Answer("Stay close.", "Protect my companion."))));
        }));
        var result = await AgentProviderFactory.Create(Profile(adapter), RemoteConfig, client).Generate<Answer>(Request, default);
        Assert.Equal(new Answer("Stay close.", "Protect my companion."), result);
        Assert.DoesNotContain("PRIVATE", Json.Write(result));
    }

    [Fact]
    public async Task OllamaRequestUsesJsonNonstreamAndIgnoresThinking()
    {
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal("http://host.docker.internal:11434/api/chat", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("json", body.RootElement.GetProperty("format").GetString());
            Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
            return Response(Json.Write(new { done = true, message = new { content = Json.Write(new Answer("Welcome.", "Greet the visitor.")), thinking = "private secret" } }));
        }));
        var result = await AgentProviderFactory.Create(Profile("ollama"), Config(), client).Generate<Answer>(Request, default);
        Assert.Equal("Welcome.", result.Speech);
        Assert.DoesNotContain("private secret", Json.Write(result));
    }

    [Fact]
    public async Task OfficialOpenAiDefaultEndpointIsUsed()
    {
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("https://api.openai.com/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Response(OpenAiEnvelope(Json.Write(new Answer("Hi", "Greet")))));
        }));
        await AgentProviderFactory.Create(Profile("openai"), Config(("ACTOR_SMALL_API_KEY", "test-secret")), client).Generate<Answer>(Request, default);
    }

    [Fact]
    public async Task CharacterAndDirectorUseDifferentModelsWithSharedOpenAiConfiguration()
    {
        var config = Config(("CHARACTER_ADAPTER", "openai"), ("CHARACTER_MODEL", "small-model"),
            ("DIRECTOR_ADAPTER", "openai"), ("DIRECTOR_MODEL", "large-model"),
            ("OPENAI_API_KEY", "shared-test-key"), ("OPENAI_BASE_URL", "https://vendor.test/v1"));
        var profiles = ProviderRegistry.Defaults(config);
        var character = profiles.Single(x => x.Id == "character");
        var director = profiles.Single(x => x.Id == "director");
        var sentModels = new List<string>();
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal("https://vendor.test/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("shared-test-key", request.Headers.Authorization!.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            sentModels.Add(body.RootElement.GetProperty("model").GetString()!);
            return Response(OpenAiEnvelope(Json.Write(new Answer("Hi", "Greet"))));
        }));
        Assert.True(AgentProviderFactory.Capabilities(character, config).Available);
        Assert.True(AgentProviderFactory.Capabilities(director, config).Available);
        await AgentProviderFactory.Create(character, config, client).Generate<Answer>(Request, default);
        await AgentProviderFactory.Create(director, config, client).Generate<Answer>(Request, default);
        Assert.Equal(new[] { "small-model", "large-model" }, sentModels);
    }

    [Theory]
    [InlineData("character")]
    [InlineData("director")]
    public async Task ExplicitRoleConfigurationOverridesVendorConfiguration(string role)
    {
        var prefix = role.ToUpperInvariant();
        var config = Config(($"{prefix}_API_KEY", "role-key"), ($"{prefix}_BASE_URL", "https://role.test/v1"),
            ("OPENAI_API_KEY", "vendor-key"), ("OPENAI_BASE_URL", "https://vendor.test/v1"));
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("https://role.test/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("role-key", request.Headers.Authorization!.Parameter);
            return Task.FromResult(Response(OpenAiEnvelope(Json.Write(new Answer("Hi", "Greet")))));
        }));
        var profile = Profile("openai"); profile.Id = role;
        await AgentProviderFactory.Create(profile, config, client).Generate<Answer>(Request, default);
    }

    [Theory]
    [InlineData("character")]
    [InlineData("director")]
    public async Task BlankRoleConfigurationInheritsVendorConfiguration(string role)
    {
        var prefix = role.ToUpperInvariant();
        var config = Config(($"{prefix}_API_KEY", "  "), ($"{prefix}_BASE_URL", ""),
            ("OPENAI_API_KEY", "vendor-key"), ("OPENAI_BASE_URL", "https://vendor.test/v1"));
        var profile = Profile("openai"); profile.Id = role;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("https://vendor.test/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("vendor-key", request.Headers.Authorization!.Parameter);
            return Task.FromResult(Response(OpenAiEnvelope(Json.Write(new Answer("Hi", "Greet")))));
        }));
        await AgentProviderFactory.Create(profile, config, client).Generate<Answer>(Request, default);
    }

    [Theory]
    [InlineData("openai", "https://api.openai.com/v1/chat/completions")]
    [InlineData("ollama", "http://host.docker.internal:11434/api/chat")]
    public async Task BlankVendorBaseUrlUsesAdapterDefault(string adapter, string endpoint)
    {
        var profile = Profile(adapter); profile.Id = "character";
        var vendor = adapter.ToUpperInvariant();
        var config = Config(("CHARACTER_API_KEY", ""), ("CHARACTER_BASE_URL", " "),
            ($"{vendor}_API_KEY", adapter == "openai" ? "vendor-key" : ""), ($"{vendor}_BASE_URL", " "));
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(endpoint, request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Response(adapter == "ollama"
                ? Json.Write(new { done = true, message = new { content = Json.Write(new Answer("Hi", "Greet")) } })
                : OpenAiEnvelope(Json.Write(new Answer("Hi", "Greet")))));
        }));
        await AgentProviderFactory.Create(profile, config, client).Generate<Answer>(Request, default);
    }

    [Fact]
    public void BlankCompatibleRoleConfigurationDoesNotBorrowVendorConfiguration()
    {
        var profile = Profile(); profile.Id = "character";
        var config = Config(("CHARACTER_API_KEY", " "), ("CHARACTER_BASE_URL", ""),
            ("OPENAI_API_KEY", "vendor-key"), ("OPENAI_BASE_URL", "https://vendor.test/v1"));
        Assert.False(AgentProviderFactory.Capabilities(profile, config).Available);
        Assert.Contains("CHARACTER_API_KEY", Assert.Throws<InvalidOperationException>(() => AgentProviderFactory.Create(profile, config)).Message);
    }

    [Fact]
    public async Task OllamaRoleUsesVendorEndpointAndOptionalKey()
    {
        var profile = Profile("ollama"); profile.Id = "character";
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("http://localhost:11434/api/chat", request.RequestUri!.AbsoluteUri);
            Assert.Equal("ollama-test", request.Headers.Authorization!.Parameter);
            return Task.FromResult(Response(Json.Write(new { done = true, message = new { content = Json.Write(new Answer("Hi", "Greet")) } })));
        }));
        await AgentProviderFactory.Create(profile, Config(("OLLAMA_BASE_URL", "http://localhost:11434"), ("OLLAMA_API_KEY", "ollama-test")), client).Generate<Answer>(Request, default);
    }

    [Fact]
    public void CompatibleRoleRequiresExplicitRoleCredentialsAndEndpoint()
    {
        var profile = Profile(); profile.Id = "director";
        var config = Config(("OPENAI_API_KEY", "vendor-key"), ("OPENAI_BASE_URL", "https://vendor.test/v1"),
            ("DEEPSEEK_API_KEY", "deepseek-key"), ("DEEPSEEK_BASE_URL", "https://deepseek.test"));
        Assert.Contains("DIRECTOR_API_KEY", Assert.Throws<InvalidOperationException>(() => AgentProviderFactory.Create(profile, config)).Message);
        Assert.Contains("DIRECTOR_BASE_URL", Assert.Throws<InvalidOperationException>(() => AgentProviderFactory.Create(profile,
            Config(("DIRECTOR_API_KEY", "role-key"), ("OPENAI_BASE_URL", "https://vendor.test/v1")))).Message);
    }

    [Fact]
    public void OrdinaryProfilesCannotBorrowVendorCredentials()
    {
        Assert.Contains("ACTOR_SMALL_API_KEY", Assert.Throws<InvalidOperationException>(() => AgentProviderFactory.Create(Profile("openai"),
            Config(("OPENAI_API_KEY", "vendor-key")))).Message);
    }

    [Fact]
    public void RoleDefaultsUseEnabledFixturesUntilConfigured()
    {
        var profiles = ProviderRegistry.Defaults(Config()).Where(x => x.Id is "character" or "director").ToArray();
        Assert.Equal(2, profiles.Length);
        Assert.All(profiles, profile => { Assert.True(profile.Enabled); Assert.Equal("fixture", profile.Adapter); Assert.Equal("fixture-v1", profile.Model); });
    }

    [Fact]
    public async Task TrustedInstructionsArePlacedInSystemMessage()
    {
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var messages = body.RootElement.GetProperty("messages");
            Assert.Contains("The dice result is authoritative.", messages[0].GetProperty("content").GetString());
            Assert.DoesNotContain("The dice result is authoritative.", messages[1].GetProperty("content").GetString());
            return Response(OpenAiEnvelope(Json.Write(new Answer("Hi", "Greet"))));
        }));
        await AgentProviderFactory.Create(Profile(), RemoteConfig, client).Generate<Answer>(Request with { Instructions = "The dice result is authoritative." }, default);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"not-json private data\"}}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"[]\"}}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":null}}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":{\"secret\":true}}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"{}\"}}]}")]
    public async Task MalformedEnvelopeOrContentProducesSafeError(string envelope)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response(envelope))));
        var provider = AgentProviderFactory.Create(Profile(), RemoteConfig, client);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.Generate<Answer>(Request, default));
        Assert.Equal("Model provider returned malformed structured JSON.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task HttpErrorNeverExposesProviderResponseOrCredentials()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response("test-secret private-context", HttpStatusCode.Unauthorized))));
        var provider = AgentProviderFactory.Create(Profile(), RemoteConfig, client);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.Generate<Answer>(Request, default));
        Assert.Contains("HTTP 401", error.Message);
        Assert.DoesNotContain("test-secret", error.ToString());
        Assert.DoesNotContain("private-context", error.ToString());
    }

    [Fact]
    public async Task NetworkErrorDoesNotExposeEndpointOrKey()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new HttpRequestException("test-secret https://example.test/private")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AgentProviderFactory.Create(Profile(), RemoteConfig, client).Generate<Answer>(Request, default));
        Assert.DoesNotContain("test-secret", error.ToString());
        Assert.DoesNotContain("example.test", error.ToString());
    }

    [Fact]
    public async Task CancellationReachesInFlightRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Response("{}");
        }));
        using var cancellation = new CancellationTokenSource();
        var pending = AgentProviderFactory.Create(Profile(), RemoteConfig, client).Generate<Answer>(Request, cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task OversizedResponseIsRejected()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response(new string('x', 1024 * 1024 + 1)))));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AgentProviderFactory.Create(Profile(), RemoteConfig, client).Generate<Answer>(Request, default));
        Assert.Contains("size", error.Message);
    }

    [Fact]
    public async Task IncompleteOllamaResponseIsRejected()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response(Json.Write(new
        { done = false, message = new { content = Json.Write(new Answer("hi", "greet")) } })))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AgentProviderFactory.Create(Profile("ollama"), Config(), client).Generate<Answer>(Request, default));
    }

    [Fact]
    public async Task TimeoutIsAResumableSafeError()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new TaskCanceledException("private endpoint and key")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AgentProviderFactory.Create(Profile(), RemoteConfig, client).Generate<Answer>(Request, default));
        Assert.Contains("timed out", error.Message);
        Assert.DoesNotContain("private endpoint", error.ToString());
    }

    [Fact]
    public async Task OversizedContextIsRejectedBeforeSending()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new Exception("Must not send")));
        var request = Request with { Context = new { text = new string('x', 200001) } };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AgentProviderFactory.Create(Profile(), RemoteConfig, client).Generate<Answer>(request, default));
        Assert.Contains("context", error.Message);
    }

    [Fact]
    public void MissingConfigurationAndDisabledProfilesAreUnavailableWithoutFallback()
    {
        var profile = Profile();
        Assert.False(AgentProviderFactory.Capabilities(profile, Config()).Available);
        Assert.Contains("ACTOR_SMALL_API_KEY", Assert.Throws<InvalidOperationException>(() => AgentProviderFactory.Create(profile, Config())).Message);
        Assert.Contains("BASE_URL", Assert.Throws<InvalidOperationException>(() => AgentProviderFactory.Create(profile, Config(("ACTOR_SMALL_API_KEY", "test")))).Message);
        profile.Model = "";
        Assert.Contains("model name", Assert.Throws<InvalidOperationException>(() => AgentProviderFactory.Create(profile, RemoteConfig)).Message);
        profile.Model = "test";
        profile.Enabled = false;
        Assert.Contains("disabled", Assert.Throws<InvalidOperationException>(() => AgentProviderFactory.Create(profile, RemoteConfig)).Message);
        Assert.True(AgentProviderFactory.Capabilities(Profile("ollama"), Config()).Available);
    }

    [Theory]
    [InlineData("ftp://example.test")]
    [InlineData("https://username:password@example.test")]
    [InlineData("https://example.test?key=secret")]
    [InlineData("https://example.test#secret")]
    public void InvalidBaseUrlIsRejectedWithoutEchoingSecrets(string url)
    {
        var error = Assert.Throws<InvalidOperationException>(() => AgentProviderFactory.Create(Profile(), Config(("ACTOR_SMALL_API_KEY", "test"), ("ACTOR_SMALL_BASE_URL", url))));
        Assert.DoesNotContain(url, error.Message);
    }

    [Fact]
    public async Task RealHttpTransportCompletesAgainstLocalLoopbackProvider()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serve = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(cancellation.Token);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
            var requestLine = await reader.ReadLineAsync(cancellation.Token);
            Assert.Equal("POST /v1/chat/completions HTTP/1.1", requestLine);
            var length = 0;
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(cancellation.Token)))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Split(':')[1]);
            var body = new char[length];
            Assert.Equal(length, await reader.ReadBlockAsync(body.AsMemory(), cancellation.Token));
            Assert.Contains("character-proposal", new string(body));
            var payload = Encoding.UTF8.GetBytes(OpenAiEnvelope(Json.Write(new Answer("The gate opens.", "Respond to the visitor."))));
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, cancellation.Token);
            await stream.WriteAsync(payload, cancellation.Token);
        }, cancellation.Token);
        var provider = AgentProviderFactory.Create(Profile(), Config(("ACTOR_SMALL_API_KEY", "local-test"), ("ACTOR_SMALL_BASE_URL", $"http://127.0.0.1:{port}/v1")));
        var answer = await provider.Generate<Answer>(Request, cancellation.Token);
        Assert.Equal("The gate opens.", answer.Speech);
        await serve;
    }
}
