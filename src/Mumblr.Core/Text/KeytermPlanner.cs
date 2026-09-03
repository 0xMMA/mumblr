namespace Mumblr.Core.Text;

/// <summary>Per-backend keyterm limits. Batch allows far more terms than realtime.</summary>
public readonly record struct KeytermLimits(int MaxTerms, int MaxTermLength)
{
    public static readonly KeytermLimits Batch = new(1000, 50);
    public static readonly KeytermLimits Realtime = new(50, 20);
}

/// <summary>
/// Trims a user maintained, priority ordered keyterm list down to what a backend accepts.
/// Order is the priority: the head of the list survives.
/// </summary>
public static class KeytermPlanner
{
    public static IReadOnlyList<string> Plan(IEnumerable<string> keyterms, KeytermLimits limits)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var raw in keyterms)
        {
            var term = raw?.Trim();
            if (string.IsNullOrEmpty(term) || term.Length > limits.MaxTermLength)
                continue;
            if (!seen.Add(term))
                continue;

            result.Add(term);
            if (result.Count == limits.MaxTerms)
                break;
        }

        return result;
    }
}
