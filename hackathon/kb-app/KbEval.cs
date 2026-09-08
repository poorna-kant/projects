namespace KbApp;

// V2 governance eval: a "background" data-quality check that runs whenever a change is suggested.
// Its first job is duplicate detection — flag existing KB items whose meaning overlaps the proposed
// one, so the same concept isn't approved twice under a different wording.
//
// Offline-first: this uses a lexical semantic-similarity scorer (normalized token overlap + char
// n-grams) that needs no model, so it works in the air-gapped container today. It is deliberately
// structured behind one Similarity() seam: swap in sentence embeddings (cosine over vectors) or an
// LLM judge when a model endpoint is available, and the review flow is unchanged.
public sealed class KbEval
{
    private readonly TreeReader _tree;
    private readonly double _threshold;
    public KbEval(TreeReader tree, double threshold = 0.55) { _tree = tree; _threshold = threshold; }

    public List<DupeMatch> FindDuplicates(string domain, string type, string? excludeId, string candidate, int top = 3)
    {
        var cand = Doc.Build(candidate);
        var outp = new List<DupeMatch>();
        if (cand.Empty) return outp;
        foreach (var (id, title, text) in _tree.EvalItems(domain, type))
        {
            if (!string.IsNullOrEmpty(excludeId) && string.Equals(id, excludeId, StringComparison.OrdinalIgnoreCase)) continue;
            var score = Similarity(cand, Doc.Build(text));
            if (score >= _threshold)
                outp.Add(new DupeMatch(id, title, type, Math.Round(score, 3), Verdict(score)));
        }
        return outp.OrderByDescending(m => m.Score).Take(top).ToList();
    }

    private static string Verdict(double s) =>
        s >= 0.85 ? "Almost certainly the same item — near-identical wording."
        : s >= 0.70 ? "Strong overlap — likely the same concept in different words."
        : "Some overlap — worth a look before approving.";

    // Blended lexical similarity: token cosine (word importance) + token Jaccard (set overlap)
    // + char-trigram cosine (catches variants/typos). Robust to word reordering and add/drop.
    private static double Similarity(Doc a, Doc b)
    {
        if (a.Empty || b.Empty) return 0;
        var tokCos = Cosine(a.TokenFreq, b.TokenFreq);
        var tokJac = Jaccard(a.TokenSet, b.TokenSet);
        var triCos = Cosine(a.TriFreq, b.TriFreq);
        return 0.5 * tokCos + 0.3 * tokJac + 0.2 * triCos;
    }

    private static double Cosine(Dictionary<string, int> x, Dictionary<string, int> y)
    {
        if (x.Count == 0 || y.Count == 0) return 0;
        double dot = 0;
        var (small, big) = x.Count <= y.Count ? (x, y) : (y, x);
        foreach (var kv in small) if (big.TryGetValue(kv.Key, out var v)) dot += kv.Value * v;
        double mx = Math.Sqrt(x.Values.Sum(v => (double)v * v));
        double my = Math.Sqrt(y.Values.Sum(v => (double)v * v));
        return mx == 0 || my == 0 ? 0 : dot / (mx * my);
    }

    private static double Jaccard(HashSet<string> x, HashSet<string> y)
    {
        if (x.Count == 0 || y.Count == 0) return 0;
        var inter = x.Count <= y.Count ? x.Count(y.Contains) : y.Count(x.Contains);
        return (double)inter / (x.Count + y.Count - inter);
    }

    // ---- normalized document model ----
    private sealed class Doc
    {
        public HashSet<string> TokenSet = new();
        public Dictionary<string, int> TokenFreq = new();
        public Dictionary<string, int> TriFreq = new();
        public bool Empty => TokenFreq.Count == 0;

        public static Doc Build(string? raw)
        {
            var d = new Doc();
            if (string.IsNullOrWhiteSpace(raw)) return d;
            var norm = new System.Text.StringBuilder();
            foreach (var ch in raw.ToLowerInvariant())
                norm.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
            var words = norm.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var kept = new List<string>();
            foreach (var w in words)
            {
                if (w.Length < 2 || Stop.Contains(w)) continue;
                var s = Stem(w);
                kept.Add(s);
                d.TokenSet.Add(s);
                d.TokenFreq[s] = d.TokenFreq.GetValueOrDefault(s) + 1;
            }
            var joined = string.Join(' ', kept);
            for (int i = 0; i + 3 <= joined.Length; i++)
            {
                var tri = joined.Substring(i, 3);
                if (tri.Trim().Length < 2) continue;
                d.TriFreq[tri] = d.TriFreq.GetValueOrDefault(tri) + 1;
            }
            return d;
        }

        // Light suffix stripping so singular/plural and common variants collapse together.
        private static string Stem(string w)
        {
            foreach (var suf in Suffixes)
                if (w.Length - suf.Length >= 3 && w.EndsWith(suf, StringComparison.Ordinal))
                    return w[..^suf.Length];
            return w;
        }

        private static readonly string[] Suffixes = { "tions", "tion", "ments", "ment", "ings", "ing", "ies", "es", "s" };
        private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
        {
            "the","a","an","of","to","and","or","is","are","for","in","on","with","that","this","it","its",
            "as","by","from","at","be","will","not","no","how","what","why","which","each","every","per",
            "into","than","then","when","where","we","you","they","he","she","but","if","so","do","does",
            "can","cannot","must","should","may","these","those","there","their","our","your","all","any",
            "one","two","use","used","using","via","across","over","under","between","within"
        };
    }
}

public sealed record DupeMatch(string Id, string Title, string Type, double Score, string Verdict);
