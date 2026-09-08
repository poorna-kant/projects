using KbApp;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// Resolve the Knowledge Base repository root so import paths work regardless of CWD.
static string ProjectRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null &&
           (!Directory.Exists(Path.Combine(dir.FullName, "kb-app")) ||
            !Directory.Exists(Path.Combine(dir.FullName, "knowledge-base"))))
        dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}
var root = ProjectRoot();
// V2: KB artifacts live in the tree. Plan(rel) targets the plan domain; the KB root enables multi-domain seeding.
string Plan(string rel) => Path.Combine(root, "knowledge-base", "plan", rel);
string KbRoot() => Path.Combine(root, "knowledge-base");

// V2 is fully tree-based: compile the narrative KB markdown artifact from the structured tree's concepts.
static string TreeKbComposite(string solutionRoot)
{
    var conceptsDir = Path.Combine(solutionRoot, "knowledge-base", "plan", "concepts");
    Directory.CreateDirectory("data");
    var outPath = Path.Combine("data", "knowledge-base.generated.md");
    if (Directory.Exists(conceptsDir))
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# SCOPE Knowledge Base").AppendLine();
        sb.AppendLine("Compiled from the structured public demo tree (knowledge-base/plan/concepts).").AppendLine();
        foreach (var f in Directory.EnumerateFiles(conceptsDir, "*.md").OrderBy(x => x, StringComparer.Ordinal))
        {
            var md = File.ReadAllText(f);
            if (md.StartsWith("---", StringComparison.Ordinal))
            {
                var e = md.IndexOf("\n---", 3, StringComparison.Ordinal);
                if (e >= 0) { var a = md.IndexOf('\n', e + 1); if (a >= 0) md = md[(a + 1)..]; }
            }
            sb.AppendLine(md.Trim()).AppendLine();
        }
        File.WriteAllText(outPath, sb.ToString());
    }
    return outPath;
}

// Config precedence: env-var over appsettings (mirrors the agent PoC drill).
// Local defaults today; the SAME keys point at Azure paths later (no code change).
string dbPath            = cfg["KB_DB_PATH"]            ?? "data/kb.db";
string snapshotRoot      = cfg["KB_SNAPSHOT_ROOT"]      ?? "data/snapshots";
string publishedConcepts = cfg["KB_PUBLISHED_CONCEPTS"] ?? "data/published/concepts.json";
// V2: seed the glossary from EVERY domain in the KB tree (pass the kb root; the store discovers domains).
string conceptsSeed      = cfg["KB_CONCEPTS_SEED"]      ?? KbRoot();
// V2: root of the CSCP-structured tree the typed-catalog browsers read (kb root; /kb in the container).
string treeRoot          = cfg["KB_TREE_ROOT"]          ?? (Directory.Exists(conceptsSeed) ? conceptsSeed : KbRoot());
string reportsSeed       = cfg["KB_REPORTS_SEED"]       ?? Plan("_runtime/reports.json");
string publishedRegistry = cfg["KB_PUBLISHED_REGISTRY"] ?? "data/published/view-registry.json";
// V2: JSON mirror of the canonical registry.yaml, for the JSON-based authoring store.
string registrySeed      = cfg["KB_REGISTRY_SEED"]      ?? Plan("_runtime/registry.json");

var store = new KbStore(dbPath, snapshotRoot, publishedConcepts, conceptsSeed, reportsSeed);
var registry = new RegistryStore(dbPath, snapshotRoot, publishedRegistry, registrySeed);

string publishedGlossary  = cfg["KB_PUBLISHED_GLOSSARY"]  ?? "data/published/glossary.json";
string publishedGrounding = cfg["KB_PUBLISHED_GROUNDING"] ?? "data/published/grounding-index.json";
string publishedKb        = cfg["KB_PUBLISHED_KB"]        ?? "data/published/supply-planning-kb.md";
string glossarySeed  = cfg["KB_GLOSSARY_SEED"]  ?? Plan("_runtime/schema-glossary.yaml");
string groundingSeed = cfg["KB_GROUNDING_SEED"] ?? Plan("_generated/grounding-index.json");
// V2: the narrative KB artifact is compiled from the tree's concepts (no flat supply-planning-kb.md).
string kbSeed        = cfg["KB_KB_SEED"]        ?? TreeKbComposite(root);
var artifacts = new ArtifactStore(dbPath, snapshotRoot);
artifacts.Register("glossary", "json", glossarySeed, publishedGlossary);
artifacts.Register("grounding", "json", groundingSeed, publishedGrounding);
artifacts.Register("knowledge-base", "markdown", kbSeed, publishedKb);

// Per-dataset synthetic refresh status used by the public demonstration.
string publishedRefresh = cfg["KB_PUBLISHED_REFRESH"] ?? "data/published/refresh-registry.json";
string refreshSeed      = cfg["KB_REFRESH_SEED"]      ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot", "refresh-registry.json");
artifacts.Register("refresh-registry", "json", refreshSeed, publishedRefresh);

// Steward "Check now" preserves the workflow but does not connect to a live source in this public edition.
var freshness = new FreshnessService(cfg, root, refreshSeed, json =>
{
    artifacts.SetContent("refresh-registry", json);
    artifacts.Publish("refresh-registry");
});

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

// ---- authoring plane (the app) ---------------------------------------------
app.MapGet("/api/terms", () => Results.Json(store.AllTerms().Select(ToDto)));
app.MapGet("/api/terms/{id}", (string id) =>
    store.GetTerm(id) is { } t ? Results.Json(ToDto(t)) : Results.NotFound());
app.MapGet("/api/sources", () => Results.Json(store.AllSources()));

app.MapPost("/api/terms", (ProposeDto d) =>
    Results.Json(ToDto(store.Propose(d.Name, d.Area, d.Owner, d.Def, d.Why, d.Derived))));
app.MapPost("/api/terms/{id}/approve", (string id) =>
    store.SetStatus(id, "Approved") is { } t ? Results.Json(ToDto(t)) : Results.NotFound());
app.MapPost("/api/terms/{id}/status", (string id, StatusDto d) =>
    store.SetStatus(id, d.Status) is { } t ? Results.Json(ToDto(t)) : Results.NotFound());
app.MapPost("/api/terms/{id}/definition", (string id, EditDto d) =>
    store.UpdateDefinition(id, d.Def, d.Why, d.Derived) is { } t ? Results.Json(ToDto(t)) : Results.NotFound());
app.MapDelete("/api/terms/{id}", (string id) =>
    store.DeleteTerm(id) ? Results.Ok() : Results.NotFound());

// ---- publish (approved only -> the agent's exact glossary schema) ----------
app.MapPost("/api/publish", () => Results.Json(store.Publish()));
app.MapGet("/api/publishes", () => Results.Json(store.Publishes()));
app.MapGet("/api/kb/current", () => Results.Json(store.CurrentInfo()));

// the raw file the agent reads (also on disk at KB_PUBLISHED_CONCEPTS)
app.MapGet("/api/published/concepts", () =>
    File.Exists(publishedConcepts)
        ? Results.Content(File.ReadAllText(publishedConcepts), "application/json")
        : Results.NotFound(new { message = "No version published yet." }));

// ---- semantic layer: measures + business rules (view-registry) -------------
app.MapGet("/api/measures", () => Results.Json(registry.AllMeasures()));
app.MapGet("/api/views", () => Results.Json(registry.AllViews()));
app.MapPost("/api/measures/{key}/edit", (string key, MeasureEditDto d) =>
    registry.EditMeasure(key, d.Qualifier, d.Explanation, d.Rule) ? Results.Ok() : Results.NotFound());
app.MapPost("/api/measures", (MeasureAddDto d) =>
    registry.AddMeasure(d.Key, d.View, d.Column, d.Qualifier, d.Explanation, d.Rule, d.Aggregate)
        ? Results.Ok()
        : Results.BadRequest(new { message = "Could not add measure \u2014 the view must already be governed here and the key must be unique." }));
app.MapPost("/api/views/{view}/approve", (string view) =>
    registry.ApproveView(view) ? Results.Ok() : Results.NotFound());
app.MapPost("/api/registry/publish", () => Results.Json(registry.Publish()));
app.MapGet("/api/registry/current", () => Results.Json(registry.CurrentInfo()));
app.MapGet("/api/published/registry", () =>
    File.Exists(publishedRegistry)
        ? Results.Content(File.ReadAllText(publishedRegistry), "application/json")
        : Results.NotFound(new { message = "No registry version published yet." }));

// ---- supporting governed artifacts (schema-linking glossary, grounding, markdown KB) ----
app.MapGet("/api/artifacts", () => Results.Json(artifacts.All()));
app.MapGet("/api/artifacts/{name}", (string name) =>
{
    var a = artifacts.Get(name);
    if (a is null) return Results.NotFound();
    var (kind, content, version, status) = a.Value;
    bool editable = name != "grounding";                 // 830KB machine-generated index is view-only
    bool truncated = false;
    var body = content;
    if (!editable && content.Length > 6000) { body = content[..6000]; truncated = true; }
    return Results.Json(new { name, kind, version, status, editable, length = content.Length, truncated, content = body });
});
app.MapPost("/api/artifacts/{name}/content", (string name, ArtifactEditDto d) =>
{
    if (name == "grounding")
        return Results.BadRequest(new { message = "The grounding index is machine-generated from the data and is not hand-edited." });
    var a = artifacts.Get(name);
    if (a is null) return Results.NotFound();
    if (a.Value.Kind == "json")
    {
        try { using var _ = System.Text.Json.JsonDocument.Parse(d.Content); }
        catch { return Results.BadRequest(new { message = "Invalid JSON \u2014 not saved." }); }
    }
    return artifacts.SetContent(name, d.Content) ? Results.Ok() : Results.NotFound();
});
app.MapPost("/api/artifacts/{name}/publish", (string name) => Results.Json(artifacts.Publish(name)));
app.MapPost("/api/artifacts/publish-all", () => Results.Json(artifacts.PublishAll()));

// author a brand-new governed file (JSON collection or Markdown) from the app
app.MapPost("/api/artifacts", (AddArtifactDto d) =>
{
    var name = (d.Name ?? "").Trim().ToLowerInvariant().Replace(' ', '-');
    name = new string(name.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
    if (name.Length == 0) return Results.BadRequest(new { message = "Enter a valid file name (letters, numbers, dashes)." });
    var kind = d.Kind == "markdown" ? "markdown" : "json";
    if (artifacts.Get(name) is not null) return Results.BadRequest(new { message = $"A file named '{name}' already exists." });
    var pubDir = Path.GetDirectoryName(Path.GetFullPath(publishedGlossary))!;
    var ext = kind == "markdown" ? ".md" : ".json";
    var starter = kind == "markdown"
        ? $"# {d.Name}\n\nDescribe this knowledge here.\n"
        : "{\n  \"items\": []\n}\n";
    return artifacts.Add(name, kind, Path.Combine(pubDir, name + ext), starter)
        ? Results.Json(new { name, kind })
        : Results.BadRequest(new { message = "Could not create the file." });
});

// ---- data-refresh freshness verification (steward "Check now") --------------
app.MapPost("/api/refresh/{view}/verify", (string view) => Results.Json(freshness.Start(view).ToDto()));
app.MapGet("/api/refresh/jobs/{id}", (string id) =>
    freshness.Get(id) is { } j ? Results.Json(j.ToDto()) : Results.NotFound());

// ---- demo: answer from the SAME published file the real agent consumes -----
app.MapGet("/api/agent/answer", (string q) => Results.Json(store.AnswerFromPublished(q ?? "")));

// ---- V2: typed-catalog browsers read straight from the CSCP-structured tree (domain-scoped) ----
var tree = new TreeReader(treeRoot);
var kbEval = new KbEval(tree);
app.MapGet("/api/domains", () => Results.Json(tree.Domains()));
app.MapGet("/api/catalog/{domain}/objects", (string domain) => Results.Json(tree.Objects(domain)));
app.MapGet("/api/catalog/{domain}/concepts", (string domain) => Results.Json(tree.Concepts(domain)));
app.MapGet("/api/catalog/{domain}/policies", (string domain) => Results.Json(tree.Policies(domain)));
app.MapGet("/api/catalog/{domain}/systems", (string domain) => Results.Json(tree.Systems(domain)));
app.MapGet("/api/catalog/{domain}/grounding", (string domain) => Results.Json(tree.Grounding(domain)));
app.MapGet("/api/catalog/{domain}/validation", (string domain) => Results.Json(tree.Validation(domain)));
app.MapGet("/api/catalog/{domain}/metrics", (string domain) => Results.Json(tree.Metrics(domain)));
// Full untruncated record for one catalog item (drill-in from a browser row). id via query to allow slashes.
app.MapGet("/api/catalog/{domain}/{type}/item", (string domain, string type, string id) =>
{
    var d = tree.Detail(domain, type, id);
    return d is null ? Results.NotFound(new { message = "Not found." }) : Results.Json(d);
});

// ---- V2 tree-as-source: read + WRITE the domain glossary straight to YAML, with a validation gate ----
var treeStore = new TreeStore(treeRoot);
app.MapGet("/api/tree/{domain}/terms", (string domain) => Results.Json(treeStore.Read(domain)));
app.MapPost("/api/tree/{domain}/terms/save", (string domain, TermEditDto d) =>
{
    // background dedup eval runs on every add/edit before the write lands
    var dupes = kbEval.FindDuplicates(domain, "terms", d.Id, TermText(d));
    var (ok, errors, doc) = treeStore.Save(domain, d);
    return ok ? Results.Json(new { ok, doc, duplicates = dupes }) : Results.BadRequest(new { ok, errors });
});
app.MapPost("/api/tree/{domain}/terms/dupecheck", (string domain, TermEditDto d) =>
    Results.Json(new { matches = kbEval.FindDuplicates(domain, "terms", d.Id, TermText(d)) }));
app.MapPost("/api/tree/{domain}/terms/delete", (string domain, TermDeleteDto d) =>
{
    var (ok, errors, doc) = treeStore.Delete(domain, d.Id);
    return ok ? Results.Json(new { ok, doc }) : Results.BadRequest(new { ok, errors });
});
app.MapPost("/api/domains/scaffold", (ScaffoldDto d) =>
{
    var (ok, msg) = treeStore.ScaffoldDomain(d.Name);
    return ok ? Results.Json(new { ok, msg }) : Results.BadRequest(new { ok, message = msg });
});
app.MapPost("/api/domains/{domain}/metrics/generate", (string domain) =>
{
    var (ok, count, msg) = treeStore.GenerateMetrics(domain);
    return ok ? Results.Json(new { ok, count, msg }) : Results.BadRequest(new { ok, message = msg });
});

// ---- V2 governed catalog editing: read the editable fields, propose a change, steward review/apply ----
var reviews = new ReviewStore(treeRoot);
// The raw editable fields for one item (populates the edit form). id via query to allow slashes.
app.MapGet("/api/catalog/{domain}/{type}/edit", (string domain, string type, string id) =>
{
    var (found, title, file, fields, body) = treeStore.ReadCatalogItem(domain, type, id);
    return found
        ? Results.Json(new { title, file, body, fields = fields.Select(f => new { key = f.Key, label = f.Label, value = f.Value, multiline = f.Multiline }) })
        : Results.NotFound(new { message = "Not found." });
});
// Steward direct save: applies straight to the tree file (duplicate eval returned as a non-blocking warning).
app.MapPost("/api/catalog/{domain}/{type}/apply", (string domain, string type, CatApplyDto d) =>
{
    var warnings = kbEval.FindDuplicates(domain, type, d.ItemId, CandidateText(d.Fields, d.Body));
    var (ok, errors, file) = treeStore.SaveCatalogItem(domain, type, d.ItemId, d.Fields ?? new(), d.Body);
    return ok ? Results.Json(new { ok, file, warnings }) : Results.BadRequest(new { ok, errors });
});
// On-demand duplicate/consistency eval (used live from the edit form before submitting).
app.MapPost("/api/eval/dupecheck", (ReviewSubmitDto d) =>
    Results.Json(new { matches = kbEval.FindDuplicates(d.Domain, d.Type, d.ItemId, CandidateText(d.Fields, d.Body)) }));
// Non-steward edit: parked as a proposal for the steward to review (current values captured for the diff).
app.MapPost("/api/reviews", (ReviewSubmitDto d) =>
{
    if (string.Equals(d.Type, "terms", StringComparison.OrdinalIgnoreCase))
    {
        if (!ValidDomain(d.Domain))
            return Results.BadRequest(new { message = "Select a valid knowledge-base domain." });
        var proposed = TermFromProposalFields(d.Fields, d.ItemId);
        if (string.IsNullOrWhiteSpace(proposed.Term))
            return Results.BadRequest(new { message = "Term name is required." });
        var doc = treeStore.Read(d.Domain);
        var current = doc.Terms.FirstOrDefault(t =>
            (!string.IsNullOrWhiteSpace(proposed.Id) && string.Equals(t.Id, proposed.Id, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(t.Term?.Trim(), proposed.Term.Trim(), StringComparison.OrdinalIgnoreCase));

        var proposal = new Proposal
        {
            Domain = d.Domain,
            Type = "terms",
            ItemId = string.IsNullOrWhiteSpace(proposed.Id) ? (d.ItemId ?? "") : proposed.Id!,
            Title = string.IsNullOrWhiteSpace(d.Title) ? (proposed.Term ?? "Glossary term") : d.Title!,
            Fields = TermProposalFields(proposed),
            Current = CurrentTermFields(current),
            ProposedBy = string.IsNullOrWhiteSpace(d.By) ? "a user" : d.By!,
            // background eval: attach likely duplicate terms so steward sees them at review time
            Duplicates = kbEval.FindDuplicates(d.Domain, "terms", proposed.Id, TermText(proposed)),
        };
        return Results.Json(new { ok = true, id = reviews.Submit(proposal), duplicates = proposal.Duplicates });
    }

    var (found, title, _, fields, body) = treeStore.ReadCatalogItem(d.Domain, d.Type, d.ItemId);
    if (!found) return Results.NotFound(new { message = "Item not found." });
    var p = new Proposal
    {
        Domain = d.Domain,
        Type = d.Type,
        ItemId = d.ItemId,
        Title = string.IsNullOrWhiteSpace(d.Title) ? title : d.Title!,
        Fields = d.Fields ?? new(),
        Body = d.Body,
        Current = fields.ToDictionary(f => f.Key, f => f.Value),
        CurrentBody = body,
        ProposedBy = string.IsNullOrWhiteSpace(d.By) ? "a user" : d.By!,
        // background eval: attach any likely-duplicate KB items so the steward sees them at review time
        Duplicates = kbEval.FindDuplicates(d.Domain, d.Type, d.ItemId, CandidateText(d.Fields, d.Body)),
    };
    return Results.Json(new { ok = true, id = reviews.Submit(p), duplicates = p.Duplicates });
});
app.MapGet("/api/reviews", () => Results.Json(reviews.List()));
app.MapPost("/api/reviews/{rid}/approve", (string rid) =>
{
    var p = reviews.Get(rid);
    if (p is null) return Results.NotFound(new { message = "Proposal not found." });

    if (string.Equals(p.Type, "terms", StringComparison.OrdinalIgnoreCase))
    {
        if (!ValidDomain(p.Domain))
            return Results.BadRequest(new { ok = false, errors = new[] { "The proposal references an invalid knowledge-base domain." } });
        var proposed = TermFromProposalFields(p.Fields, p.ItemId);
        var (okTerm, errorsTerm, docTerm) = treeStore.Save(p.Domain, proposed);
        if (!okTerm) return Results.BadRequest(new { ok = false, errors = errorsTerm });
        reviews.Delete(rid);
        return Results.Json(new { ok = true, doc = docTerm });
    }

    var (ok, errors, file) = treeStore.SaveCatalogItem(p.Domain, p.Type, p.ItemId, p.Fields, p.Body);
    if (!ok) return Results.BadRequest(new { ok, errors });
    reviews.Delete(rid);
    return Results.Json(new { ok = true, file });
});
app.MapPost("/api/reviews/{rid}/reject", (string rid, RejectDto dto) =>
{
    var reason = (dto?.Reason ?? "").Trim();
    if (reason.Length == 0)
        return Results.Json(new { ok = false, errors = new[] { "A rejection reason is required." } }, statusCode: 400);
    var p = reviews.Reject(rid, reason, string.IsNullOrWhiteSpace(dto?.By) ? "Data steward" : dto!.By!);
    return p is null
        ? Results.Json(new { ok = false, errors = new[] { "Proposal not found." } }, statusCode: 404)
        : Results.Json(new { ok = true });
});
app.MapGet("/api/reviews/rejected", () => Results.Json(reviews.ListRejected()));

app.Run();

// Flatten a proposed edit into one text blob for the duplicate eval.
static string CandidateText(Dictionary<string, string>? fields, string? body)
{
    var parts = new List<string>();
    if (fields != null) parts.AddRange(fields.Values.Where(v => !string.IsNullOrWhiteSpace(v)));
    if (!string.IsNullOrWhiteSpace(body)) parts.Add(body!);
    return string.Join(" \n ", parts);
}

// Flatten a proposed glossary term into one text blob for the duplicate eval.
static string TermText(TermEditDto d) =>
    string.Join(" \n ", new[] { d.Term, d.Definition, d.Aliases, d.Category }.Where(s => !string.IsNullOrWhiteSpace(s)));

static bool ValidDomain(string? domain) =>
    !string.IsNullOrWhiteSpace(domain) &&
    domain.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_');

static TermEditDto TermFromProposalFields(Dictionary<string, string>? fields, string? fallbackId)
{
    fields ??= new();
    fields.TryGetValue("Id", out var id);
    fields.TryGetValue("Term", out var term);
    fields.TryGetValue("Category", out var category);
    fields.TryGetValue("Definition", out var definition);
    fields.TryGetValue("Aliases", out var aliases);
    fields.TryGetValue("SeeAlso", out var seeAlso);
    return new TermEditDto(
        string.IsNullOrWhiteSpace(id) ? (string.IsNullOrWhiteSpace(fallbackId) ? null : fallbackId) : id,
        term,
        category,
        definition,
        aliases,
        seeAlso);
}

static Dictionary<string, string> TermProposalFields(TermEditDto d) => new(StringComparer.Ordinal)
{
    ["Id"] = d.Id ?? "",
    ["Term"] = d.Term ?? "",
    ["Category"] = d.Category ?? "",
    ["Definition"] = d.Definition ?? "",
    ["Aliases"] = d.Aliases ?? "",
    ["SeeAlso"] = d.SeeAlso ?? "",
};

static Dictionary<string, string> CurrentTermFields(TermItem? t) => new(StringComparer.Ordinal)
{
    ["Id"] = t?.Id ?? "",
    ["Term"] = t?.Term ?? "",
    ["Category"] = t?.Category ?? "",
    ["Definition"] = t?.Definition ?? "",
    ["Aliases"] = t?.Aliases is { Count: > 0 } ? string.Join(", ", t.Aliases) : "",
    ["SeeAlso"] = t?.SeeAlso is { Count: > 0 } ? string.Join(", ", t.SeeAlso) : "",
};

static object ToDto(Term t) => new
{
    t.Id, t.Name, area = t.Area, t.Owner, t.Oi, t.Status, t.Def,
    aliases = string.Join(", ", KbStore.SplitAliases(t.Aliases)),
    t.Sensitivity, t.Hierarchy, t.Sources, t.Reports, t.Why, t.Derived,
    related = t.Related, t.Version, changed = t.LastChanged,
    trust = KbStore.TrustFor(t.Status)
};

record ProposeDto(string Name, string Area, string Owner, string Def, string Why, string Derived);
record StatusDto(string Status);
record EditDto(string Def, string Why, string Derived);
record MeasureEditDto(string? Qualifier, string? Explanation, string? Rule);
record MeasureAddDto(string Key, string View, string? Column, string? Qualifier, string? Explanation, string? Rule, string? Aggregate);
record ArtifactEditDto(string Content);
record AddArtifactDto(string Name, string Kind);
