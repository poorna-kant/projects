using System.Text.Json;

namespace KbApp;

// V2 governance: a proposed catalog edit is parked here as a JSON proposal until a data steward
// approves it (applied to the tree) or rejects it. Stored under {kbRoot}/_reviews (outside any domain).
// In prod (repo-backed KB) an approval would open a pull request instead of writing the file directly.
public sealed class ReviewStore
{
    private readonly string _dir;
    private static readonly JsonSerializerOptions J = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public ReviewStore(string kbRoot) => _dir = Path.Combine(kbRoot, "_reviews");

    public string Submit(Proposal p)
    {
        Directory.CreateDirectory(_dir);
        if (string.IsNullOrEmpty(p.Id))
            p.Id = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..6];
        if (string.IsNullOrEmpty(p.ProposedAt)) p.ProposedAt = DateTime.UtcNow.ToString("u");
        File.WriteAllText(Path.Combine(_dir, p.Id + ".json"), JsonSerializer.Serialize(p, J));
        return p.Id;
    }

    public List<Proposal> List()
    {
        var outp = new List<Proposal>();
        if (!Directory.Exists(_dir)) return outp;
        foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
            try { if (JsonSerializer.Deserialize<Proposal>(File.ReadAllText(f), J) is { } p) outp.Add(p); } catch { }
        return outp.OrderBy(p => p.ProposedAt).ToList();
    }

    public Proposal? Get(string id)
    {
        var f = Path.Combine(_dir, SafeId(id) + ".json");
        if (!File.Exists(f)) return null;
        try { return JsonSerializer.Deserialize<Proposal>(File.ReadAllText(f), J); } catch { return null; }
    }

    public bool Delete(string id)
    {
        var f = Path.Combine(_dir, SafeId(id) + ".json");
        if (!File.Exists(f)) return false;
        File.Delete(f);
        return true;
    }

    // Reject with a mandatory note: archive the proposal (status + reason) so the proposer can see why,
    // then remove it from the active queue. The rejected archive lives outside the pending folder.
    public Proposal? Reject(string id, string note, string by)
    {
        var p = Get(id);
        if (p is null) return null;
        p.Status = "rejected";
        p.RejectionNote = note;
        p.RejectedBy = by;
        p.RejectedAt = DateTime.UtcNow.ToString("u");
        Directory.CreateDirectory(RejectedDir);
        File.WriteAllText(Path.Combine(RejectedDir, SafeId(id) + ".json"), JsonSerializer.Serialize(p, J));
        Delete(id);
        return p;
    }

    public List<Proposal> ListRejected()
    {
        var outp = new List<Proposal>();
        if (!Directory.Exists(RejectedDir)) return outp;
        foreach (var f in Directory.EnumerateFiles(RejectedDir, "*.json"))
            try { if (JsonSerializer.Deserialize<Proposal>(File.ReadAllText(f), J) is { } p) outp.Add(p); } catch { }
        return outp.OrderByDescending(p => p.RejectedAt).ToList();
    }

    private string RejectedDir => Path.Combine(_dir, "rejected");
    private static string SafeId(string id) => new(id.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
}

public sealed class Proposal
{
    public string Id { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Type { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string Title { get; set; } = "";
    public Dictionary<string, string> Fields { get; set; } = new();
    public string? Body { get; set; }
    public Dictionary<string, string> Current { get; set; } = new();
    public string? CurrentBody { get; set; }
    public string ProposedBy { get; set; } = "";
    public string? ProposedAt { get; set; }
    public List<DupeMatch> Duplicates { get; set; } = new();
    public string Status { get; set; } = "pending";
    public string? RejectionNote { get; set; }
    public string? RejectedBy { get; set; }
    public string? RejectedAt { get; set; }
}

public record CatApplyDto(string ItemId, Dictionary<string, string>? Fields, string? Body);
public record ReviewSubmitDto(string Domain, string Type, string ItemId, string? Title, Dictionary<string, string>? Fields, string? Body, string? By);
public record RejectDto(string? Reason, string? By);
