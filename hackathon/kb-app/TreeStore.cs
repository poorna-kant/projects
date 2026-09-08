using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KbApp;

// V2 tree-as-source: reads and WRITES the domain glossary (knowledge-base/{domain}/terms/index.yaml).
// Edits go straight to the YAML the agent reads. A validation gate runs before every write.
public sealed class TreeStore
{
    private readonly string _kbRoot;
    private readonly IDeserializer _de = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance).IgnoreUnmatchedProperties().Build();
    private readonly ISerializer _ser = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull).Build();
    private readonly IDeserializer _dict = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();

    public TreeStore(string kbRoot) => _kbRoot = kbRoot;

    private string TermsPath(string domain) => Path.Combine(_kbRoot, domain, "terms", "index.yaml");

    public GlossaryDoc Read(string domain)
    {
        var p = TermsPath(domain);
        if (!File.Exists(p))
            return new GlossaryDoc { Type = "Glossary", Id = $"{domain}/terms", Domain = domain, Terms = new() };
        try { return _de.Deserialize<GlossaryDoc>(File.ReadAllText(p)) ?? Empty(domain); }
        catch { return Empty(domain); }
    }

    private static GlossaryDoc Empty(string d) => new() { Type = "Glossary", Id = $"{d}/terms", Domain = d, Terms = new() };

    // Upsert a term (by id, or create a new one from its name). Returns validation errors on failure.
    public (bool ok, List<string> errors, GlossaryDoc doc) Save(string domain, TermEditDto d)
    {
        var doc = Read(domain);
        var name = (d.Term ?? "").Trim();
        if (name.Length == 0) return (false, new() { "Term name is required." }, doc);

        var id = string.IsNullOrWhiteSpace(d.Id) ? $"{domain}/terms/{Slug(name)}" : d.Id!.Trim();
        var item = doc.Terms.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        var isNew = item is null;
        if (isNew) { item = new TermItem { Id = id }; doc.Terms.Add(item); }

        item!.Term = Clean(name);
        item.Category = string.IsNullOrWhiteSpace(d.Category) ? "General" : Clean(d.Category!.Trim());
        item.Definition = Clean((d.Definition ?? "").Trim());
        item.Aliases = SplitList(d.Aliases);
        item.SeeAlso = SplitList(d.SeeAlso);

        var errors = Validate(doc);
        if (errors.Count > 0)
        {
            if (isNew) doc.Terms.RemoveAll(t => t.Id == id); // roll back the tentative add
            return (false, errors, doc);
        }
        WriteFile(domain, doc);
        return (true, new(), doc);
    }

    public (bool ok, List<string> errors, GlossaryDoc doc) Delete(string domain, string id)
    {
        var doc = Read(domain);
        var removed = doc.Terms.RemoveAll(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return (false, new() { $"No term with id '{id}'." }, doc);
        WriteFile(domain, doc);
        return (true, new(), doc);
    }

    // ---- V2: governed editing of the typed catalogs (objects / concepts / policies / systems) ----
    // A curated set of human-facing fields is editable; every other key in the YAML/markdown is preserved.
    public sealed record CatField(string Key, string Label, string Value, bool Multiline);

    private static readonly Dictionary<string, (string key, string label, bool multiline)[]> CatSchema =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["objects"] = new[] { ("title", "Title", false), ("description", "Description", true),
                                  ("keystone_primary_table", "Keystone view / table", false),
                                  ("keystone_key_fields", "Key fields (comma-separated)", false) },
            ["concepts"] = new[] { ("name", "Name", false), ("summary", "Summary", true) },
            ["policies"] = new[] { ("name", "Name", false), ("category", "Category", false), ("description", "Description", true) },
            ["systems"] = new[] { ("name", "Name", false), ("display_name", "Display name", false), ("role", "Role", true) },
        };

    public bool CatEditable(string type) => CatSchema.ContainsKey(type);

    // Read the raw editable fields for one item (populates the edit form and the review diff).
    public (bool found, string title, string file, List<CatField> fields, string? body) ReadCatalogItem(string domain, string type, string id)
    {
        if (!CatSchema.TryGetValue(type, out var schema) || string.IsNullOrWhiteSpace(id))
            return (false, "", "", new(), null);

        if (type == "concepts")
        {
            var (path, fm, body) = FindConcept(domain, id);
            if (path is null) return (false, "", "", new(), null);
            var flds = schema.Select(s => new CatField(s.key, s.label, DStr(fm!, s.key), s.multiline)).ToList();
            return (true, DStr(fm!, "name"), RelPath(path), flds, body);
        }

        var (fpath, map) = FindYaml(domain, type, id);
        if (fpath is null) return (false, "", "", new(), null);
        var title = DStr(map!, "title"); if (title.Length == 0) title = DStr(map!, "name");
        var fields = new List<CatField>();
        foreach (var s in schema)
        {
            string val;
            if (s.key.StartsWith("keystone_", StringComparison.Ordinal))
            {
                var km = MapOf(map!, "keystone_mapping");
                var sub = s.key["keystone_".Length..];
                val = km is null ? "" : (sub == "key_fields" ? JoinL(km, "key_fields") : DStr(km, sub));
            }
            else val = DStr(map!, s.key);
            fields.Add(new CatField(s.key, s.label, val, s.multiline));
        }
        return (true, title, RelPath(fpath), fields, null);
    }

    // Apply an edit to the tree file (steward approve, or steward direct save). Unknown keys are preserved.
    public (bool ok, List<string> errors, string file) SaveCatalogItem(string domain, string type, string id, Dictionary<string, string> fields, string? body)
    {
        if (!CatSchema.ContainsKey(type)) return (false, new() { "This catalog is not editable." }, "");
        if (string.IsNullOrWhiteSpace(id)) return (false, new() { "Missing item id." }, "");
        fields ??= new();

        if (type == "concepts")
        {
            var (path, fm, _) = FindConcept(domain, id);
            if (path is null) return (false, new() { "Concept not found." }, "");
            if (fields.TryGetValue("name", out var nm))
            {
                if (string.IsNullOrWhiteSpace(nm)) return (false, new() { "Name is required." }, "");
                fm!["name"] = Clean(nm.Trim());
            }
            if (fields.TryGetValue("summary", out var sm)) fm!["summary"] = Clean((sm ?? "").Trim());
            File.WriteAllText(path, "---\n" + _ser.Serialize(fm) + "---\n\n" + (body ?? "").Trim() + "\n");
            return (true, new(), RelPath(path));
        }

        var (fpath, mp) = FindYaml(domain, type, id);
        if (fpath is null) return (false, new() { "Item not found." }, "");

        var nameKey = type == "objects" ? "title" : "name";
        if (fields.TryGetValue(nameKey, out var primary) && string.IsNullOrWhiteSpace(primary))
            return (false, new() { (nameKey == "title" ? "Title" : "Name") + " is required." }, "");

        foreach (var kv in fields)
        {
            var v = kv.Value ?? "";
            if (kv.Key.StartsWith("keystone_", StringComparison.Ordinal))
            {
                var km = MapOf(mp!, "keystone_mapping");
                if (km is null) { km = new Dictionary<object, object>(); mp!["keystone_mapping"] = km; }
                var sub = kv.Key["keystone_".Length..];
                if (sub == "key_fields") km[sub] = (SplitList(v) ?? new()).Cast<object>().ToList();
                else km[sub] = Clean(v.Trim());
            }
            else mp![kv.Key] = Clean(v.Trim());
        }
        File.WriteAllText(fpath, LeadComment(fpath) + _ser.Serialize(mp));
        return (true, new(), RelPath(fpath));
    }

    private (string? path, Dictionary<object, object>? fm, string body) FindConcept(string domain, string id)
    {
        var dir = Path.Combine(_kbRoot, domain, "concepts");
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir, "*.md"))
            {
                var (fm, body) = SplitMd(File.ReadAllText(f));
                if (fm != null && string.Equals(DStr(fm, "id"), id, StringComparison.OrdinalIgnoreCase))
                    return (f, fm, body);
            }
        return (null, null, "");
    }

    private (string? path, Dictionary<object, object>? map) FindYaml(string domain, string type, string id)
    {
        var dir = Path.Combine(_kbRoot, domain, type);
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir, "*.yaml").Where(x => Path.GetFileName(x) != "index.yaml"))
            {
                Dictionary<object, object> m;
                try { m = _dict.Deserialize<Dictionary<object, object>>(File.ReadAllText(f)) ?? new(); } catch { continue; }
                if (string.Equals(DStr(m, "id"), id, StringComparison.OrdinalIgnoreCase)) return (f, m);
            }
        return (null, null);
    }

    private (Dictionary<object, object>? fm, string body) SplitMd(string md)
    {
        if (!md.StartsWith("---", StringComparison.Ordinal)) return (null, md);
        var end = md.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return (null, md);
        var fmText = md.Substring(3, end - 3);
        var close = md.IndexOf('\n', end + 4);
        var body = close < 0 ? "" : md[(close + 1)..];
        try { return (_dict.Deserialize<Dictionary<object, object>>(fmText) ?? new(), body.Trim()); }
        catch { return (null, body.Trim()); }
    }

    private static string LeadComment(string path)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("#", StringComparison.Ordinal)) sb.Append(line).Append('\n');
                else break;
            }
            return sb.ToString();
        }
        catch { return ""; }
    }

    private string RelPath(string f) { try { return Path.GetRelativePath(_kbRoot, f).Replace('\\', '/'); } catch { return Path.GetFileName(f); } }
    private static Dictionary<object, object>? MapOf(Dictionary<object, object> m, string key)
        => m.TryGetValue(key, out var v) && v is Dictionary<object, object> d ? d : null;
    private static string JoinL(Dictionary<object, object> m, string key)
        => m.TryGetValue(key, out var v) && v is List<object> l ? string.Join(", ", l.Select(x => x?.ToString())) : "";

    // ---- validation gate (same spirit as scripts/validate-kb.py, run before every write) ----
    private static List<string> Validate(GlossaryDoc doc)
    {
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in doc.Terms)
        {
            if (string.IsNullOrWhiteSpace(t.Term)) { errors.Add("A term is missing its name."); continue; }
            if (string.IsNullOrWhiteSpace(t.Definition)) errors.Add($"\"{t.Term}\" is missing a definition.");
            if (!string.IsNullOrWhiteSpace(t.Id) && !seen.Add(t.Id!)) errors.Add($"Duplicate term id '{t.Id}'.");
        }
        return errors;
    }

    private void WriteFile(string domain, GlossaryDoc doc)
    {
        var path = TermsPath(domain);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var body = _ser.Serialize(doc);
        File.WriteAllText(path, "# Plan-domain glossary. Authored via the Knowledge Base app (tree-as-source).\n" + body);
    }

    // V2: scaffold a new domain so it is instantly discovered by the app and the agent (no code change).
    public (bool ok, string msg) ScaffoldDomain(string? rawName)
    {
        var name = (rawName ?? "").Trim().ToLowerInvariant();
        name = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        if (name.Length == 0) return (false, "Enter a valid domain name (letters, numbers, dashes).");
        var dir = Path.Combine(_kbRoot, name);
        if (File.Exists(Path.Combine(dir, "overview.yaml"))) return (false, $"Domain '{name}' already exists.");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "overview.yaml"),
            $"type: Overview\nid: {name}/overview\ntitle: {Cap(name)}\ndomain: {name}\ndescription: TODO describe the {name} domain.\noverview: TODO\ncatalogs: [concepts, objects, policies, terms, systems, metrics, flows, process, workflows]\n");
        File.WriteAllText(Path.Combine(dir, "index.yaml"),
            $"type: DomainIndex\nid: {name}\ntitle: {Cap(name)}\ndomain: {name}\ndescription: TODO.\noverview: {name}/overview\ncatalogs: []\n");
        foreach (var (cat, typ, key) in new[] { ("concepts", "ConceptIndex", "concepts"), ("objects", "ObjectIndex", "objects"), ("policies", "PolicyIndex", "policies"), ("systems", "SystemIndex", "systems"), ("metrics", "MetricIndex", "metrics"), ("flows", "FlowIndex", "flows"), ("workflows", "WorkflowIndex", "workflows") })
        {
            Directory.CreateDirectory(Path.Combine(dir, cat));
            File.WriteAllText(Path.Combine(dir, cat, "index.yaml"), $"type: {typ}\nid: {name}/{cat}\ndomain: {name}\n{key}: []\n");
        }
        Directory.CreateDirectory(Path.Combine(dir, "process"));
        File.WriteAllText(Path.Combine(dir, "process", "index.yaml"), $"type: ProcessIndex\nid: {name}/process\ndomain: {name}\nsubprocesses: []\n");
        Directory.CreateDirectory(Path.Combine(dir, "terms"));
        File.WriteAllText(Path.Combine(dir, "terms", "index.yaml"), $"type: Glossary\nid: {name}/terms\ndomain: {name}\ndescription: {name}-domain glossary.\nterms: []\n");
        Directory.CreateDirectory(Path.Combine(dir, "_runtime"));
        File.WriteAllText(Path.Combine(dir, "_runtime", "registry.yaml"), $"version: {name}-registry-0\noutOfDomainTerms: []\nviews: []\n");
        return (true, $"Domain '{name}' created \u2014 it is now discoverable.");
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    // V2: the Metrics "feed" — machine-generate the metrics catalog from the governed semantic layer
    // (_runtime/registry.yaml measures), mirroring how _generated/grounding-index.json is produced.
    // Each metric back-links to its measure; replace with the Keystone Metrics-Tree feed when connected.
    public (bool ok, int count, string msg) GenerateMetrics(string domain)
    {
        var regPath = Path.Combine(_kbRoot, domain, "_runtime", "registry.yaml");
        if (!File.Exists(regPath)) return (false, 0, $"No _runtime/registry.yaml for '{domain}' — nothing to derive metrics from.");
        Dictionary<object, object> reg;
        try { reg = _dict.Deserialize<Dictionary<object, object>>(File.ReadAllText(regPath)) ?? new(); }
        catch (Exception e) { return (false, 0, "registry.yaml parse error: " + e.Message); }

        var metrics = new List<MetricItem>();
        if (reg.TryGetValue("views", out var vs) && vs is List<object> views)
            foreach (var v in views)
            {
                if (v is not Dictionary<object, object> vd) continue;
                var vdomain = DStr(vd, "domain");
                if (!vd.TryGetValue("measures", out var ms) || ms is not List<object> measures) continue;
                foreach (var m in measures)
                {
                    if (m is not Dictionary<object, object> md) continue;
                    var key = DStr(md, "key");
                    if (key.Length == 0) continue;
                    var qualifier = DStr(md, "qualifier");
                    metrics.Add(new MetricItem
                    {
                        Id = $"{domain}/metrics/{key.Replace('.', '-')}",
                        Name = MetricName(qualifier, key),
                        MetricCode = "PLAN-" + key.ToUpperInvariant().Replace('.', '-'),
                        Category = Cap(vdomain.Length > 0 ? vdomain : domain),
                        Unit = MetricUnit(key, qualifier),
                        Description = Clean(DStr(md, "explanation")),
                        MeasureRef = key,
                        Source = "derived from the governed semantic layer (_runtime/registry.yaml)"
                    });
                }
            }

        var doc = new MetricsDoc
        {
            Type = "MetricIndex",
            Id = $"{domain}/metrics",
            Domain = domain,
            Metrics = metrics,
            Note = "Machine-generated from the governed semantic layer (_runtime/registry.yaml), read-only. " +
                   "Each metric back-links to its measure via measure_ref. Replace with the Keystone Metrics-Tree feed (real metric_code) when connected."
        };
        var path = Path.Combine(_kbRoot, domain, "metrics", "index.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# Metrics catalog — machine-generated from the semantic layer (tree-as-source generator).\n" + _ser.Serialize(doc));
        return (true, metrics.Count, $"Generated {metrics.Count} metric{(metrics.Count == 1 ? "" : "s")} for '{domain}' from the semantic layer.");
    }

    private static string DStr(Dictionary<object, object> m, string key)
        => m.TryGetValue(key, out var v) && v != null ? v.ToString()!.Trim() : "";

    private static string MetricName(string qualifier, string key)
    {
        if (string.IsNullOrWhiteSpace(qualifier))
            return Cap(key.Replace('.', ' ').Replace('-', ' '));
        var head = qualifier.Split('\u2014')[0];          // text before the em dash
        head = System.Text.RegularExpressions.Regex.Replace(head, @"\([^)]*\)", "").Trim();
        head = System.Text.RegularExpressions.Regex.Replace(head, @"\s+", " ");
        return head.Length > 0 ? head : Cap(key.Replace('.', ' '));
    }

    private static string MetricUnit(string key, string qualifier)
    {
        var k = key.ToLowerInvariant();
        var q = qualifier.ToLowerInvariant();
        if (k.Contains("slipday") || k.Contains("leadtime") || k.Contains("offset") || q.Contains(" days")) return "days";
        if (k.EndsWith(".ids") || q.Contains("count of distinct") || q.Contains("how many") || q.Contains("distinct ")) return "count";
        if (k.StartsWith("t2.")) return "components";
        return "racks";
    }

    private static string Clean(string s) => s.Replace(" \u2014 ", ", ").Replace("\u2014", ",");
    private static List<string>? SplitList(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var list = csv.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Select(Clean).Where(x => x.Length > 0).Distinct().ToList();
        return list.Count > 0 ? list : null;
    }
    private static string Slug(string s)
    {
        var chars = s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }
}

public sealed class GlossaryDoc
{
    public string? Type { get; set; } = "Glossary";
    public string? Id { get; set; }
    public string? Domain { get; set; }
    public string? Description { get; set; }
    public List<TermItem> Terms { get; set; } = new();
}

public sealed class TermItem
{
    public string? Id { get; set; }
    public string? Term { get; set; }
    public string? Category { get; set; }
    public string? Expansion { get; set; }
    public string? Definition { get; set; }
    public List<string>? Aliases { get; set; }
    public List<string>? SeeAlso { get; set; }
    public List<string>? Synonyms { get; set; }
}

public record TermEditDto(string? Id, string? Term, string? Category, string? Definition, string? Aliases, string? SeeAlso);
public record TermDeleteDto(string Id);
public record ScaffoldDto(string? Name);

public sealed class MetricsDoc
{
    public string? Type { get; set; } = "MetricIndex";
    public string? Id { get; set; }
    public string? Domain { get; set; }
    public List<MetricItem> Metrics { get; set; } = new();
    public string? Note { get; set; }
}

public sealed class MetricItem
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? MetricCode { get; set; }
    public string? Category { get; set; }
    public string? Unit { get; set; }
    public string? Description { get; set; }
    public string? MeasureRef { get; set; }
    public string? Source { get; set; }
}
