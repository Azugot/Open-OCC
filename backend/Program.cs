using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Story;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<StoryDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Port=5432;Database=storyapp;Username=storyapp;Password=change-me"));
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
    foreach (var profile in ProviderRegistry.Defaults(app.Configuration))
        if (!await db.Providers.AnyAsync(x => x.Id == profile.Id)) db.Providers.Add(profile);
    foreach (var run in await db.GenerationRuns.Where(x => x.Status == "running").ToListAsync())
    { run.Status = "interrupted"; run.Error = "Application restarted before the turn completed. No partial turn was committed."; }
    await db.SaveChangesAsync();
}
app.MapGet("/health", async (StoryDb db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));
app.MapGet("/api/campaigns", async (StoryDb db, CancellationToken ct) => await db.Campaigns.OrderByDescending(x => x.CreatedAt).Select(c => new
    { c.Id, c.Name, c.CreatedAt, branches = db.Branches.Where(b => b.CampaignId == c.Id).OrderBy(b => b.CreatedAt).Select(b => new { b.Id, b.Name }).ToList() }).ToListAsync(ct));
app.MapPost("/api/campaigns", async (CreateCampaign request, StoryDb db, CancellationToken ct) => Results.Ok(await CampaignService.Create(db, request, ct)));
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
        runs = await db.GenerationRuns.Where(x => x.BranchId == id).OrderByDescending(x => x.CreatedAt).Take(10).ToListAsync(ct) });
});
app.MapPost("/api/branches/{id:guid}/fork", async (Guid id, ForkRequest request, StoryDb db, CancellationToken ct) => Results.Ok(await CampaignService.Fork(db, id, request, ct)));
app.MapPost("/api/branches/{id:guid}/turns", async (Guid id, TurnRequest request, StoryDb db, IConfiguration config, CancellationToken ct) =>
{
    CampaignService.RequireText(request.Action, 4000, "Player action");
    var branch = await db.Branches.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
    if (branch.HeadCheckpointId != request.ExpectedCheckpointId) return Results.Conflict(new { error = "Reload this branch before generating." });
    var profileId = config["NARRATION_PROVIDER"] ?? "fixture";
    var profile = await db.Providers.SingleOrDefaultAsync(x => x.Id == profileId, ct) ?? throw new InvalidOperationException("Narration provider profile not found.");
    var provider = ProviderRegistry.Resolve(profile.Adapter);
    if (!profile.Enabled || !provider.Capabilities.Available) throw new InvalidOperationException(provider.Capabilities.Note);
    var checkpoint = await db.Checkpoints.SingleAsync(x => x.Id == branch.HeadCheckpointId, ct);
    var run = new GenerationRun { BranchId = id, Action = request.Action.Trim(), ExpectedCheckpointId = checkpoint.Id, Provider = profile.Id, Model = config["NARRATION_MODEL"] ?? profile.Model };
    db.GenerationRuns.Add(run);
    await db.SaveChangesAsync(ct);
    try
    {
        var facts = await db.Facts.Where(x => x.BranchId == id && x.ReviewStatus == "accepted" && x.Visibility == "public").Select(x => x.Text).ToArrayAsync(ct);
        var narrative = await provider.Narrate(new NarrationRequest(run.Action, Json.Read<StoryState>(checkpoint.StateJson), facts), ct);
        // Refresh the branch: the database concurrency token also catches a later competing commit.
        await db.Entry(branch).ReloadAsync(ct);
        await CampaignService.CommitTurn(db, run, narrative, ct);
        return Results.Ok(new { run.Id });
    }
    catch (Exception e)
    {
        db.ChangeTracker.Clear();
        var saved = await db.GenerationRuns.SingleAsync(x => x.Id == run.Id, CancellationToken.None);
        saved.Status = e is OperationCanceledException ? "cancelled" : "failed";
        saved.Error = "Turn did not commit. Reload the branch and retry.";
        await db.SaveChangesAsync(CancellationToken.None);
        throw;
    }
});
app.MapGet("/api/providers", async (StoryDb db, IConfiguration config) => new {
    tasks = new { narration = config["NARRATION_PROVIDER"] ?? "fixture", reconstruction = config["RECONSTRUCTION_PROVIDER"] ?? "fixture", memory = config["MEMORY_PROVIDER"] ?? "fixture" },
    profiles = (await db.Providers.OrderBy(x => x.Name).ToListAsync()).Select(p => new { p.Id, p.Name, p.Adapter, p.Model, p.Enabled,
        configured = p.Id == "fixture" || (p.Id == "ollama" ? !string.IsNullOrWhiteSpace(config["OLLAMA_BASE_URL"]) : !string.IsNullOrWhiteSpace(config[$"{p.Id.ToUpperInvariant()}_API_KEY"])), capabilities = ProviderRegistry.Resolve(p.Adapter).Capabilities }) });
app.MapPut("/api/providers/{id}", async (string id, ProfileUpdate request, StoryDb db) =>
{
    var profile = await db.Providers.SingleOrDefaultAsync(x => x.Id == id) ?? throw new KeyNotFoundException();
    if (request.Model is null || request.Model.Length > 200) throw new InvalidOperationException("Provide a model name of at most 200 characters.");
    profile.Model = request.Model.Trim(); profile.Enabled = request.Enabled;
    await db.SaveChangesAsync(); return Results.NoContent();
});
app.MapPost("/api/branches/{id:guid}/imports", async (Guid id, HttpRequest request, StoryDb db, CancellationToken ct) =>
{
    var branch = await db.Branches.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file") ?? throw new InvalidOperationException("Select a UTF-8 .txt or .json file.");
    var name = Path.GetFileName(file.FileName.Replace('\\', '/'));
    if (file.Length is <= 0 or > ImportParser.MaxBytes || name.Length > 200 || Path.GetExtension(name).ToLowerInvariant() is not (".txt" or ".json"))
        throw new InvalidOperationException("Supported uploads: .txt or .json, up to 2 MiB, with a filename of at most 200 characters.");
    using var stream = new MemoryStream(); await file.CopyToAsync(stream, ct);
    var bytes = stream.ToArray();
    var source = new ImportedSource { CampaignId = branch.CampaignId, FileName = name, Bytes = bytes, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
    var job = new ImportJob { BranchId = id, SourceId = source.Id, ExpectedCheckpointId = branch.HeadCheckpointId };
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
    job.Status = "cancelled"; await db.SaveChangesAsync(); return Results.NoContent();
});
app.MapPost("/api/imports/{id:guid}/retry", async (Guid id, StoryDb db) =>
{
    var job = await db.ImportJobs.SingleOrDefaultAsync(x => x.Id == id) ?? throw new KeyNotFoundException();
    if (job.Status is not ("cancelled" or "failed")) throw new InvalidOperationException("Only failed or cancelled imports can resume.");
    job.Status = "queued"; job.Error = null; await db.SaveChangesAsync(); return Results.NoContent();
});
app.MapPost("/api/imports/{id:guid}/approve", async (Guid id, ApproveImport request, StoryDb db, CancellationToken ct) => { await CampaignService.Approve(db, id, request, ct); return Results.NoContent(); });
app.Run();

public record ProfileUpdate(string Model, bool Enabled);
public partial class Program { }
