using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Story;

public record ReanalyzeImport(int InputCharacterLimit = 24000, int OutputTokenLimit = 3000, int MaxCalls = 100,
    string? Provider = null, string? Model = null, bool PrepareOnly = false, bool ForkOriginal = false, string TimelineName = "Imported story");

public static class ImportPreparation
{
    public static async Task Prepare(StoryDb db, ImportJob job, CancellationToken ct)
    {
        try { await Reconstruction.Step(db, job, null, ct); }
        catch (Exception error) when (error is InvalidOperationException or JsonException or KeyNotFoundException or
            System.Xml.XmlException or InvalidDataException or DecoderFallbackException or ArgumentException)
        {
            // Discard partially constructed blocks, while retaining the already saved original and job.
            foreach (var entry in db.ChangeTracker.Entries().Where(x => x.State == EntityState.Added).ToArray()) entry.State = EntityState.Detached;
            job.Status = "paused";
            job.Error = "Source preparation failed: " + error.Message + " Upload a corrected copy; the original remains downloadable.";
            await db.SaveChangesAsync(ct);
        }
    }

    public static async Task<ImportJob> Upload(StoryDb db, ProviderRouting routing, Guid branchId, string fileName, byte[] bytes,
        ImportOptions options, bool prepareOnly, string? provider, string? model, string destination, string timelineName,
        Guid? checkpointId, CancellationToken ct)
    {
        if (destination is not ("current" or "new")) throw new InvalidOperationException("Choose the current timeline or a new timeline.");
        Reconstruction.ValidateOptions(options);
        var branch = await db.Branches.SingleOrDefaultAsync(x => x.Id == branchId, ct) ?? throw new KeyNotFoundException();
        var at = checkpointId ?? branch.HeadCheckpointId;
        if (destination == "current" && at != branch.HeadCheckpointId) throw new DbUpdateConcurrencyException("This timeline advanced. Reload the destination before importing.");
        var selection = await routing.Capture(db, provider, model, ct);
        if (!prepareOnly) await routing.ResolveCaptured(db, selection.Provider, selection.Model, ct);
        ImportJob job;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            if (destination == "new") branch = await CampaignService.Fork(db, branchId, new(at, timelineName), ct);
            var source = new ImportedSource { CampaignId = branch.CampaignId, FileName = fileName, Bytes = bytes,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
            job = new ImportJob { BranchId = branch.Id, SourceId = source.Id, ExpectedCheckpointId = branch.HeadCheckpointId,
                Method = Reconstruction.Method, Provider = selection.Provider, Model = selection.Model,
                Status = prepareOnly ? "preparing" : "queued", InputCharacterLimit = options.InputCharacterLimit,
                OutputTokenLimit = options.OutputTokenLimit, MaxCalls = options.MaxCalls };
            db.AddRange(source, job); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        }
        if (prepareOnly) await Prepare(db, job, ct);
        return job;
    }
}
