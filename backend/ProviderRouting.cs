using Microsoft.EntityFrameworkCore;

namespace Story;

public sealed record RoutedProvider(ProviderProfile Profile, IStoryProvider Provider);

public sealed class ProviderRouting(ProviderFactory factory, IConfiguration configuration)
{
    public static readonly string[] Tasks = ["narration", "reconstruction", "memory"];

    public async Task<RoutedProvider> Resolve(StoryDb db, string task, CancellationToken ct)
    {
        if (!Tasks.Contains(task)) throw new InvalidOperationException("Unknown provider task.");
        var configured = configuration[$"{task.ToUpperInvariant()}_PROVIDER"] ?? "fixture";
        var selected = await db.Settings.Where(x => x.Key == $"provider:{task}").Select(x => x.Value).SingleOrDefaultAsync(ct) ?? configured;
        var profile = await db.Providers.SingleOrDefaultAsync(x => x.Id == selected, ct)
            ?? throw new InvalidOperationException($"The {task} provider profile does not exist.");
        if (!profile.Enabled) throw new InvalidOperationException($"The {profile.Name} profile is disabled.");
        return new(profile, factory.Create(profile));
    }

    public async Task<RoutedProvider> ResolveCaptured(StoryDb db, string profileId, string model, CancellationToken ct)
    {
        var stored = await db.Providers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == profileId, ct)
            ?? throw new InvalidOperationException("The import provider profile no longer exists.");
        if (!stored.Enabled) throw new InvalidOperationException($"The {stored.Name} profile is disabled.");
        stored.Model = model;
        return new(stored, factory.Create(stored));
    }

    public async Task<Dictionary<string, string>> Read(StoryDb db, CancellationToken ct)
    {
        var saved = await db.Settings.Where(x => Tasks.Select(t => $"provider:{t}").Contains(x.Key)).ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        return Tasks.ToDictionary(t => t, t => saved.GetValueOrDefault($"provider:{t}") ?? configuration[$"{t.ToUpperInvariant()}_PROVIDER"] ?? "fixture");
    }

    public async Task Save(StoryDb db, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        if (values.Count != Tasks.Length || Tasks.Any(t => !values.ContainsKey(t))) throw new InvalidOperationException("Select a profile for narration, reconstruction, and memory.");
        var ids = values.Values.Distinct().ToArray();
        var profiles = await db.Providers.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        if (profiles.Count != ids.Length) throw new InvalidOperationException("One or more provider profiles do not exist.");
        if (profiles.Any(x => !x.Enabled || string.IsNullOrWhiteSpace(x.Model) || !IsConfigured(x)))
            throw new InvalidOperationException("Every routed profile must be enabled and have a model and server configuration.");
        foreach (var task in Tasks)
        {
            var key = $"provider:{task}";
            var setting = await db.Settings.SingleOrDefaultAsync(x => x.Key == key, ct);
            if (setting is null) db.Settings.Add(new AppSetting { Key = key, Value = values[task] });
            else setting.Value = values[task];
        }
        await db.SaveChangesAsync(ct);
    }

    public bool IsConfigured(ProviderProfile profile) => profile.Id switch
    {
        "fixture" => true,
        "ollama" => !string.IsNullOrWhiteSpace(configuration["OLLAMA_BASE_URL"]),
        "lemonade" => !string.IsNullOrWhiteSpace(configuration["LEMONADE_BASE_URL"]),
        "openai" => !string.IsNullOrWhiteSpace(configuration["OPENAI_API_KEY"]),
        "anthropic" => !string.IsNullOrWhiteSpace(configuration["ANTHROPIC_API_KEY"]),
        _ => !string.IsNullOrWhiteSpace(configuration[$"{profile.Id.ToUpperInvariant()}_BASE_URL"])
             && !string.IsNullOrWhiteSpace(configuration[$"{profile.Id.ToUpperInvariant()}_API_KEY"])
    };
}

public sealed record ProviderRoutes(string Narration, string Reconstruction, string Memory);
