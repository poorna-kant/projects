using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace KbApp;

public sealed class FreshnessJob
{
    public required string Id { get; init; }
    public required string View { get; init; }
    public required string Label { get; init; }
    public string State { get; set; } = "checking";
    public string? Outcome { get; set; }
    public string? KeystoneLatest { get; set; }
    public string? Ours { get; set; }
    public string? Message { get; set; }
    public bool Synced { get; set; }
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    public object ToDto() => new
    {
        id = Id,
        view = View,
        label = Label,
        state = State,
        outcome = Outcome,
        keystoneLatest = KeystoneLatest,
        ours = Ours,
        message = Message,
        synced = Synced,
        elapsedMs = (int)(DateTimeOffset.UtcNow - StartedUtc).TotalMilliseconds
    };
}

// Public demo adapter: preserves the steward workflow without connecting to a private data platform.
public sealed class FreshnessService
{
    private readonly string _refreshFile;
    private readonly ConcurrentDictionary<string, FreshnessJob> _jobs = new();

    public FreshnessService(IConfiguration configuration, string projectRoot, string refreshFile, Action<string>? publishJson)
    {
        _refreshFile = refreshFile;
    }

    public FreshnessJob? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    public FreshnessJob Start(string view)
    {
        var label = FindLabel(view) ?? view;
        var job = new FreshnessJob
        {
            Id = Guid.NewGuid().ToString("n"),
            View = view,
            Label = label,
            State = "done",
            Outcome = "reference",
            Message = "This public demo uses synthetic freshness metadata and does not connect to a live data source."
        };
        _jobs[job.Id] = job;
        return job;
    }

    private string? FindLabel(string view)
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(_refreshFile))?.AsObject();
            var datasets = root?["datasets"]?.AsArray();
            if (datasets is null) return null;
            foreach (var dataset in datasets)
                if (dataset?["view"]?.GetValue<string>() == view)
                    return dataset?["label"]?.GetValue<string>();
        }
        catch
        {
            // A missing demo registry is reported through the generic view label.
        }
        return null;
    }
}
