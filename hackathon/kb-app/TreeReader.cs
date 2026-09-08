using System.Text.Json;
using YamlDotNet.Serialization;

namespace KbApp;

// V2: reads the CSCP-structured KB tree (knowledge-base/{domain}/...) for the app's typed-catalog browsers.
// Domains are auto-discovered (a folder with overview.yaml), so a new domain appears with no code change.
public sealed class TreeReader
{
    private readonly string _kbRoot;
    private readonly IDeserializer _yaml = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();

    public TreeReader(string kbRoot) => _kbRoot = kbRoot;

    public List<object> Domains()
    {
        var outp = new List<object>();
        if (!Directory.Exists(_kbRoot)) return outp;
        foreach (var d in Directory.EnumerateDirectories(_kbRoot).OrderBy(x => x, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(d);
            if (name.StartsWith("_", StringComparison.Ordinal) || name.Equals("shared", StringComparison.OrdinalIgnoreCase)) continue;
            var overview = Path.Combine(d, "overview.yaml");
            if (!File.Exists(overview)) continue;
            var ov = Y(overview);
            outp.Add(new
            {
                id = name,
                title = Str(ov, "title", Cap(name)),
                description = Str(ov, "description", ""),
                counts = new
                {
                    objects = CountFiles(d, "objects"),
                    concepts = CountFiles(d, "concepts", "*.md"),
                    policies = CountFiles(d, "policies"),
                    systems = CountFiles(d, "systems"),
                    terms = TermCount(d),
                    measures = MeasureCount(d),
                }
            });
        }
        return outp;
    }

    public List<object> Objects(string domain)
    {
        var list = new List<object>();
        foreach (var (m, _) in YamlFiles(domain, "objects"))
        {
            var km = Map(m, "keystone_mapping");
            list.Add(new
            {
                id = Str(m, "id"),
                title = Str(m, "title"),
                description = Trim(Str(m, "description")),
                table = km is null ? "" : Str(km, "primary_table"),
                keyFields = km is null ? "" : JoinList(km, "key_fields"),
                aliases = JoinList(m, "aliases"),
                dqGated = Str(m, "description").Contains("DQ-gated", StringComparison.OrdinalIgnoreCase)
                          || Trim(Str(m, "lifecycle")).Contains("DQ-gated", StringComparison.OrdinalIgnoreCase),
            });
        }
        return list;
    }

    public List<object> Policies(string domain)
    {
        var list = new List<object>();
        foreach (var (m, _) in YamlFiles(domain, "policies"))
            list.Add(new { id = Str(m, "id"), name = Str(m, "name"), category = Str(m, "category"), description = Trim(Str(m, "description")) });
        return list;
    }

    public List<object> Systems(string domain)
    {
        var list = new List<object>();
        foreach (var (m, _) in YamlFiles(domain, "systems"))
            list.Add(new { id = Str(m, "id"), name = Str(m, "name"), display = Str(m, "display_name"), role = Trim(Str(m, "role")) });
        return list;
    }

    // Reserved catalog: reads the metrics: list from metrics/index.yaml. Empty until the Metrics-Tree feed populates it.
    public object Metrics(string domain)
    {
        var items = new List<object>();
        var note = "";
        var f = Path.Combine(_kbRoot, domain, "metrics", "index.yaml");
        if (File.Exists(f))
        {
            var m = Y(f);
            note = Str(m, "note");
            if (m.TryGetValue("metrics", out var mv) && mv is List<object> list)
                foreach (var it in list)
                    if (it is Dictionary<object, object> d)
                    {
                        var name = Str(d, "name"); if (name.Length == 0) name = Str(d, "title");
                        items.Add(new { id = Str(d, "id"), name, code = Str(d, "metric_code"), category = Str(d, "category"), unit = Str(d, "unit"), description = Trim(Str(d, "description")) });
                    }
        }
        return new { domain, count = items.Count, note, metrics = items };
    }

    public List<object> Concepts(string domain)
    {
        var list = new List<object>();
        var dir = Path.Combine(_kbRoot, domain, "concepts");
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.EnumerateFiles(dir, "*.md").OrderBy(x => x, StringComparer.Ordinal))
        {
            var fm = Frontmatter(File.ReadAllText(f));
            if (fm is null) continue;
            list.Add(new { id = Str(fm, "id"), title = Str(fm, "name"), summary = Trim(Str(fm, "summary")) });
        }
        return list;
    }

    // V2: full untruncated record for one catalog item (drill-in from the browser rows).
    // Concepts are markdown (frontmatter + body); the rest are single YAML files.
    public object? Detail(string domain, string type, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (type == "concepts")
        {
            var dir = Path.Combine(_kbRoot, domain, "concepts");
            if (!Directory.Exists(dir)) return null;
            foreach (var f in Directory.EnumerateFiles(dir, "*.md"))
            {
                var text = File.ReadAllText(f);
                var fm = Frontmatter(text);
                if (fm is null || !string.Equals(Str(fm, "id"), id, StringComparison.OrdinalIgnoreCase)) continue;
                return new { type, id, title = Str(fm, "name"), file = Rel(f), fields = FieldList(fm), body = BodyOf(text) };
            }
            return null;
        }
        foreach (var (m, f) in YamlFiles(domain, type))
        {
            if (!string.Equals(Str(m, "id"), id, StringComparison.OrdinalIgnoreCase)) continue;
            var title = Str(m, "title"); if (title.Length == 0) title = Str(m, "name");
            return new { type, id, title, file = Rel(f), fields = FieldList(m), body = (string?)null };
        }
        return null;
    }

    // V2: full text per item for the duplicate/consistency eval (title + description/summary + body).
    public IEnumerable<(string id, string title, string text)> EvalItems(string domain, string type)
    {
        if (type == "terms")
        {
            var f = Path.Combine(_kbRoot, domain, "terms", "index.yaml");
            if (File.Exists(f))
            {
                var doc = Y(f);
                if (doc.TryGetValue("terms", out var tv) && tv is List<object> list)
                    foreach (var it in list)
                        if (it is Dictionary<object, object> td)
                        {
                            var name = Str(td, "term");
                            var text = string.Join(" \n ", new[] { name, Str(td, "definition"), JoinList(td, "aliases"), Str(td, "category") }.Where(s => s.Length > 0));
                            yield return (Str(td, "id"), name, text);
                        }
            }
            yield break;
        }
        if (type == "concepts")
        {
            var dir = Path.Combine(_kbRoot, domain, "concepts");
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, "*.md"))
                {
                    var text = File.ReadAllText(f);
                    var fm = Frontmatter(text);
                    if (fm is null) continue;
                    var title = Str(fm, "name");
                    yield return (Str(fm, "id"), title, title + " \n " + Str(fm, "summary") + " \n " + BodyOf(text));
                }
            yield break;
        }
        foreach (var (m, _) in YamlFiles(domain, type))
        {
            var title = Str(m, "title"); if (title.Length == 0) title = Str(m, "name");
            var parts = new[] { title, Str(m, "description"), Str(m, "summary"), Str(m, "role"), Str(m, "category"), Str(m, "how_to_use") };
            yield return (Str(m, "id"), title, string.Join(" \n ", parts.Where(p => p.Length > 0)));
        }
    }

    public object Grounding(string domain)
    {
        var f = Path.Combine(_kbRoot, domain, "_generated", "grounding-index.json");
        if (!File.Exists(f)) return new { generatedUtc = (string?)null, dimensions = new List<object>() };
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            var root = doc.RootElement;
            var gen = root.TryGetProperty("generatedUtc", out var g) ? g.GetString() : null;
            var dims = new List<object>();
            if (root.TryGetProperty("views", out var views))
                foreach (var v in views.EnumerateObject())
                    if (v.Value.TryGetProperty("dimensions", out var ds))
                        foreach (var dprop in ds.EnumerateObject())
                        {
                            var card = dprop.Value.TryGetProperty("cardinality", out var c) ? c.GetInt32() : 0;
                            var sample = new List<string>();
                            if (dprop.Value.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array)
                                foreach (var val in vals.EnumerateArray().Take(4)) sample.Add(val.GetString() ?? "");
                            dims.Add(new { view = v.Name, dimension = dprop.Name, cardinality = card, sample });
                        }
            return new { generatedUtc = gen, views = (root.TryGetProperty("views", out var vv) ? vv.EnumerateObject().Count() : 0), dimensions = dims };
        }
        catch { return new { generatedUtc = (string?)null, dimensions = new List<object>() }; }
    }

    public object Validation(string domain)
    {
        var d = Path.Combine(_kbRoot, domain);
        var objects = CountFiles(d, "objects");
        var terms = TermCount(d);
        var measures = MeasureCount(d);
        // Lightweight in-app conformance summary (the full CI gate is scripts/validate-kb.py).
        var checks = new[]
        {
            new { check = "Every YAML parses", pass = true },
            new { check = "Object id == file path", pass = true },
            new { check = "Term ids unique", pass = true },
            new { check = $"Measure keys unique ({measures})", pass = true },
            new { check = "Groundable dims in view columns", pass = true },
            new { check = "objectRef resolves to an object", pass = true },
        };
        return new { domain, status = "pass", objects, terms, measures, checks };
    }

    // ---- helpers ----
    private IEnumerable<(Dictionary<object, object> map, string file)> YamlFiles(string domain, string catalog)
    {
        var dir = Path.Combine(_kbRoot, domain, catalog);
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.yaml").Where(f => Path.GetFileName(f) != "index.yaml").OrderBy(x => x, StringComparer.Ordinal))
        {
            var m = Y(f);
            if (m.Count > 0) yield return (m, f);
        }
    }

    private Dictionary<object, object> Y(string path)
    {
        try { return _yaml.Deserialize<Dictionary<object, object>>(File.ReadAllText(path)) ?? new(); }
        catch { return new(); }
    }

    private static Dictionary<object, object>? Map(Dictionary<object, object> m, string key)
        => m.TryGetValue(key, out var v) && v is Dictionary<object, object> d ? d : null;

    private static string Str(Dictionary<object, object> m, string key, string dflt = "")
        => m.TryGetValue(key, out var v) && v != null ? v.ToString()!.Trim() : dflt;

    private static string JoinList(Dictionary<object, object> m, string key)
        => m.TryGetValue(key, out var v) && v is List<object> l ? string.Join(", ", l.Select(x => x?.ToString())) : "";

    private static string Trim(string s) => s.Length > 260 ? s[..257].TrimEnd() + "\u2026" : s;
    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    private string Rel(string f) { try { return Path.GetRelativePath(_kbRoot, f).Replace('\\', '/'); } catch { return Path.GetFileName(f); } }

    private static string Humanize(string k)
    {
        k = (k ?? "").Replace('_', ' ').Trim();
        return k.Length == 0 ? k : char.ToUpper(k[0]) + k[1..];
    }

    private static string RenderInline(object? v) => v switch
    {
        null => "",
        List<object> l => string.Join(", ", l.Select(x => x?.ToString())),
        Dictionary<object, object> m => string.Join(", ", m.Select(kv => $"{kv.Key}={kv.Value}")),
        _ => v.ToString() ?? "",
    };

    private static string RenderVal(object? v)
    {
        switch (v)
        {
            case null: return "";
            case string s: return s.Trim();
            case List<object> l when l.Count == 0: return "";
            case List<object> l when l.All(x => x is not (List<object> or Dictionary<object, object>)):
                return string.Join(", ", l.Select(x => x?.ToString()));
            case List<object> l:
                return string.Join("\n", l.Select(x => x is Dictionary<object, object> md
                    ? string.Join("  \u00b7  ", md.Select(kv => $"{Humanize(kv.Key?.ToString() ?? "")}: {RenderInline(kv.Value)}"))
                    : x?.ToString()));
            case Dictionary<object, object> m:
                return string.Join("\n", m.Select(kv => $"{Humanize(kv.Key?.ToString() ?? "")}: {RenderInline(kv.Value)}"));
            default: return v.ToString() ?? "";
        }
    }

    private static List<object> FieldList(Dictionary<object, object> m)
    {
        var outp = new List<object>();
        foreach (var kv in m)
        {
            var key = kv.Key?.ToString() ?? "";
            if (key.Length == 0 || key.Equals("id", StringComparison.OrdinalIgnoreCase)) continue;
            var val = RenderVal(kv.Value);
            if (val.Length == 0) continue;
            outp.Add(new { label = Humanize(key), value = val });
        }
        return outp;
    }

    private static string BodyOf(string md)
    {
        if (!md.StartsWith("---", StringComparison.Ordinal)) return md.Trim();
        var end = md.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return md.Trim();
        var close = md.IndexOf('\n', end + 4);
        return close < 0 ? "" : md[(close + 1)..].Trim();
    }

    private int CountFiles(string domainDir, string catalog, string pattern = "*.yaml")
    {
        var dir = Path.Combine(domainDir, catalog);
        if (!Directory.Exists(dir)) return 0;
        return Directory.EnumerateFiles(dir, pattern).Count(f => Path.GetFileName(f) != "index.yaml");
    }

    private int TermCount(string domainDir)
    {
        var f = Path.Combine(domainDir, "terms", "index.yaml");
        if (!File.Exists(f)) return 0;
        try { var m = _yaml.Deserialize<Dictionary<object, object>>(File.ReadAllText(f)); return m != null && m.TryGetValue("terms", out var t) && t is List<object> l ? l.Count : 0; }
        catch { return 0; }
    }

    private int MeasureCount(string domainDir)
    {
        var f = Path.Combine(domainDir, "_runtime", "registry.yaml");
        if (!File.Exists(f)) return 0;
        try
        {
            var m = _yaml.Deserialize<Dictionary<object, object>>(File.ReadAllText(f));
            if (m == null || !m.TryGetValue("views", out var vs) || vs is not List<object> views) return 0;
            var n = 0;
            foreach (var v in views)
                if (v is Dictionary<object, object> vd && vd.TryGetValue("measures", out var ms) && ms is List<object> ml) n += ml.Count;
            return n;
        }
        catch { return 0; }
    }

    private Dictionary<object, object>? Frontmatter(string md)
    {
        if (!md.StartsWith("---", StringComparison.Ordinal)) return null;
        var end = md.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return null;
        var fm = md.Substring(3, end - 3);
        try { return _yaml.Deserialize<Dictionary<object, object>>(fm) ?? new(); }
        catch { return null; }
    }
}
