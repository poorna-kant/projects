using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KbApp;

// ---- domain records ---------------------------------------------------------
// Area carries the agent's concept Category; Aliases is pipe-delimited; Related is the agent's seeAlso.
public record Term(
    string Id, string Name, string Area, string Owner, string Oi, string Status,
    string Def, string Aliases, string Sensitivity, string Hierarchy,
    string Sources, string Reports, string Why, string Derived,
    List<string> Related, int Version, string LastChanged);

public record Source(
    string Id, string Name, string Type, string Owner, bool Validated,
    string Status, string? ReplacedBy, string Note, string? Url = null);

public record Trust(string Score, string Label, string Checks);

public record PublishInfo(int Version, string PublishedAt, int TermCount, string File);

// ---- store ------------------------------------------------------------------
// The Knowledge Base is the SOURCE OF TRUTH. Nothing is hardcoded: the store is seeded by IMPORTING
// the agent's own governed files (concepts.json + reports.json). On publish, it emits the agent's exact
// glossary schema so the agent answers definitional questions from what the business approved here.
// Local-first demonstration; run one instance with persistent storage.
public sealed class KbStore
{
    private readonly string _cs;
    private readonly string _snapshotRoot;
    private readonly string _publishedConcepts;   // fixed file the agent reads (co-exist swap point)
    private readonly string _conceptsSeed;        // agent's concepts.json (import source)
    private readonly string _reportsSeed;         // agent's reports.json (import source)
    private string _importedFrom = "unknown";

    private static readonly JsonSerializerOptions J = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions R = new() { PropertyNameCaseInsensitive = true };

    public KbStore(string dbPath, string snapshotRoot, string publishedConcepts,
                   string conceptsSeed, string reportsSeed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        Directory.CreateDirectory(snapshotRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(publishedConcepts))!);
        _cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        _snapshotRoot = snapshotRoot;
        _publishedConcepts = publishedConcepts;
        _conceptsSeed = conceptsSeed;
        _reportsSeed = reportsSeed;
        Init();
        ImportIfEmpty();
        BackfillSourceUrls();   // ensure report URLs are populated even for a pre-existing store
        if (!File.Exists(_publishedConcepts)) Publish();   // headless bootstrap so the agent has a version to read
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }

    private void Exec(SqliteConnection c, string sql, Action<SqliteCommand>? bind = null)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd);
        cmd.ExecuteNonQuery();
    }

    private void Init()
    {
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS terms(
              id TEXT PRIMARY KEY, name TEXT, area TEXT, owner TEXT, oi TEXT, status TEXT,
              def TEXT, aliases TEXT, sensitivity TEXT, hierarchy TEXT, sources TEXT,
              reports TEXT, why TEXT, derived TEXT, related TEXT, version INTEGER, last_changed TEXT);
            CREATE TABLE IF NOT EXISTS sources(
              id TEXT PRIMARY KEY, name TEXT, type TEXT, owner TEXT, validated INTEGER,
              status TEXT, replaced_by TEXT, note TEXT, url TEXT);
            CREATE TABLE IF NOT EXISTS publishes(
              version INTEGER PRIMARY KEY, published_at TEXT, term_count INTEGER, file TEXT);
            """);
        try { Exec(c, "ALTER TABLE sources ADD COLUMN url TEXT"); } catch { /* column already present */ }
    }

    // Approval is a workflow state, not a measured data-quality score.
    public static Trust TrustFor(string status) => status switch
    {
        "Approved"     => new("", "Approved", "Approved demo knowledge; data quality is not independently verified"),
        "In review"    => new("", "In review", "Awaiting review"),
        "Needs update" => new("", "Re-check needed", "Approval requires review"),
        _              => new("", "Draft", "Not approved")
    };

    // ---- reads --------------------------------------------------------------
    public List<Term> AllTerms()
    {
        if (Directory.Exists(_conceptsSeed)) return ApprovedTreeTerms();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM terms ORDER BY name";
        using var r = cmd.ExecuteReader();
        var list = new List<Term>();
        while (r.Read()) list.Add(ReadTerm(r));
        return list;
    }

    public Term? GetTerm(string id)
    {
        if (Directory.Exists(_conceptsSeed))
            return ApprovedTreeTerms().FirstOrDefault(term =>
                term.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || Slug(term.Name) == id);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM terms WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadTerm(r) : null;
    }

    public List<Source> AllSources()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,name,type,owner,validated,status,replaced_by,note,url FROM sources ORDER BY status, name";
        using var r = cmd.ExecuteReader();
        var list = new List<Source>();
        while (r.Read())
            list.Add(new Source(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetInt32(4) == 1, r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7),
                r.IsDBNull(8) ? null : r.GetString(8)));
        return list;
    }

    private static Term ReadTerm(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9),
        r.GetString(10), r.GetString(11), r.GetString(12), r.GetString(13),
        SplitList(r.GetString(14)), r.GetInt32(15), r.GetString(16));

    private static List<string> SplitList(string s) =>
        string.IsNullOrWhiteSpace(s) ? new() : s.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();

    public static string[] SplitAliases(string a) =>
        string.IsNullOrWhiteSpace(a) ? Array.Empty<string>() : a.Split('|', StringSplitOptions.RemoveEmptyEntries);

    // ---- writes (authoring plane) ------------------------------------------
    public Term Propose(string name, string area, string owner, string def, string why, string derived)
    {
        var id = Slug(name);
        using var c = Open();
        Exec(c, """
            INSERT OR REPLACE INTO terms(id,name,area,owner,oi,status,def,aliases,sensitivity,
              hierarchy,sources,reports,why,derived,related,version,last_changed)
            VALUES($id,$name,$area,$owner,$oi,'Draft',$def,'', 'Internal',$area,'','',$why,$derived,'',1,$lc)
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$area", string.IsNullOrWhiteSpace(area) ? "General" : area);
            cmd.Parameters.AddWithValue("$owner", string.IsNullOrWhiteSpace(owner) ? "Unassigned" : owner);
            cmd.Parameters.AddWithValue("$oi", Initials(owner));
            cmd.Parameters.AddWithValue("$def", def);
            cmd.Parameters.AddWithValue("$why", why ?? "");
            cmd.Parameters.AddWithValue("$derived", derived ?? "");
            cmd.Parameters.AddWithValue("$lc", $"{(string.IsNullOrWhiteSpace(owner) ? "someone" : owner)} \u00b7 just now");
        });
        return GetTerm(id)!;
    }

    public Term? SetStatus(string id, string status)
    {
        var t = GetTerm(id);
        if (t is null) return null;
        using var c = Open();
        Exec(c, "UPDATE terms SET status=$s, last_changed=$lc WHERE id=$id", cmd =>
        {
            cmd.Parameters.AddWithValue("$s", status);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$lc", $"{t.Owner} \u00b7 just now");
        });
        return GetTerm(id);
    }

    public Term? UpdateDefinition(string id, string def, string why, string derived)
    {
        var t = GetTerm(id);
        if (t is null) return null;
        using var c = Open();
        Exec(c, "UPDATE terms SET def=$d, why=$w, derived=$dv, status='In review', last_changed=$lc WHERE id=$id", cmd =>
        {
            cmd.Parameters.AddWithValue("$d", def ?? t.Def);
            cmd.Parameters.AddWithValue("$w", why ?? t.Why);
            cmd.Parameters.AddWithValue("$dv", derived ?? t.Derived);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$lc", $"{t.Owner} \u00b7 just now");
        });
        return GetTerm(id);
    }

    public bool DeleteTerm(string id)
    {
        if (GetTerm(id) is null) return false;
        using var c = Open();
        Exec(c, "DELETE FROM terms WHERE id=$id", cmd => cmd.Parameters.AddWithValue("$id", id));
        return true;
    }

    // ---- publish: emit the AGENT'S EXACT glossary schema (approved only) -----
    public PublishInfo Publish()
    {
        var version = NextVersion();
        var approved = AllTerms().Where(t => t.Status == "Approved").ToList();
        var treeJson = Directory.Exists(_conceptsSeed) ? ApprovedTreeSnapshot(version) : null;
        var publishedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var stamp = $"concepts-app-v{version}";

        // 1) the agent contract: concepts.json shape { version, note, concepts:[{term,aliases,category,definition,seeAlso}] }
        var conceptsDoc = new
        {
            version = stamp,
            note = "Published by the public Knowledge Base demo. Approved business definitions only. " +
                   "This is the governed source the agent answers definitional questions from.",
            concepts = approved.Select(t => new
            {
                term = t.Name,
                aliases = SplitAliases(t.Aliases),
                category = t.Area,
                definition = t.Def,
                seeAlso = t.Related
            })
        };
        var conceptsJson = JsonSerializer.Serialize(conceptsDoc, J);
        File.WriteAllText(Path.Combine(_snapshotRoot, $"kb-concepts-v{version}.json"), conceptsJson);

        // 2) rich internal snapshot (for the app's own inspection / demo endpoint)
        var snapshot = new
        {
            version, stamp, publishedAt, termCount = approved.Count,
            terms = approved.Select(t => new
            {
                t.Name, t.Area, t.Owner, t.Status, t.Def, t.Why, t.Derived,
                aliases = SplitAliases(t.Aliases), t.Related, trust = TrustFor(t.Status)
            }),
            sources = AllSources()
        };
        File.WriteAllText(Path.Combine(_snapshotRoot, $"kb-snapshot-v{version}.json"),
                          JsonSerializer.Serialize(snapshot, J));
        if (treeJson is not null)
        {
            File.WriteAllText(Path.Combine(_snapshotRoot, $"kb-tree-v{version}.json"), treeJson);
            KnowledgePath.WriteText(Path.Combine(Path.GetDirectoryName(_publishedConcepts)!, "tree.json"), treeJson);
        }
        KnowledgePath.WriteText(_publishedConcepts, conceptsJson);

        using var c = Open();
        Exec(c, "INSERT INTO publishes(version,published_at,term_count,file) VALUES($v,$p,$n,$f)", cmd =>
        {
            cmd.Parameters.AddWithValue("$v", version);
            cmd.Parameters.AddWithValue("$p", publishedAt);
            cmd.Parameters.AddWithValue("$n", approved.Count);
            cmd.Parameters.AddWithValue("$f", Path.GetFileName(_publishedConcepts));
        });
        return new PublishInfo(version, publishedAt, approved.Count, Path.GetFileName(_publishedConcepts));
    }

    private List<Term> ApprovedTreeTerms()
    {
        var reader = new TreeStore(_conceptsSeed);
        var terms = new List<Term>();
        foreach (var directory in Directory.EnumerateDirectories(_conceptsSeed).OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(directory, "overview.yaml"))) continue;
            var domain = Path.GetFileName(directory);
            var glossary = reader.Read(domain);
            var errors = TreeStore.Validate(glossary);
            if (errors.Count > 0) throw new InvalidDataException(string.Join(" ", errors));
            foreach (var term in glossary.Terms)
            {
                var category = term.Category ?? "General";
                terms.Add(new Term(term.Id ?? $"{domain}/terms/{Slug(term.Term!)}", term.Term!, category,
                    "Demo steward", "DS", "Approved", term.Definition!, string.Join('|', term.Aliases ?? []),
                    "Synthetic Public Demo", domain, "", "", "", "", term.SeeAlso ?? [], 1, "Approved authoring tree"));
            }
        }
        return terms.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    private string ApprovedTreeSnapshot(int version)
    {
        var records = new List<object>();
        var yaml = new YamlDotNet.Serialization.DeserializerBuilder().Build();
        foreach (var domain in Directory.EnumerateDirectories(_conceptsSeed).OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(domain, "overview.yaml"))) continue;
            foreach (var catalog in new[] { "objects", "concepts", "policies", "systems", "metrics" })
            {
                var directory = KnowledgePath.Catalog(_conceptsSeed, Path.GetFileName(domain), catalog);
                if (!Directory.Exists(directory)) continue;
                foreach (var file in Directory.EnumerateFiles(directory).OrderBy(x => x, StringComparer.Ordinal))
                {
                    if (Path.GetFileName(file) == "index.yaml") continue;
                    var content = File.ReadAllText(file);
                    if (Path.GetExtension(file) == ".yaml") yaml.Deserialize<object>(content);
                    else if (Path.GetExtension(file) != ".md") continue;
                    records.Add(new { path = Path.GetRelativePath(_conceptsSeed, file).Replace('\\', '/'), content });
                }
            }
        }
        return JsonSerializer.Serialize(new { version, syntheticData = true, records }, J);
    }

    public List<PublishInfo> Publishes()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT version,published_at,term_count,file FROM publishes ORDER BY version DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<PublishInfo>();
        while (r.Read())
            list.Add(new PublishInfo(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetString(3)));
        return list;
    }

    public object CurrentInfo()
    {
        var p = Publishes().FirstOrDefault();
        return p is null
            ? new { published = false, importedFrom = _importedFrom, publishedConcepts = _publishedConcepts }
            : new { published = true, p.Version, p.PublishedAt, p.TermCount, importedFrom = _importedFrom, publishedConcepts = _publishedConcepts };
    }

    private int NextVersion()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version),0)+1 FROM publishes";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ---- import (seed the KB from the agent's own governed files) ------------
    private void ImportIfEmpty()
    {
        using (var c = Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM terms";
            if (Convert.ToInt32(cmd.ExecuteScalar()) > 0) { _importedFrom = "existing store"; return; }
        }
        ImportConcepts();
        ImportReports();
    }

    private void ImportConcepts()
    {
        // V2 multi-domain: a directory is the KB root; seed terms from EVERY domain (a folder with overview.yaml).
        // Drop in a new domain folder tomorrow and its terms are picked up here with no code change.
        if (Directory.Exists(_conceptsSeed))
        {
            var domains = Directory.EnumerateDirectories(_conceptsSeed)
                .Where(d => !Path.GetFileName(d).StartsWith("_", StringComparison.Ordinal))
                .Where(d => !string.Equals(Path.GetFileName(d), "shared", StringComparison.OrdinalIgnoreCase))
                .Where(d => File.Exists(Path.Combine(d, "overview.yaml")))
                .OrderBy(d => d, StringComparer.Ordinal).ToList();
            var seeded = new List<string>();
            foreach (var d in domains)
            {
                var tf = Path.Combine(d, "terms", "index.yaml");
                if (File.Exists(tf)) { ImportConceptsYamlFile(tf); seeded.Add(Path.GetFileName(d)); }
            }
            _importedFrom = seeded.Count > 0 ? "tree domains: " + string.Join(", ", seeded) : "no domains found";
            return;
        }
        if (!File.Exists(_conceptsSeed)) { _importedFrom = "no concepts file found"; return; }
        // A single YAML file is one domain's glossary; JSON is the legacy V1 flat concepts.
        if (_conceptsSeed.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)) { ImportConceptsYamlFile(_conceptsSeed); return; }
        using var doc = JsonDocument.Parse(File.ReadAllText(_conceptsSeed));
        var root = doc.RootElement;
        _importedFrom = root.TryGetProperty("version", out var v) ? v.GetString() ?? "concepts" : "concepts";
        if (!root.TryGetProperty("concepts", out var arr)) return;

        foreach (var cpt in arr.EnumerateArray())
        {
            var term = Str(cpt, "term");
            var def = Str(cpt, "definition");
            if (string.IsNullOrWhiteSpace(term) || string.IsNullOrWhiteSpace(def)) continue;
            var aliases = Arr(cpt, "aliases");
            var category = Str(cpt, "category", "General");
            var seeAlso = Arr(cpt, "seeAlso");

            InsertTerm(new Term(
                Slug(term), term, category, "Imported", "IM", "Approved",
                def, string.Join('|', aliases), "Internal", category,
                "", "", "", "", seeAlso.ToList(), 1, $"imported from {_importedFrom}"));
        }
    }

    // V2: seed the glossary from a domain's terms/index.yaml (the plan Glossary or any future domain).
    private void ImportConceptsYamlFile(string path)
    {
        var de = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var dto = de.Deserialize<YamlGlossary>(File.ReadAllText(path));
        _importedFrom = dto?.Id ?? "plan/terms";
        if (dto?.Terms is null) return;

        foreach (var t in dto.Terms)
        {
            if (string.IsNullOrWhiteSpace(t.Term) || string.IsNullOrWhiteSpace(t.Definition)) continue;
            var aliases = t.Aliases ?? new List<string>();
            var category = string.IsNullOrWhiteSpace(t.Category) ? "General" : t.Category!;
            var seeAlso = t.SeeAlso ?? new List<string>();

            InsertTerm(new Term(
                Slug(t.Term!), t.Term!, category, "Imported", "IM", "Approved",
                t.Definition!, string.Join('|', aliases), "Internal", category,
                "", "", "", "", seeAlso, 1, $"imported from {_importedFrom}"));
        }
    }

    private sealed class YamlGlossary
    {
        public string? Id { get; set; }
        public List<YamlTerm>? Terms { get; set; }
    }

    private sealed class YamlTerm
    {
        public string? Term { get; set; }
        public string? Category { get; set; }
        public string? Definition { get; set; }
        public List<string>? Aliases { get; set; }
        public List<string>? SeeAlso { get; set; }
    }

    private void ImportReports()
    {
        if (!File.Exists(_reportsSeed)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(_reportsSeed));
        if (!doc.RootElement.TryGetProperty("reports", out var arr)) return;
        foreach (var rp in arr.EnumerateArray())
        {
            var name = Str(rp, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var active = rp.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True;
            var qs = Arr(rp, "coverageQuestions");
            var url = Str(rp, "url");
            InsertSource(new Source(
                Slug(name), name, "Report", "Imported", active,
                active ? "Active" : "Retired", null,
                qs.Length > 0 ? "Covers: " + string.Join(", ", qs.Take(3)) : "",
                string.IsNullOrWhiteSpace(url) ? null : url));
        }
    }

    // Fill missing synthetic report URLs from the checked-in demo catalog.
    private void BackfillSourceUrls()
    {
        if (!File.Exists(_reportsSeed)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(_reportsSeed));
        if (!doc.RootElement.TryGetProperty("reports", out var arr)) return;
        using var c = Open();
        foreach (var rp in arr.EnumerateArray())
        {
            var name = Str(rp, "name");
            var url = Str(rp, "url");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
            Exec(c, "UPDATE sources SET url=$u WHERE name=$n AND (url IS NULL OR url='')", cmd =>
            {
                cmd.Parameters.AddWithValue("$u", url);
                cmd.Parameters.AddWithValue("$n", name);
            });
        }
    }

    private void InsertTerm(Term t)
    {
        using var c = Open();
        Exec(c, """
            INSERT OR REPLACE INTO terms(id,name,area,owner,oi,status,def,aliases,sensitivity,hierarchy,
              sources,reports,why,derived,related,version,last_changed)
            VALUES($id,$name,$area,$owner,$oi,$status,$def,$aliases,$sensitivity,$hierarchy,
              $sources,$reports,$why,$derived,$related,$version,$lc)
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("$id", t.Id);
            cmd.Parameters.AddWithValue("$name", t.Name);
            cmd.Parameters.AddWithValue("$area", t.Area);
            cmd.Parameters.AddWithValue("$owner", t.Owner);
            cmd.Parameters.AddWithValue("$oi", t.Oi);
            cmd.Parameters.AddWithValue("$status", t.Status);
            cmd.Parameters.AddWithValue("$def", t.Def);
            cmd.Parameters.AddWithValue("$aliases", t.Aliases);
            cmd.Parameters.AddWithValue("$sensitivity", t.Sensitivity);
            cmd.Parameters.AddWithValue("$hierarchy", t.Hierarchy);
            cmd.Parameters.AddWithValue("$sources", t.Sources);
            cmd.Parameters.AddWithValue("$reports", t.Reports);
            cmd.Parameters.AddWithValue("$why", t.Why);
            cmd.Parameters.AddWithValue("$derived", t.Derived);
            cmd.Parameters.AddWithValue("$related", string.Join('|', t.Related));
            cmd.Parameters.AddWithValue("$version", t.Version);
            cmd.Parameters.AddWithValue("$lc", t.LastChanged);
        });
    }

    private void InsertSource(Source s)
    {
        using var c = Open();
        Exec(c, """
            INSERT OR REPLACE INTO sources(id,name,type,owner,validated,status,replaced_by,note,url)
            VALUES($id,$name,$type,$owner,$validated,$status,$rb,$note,$url)
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("$id", s.Id);
            cmd.Parameters.AddWithValue("$name", s.Name);
            cmd.Parameters.AddWithValue("$type", s.Type);
            cmd.Parameters.AddWithValue("$owner", s.Owner);
            cmd.Parameters.AddWithValue("$validated", s.Validated ? 1 : 0);
            cmd.Parameters.AddWithValue("$status", s.Status);
            cmd.Parameters.AddWithValue("$rb", (object?)s.ReplacedBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$note", s.Note);
            cmd.Parameters.AddWithValue("$url", (object?)s.Url ?? DBNull.Value);
        });
    }

    // ---- demo: answer from the SAME published file the agent reads ----------
    public object AnswerFromPublished(string q)
    {
        if (string.IsNullOrWhiteSpace(q))
            return new { answered = false, message = "Enter a synthetic glossary term or alias." };
        if (!File.Exists(_publishedConcepts))
            return new { answered = false, message = "Nothing published yet \u2014 the app has not released a glossary version." };
        using var doc = JsonDocument.Parse(File.ReadAllText(_publishedConcepts));
        var root = doc.RootElement;
        var stamp = root.TryGetProperty("version", out var v) ? v.GetString() : "unknown";
        if (root.TryGetProperty("concepts", out var arr))
        {
            var qn = q.Trim().ToLowerInvariant();
            foreach (var cpt in arr.EnumerateArray())
            {
                var term = Str(cpt, "term");
                var aliases = Arr(cpt, "aliases").Select(a => a.ToLowerInvariant()).ToArray();
                if (term.ToLowerInvariant() == qn || aliases.Contains(qn) ||
                    term.ToLowerInvariant().Contains(qn))
                    return new
                    {
                        answered = true, term,
                        definition = Str(cpt, "definition"),
                        category = Str(cpt, "category"),
                        seeAlso = Arr(cpt, "seeAlso"),
                        asOf = stamp
                    };
            }
        }
        return new { answered = false, asOf = stamp, message = $"No published concept matches '{q}'." };
    }

    // ---- helpers ------------------------------------------------------------
    private static string Str(JsonElement e, string prop, string fallback = "") =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;

    private static string[] Arr(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : Array.Empty<string>();

    private static string Slug(string s) =>
        new string(s.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray())
            .Trim('-').Replace("---", "-").Replace("--", "-");

    private static string Initials(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner)) return "?";
        var parts = owner.Replace(".", "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            ? $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}"
            : owner[..Math.Min(2, owner.Length)].ToUpperInvariant();
    }
}
