using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Story;

public record DiscoveredModel(string Id, string Name, bool ChatCapable, bool? Downloaded);
public record ModelCatalog(bool Supported, DiscoveredModel[] Models, string? Error = null, DateTime? FetchedAt = null);

public sealed class ModelDiscovery(ProviderFactory factory, IHttpClientFactory clients)
{
    private readonly ConcurrentDictionary<string, ModelCatalog> cache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new();

    public async Task<ModelCatalog> Read(ProviderProfile profile, bool refresh, CancellationToken ct)
    {
        if (profile.Id != "deepseek" && profile.Adapter != "lemonade") return new(false, []);
        var gate = gates.GetOrAdd(profile.Id, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!refresh && cache.TryGetValue(profile.Id, out var saved) && saved.FetchedAt > DateTime.UtcNow.AddMinutes(-1)) return saved;
            var (baseUrl, key) = factory.Configuration(profile);
            using var client = clients.CreateClient("model-discovery");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "models"));
            if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return new(true, [], $"Model discovery returned HTTP {(int)response.StatusCode}. Check connector credentials and server availability.");
            const int limit = 1024 * 1024;
            if (response.Content.Headers.ContentLength > limit) throw new InvalidOperationException("Model catalog exceeds the size limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + count > limit) throw new InvalidOperationException("Model catalog exceeds the size limit.");
                buffer.Write(chunk, 0, count);
            }
            var result = new ModelCatalog(true, Parse(buffer.ToArray(), profile.Adapter == "lemonade"), FetchedAt: DateTime.UtcNow);
            cache[profile.Id] = result;
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(true, [], "Model discovery timed out. You can refresh or enter a model ID manually."); }
        catch (HttpRequestException) { return new(true, [], "Model server could not be reached. Check its address and availability."); }
        catch (IOException) { return new(true, [], "Model catalog could not be read. Try refreshing."); }
        catch (JsonException) { return new(true, [], "Model server returned an invalid catalog. You can enter a model ID manually."); }
        catch (InvalidOperationException e) { return new(true, [], e.Message); }
        finally { gate.Release(); }
    }

    public static DiscoveredModel[] Parse(byte[] bytes, bool lemonade)
    {
        using var doc = JsonDocument.Parse(bytes);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new JsonException("Missing model list.");
        if (data.GetArrayLength() > 2000) throw new InvalidOperationException("Model catalog has too many entries.");
        var models = new Dictionary<string, DiscoveredModel>(StringComparer.Ordinal);
        foreach (var row in data.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String) continue;
            var id = idValue.GetString()!.Trim();
            if (id.Length is 0 or > 200 || id.Any(char.IsControl)) continue;
            var name = row.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : id;
            if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl)) name = id;
            bool? downloaded = row.TryGetProperty("downloaded", out var d) && d.ValueKind is JsonValueKind.True or JsonValueKind.False ? d.GetBoolean() : null;
            var chat = true;
            if (lemonade)
            {
                var nonChat = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "image", "image-generation", "embeddings", "embedding", "reranking", "transcription", "tts", "audio", "audio-generation", "upscaling", "classification", "3d", "mesh" };
                if (row.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array)
                    chat = !labels.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && nonChat.Contains(x.GetString()!));
                if (row.TryGetProperty("recipe", out var recipe) && recipe.ValueKind == JsonValueKind.String)
                {
                    var nonChatRecipes = new[] { "sd-cpp", "stable-diffusion", "whisper", "kokoro", "moonshine", "trellis", "esrgan" };
                    chat &= !nonChatRecipes.Any(x => recipe.GetString()!.Contains(x, StringComparison.OrdinalIgnoreCase));
                }
            }
            models.TryAdd(id, new(id, name!, chat && downloaded != false, downloaded));
        }
        return models.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }
}
