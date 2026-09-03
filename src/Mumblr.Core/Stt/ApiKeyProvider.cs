namespace Mumblr.Core.Stt;

/// <summary>
/// The ElevenLabs key only ever comes from the environment - never from the config file or the repo.
/// </summary>
public static class ApiKeyProvider
{
    public const string PrimaryVariable = "ELEVENLABS_API_KEY";
    public const string FallbackVariable = "XI_API_KEY";

    public static string? TryGet(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;

        var key = read(PrimaryVariable);
        if (!string.IsNullOrWhiteSpace(key))
            return key.Trim();

        key = read(FallbackVariable);
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    public static string Require(Func<string, string?>? read = null) =>
        TryGet(read) ?? throw new InvalidOperationException(
            $"No ElevenLabs API key. Set the {PrimaryVariable} environment variable (or {FallbackVariable}) and restart mumblr.");
}
