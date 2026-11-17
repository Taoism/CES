namespace Ces.Disc;

/// <summary>One row of the side-by-side BDInfo summary: a label and each source's value ("" when it has none).</summary>
public sealed record BdInfoRow(string Label, string[] Values)
{
    /// <summary>The sources that have this row don't all agree, or some sources lack it.</summary>
    public bool Differs => Values.Distinct(StringComparer.Ordinal).Skip(1).Any();
}

/// <summary>Lines up BDInfo quick summaries of several discs (Audio #2 next to Audio #2, …).</summary>
public static class BdInfoTable
{
    /// <summary>Labels that differ between any two discs and say nothing about the content.</summary>
    private static readonly HashSet<string> DiscOnly = new(StringComparer.Ordinal) { "Disc Title", "Disc Label", "Disc Size", "Protection" };

    public static List<BdInfoRow> Build(IReadOnlyList<List<(string Label, string Value)>?> summaries, bool includeDiscRows = true)
    {
        var keys = new List<(string Label, int N)>();
        var maps = summaries.Select(s =>
        {
            var map = new Dictionary<(string, int), string>();
            if (s == null)
                return map;
            var count = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (label, value) in s)
            {
                count[label] = count.TryGetValue(label, out var c) ? c + 1 : 1;
                var key = (label, count[label]);
                map[key] = value;
                if (!keys.Contains(key))
                    keys.Add(key);
            }
            return map;
        }).ToList();
        // a label's later occurrences (Audio #3 from source 2) go after its earlier ones
        var ordered = keys
            .OrderBy(k => keys.FindIndex(y => y.Label == k.Label))
            .ThenBy(k => k.N)
            .Where(k => includeDiscRows || !DiscOnly.Contains(k.Label));
        var lastCount = keys.GroupBy(k => k.Label).ToDictionary(g => g.Key, g => g.Max(k => k.N), StringComparer.Ordinal);
        return ordered.Select(k => new BdInfoRow(
            lastCount[k.Label] > 1 ? $"{k.Label} {k.N}" : k.Label,
            maps.Select(m => m.TryGetValue(k, out var v) ? v : "").ToArray())).ToList();
    }
}
