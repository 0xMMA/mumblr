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
    /// <summary>
    /// ElevenLabs rejects the whole request - not just the offending term - if any keyterm carries
    /// one of these. Verified against a live 400: "Some keyword contains invalid characters".
    /// </summary>
    public static readonly char[] ForbiddenCharacters = ['<', '>', '{', '}', '[', ']', '\\'];

    /// <summary>A keyterm may hold at most this many words after normalisation.</summary>
    public const int MaxWords = 5;

    public static IReadOnlyList<string> Plan(IEnumerable<string> keyterms, KeytermLimits limits)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var raw in keyterms)
        {
            var term = raw?.Trim();
            if (string.IsNullOrEmpty(term) || term.Length > limits.MaxTermLength)
                continue;
            if (!IsAcceptable(term))
                continue;
            if (!seen.Add(term))
                continue;

            result.Add(term);
            if (result.Count == limits.MaxTerms)
                break;
        }

        return result;
    }

    /// <summary>
    /// A term the API would reject is dropped rather than repaired. Truncating or stripping
    /// characters would silently bias the transcript towards a word the user never asked for,
    /// and one bad term otherwise costs the entire recording.
    /// </summary>
    public static bool IsAcceptable(string term) =>
        term.IndexOfAny(ForbiddenCharacters) < 0 &&
        term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= MaxWords;
}
