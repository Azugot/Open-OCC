using Microsoft.EntityFrameworkCore;

namespace Story;

public static class WorldService
{
    public static async Task<World> Create(StoryDb db, CreateWorldRequest request, CancellationToken ct)
    {
        CampaignService.RequireText(request.Name, 100, "World name");
        if (request.Description.Length > 2000) throw new InvalidOperationException("World description must be at most 2000 characters.");
        var world = new World { Name = request.Name.Trim(), Description = request.Description.Trim() };
        db.Worlds.Add(world);
        db.WorldVersions.Add(new WorldVersion { WorldId = world.Id, Version = 1 });
        await db.SaveChangesAsync(ct);
        return world;
    }

    public static async Task<WorldVersion> CreateVersion(StoryDb db, Guid worldId, CreateWorldVersionRequest request, CancellationToken ct)
    {
        if (!await db.Worlds.AnyAsync(x => x.Id == worldId, ct)) throw new KeyNotFoundException();
        ValidateJson(request.RulesJson, 20000, "Rules");
        ValidateJson(request.LocationsJson, 20000, "Locations");
        ValidateJson(request.FactionsJson, 20000, "Factions");
        if (request.AuthorInstructions.Length > 8000) throw new InvalidOperationException("Author instructions must be at most 8000 characters.");
        var version = (await db.WorldVersions.Where(x => x.WorldId == worldId).MaxAsync(x => (int?)x.Version, ct) ?? 0) + 1;
        var row = new WorldVersion { WorldId = worldId, Version = version, RulesJson = request.RulesJson, LocationsJson = request.LocationsJson, FactionsJson = request.FactionsJson, AuthorInstructions = request.AuthorInstructions.Trim() };
        db.WorldVersions.Add(row); await db.SaveChangesAsync(ct); return row;
    }

    private static void ValidateJson(string value, int max, string name)
    {
        CampaignService.RequireText(value, max, name);
        try { using var _ = System.Text.Json.JsonDocument.Parse(value); } catch { throw new InvalidOperationException($"{name} must be valid JSON."); }
    }
}

public static class MechanicsService
{
    public static async Task<Checkpoint> Apply(StoryDb db, Guid branchId, ApplyMechanicsRequest request, CancellationToken ct)
    {
        CampaignService.RequireText(request.Subject, 100, "Mechanic subject");
        CampaignService.RequireText(request.Reason, 500, "Mechanic reason");
        if (request.Kind is not ("inventory" or "skill")) throw new InvalidOperationException("Mechanic kind must be inventory or skill.");
        if (request.Delta is < -1000000 or > 1000000 || request.Delta == 0) throw new InvalidOperationException("Mechanic delta must be non-zero and bounded.");
        var branch = await db.Branches.SingleOrDefaultAsync(x => x.Id == branchId, ct) ?? throw new KeyNotFoundException();
        if (branch.HeadCheckpointId != request.ExpectedCheckpointId) throw new DbUpdateConcurrencyException("Branch changed. Reload before applying mechanics.");
        var current = await db.Checkpoints.SingleAsync(x => x.Id == branch.HeadCheckpointId, ct);
        var state = Json.Read<StoryState>(current.StateJson);
        var inventory = new Dictionary<string, int>(state.Inventory);
        var skills = new Dictionary<string, string>(state.Skills);
        if (request.Kind == "inventory")
        {
            var next = inventory.GetValueOrDefault(request.Subject) + request.Delta;
            if (next < 0 || next > 1_000_000) throw new InvalidOperationException("Inventory cannot become negative or exceed its limit.");
            inventory[request.Subject] = next;
        }
        else skills[request.Subject] = AdvanceSkill(skills.GetValueOrDefault(request.Subject, "F · 0/5"), request.Delta);
        var nextState = state with { Inventory = inventory, Skills = skills };
        nextState.Validate();
        var checkpoint = new Checkpoint { BranchId = branchId, Sequence = current.Sequence + 1, Label = "Deterministic mechanics", StateJson = Json.Write(nextState) };
        db.Checkpoints.Add(checkpoint);
        db.Messages.Add(new Message { BranchId = branchId, Sequence = checkpoint.Sequence, Role = "system", Content = $"{request.Kind} changed: {request.Subject} {FormatDelta(request.Delta)} ({request.Reason.Trim()})", Provider = "deterministic", Model = "mechanics-v1" });
        db.Mechanics.Add(new MechanicsLedgerEntry { BranchId = branchId, Sequence = checkpoint.Sequence, Kind = request.Kind, Subject = request.Subject.Trim(), Delta = request.Delta, Reason = request.Reason.Trim() });
        db.Events.Add(new StoryEvent { BranchId = branchId, Sequence = checkpoint.Sequence, Type = "mechanics", Summary = $"{request.Kind}: {request.Subject} {FormatDelta(request.Delta)}" });
        var summary = await db.Summaries.SingleOrDefaultAsync(x => x.BranchId == branchId, ct);
        var summaryText = $"At sequence {checkpoint.Sequence}, {request.Kind} changed {request.Subject.Trim()} by {FormatDelta(request.Delta)} ({request.Reason.Trim()}).";
        if (summary is null) db.Summaries.Add(new CampaignSummary { BranchId = branchId, ThroughSequence = checkpoint.Sequence, Text = summaryText });
        else { summary.ThroughSequence = checkpoint.Sequence; summary.Text = summaryText; summary.UpdatedAt = DateTime.UtcNow; }
        branch.HeadCheckpointId = checkpoint.Id; branch.Revision++;
        await db.SaveChangesAsync(ct); return checkpoint;
    }

    private static string AdvanceSkill(string value, int delta)
    {
        var level = 0; var pips = 0;
        var match = System.Text.RegularExpressions.Regex.Match(value ?? "", @"(?<level>\d+)\s*/\s*(?<pips>\d+)");
        if (match.Success) { level = int.Parse(match.Groups["level"].Value); pips = int.Parse(match.Groups["pips"].Value); }
        var total = Math.Clamp(level * 5 + pips + delta, 0, 500);
        return $"F · {total / 5}/{total % 5}";
    }
    private static string FormatDelta(int delta) => delta > 0 ? $"+{delta}" : delta.ToString();
}
