using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Mumblr.App.Updates;

/// <summary>
/// Checks the GitHub releases the portable package and the installer both come from. Updates are
/// never applied behind the user's back - the UI offers a button and restarts on demand.
/// </summary>
public sealed class UpdateService
{
    /// <summary>Where releases come from, and where the status bar's link points.</summary>
    public const string ProjectUrl = "https://github.com/0xMMA/mumblr";

    private readonly string repositoryUrl;
    private UpdateManager? manager;
    private UpdateInfo? pending;

    public UpdateService(string repositoryUrl = ProjectUrl) =>
        this.repositoryUrl = repositoryUrl;

    /// <summary>
    /// Why a check produced no update. "Up to date" and "could not ask" look identical from the
    /// outside and must never be reported as the same thing - a private repository answers 404 to
    /// an unauthenticated client, and claiming "latest build" there is a lie, not an answer.
    /// </summary>
    public enum UpdateCheck
    {
        UpToDate,
        Available,
        NotInstalled,
        Failed,
    }

    /// <summary>Null until an update was found and downloaded.</summary>
    public string? AvailableVersion { get; private set; }

    public bool HasUpdate => pending is not null;

    /// <summary>Looks for a newer release and downloads it, and says what actually happened.</summary>
    public async Task<UpdateCheck> CheckAsync()
    {
        try
        {
            manager ??= new UpdateManager(new GithubSource(repositoryUrl, AccessToken, prerelease: false));

            // A plain `dotnet run` or an unzipped build without the Velopack layout cannot update.
            if (!manager.IsInstalled)
                return UpdateCheck.NotInstalled;

            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
                return UpdateCheck.UpToDate;

            await manager.DownloadUpdatesAsync(update).ConfigureAwait(false);

            pending = update;
            AvailableVersion = update.TargetFullRelease.Version.ToString();
            return UpdateCheck.Available;
        }
        catch (Exception)
        {
            // Offline, rate limited, or a private repository answering 404 to an anonymous client.
            // Never block dictating over an update check - but never claim to be up to date either.
            return UpdateCheck.Failed;
        }
    }

    /// <summary>
    /// A private repository serves its releases only to an authenticated client. The token comes
    /// from the environment for the same reason the ElevenLabs key does: never from config, never
    /// from the repo. Unset is the normal case once the releases are public.
    /// </summary>
    public const string TokenVariable = "MUMBLR_GITHUB_TOKEN";

    private static string? AccessToken
    {
        get
        {
            var token = Environment.GetEnvironmentVariable(TokenVariable);
            return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        }
    }

    /// <summary>Applies the downloaded update and restarts. Only call after the buffer was flushed.</summary>
    public void ApplyAndRestart()
    {
        if (manager is null || pending is null)
            return;

        manager.ApplyUpdatesAndRestart(pending);
    }
}
