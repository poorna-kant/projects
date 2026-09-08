using Microsoft.Data.Sqlite;

namespace KbApp;

public record ArtifactRow(string Name, string Kind, int Version, string Status, string PublishedFile, string UpdatedAt);

// Governs the remaining agent knowledge files verbatim (schema-linking glossary, value-grounding index,
// narrative markdown KB). Imported from the agent's own files, versioned, and republished byte-for-byte so
// the agent reads ALL of its knowledge from the KB — everything except the data (which stays on the replica).
public sealed class ArtifactStore
{
    private readonly string _cs;
    private readonly string _snapshotRoot;

    public ArtifactStore(string dbPath, string snapshotRoot)
    {
        _cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        _snapshotRoot = snapshotRoot;
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS artifacts(
              name TEXT PRIMARY KEY, kind TEXT, published_path TEXT, content TEXT,
              version INTEGER, status TEXT, updated TEXT);
            """);
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }
    private void Exec(SqliteConnection c, string sql, Action<SqliteCommand>? bind = null)
    { using var cmd = c.CreateCommand(); cmd.CommandText = sql; bind?.Invoke(cmd); cmd.ExecuteNonQuery(); }

    // Register on startup: import verbatim from the agent's file (once) and bootstrap-publish so the
    // published file exists for the agent to read.
    public void Register(string name, string kind, string seedPath, string publishedPath)
    {
        string? existing = null;
        using (var c = Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT content FROM artifacts WHERE name=$n";
            cmd.Parameters.AddWithValue("$n", name);
            existing = cmd.ExecuteScalar() as string;
        }

        if (existing is null)
        {
            if (!File.Exists(seedPath)) return; // nothing to import
            var content = File.ReadAllText(seedPath);
            using var c = Open();
            Exec(c, """
                INSERT OR REPLACE INTO artifacts(name,kind,published_path,content,version,status,updated)
                VALUES($n,$k,$p,$c,1,'Approved',$u)
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$k", kind);
                cmd.Parameters.AddWithValue("$p", publishedPath);
                cmd.Parameters.AddWithValue("$c", content);
                cmd.Parameters.AddWithValue("$u", $"imported from {Path.GetFileName(seedPath)}");
            });
            WritePublished(name, publishedPath, content, 1);
        }
        else if (!File.Exists(publishedPath))
        {
            WritePublished(name, publishedPath, existing, CurrentVersion(name));
        }
    }

    public object Publish(string name)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT kind,published_path,content,version FROM artifacts WHERE name=$n";
        q.Parameters.AddWithValue("$n", name);
        using var r = q.ExecuteReader();
        if (!r.Read()) return new { published = false, message = $"Unknown artifact '{name}'." };
        var publishedPath = r.GetString(1);
        var content = r.GetString(2);
        var version = r.GetInt32(3) + 1;
        r.Close();
        Exec(c, "UPDATE artifacts SET version=$v, status='Approved', updated=$u WHERE name=$n", cmd =>
        {
            cmd.Parameters.AddWithValue("$v", version);
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            cmd.Parameters.AddWithValue("$n", name);
        });
        WritePublished(name, publishedPath, content, version);
        return new { published = true, name, version, file = Path.GetFileName(publishedPath) };
    }

    public List<object> PublishAll() => All().Select(a => Publish(a.Name)).ToList();

    public List<ArtifactRow> All()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name,kind,version,status,published_path,updated FROM artifacts ORDER BY name";
        using var r = cmd.ExecuteReader();
        var list = new List<ArtifactRow>();
        while (r.Read())
            list.Add(new ArtifactRow(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3),
                Path.GetFileName(r.GetString(4)), r.GetString(5)));
        return list;
    }

    private int CurrentVersion(string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT version FROM artifacts WHERE name=$n";
        cmd.Parameters.AddWithValue("$n", name);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 1);
    }

    public (string Kind, string Content, int Version, string Status)? Get(string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT kind,content,version,status FROM artifacts WHERE name=$n";
        cmd.Parameters.AddWithValue("$n", name);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3)) : null;
    }

    public bool SetContent(string name, string content)
    {
        if (Get(name) is null) return false;
        using var c = Open();
        Exec(c, "UPDATE artifacts SET content=$c, status='In review', updated=$u WHERE name=$n", cmd =>
        {
            cmd.Parameters.AddWithValue("$c", content);
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            cmd.Parameters.AddWithValue("$n", name);
        });
        return true;
    }

    // Add a brand-new governed file (Draft, not yet published) authored in the app.
    public bool Add(string name, string kind, string publishedPath, string starterContent)
    {
        if (Get(name) is not null) return false;
        using var c = Open();
        Exec(c, """
            INSERT INTO artifacts(name,kind,published_path,content,version,status,updated)
            VALUES($n,$k,$p,$c,0,'Draft',$u)
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$p", publishedPath);
            cmd.Parameters.AddWithValue("$c", starterContent);
            cmd.Parameters.AddWithValue("$u", "authored in app");
        });
        return true;
    }

    private void WritePublished(string name, string publishedPath, string content, int version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(publishedPath))!);
        File.WriteAllText(publishedPath, content);
        var ext = Path.GetExtension(publishedPath);
        File.WriteAllText(Path.Combine(_snapshotRoot, $"{name}-v{version}{ext}"), content);
    }
}
