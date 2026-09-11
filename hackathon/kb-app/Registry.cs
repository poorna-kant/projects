using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace KbApp;

public record MeasureRow(
    string Key, string View, string Entity, string Domain, string Column,
    string Qualifier, string Explanation, string Horizon, string Rule,
    string Aggregate, List<string> Dimensions, string Status);

public record ViewRow(
    string View, string Entity, string Domain, bool InScope, bool DqGated,
    string AccessStatus, List<string> ReportCoverage, string Sensitivity, string Status);

// Phase 2: the SEMANTIC LAYER (measures + business rules + DQ/scope/snapshot governance).
// Imports the agent's view-registry.json verbatim (each view kept as a blob so NO agent-consumed
// field is ever dropped), surfaces measures/rules for governance, and republishes an approved,
// versioned view-registry.json the agent reads for its NUMBERS. Shares the KB SQLite db.
public sealed class RegistryStore
{
    private readonly string _cs;
    private readonly string _snapshotRoot;
    private readonly string _publishedRegistry;
    private readonly string _registrySeed;
    private string _importedFrom = "unknown";

    private static readonly JsonDocumentOptions DocOpts = new()
    { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions J = new()
    { WriteIndented = true, PropertyNamingPolicy = null };

    public RegistryStore(string dbPath, string snapshotRoot, string publishedRegistry, string registrySeed)
    {
        _cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        _snapshotRoot = snapshotRoot;
        _publishedRegistry = publishedRegistry;
        _registrySeed = registrySeed;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(publishedRegistry))!);
        Init();
        ImportIfEmpty();
        if (!File.Exists(_publishedRegistry)) Publish();   // headless bootstrap so the agent has a registry to read
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }
    private void Exec(SqliteConnection c, string sql, Action<SqliteCommand>? bind = null)
    {
        using var cmd = c.CreateCommand(); cmd.CommandText = sql; bind?.Invoke(cmd); cmd.ExecuteNonQuery();
    }

    private void Init()
    {
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS reg_meta(
              id INTEGER PRIMARY KEY CHECK(id=1), version TEXT, source_json TEXT,
              loaddefaults_json TEXT, outofdomain_json TEXT);
            CREATE TABLE IF NOT EXISTS reg_views(
              view TEXT PRIMARY KEY, entity TEXT, domain TEXT, in_scope INTEGER, dq_gated INTEGER,
              access_status TEXT, report_coverage TEXT, sensitivity TEXT, status TEXT, blob TEXT);
            CREATE TABLE IF NOT EXISTS reg_measures(
              mkey TEXT PRIMARY KEY, view TEXT, mcolumn TEXT, qualifier TEXT, explanation TEXT,
              horizon TEXT, rule TEXT, aggregate TEXT, dims TEXT);
            CREATE TABLE IF NOT EXISTS reg_publishes(
              version INTEGER PRIMARY KEY, published_at TEXT, view_count INTEGER, file TEXT);
            """);
    }

    // ---- import (from the agent's own registry; no hardcode) ----------------
    private void ImportIfEmpty()
    {
        using (var c = Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM reg_views";
            if (Convert.ToInt32(cmd.ExecuteScalar()) > 0) { _importedFrom = "existing store"; return; }
        }
        if (!File.Exists(_registrySeed)) { _importedFrom = "no registry file found"; return; }

        using var doc = JsonDocument.Parse(File.ReadAllText(_registrySeed), DocOpts);
        var root = doc.RootElement;
        _importedFrom = root.TryGetProperty("version", out var vv) ? vv.GetString() ?? "registry" : "registry";

        using var c2 = Open();
        Exec(c2, "INSERT OR REPLACE INTO reg_meta(id,version,source_json,loaddefaults_json,outofdomain_json) VALUES(1,$v,$s,$l,$o)", cmd =>
        {
            cmd.Parameters.AddWithValue("$v", _importedFrom);
            cmd.Parameters.AddWithValue("$s", RawOrNull(root, "source"));
            cmd.Parameters.AddWithValue("$l", RawOrNull(root, "loadDefaults"));
            cmd.Parameters.AddWithValue("$o", RawOrNull(root, "outOfDomainTerms"));
        });

        if (!root.TryGetProperty("views", out var views)) return;
        foreach (var v in views.EnumerateArray())
        {
            var view = Str(v, "view");
            if (string.IsNullOrWhiteSpace(view)) continue;
            InsertView(view, Str(v, "entity"), Str(v, "domain", "supply"),
                Bool(v, "inScope"), Bool(v, "dqGated"), Str(v, "accessStatus", "granted"),
                RawOrNull(v, "reportCoverage"), Str(v, "sensitivity", "Synthetic Public Demo"),
                "Approved", v.GetRawText());

            if (v.TryGetProperty("measures", out var ms) && ms.ValueKind == JsonValueKind.Array)
                foreach (var m in ms.EnumerateArray())
                {
                    var key = Str(m, "key");
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    InsertMeasure(key, view, Str(m, "column"), Str(m, "qualifier"),
                        Str(m, "explanation"), Str(m, "horizon"), BuildRule(m),
                        Str(m, "aggregate", "sum"), RawOrNull(m, "analysisDimensions"));
                }
        }
    }

    private void InsertView(string view, string entity, string domain, bool inScope, bool dqGated,
        string access, string coverageJson, string sensitivity, string status, string blob)
    {
        using var c = Open();
        Exec(c, """
            INSERT OR REPLACE INTO reg_views(view,entity,domain,in_scope,dq_gated,access_status,
              report_coverage,sensitivity,status,blob)
            VALUES($view,$entity,$domain,$in,$dq,$acc,$cov,$sen,$st,$blob)
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("$view", view);
            cmd.Parameters.AddWithValue("$entity", entity);
            cmd.Parameters.AddWithValue("$domain", domain);
            cmd.Parameters.AddWithValue("$in", inScope ? 1 : 0);
            cmd.Parameters.AddWithValue("$dq", dqGated ? 1 : 0);
            cmd.Parameters.AddWithValue("$acc", access);
            cmd.Parameters.AddWithValue("$cov", coverageJson);
            cmd.Parameters.AddWithValue("$sen", sensitivity);
            cmd.Parameters.AddWithValue("$st", status);
            cmd.Parameters.AddWithValue("$blob", blob);
        });
    }

    private void InsertMeasure(string key, string view, string col, string qual, string expl,
        string horizon, string rule, string agg, string dimsJson)
    {
        using var c = Open();
        Exec(c, """
            INSERT OR REPLACE INTO reg_measures(mkey,view,mcolumn,qualifier,explanation,horizon,rule,aggregate,dims)
            VALUES($k,$v,$c,$q,$e,$h,$r,$a,$d)
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", view);
            cmd.Parameters.AddWithValue("$c", col);
            cmd.Parameters.AddWithValue("$q", qual);
            cmd.Parameters.AddWithValue("$e", expl);
            cmd.Parameters.AddWithValue("$h", horizon);
            cmd.Parameters.AddWithValue("$r", rule);
            cmd.Parameters.AddWithValue("$a", agg);
            cmd.Parameters.AddWithValue("$d", dimsJson);
        });
    }

    // ---- reads (UI) ---------------------------------------------------------
    public List<MeasureRow> AllMeasures()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT m.mkey,m.view,v.entity,v.domain,m.mcolumn,m.qualifier,m.explanation,m.horizon,
                   m.rule,m.aggregate,m.dims,v.status
            FROM reg_measures m JOIN reg_views v ON v.view=m.view ORDER BY v.domain, m.mkey
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<MeasureRow>();
        while (r.Read())
            list.Add(new MeasureRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
                r.GetString(9), JsonToList(r.GetString(10)), r.GetString(11)));
        return list;
    }

    public List<ViewRow> AllViews()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT view,entity,domain,in_scope,dq_gated,access_status,report_coverage,sensitivity,status FROM reg_views ORDER BY domain, view";
        using var r = cmd.ExecuteReader();
        var list = new List<ViewRow>();
        while (r.Read())
            list.Add(new ViewRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3) == 1,
                r.GetInt32(4) == 1, r.GetString(5), JsonToList(r.GetString(6)), r.GetString(7), r.GetString(8)));
        return list;
    }

    // ---- edit + approve (governance) ----------------------------------------
    public bool EditMeasure(string key, string? qualifier, string? explanation, string? rule)
    {
        using var c = Open();
        // find the view, update the measure, and put that view back into review
        string? view = null;
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT view FROM reg_measures WHERE mkey=$k";
            q.Parameters.AddWithValue("$k", key);
            view = q.ExecuteScalar() as string;
        }
        if (view is null) return false;
        Exec(c, "UPDATE reg_measures SET qualifier=COALESCE($q,qualifier), explanation=COALESCE($e,explanation), rule=COALESCE($r,rule) WHERE mkey=$k", cmd =>
        {
            cmd.Parameters.AddWithValue("$q", (object?)qualifier ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$e", (object?)explanation ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$r", (object?)rule ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$k", key);
        });
        Exec(c, "UPDATE reg_views SET status='In review' WHERE view=$v", cmd => cmd.Parameters.AddWithValue("$v", view));
        return true;
    }

    // add a brand-new measure under an existing view, and put that view back into review
    public bool AddMeasure(string key, string view, string? column, string? qualifier,
        string? explanation, string? rule, string? aggregate)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(view)) return false;
        using var c = Open();
        // the view must already be governed here, and the key must be new
        bool viewExists, keyTaken;
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT (SELECT COUNT(*) FROM reg_views WHERE view=$v), (SELECT COUNT(*) FROM reg_measures WHERE mkey=$k)";
            q.Parameters.AddWithValue("$v", view);
            q.Parameters.AddWithValue("$k", key);
            using var r = q.ExecuteReader();
            r.Read();
            viewExists = r.GetInt32(0) > 0;
            keyTaken = r.GetInt32(1) > 0;
        }
        if (!viewExists || keyTaken) return false;
        InsertMeasure(key, view, column ?? "", qualifier ?? "", explanation ?? "", "",
            string.IsNullOrWhiteSpace(rule) ? "sum of the column, no extra filter" : rule!,
            string.IsNullOrWhiteSpace(aggregate) ? "sum" : aggregate!, "null");
        Exec(c, "UPDATE reg_views SET status='In review' WHERE view=$v", cmd => cmd.Parameters.AddWithValue("$v", view));
        return true;
    }

    public bool ApproveView(string view)
    {
        using var c = Open();
        Exec(c, "UPDATE reg_views SET status='Approved' WHERE view=$v", cmd => cmd.Parameters.AddWithValue("$v", view));
        return true;
    }

    // ---- publish: rebuild an approved, versioned view-registry.json ----------
    public object Publish()
    {
        var version = NextVersion();
        var publishedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        string sourceJson, loadJson, oodJson;
        using (var c = Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT source_json,loaddefaults_json,outofdomain_json FROM reg_meta WHERE id=1";
            using var r = cmd.ExecuteReader();
            r.Read();
            sourceJson = r.IsDBNull(0) ? "null" : r.GetString(0);
            loadJson = r.IsDBNull(1) ? "null" : r.GetString(1);
            oodJson = r.IsDBNull(2) ? "[]" : r.GetString(2);
        }

        // overlay any edited measure qualifier/explanation onto each approved view blob
        var edits = AllMeasures().ToDictionary(m => m.Key, m => m);
        var viewsArr = new JsonArray();
        int approved = 0;
        using (var c = Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT blob FROM reg_views WHERE status='Approved' ORDER BY view";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var node = JsonNode.Parse(r.GetString(0))!;
                var viewName = node["view"]?.GetValue<string>();
                var ms = node["measures"] as JsonArray;
                if (ms is null) { ms = new JsonArray(); node["measures"] = ms; }

                var present = new HashSet<string>(StringComparer.Ordinal);
                foreach (var mn in ms)
                {
                    var k = mn?["key"]?.GetValue<string>();
                    if (k is null) continue;
                    present.Add(k);
                    if (edits.TryGetValue(k, out var em))
                    {
                        mn!["qualifier"] = em.Qualifier;
                        mn["explanation"] = em.Explanation;
                        mn["businessRule"] = em.Rule;
                    }
                }

                // inject measures the steward added here that aren't in the original blob
                if (viewName is not null)
                    foreach (var em in edits.Values)
                        if (em.View == viewName && !present.Contains(em.Key))
                            ms.Add(new JsonObject
                            {
                                ["key"] = em.Key,
                                ["column"] = em.Column,
                                ["qualifier"] = em.Qualifier,
                                ["explanation"] = em.Explanation,
                                ["aggregate"] = em.Aggregate,
                                ["businessRule"] = em.Rule
                            });

                viewsArr.Add(node);
                approved++;
            }
        }

        var outDoc = new JsonObject
        {
            ["version"] = $"registry-app-v{version}",
            ["note"] = "Published by the public Knowledge Base demo. Approved measures and business rules only.",
            ["source"] = JsonNode.Parse(sourceJson),
            ["loadDefaults"] = JsonNode.Parse(loadJson),
            ["outOfDomainTerms"] = JsonNode.Parse(oodJson),
            ["views"] = viewsArr
        };
        var json = outDoc.ToJsonString(J);
        File.WriteAllText(_publishedRegistry, json);
        File.WriteAllText(Path.Combine(_snapshotRoot, $"view-registry-v{version}.json"), json);

        using (var c = Open())
            Exec(c, "INSERT INTO reg_publishes(version,published_at,view_count,file) VALUES($v,$p,$n,$f)", cmd =>
            {
                cmd.Parameters.AddWithValue("$v", version);
                cmd.Parameters.AddWithValue("$p", publishedAt);
                cmd.Parameters.AddWithValue("$n", approved);
                cmd.Parameters.AddWithValue("$f", Path.GetFileName(_publishedRegistry));
            });
        return new { version = $"registry-app-v{version}", publishedAt, viewCount = approved, file = Path.GetFileName(_publishedRegistry) };
    }

    public object CurrentInfo()
    {
        int? v = null; string? at = null; int? n = null;
        using (var c = Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT version,published_at,view_count FROM reg_publishes ORDER BY version DESC LIMIT 1";
            using var r = cmd.ExecuteReader();
            if (r.Read()) { v = r.GetInt32(0); at = r.GetString(1); n = r.GetInt32(2); }
        }
        return v is null
            ? new { published = false, importedFrom = _importedFrom, publishedRegistry = _publishedRegistry }
            : new { published = true, version = $"registry-app-v{v}", publishedAt = at, viewCount = n, importedFrom = _importedFrom, publishedRegistry = _publishedRegistry };
    }

    private int NextVersion()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version),0)+1 FROM reg_publishes";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ---- helpers ------------------------------------------------------------
    private static string BuildRule(JsonElement m)
    {
        var declared = Str(m, "businessRule");
        if (!string.IsNullOrWhiteSpace(declared)) return declared;
        var parts = new List<string>();
        var rfc = Str(m, "requiredFilterColumn");
        if (rfc.Length > 0)
        {
            var val = Str(m, "requiredFilterValue");
            if (val.Length == 0 && m.TryGetProperty("requiredFilterValues", out var rv) && rv.ValueKind == JsonValueKind.Array)
                val = string.Join(", ", rv.EnumerateArray().Select(x => x.GetString()));
            parts.Add($"must filter {rfc} = {val}");
        }
        var sfc = Str(m, "secondaryFilterColumn");
        if (sfc.Length > 0 && m.TryGetProperty("secondaryFilterValues", out var sv) && sv.ValueKind == JsonValueKind.Array)
            parts.Add($"and {sfc} in ({string.Join(", ", sv.EnumerateArray().Select(x => x.GetString()))})");
        var agg = Str(m, "aggregate");
        if (agg.Length > 0 && agg != "sum") parts.Add($"aggregate = {agg}");
        var slip = Str(m, "slipFromColumn");
        var dc = Str(m, "dateColumn");
        if (slip.Length > 0 && dc.Length > 0) parts.Add($"late when {dc} > {slip}");
        else if (dc.Length > 0) parts.Add($"phased by {dc}");
        return parts.Count > 0 ? string.Join("; ", parts) : "sum of the column, no extra filter";
    }

    private static string Str(JsonElement e, string prop, string fallback = "") =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;
    private static bool Bool(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;
    private static string RawOrNull(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) ? v.GetRawText() : "null";
    private static List<string> JsonToList(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null") return new();
        try { using var d = JsonDocument.Parse(json); return d.RootElement.ValueKind == JsonValueKind.Array
            ? d.RootElement.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : new(); }
        catch { return new(); }
    }
}
