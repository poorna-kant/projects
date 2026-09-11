using System.Net;
using System.Text.RegularExpressions;

namespace KbApp;

public sealed class DemoRuntime : IDisposable
{
    private readonly FileStream _instanceLock;
    public bool ReadOnly { get; }
    public string StateRoot { get; }
    public string TreeRoot { get; }
    public string PublishedRoot { get; }
    public string SnapshotRoot { get; }

    public DemoRuntime(IConfiguration configuration, string contentRoot)
    {
        var mode = configuration["SCOPE_READ_ONLY"] ?? "true";
        if (!bool.TryParse(mode, out var readOnly))
            throw new InvalidOperationException("SCOPE_READ_ONLY must be true or false.");
        ReadOnly = readOnly;
        StateRoot = Path.GetFullPath(configuration["SCOPE_STATE_PATH"] ?? Path.Combine(contentRoot, "data", "scope"));
        var webRoot = Path.GetFullPath(Path.Combine(contentRoot, "wwwroot")) + Path.DirectorySeparatorChar;
        if ((StateRoot + Path.DirectorySeparatorChar).StartsWith(webRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SCOPE_STATE_PATH must be outside the public web root.");
        Directory.CreateDirectory(StateRoot);
        _instanceLock = new FileStream(Path.Combine(StateRoot, ".instance.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        TreeRoot = Path.Combine(StateRoot, "knowledge-base");
        PublishedRoot = Path.Combine(StateRoot, "published");
        SnapshotRoot = Path.Combine(StateRoot, "snapshots");
        try
        {
            var seed = Path.GetFullPath(configuration["SCOPE_SEED_PATH"] ?? Path.Combine(contentRoot, "..", "knowledge-base"));
            if (!Directory.Exists(TreeRoot))
            {
                if (!File.Exists(Path.Combine(seed, "plan", "overview.yaml")))
                    throw new InvalidOperationException("Synthetic knowledge seed not found. Set SCOPE_SEED_PATH.");
                var staging = Path.Combine(StateRoot, ".seed-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                try
                {
                    CopySeed(seed, staging);
                    Directory.Move(staging, TreeRoot);
                }
                finally
                {
                    if (Directory.Exists(staging)) Directory.Delete(staging, true);
                }
            }
            Directory.CreateDirectory(PublishedRoot);
            Directory.CreateDirectory(SnapshotRoot);
        }
        catch
        {
            _instanceLock.Dispose();
            throw;
        }
    }

    private static void CopySeed(string source, string target)
    {
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith('.') || name == "_reviews") continue;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The synthetic seed must not contain symbolic links.");
            var destination = Path.Combine(target, name);
            Directory.CreateDirectory(destination);
            CopySeed(directory, destination);
        }
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The synthetic seed must not contain symbolic links.");
            if (Path.GetExtension(file) is ".yaml" or ".json" or ".md")
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
    }

    public bool AllowsWrite(HttpRequest request)
    {
        if (ReadOnly || request.HttpContext.Connection.RemoteIpAddress is not { } remote ||
            !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))
            return false;
        var host = request.Host.Host.Trim('[', ']');
        if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
            !(IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address)))
            return false;
        if (request.Headers["X-SCOPE-Local"] != "1") return false;
        var origin = request.Headers.Origin.ToString();
        return origin.Length == 0 || (Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
            uri.GetLeftPart(UriPartial.Authority).Equals(
                $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose() => _instanceLock.Dispose();
}

public static class KnowledgePath
{
    public static string Domain(string root, string domain)
    {
        if (string.IsNullOrWhiteSpace(domain) || !Regex.IsMatch(domain, "^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$"))
            throw new InvalidDataException("Invalid domain identifier.");
        return Path.Combine(root, domain);
    }

    public static string Catalog(string root, string domain, string catalog)
    {
        if (catalog is not ("objects" or "concepts" or "policies" or "systems" or "metrics" or "terms"))
            throw new InvalidDataException("Unknown knowledge catalog.");
        return Path.Combine(Domain(root, domain), catalog);
    }

    public static void WriteText(string path, string text)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
