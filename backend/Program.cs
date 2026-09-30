using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Story;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<StoryDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Port=5432;Database=storyapp;Username=storyapp;Password=change-me"));
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("model-discovery").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<ProviderFactory>();
builder.Services.AddSingleton<ModelDiscovery>();
builder.Services.AddScoped<ProviderRouting>();
builder.Services.AddScoped<ContinuityService>();
builder.Services.AddHostedService<ImportWorker>();
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = ImportParser.MaxBytes + 65536);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 4 * 1024 * 1024);
var app = builder.Build();

app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        // Force a browser preflight for writes; no cross-origin CORS policy is enabled.
        if (context.Request.Method is not ("GET" or "HEAD" or "OPTIONS") && context.Request.Headers["X-Open-OCC"] != "1")
        { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "Same-origin application request required." }); return; }
        var token = app.Configuration["APP_ACCESS_TOKEN"];
        if (!string.IsNullOrEmpty(token))
        {
            var actual = context.Request.Headers.Authorization.ToString();
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(actual)), SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + token)) ))
            { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { error = "Enter the application access token." }); return; }
        }
    }
    try { await next(); }
    catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or DbUpdateConcurrencyException or BadHttpRequestException)
    {
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = e is KeyNotFoundException ? 404 : e is DbUpdateConcurrencyException ? 409 : 400;
        await context.Response.WriteAsJsonAsync(new { error = e is KeyNotFoundException ? "Record not found." : e is DbUpdateConcurrencyException ? "The branch or job changed. Reload before retrying." : e.Message });
    }
});

// EF migrations use PostgreSQL's migration lock. Do not run more than one backend worker instance.
if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<StoryDb>();
    await db.Database.MigrateAsync();
    foreach (var profile in ProviderFactory.Defaults(app.Configuration))
        if (!await db.Providers.AnyAsync(x => x.Id == profile.Id)) db.Providers.Add(profile);
    foreach (var task in ProviderRouting.Tasks)
        if (!await db.Settings.AnyAsync(x => x.Key == $"provider:{task}"))
            db.Settings.Add(new AppSetting { Key = $"provider:{task}", Value = app.Configuration[$"{task.ToUpperInvariant()}_PROVIDER"] ?? "fixture" });
    // Streaming runs capture continuity context; autonomous runs retain resumable
    // draft steps instead. Only the former lack a safe execution cursor on restart.
    foreach (var run in await db.GenerationRuns.Where(x => x.Status == "running" && x.ContextJson != "{}").ToListAsync())
    { run.Status = "interrupted"; run.Error = "Application restarted before the turn completed. No partial turn was committed."; }
    await db.SaveChangesAsync();
    await StoryEngine.Recover(db);
}
app.MapGet("/health", async (StoryDb db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));
app.MapGet("/api/campaigns", async (StoryDb db, CancellationToken ct) => await db.Campaigns.OrderByDescending(x => x.CreatedAt).Select(c => new
    { c.Id, c.Name, c.CreatedAt, branches = db.Branches.Where(b => b.CampaignId == c.Id).OrderBy(b => b.CreatedAt).Select(b => new { b.Id, b.Name }).ToList() }).ToListAsync(ct));
app.MapGet("/api/worlds", async (StoryDb db, CancellationToken ct) => await db.Worlds.OrderBy(x => x.Name).Select(x => new { world = x, versions = db.WorldVersions.Where(v => v.WorldId == x.Id).OrderByDescending(v => v.Version).ToList() }).ToListAsync(ct));
app.MapDelete("/api/campaigns/{id:guid}", async (Guid id, StoryDb db, CancellationToken ct) =>
{
    try { await CampaignDeletion.Delete(db, id, ct); }
    catch (DbUpdateConcurrencyException e) { return Results.Conflict(new { error = e.Message }); }
    catch (Npgsql.PostgresException e) when (e.SqlState is "40001" or "40P01" or "23503")
    { return Results.Conflict(new { error = "This story changed while deleting. Wait for active work to finish and retry." }); }
    return Results.NoContent();
});
app.MapPost("/api/worlds", async (CreateWorldRequest request, StoryDb db, CancellationToken ct) => Results.Ok(await WorldService.Create(db, request, ct)));
app.MapPost("/api/worlds/{id:guid}/versions", async (Guid id, CreateWorldVersionRequest request, StoryDb db, CancellationToken ct) => Results.Ok(await WorldService.CreateVersion(db, id, request, ct)));
app.MapGet("/api/worlds/{id:guid}", async (Guid id, StoryDb db, CancellationToken ct) => Results.Ok(new { world = await db.Worlds.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException(), versions = await db.WorldVersions.Where(x => x.WorldId == id).OrderByDescending(x => x.Version).ToListAsync(ct) }));
app.MapGet("/api/campaigns/{id:guid}/export", async (Guid id, StoryDb db, CancellationToken ct) =>
{
    var portable = await PortabilityService.Export(db, id, ct);
    return Results.File(Encoding.UTF8.GetBytes(Json.Write(portable)), "application/json", $"open-occ-{portable.Campaign.Name.Replace(' ', '-')}.json");
});
app.MapPost("/api/campaigns/import", async (PortableImportRequest request, StoryDb db, CancellationToken ct) => Results.Ok(await PortabilityService.Import(db, request.Export, request.Name, ct)));
app.MapPost("/api/campaigns", async (CreateCampaign request, StoryDb db, CancellationToken ct) => Results.Ok(await CampaignService.Create(db, request, ct,
    new EngineSettings(app.Configuration["CHARACTER_PROVIDER"] ?? "fixture", app.Configuration["DIRECTOR_PROVIDER"] ?? app.Configuration["NARRATION_PROVIDER"] ?? "fixture"))));
app.MapGet("/api/branches/{id:guid}", async (Guid id, StoryDb db, CancellationToken ct) =>
{
    var branch = await db.Branches.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
    var campaign = await db.Campaigns.SingleAsync(x => x.Id == branch.CampaignId, ct);
    var checkpoint = await db.Checkpoints.SingleAsync(x => x.Id == branch.HeadCheckpointId, ct);
    return Results.Ok(new { branch, campaign, checkpoint, state = Json.Read<StoryState>(checkpoint.StateJson),
        messages = await db.Messages.Where(x => x.BranchId == id).OrderBy(x => x.Sequence).ToListAsync(ct),
        checkpoints = await db.Checkpoints.Where(x => x.BranchId == id).OrderByDescending(x => x.Sequence).Select(x => new { x.Id, x.Sequence, x.Label, x.CreatedAt }).ToListAsync(ct),
        branches = await db.Branches.Where(x => x.CampaignId == branch.CampaignId).OrderBy(x => x.CreatedAt).ToListAsync(ct),
        facts = await db.Facts.Where(x => x.BranchId == id && x.ReviewStatus == "accepted").ToListAsync(ct),
        threads = await db.NarrativeThreads.Where(x => x.BranchId == id).OrderByDescending(x => x.Importance).ThenByDescending(x => x.EffectiveSequence).ToListAsync(ct),
        runs = await db.GenerationRuns.Where(x => x.BranchId == id).OrderByDescending(x => x.CreatedAt).Take(10).ToListAsync(ct),
        world = await db.Campaigns.Where(x => x.Id == branch.CampaignId).Join(db.WorldVersions, c => c.WorldVersionId, v => v.Id, (c, v) => new { world = db.Worlds.Single(w => w.Id == v.WorldId), version = v }).SingleOrDefaultAsync(ct),
        characters = await db.Characters.Where(x => x.CampaignId == branch.CampaignId).OrderBy(x => x.Name).ToListAsync(ct),
        relationships = await db.Relationships.Where(x => x.BranchId == id).ToListAsync(ct),
        knowledge = await db.Knowledge.Where(x => x.BranchId == id).ToListAsync(ct),
        mechanics = await db.Mechanics.Where(x => x.BranchId == id).OrderByDescending(x => x.Sequence).Take(100).ToListAsync(ct),
        events = await db.Events.Where(x => x.BranchId == id).OrderByDescending(x => x.Sequence).Take(100).ToListAsync(ct),
        summary = await db.Summaries.SingleOrDefaultAsync(x => x.BranchId == id, ct) });
});
app.MapPost("/api/campaigns/{id:guid}/characters", async (Guid id, CreateCharacterRequest request, StoryDb db, CancellationToken ct) => { if (!await db.Campaigns.AnyAsync(x => x.Id == id, ct)) throw new KeyNotFoundException(); CampaignService.RequireText(request.Name, 100, "Character name"); CampaignService.RequireText(request.Description, 2000, "Character description"); var row = new Character { CampaignId = id, Name = request.Name.Trim(), Description = request.Description.Trim(), Goals = request.Goals.Trim(), Status = request.Status.Trim() }; db.Characters.Add(row); await db.SaveChangesAsync(ct); return Results.Ok(row); });
app.MapPost("/api/branches/{id:guid}/relationships", async (Guid id, UpsertRelationshipRequest request, StoryDb db, CancellationToken ct) => { var branch = await db.Branches.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException(); if (!await db.Characters.AnyAsync(x => x.Id == request.FromCharacterId && x.CampaignId == branch.CampaignId, ct) || !await db.Characters.AnyAsync(x => x.Id == request.ToCharacterId && x.CampaignId == branch.CampaignId, ct)) throw new InvalidOperationException("Both characters must belong to the campaign."); var row = await db.Relationships.SingleOrDefaultAsync(x => x.BranchId == id && x.FromCharacterId == request.FromCharacterId && x.ToCharacterId == request.ToCharacterId, ct); var isNew = row is null; row ??= new Relationship { BranchId = id, FromCharacterId = request.FromCharacterId, ToCharacterId = request.ToCharacterId }; row.Label = request.Label.Trim(); row.Score = Math.Clamp(request.Score, -100, 100); row.Notes = request.Notes.Trim(); row.EffectiveSequence = (await db.Checkpoints.SingleAsync(x => x.Id == branch.HeadCheckpointId, ct)).Sequence; if (isNew) db.Relationships.Add(row); await db.SaveChangesAsync(ct); return Results.Ok(row); });
app.MapPost("/api/branches/{id:guid}/knowledge", async (Guid id, UpsertKnowledgeRequest request, StoryDb db, CancellationToken ct) => { var branch = await db.Branches.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException(); if (!await db.Characters.AnyAsync(x => x.Id == request.CharacterId && x.CampaignId == branch.CampaignId, ct)) throw new InvalidOperationException("Character must belong to the campaign."); CampaignService.RequireText(request.Subject, 1000, "Knowledge subject"); if (request.Confidence is < 0 or > 1) throw new InvalidOperationException("Confidence must be between 0 and 1."); var subject = request.Subject.Trim(); var row = await db.Knowledge.SingleOrDefaultAsync(x => x.BranchId == id && x.CharacterId == request.CharacterId && x.Subject == subject, ct); var isNew = row is null; row ??= new KnowledgeRecord { BranchId = id, CharacterId = request.CharacterId, Subject = subject }; row.FactId = request.FactId; row.BeliefType = request.BeliefType.Trim(); row.Confidence = request.Confidence; row.EffectiveSequence = (await db.Checkpoints.SingleAsync(x => x.Id == branch.HeadCheckpointId, ct)).Sequence; if (isNew) db.Knowledge.Add(row); await db.SaveChangesAsync(ct); return Results.Ok(row); });
app.MapPost("/api/branches/{id:guid}/mechanics", async (Guid id, ApplyMechanicsRequest request, StoryDb db, CancellationToken ct) => Results.Ok(await MechanicsService.Apply(db, id, request, ct)));
app.MapGet("/api/branches/{id:guid}/events", async (Guid id, StoryDb db, CancellationToken ct) => await db.Events.Where(x => x.BranchId == id).OrderBy(x => x.Sequence).ToListAsync(ct));
app.MapGet("/api/branches/{id:guid}/summary", async (Guid id, StoryDb db, CancellationToken ct) => await db.Summaries.SingleOrDefaultAsync(x => x.BranchId == id, ct) ?? new CampaignSummary { BranchId = id, Text = "No summary yet." });
app.MapGet("/api/branches/{id:guid}/search", async (Guid id, string q, StoryDb db, CancellationToken ct) => Results.Ok(await RetrievalService.Search(db, id, q, ct)));
app.MapGet("/api/branches/{id:guid}/checkpoints/{checkpointId:guid}/diff", async (Guid id, Guid checkpointId, StoryDb db, CancellationToken ct) =>
{
    var checkpoint = await db.Checkpoints.SingleOrDefaultAsync(x => x.Id == checkpointId && x.BranchId == id, ct) ?? throw new KeyNotFoundException();
    var previous = await db.Checkpoints.Where(x => x.BranchId == id && x.Sequence < checkpoint.Sequence).OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(ct);
    var currentState = Json.Read<StoryState>(checkpoint.StateJson);
    var previousState = previous is null ? null : Json.Read<StoryState>(previous.StateJson);
    var changes = new List<object>();
    if (previousState is null || previousState.Location != currentState.Location) changes.Add(new { field = "location", before = previousState?.Location, after = currentState.Location });
    if (previousState is null || previousState.Time != currentState.Time) changes.Add(new { field = "time", before = previousState?.Time, after = currentState.Time });
    if (previousState is null || previousState.Objective != currentState.Objective) changes.Add(new { field = "objective", before = previousState?.Objective, after = currentState.Objective });
    if (previousState is null || previousState.Condition != currentState.Condition) changes.Add(new { field = "condition", before = previousState?.Condition, after = currentState.Condition });
    foreach (var key in currentState.Inventory.Keys.Union(previousState?.Inventory.Keys ?? Enumerable.Empty<string>()).Order()) if (currentState.Inventory.GetValueOrDefault(key) != previousState?.Inventory.GetValueOrDefault(key)) changes.Add(new { field = $"inventory.{key}", before = previousState?.Inventory.GetValueOrDefault(key), after = currentState.Inventory.GetValueOrDefault(key) });
    foreach (var key in currentState.Skills.Keys.Union(previousState?.Skills.Keys ?? Enumerable.Empty<string>()).Order()) if (currentState.Skills.GetValueOrDefault(key) != previousState?.Skills.GetValueOrDefault(key)) changes.Add(new { field = $"skills.{key}", before = previousState?.Skills.GetValueOrDefault(key), after = currentState.Skills.GetValueOrDefault(key) });
    return Results.Ok(new { checkpoint, previousCheckpointId = previous?.Id, changes });
});
app.MapGet("/api/generation-runs/{id:guid}", async (Guid id, StoryDb db, CancellationToken ct) =>
{
    var run = await db.GenerationRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
    return Results.Ok(new { run, context = run.ContextJson == "{}" ? null : Json.Read<StoryContext>(run.ContextJson) });
});
app.MapPost("/api/branches/{id:guid}/fork", async (Guid id, ForkRequest request, StoryDb db, CancellationToken ct) => Results.Ok(await CampaignService.Fork(db, id, request, ct)));
app.MapPost("/api/branches/{id:guid}/facts/{factId:guid}/correct", async (Guid id, Guid factId, CorrectFactRequest request, StoryDb db, CancellationToken ct) => { await CampaignService.CorrectFact(db, id, factId, request, ct); return Results.NoContent(); });
app.MapGet("/api/branches/{id:guid}/corrections", async (Guid id, StoryDb db, CancellationToken ct) => await db.FactCorrections.Where(x => x.BranchId == id).OrderByDescending(x => x.CreatedAt).ToListAsync(ct));
app.MapPost("/api/branches/{id:guid}/turns", async (Guid id, TurnRequest request, StoryDb db, IConfiguration config, CancellationToken ct) =>
{
    CampaignService.RequireText(request.Action, 4000, "Player action");
    var branch = await db.Branches.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
    if (branch.HeadCheckpointId != request.ExpectedCheckpointId) return Results.Conflict(new { error = "Reload this branch before generating." });
    if (StoryEngine.IsExecuting(id) || await db.GenerationRuns.AnyAsync(x => x.BranchId == id && (x.Status == "running" || x.Status == "paused"), ct))
        throw new InvalidOperationException("Finish, retry or cancel the existing draft first.");
    var checkpoint = await db.Checkpoints.SingleAsync(x => x.Id == branch.HeadCheckpointId, ct);
    var world = StoryWorld.From(checkpoint);
    var profile = await db.Providers.SingleOrDefaultAsync(x => x.Id == world.Settings.DirectorProfile, ct);
    var run = new GenerationRun { BranchId = id, Action = request.Action.Trim(), ExpectedCheckpointId = checkpoint.Id,
        Provider = world.Settings.DirectorProfile, Model = string.IsNullOrWhiteSpace(world.Settings.DirectorModel) ? profile?.Model ?? "fixture-v1" : world.Settings.DirectorModel.Trim() };
    branch.Revision++;
    db.GenerationRuns.Add(run); await db.SaveChangesAsync(ct);
    await StoryEngine.Execute(db, run, config, ct);
    return Results.Ok(new { run.Id });
});
app.MapPost("/api/branches/{id:guid}/turns/stream", async (Guid id, TurnRequest request, HttpContext http, StoryDb db, ProviderRouting routing, ContinuityService continuity, CancellationToken ct) =>
{
    CampaignService.RequireText(request.Action, 4000, "Player action");
    var branch = await db.Branches.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
    if (branch.HeadCheckpointId != request.ExpectedCheckpointId)
    {
        http.Response.StatusCode = StatusCodes.Status409Conflict;
        await http.Response.WriteAsJsonAsync(new { error = "Reload this branch before generating." }, ct);
        return;
    }
    if (StoryEngine.IsExecuting(id) || await db.GenerationRuns.AnyAsync(x => x.BranchId == id && (x.Status == "running" || x.Status == "paused"), ct))
        throw new InvalidOperationException("Finish, retry or cancel the existing draft first.");
    var routed = await routing.Resolve(db, "narration", ct);
    var profile = routed.Profile;
    var provider = routed.Provider;
    var checkpoint = await db.Checkpoints.SingleAsync(x => x.Id == branch.HeadCheckpointId, ct);
    var storyContext = await continuity.Assemble(db, id, Json.Read<StoryState>(checkpoint.StateJson), request.Action.Trim(), ct);
    var run = new GenerationRun { BranchId = id, Action = request.Action.Trim(), ExpectedCheckpointId = checkpoint.Id, Provider = profile.Id, Model = profile.Model, ContextJson = Json.Write(storyContext) };
    branch.Revision++;
    db.GenerationRuns.Add(run);
    await db.SaveChangesAsync(ct);
    try
    {
        http.Response.ContentType = "application/x-ndjson";
        await http.Response.WriteAsync(Json.Write(new { type = "start", runId = run.Id }) + "\n", ct);
        await http.Response.Body.FlushAsync(ct);
        var narrative = new StringBuilder();
        await foreach (var evt in provider.Stream(ContinuityService.NarrationPrompt(storyContext, run.Action), ct))
        {
            if (evt.Type == "delta" && !string.IsNullOrEmpty(evt.Text))
            {
                if (narrative.Length + evt.Text.Length > 200_000) throw new InvalidOperationException("Provider narrative exceeded the 200,000 character safety limit.");
                narrative.Append(evt.Text);
                await http.Response.WriteAsync(Json.Write(new { type = "delta", text = evt.Text }) + "\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }
            if (evt.Type == "done") { run.InputTokens = evt.InputTokens; run.OutputTokens = evt.OutputTokens; }
        }
        var validatedNarrative = ContinuityService.ValidateNarrative(narrative.ToString());
        var memory = await routing.Resolve(db, "memory", ct);
        StateTransition transition;
        var continuityUpdated = true;
        try
        {
            transition = await continuity.AnalyzeTransition(memory.Provider, storyContext, run.Action, validatedNarrative, ct);
        }
        catch (Exception analysisError) when (analysisError is JsonException or InvalidOperationException or HttpRequestException)
        {
            // A valid narrated turn should not disappear because a small/local model could not
            // satisfy the separate structured-memory contract. Preserve the last trusted state.
            app.Logger.LogWarning(analysisError, "Continuity analysis failed for generation run {RunId}; committing narration with unchanged state", run.Id);
            transition = new(null, [], []);
            continuityUpdated = false;
        }
        var nextState = transition.State ?? storyContext.State;
        // Refresh the branch: the database concurrency token also catches a later competing commit.
        await db.Entry(branch).ReloadAsync(ct);
        await db.Entry(run).ReloadAsync(ct);
        if (run.Status != "running") throw new OperationCanceledException("Turn was cancelled.");
        await CampaignService.CommitTurn(db, run, validatedNarrative, nextState, transition, ct);
        await http.Response.WriteAsync(Json.Write(new { type = "done", runId = run.Id, checkpointId = branch.HeadCheckpointId, continuityUpdated }) + "\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
    catch (Exception e)
    {
        app.Logger.LogError(e, "Generation run {RunId} failed before commit", run.Id);
        db.ChangeTracker.Clear();
        var saved = await db.GenerationRuns.SingleAsync(x => x.Id == run.Id, CancellationToken.None);
        if (saved.Status != "completed")
        {
            if (saved.Status != "cancelled") saved.Status = e is OperationCanceledException ? "cancelled" : "failed";
            saved.Error = "Turn did not commit. Reload the branch and retry.";
            await db.SaveChangesAsync(CancellationToken.None);
        }
        else return; // A disconnected client must not reclassify a committed turn.
        if (http.Response.HasStarted && e is not OperationCanceledException)
        {
            await http.Response.WriteAsync(Json.Write(new { type = "error", error = "Turn did not commit. Reload the branch and retry." }) + "\n", CancellationToken.None);
            return;
        }
        throw;
    }
});
app.MapGet("/api/providers", async (StoryDb db, ProviderRouting routing, IConfiguration config, CancellationToken ct) => new {
    tasks = await routing.Read(db, ct),
    taskModels = await routing.ReadModels(db, ct),
    profiles = (await db.Providers.OrderBy(x => x.Name).ToListAsync()).Select(p => new { p.Id, p.Name, p.Adapter, p.Model, p.Enabled,
        configured = routing.IsConfigured(p), adapterConfigured = routing.IsConfigured(p),
        agentSupported = p.Adapter is "fixture" or "openai" or "openai-compatible" or "ollama" or "lemonade",
        capabilities = ProviderFactory.Capabilities(p.Adapter), agentCapabilities = AgentProviderFactory.Capabilities(p, config) }) });
app.MapGet("/api/providers/{id}/models", async (string id, bool? refresh, StoryDb db, ModelDiscovery discovery, CancellationToken ct) =>
{
    var profile = await db.Providers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
    return Results.Ok(await discovery.Read(profile, refresh == true, ct));
});
app.MapPut("/api/provider-routing", async (ProviderRoutes request, StoryDb db, ProviderRouting routing, CancellationToken ct) =>
{
    await routing.Save(db, new Dictionary<string, string> { ["narration"] = request.Narration, ["reconstruction"] = request.Reconstruction, ["memory"] = request.Memory }, ct,
        new Dictionary<string, string?> { ["narration"] = request.NarrationModel, ["reconstruction"] = request.ReconstructionModel, ["memory"] = request.MemoryModel });
    return Results.NoContent();
});
app.MapPut("/api/providers/{id}", async (string id, ProfileUpdate request, StoryDb db) =>
{
    var profile = await db.Providers.SingleOrDefaultAsync(x => x.Id == id) ?? throw new KeyNotFoundException();
    if (request.Model is null || request.Model.Length > 200 || request.Model.Any(char.IsControl)) throw new InvalidOperationException("Provide a model name of at most 200 characters without control characters.");
    if (request.Adapter is not null)
    {
        if (id is not ("character" or "director") || request.Adapter is not ("fixture" or "openai" or "openai-compatible" or "ollama" or "lemonade"))
            throw new InvalidOperationException("Role profiles support fixture, openai, openai-compatible, ollama or lemonade.");
        profile.Adapter = request.Adapter;
    }
    profile.Model = request.Model.Trim(); profile.Enabled = request.Enabled;
    await db.SaveChangesAsync(); return Results.NoContent();
});
app.MapPost("/api/providers/{id}/test", async (string id, StoryDb db, ProviderFactory factory, CancellationToken ct) =>
{
    var profile = await db.Providers.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
    if (!profile.Enabled) throw new InvalidOperationException("Enable and save this profile before testing it.");
    var completion = await factory.Create(profile).Complete(new ProviderPrompt("Reply briefly.", "Return exactly: connection ok", 128), null, ct);
    return Results.Ok(new { ok = true, response = completion.Text, completion.InputTokens, completion.OutputTokens });
});
app.MapPost("/api/branches/{id:guid}/imports", async (Guid id, HttpRequest request, StoryDb db, ProviderRouting routing, CancellationToken ct) =>
{
    var branch = await db.Branches.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file") ?? throw new InvalidOperationException("Select a UTF-8 .txt, .md, .html, .docx, or .json file.");
    var name = Path.GetFileName(file.FileName.Replace('\\', '/'));
    if (file.Length is <= 0 or > ImportParser.MaxBytes || name.Length > 200 || Path.GetExtension(name).ToLowerInvariant() is not (".txt" or ".md" or ".markdown" or ".html" or ".htm" or ".docx" or ".json"))
        throw new InvalidOperationException("Supported uploads: .txt, .md, .html, .docx, or .json, up to 2 MiB, with a filename of at most 200 characters.");
    using var stream = new MemoryStream(); await file.CopyToAsync(stream, ct);
    var bytes = stream.ToArray();
    var source = new ImportedSource { CampaignId = branch.CampaignId, FileName = name, Bytes = bytes, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
    var importProvider = form["provider"].ToString();
    var importModel = form["model"].ToString();
    if (importModel.Length > 200 || importModel.Any(char.IsControl)) throw new InvalidOperationException("Model IDs must contain at most 200 characters and no control characters.");
    if (!string.IsNullOrWhiteSpace(importProvider) && string.IsNullOrWhiteSpace(importModel))
        importModel = await db.Providers.Where(x => x.Id == importProvider).Select(x => x.Model).SingleOrDefaultAsync(ct) ?? "";
    var reconstruction = string.IsNullOrWhiteSpace(importProvider) ? await routing.Resolve(db, "reconstruction", ct) :
        await routing.ResolveCaptured(db, importProvider, importModel, ct);
    var options = new ImportOptions(
        int.TryParse(form["inputCharacterLimit"], out var inputLimit) ? inputLimit : 24000,
        int.TryParse(form["outputTokenLimit"], out var outputLimit) ? outputLimit : 3000,
        int.TryParse(form["maxCalls"], out var maxCalls) ? maxCalls : 100);
    Reconstruction.ValidateOptions(options);
    var job = new ImportJob { BranchId = id, SourceId = source.Id, ExpectedCheckpointId = branch.HeadCheckpointId,
        Method = Reconstruction.Method, Provider = reconstruction.Profile.Id, Model = reconstruction.Profile.Model,
        InputCharacterLimit = options.InputCharacterLimit, OutputTokenLimit = options.OutputTokenLimit, MaxCalls = options.MaxCalls };
    db.AddRange(source, job); await db.SaveChangesAsync(ct);
    return Results.Accepted($"/api/imports/{job.Id}", job);
});
app.MapGet("/api/branches/{id:guid}/imports", async (Guid id, StoryDb db) => await db.ImportJobs.Where(x => x.BranchId == id).OrderByDescending(x => x.CreatedAt).ToListAsync());
app.MapGet("/api/imports/{id:guid}", async (Guid id, StoryDb db) =>
{
    var job = await db.ImportJobs.SingleOrDefaultAsync(x => x.Id == id) ?? throw new KeyNotFoundException();
    var source = await db.Sources.Where(x => x.Id == job.SourceId).Select(x => new { x.Id, x.FileName, x.Sha256, size = x.Bytes.Length }).SingleAsync();
    return Results.Ok(new { job, source, candidates = await db.Facts.Where(x => x.JobId == id).Select(f => new { fact = f, evidence = db.Evidence.Where(e => e.FactId == f.Id).Join(db.Segments, e => e.SegmentId, s => s.Id, (e, s) => new { s.Id, s.Ordinal, s.Speaker, s.Text }).ToList() }).ToListAsync() });
});
app.MapGet("/api/sources/{id:guid}/download", async (Guid id, StoryDb db) =>
{
    var source = await db.Sources.SingleOrDefaultAsync(x => x.Id == id) ?? throw new KeyNotFoundException();
    return Results.File(source.Bytes, "application/octet-stream", source.FileName);
});
app.MapPost("/api/imports/{id:guid}/cancel", async (Guid id, StoryDb db) =>
{
    var job = await db.ImportJobs.SingleOrDefaultAsync(x => x.Id == id) ?? throw new KeyNotFoundException();
    if (job.Status is not ("queued" or "processing" or "review")) throw new InvalidOperationException("This job cannot be cancelled.");
    job.Status = "cancelled"; job.LeaseOwner = null; job.LeaseUntil = null; job.ProposalRevision++; await db.SaveChangesAsync(); return Results.NoContent();
});
app.MapPost("/api/imports/{id:guid}/retry", async (Guid id, StoryDb db) =>
{
    var job = await db.ImportJobs.SingleOrDefaultAsync(x => x.Id == id) ?? throw new KeyNotFoundException();
    if (job.Method == Reconstruction.Method) throw new InvalidOperationException("Use Resume processing to retry an agent import.");
    if (job.Status is not ("cancelled" or "failed")) throw new InvalidOperationException("Only failed or cancelled imports can resume.");
    job.Status = "queued"; job.Error = null; await db.SaveChangesAsync(); return Results.NoContent();
});
app.MapPost("/api/imports/{id:guid}/approve", async (Guid id, ApproveImport request, StoryDb db, CancellationToken ct) => { await CampaignService.Approve(db, id, request, ct); return Results.NoContent(); });
ImportReview.Map(app);
EngineApi.Map(app);
app.Run();

public record ProfileUpdate(string Model, bool Enabled, string? Adapter = null);
public partial class Program { }
