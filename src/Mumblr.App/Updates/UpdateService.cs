using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Mumblr.App.Updates;

/// <summary>
/// Checks the GitHub releases the portable package and the installer both come from. Updates are
/// never applied behind the user's back - the UI offers a button and restarts on demand.
/// </summary>
/// <summary>
/// The update check behind a seam, so the four outcomes can be tested. Claiming "latest build"
/// after a check that never happened is the failure this interface exists to keep covered.
/// </summary>
public interface IUpdateService
{
    string? AvailableVersion { get; }

    Task<UpdateService.UpdateCheck> CheckAsync();

    void ApplyAndRestart();
}

public sealed class UpdateService : IUpdateService
{
    /// <summary>Where releases come from, and where the status bar's link points.</summary>
    public const string ProjectUrl = "https://github.com/0xMMA/mumblr";

    /// <summary>
    /// A private repository serves its releases only to an authenticated client. The token comes
    /// from the environment for the same reason the ElevenLabs key does: never from config, never
    /// from the repo. Unset is the normal case once the releases are public.
    /// </summary>
    public const string TokenVariable = "MUMBLR_GITHUB_TOKEN";

    private readonly string repositoryUrl;
    private ChannelAwareUpdateManager? manager;
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
        NoReleases,
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
            // prerelease: true is what lets a preview build see its own releases at all. Preview
            // and stable are the same binary - the channel is baked into the package by vpk, not
            // compiled in - so the flag cannot be decided per build. It has to be the one that
            // works for both, and it is: the source filters with `includePrereleases || !x.Prerelease`,
            // so true means "stable and prerelease", not "prerelease only". What keeps the two
            // apart is the channel: a release that carries no releases.<this channel>.json is
            // skipped, and stable releases carry no preview feed.
            //
            // GitHub is asked for the 10 most recent releases and no more. Preview releases that
            // pile up push the newest stable one off that list, and a stable client then stops
            // seeing updates - so preview tags stay rare and throwaway ones get deleted.
            manager ??= new ChannelAwareUpdateManager(new GithubSource(repositoryUrl, AccessToken, prerelease: true));

            // A plain `dotnet run` or an unzipped build without the Velopack layout cannot update.
            if (!manager.IsInstalled)
                return UpdateCheck.NotInstalled;

            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                // Null means "nothing newer" and "nothing at all" alike, and those must not sound
                // the same: a preview install whose channel holds no release - the betas were
                // deleted, or none was ever cut - would be told it is the latest build forever.
                var any = await manager.ChannelHasReleasesAsync().ConfigureAwait(false);
                return any ? UpdateCheck.UpToDate : UpdateCheck.NoReleases;
            }

            await manager.DownloadUpdatesAsync(update).ConfigureAwait(false);

            pending = update;
            AvailableVersion = update.TargetFullRelease.Version.ToString();
            return UpdateCheck.Available;
        }
        catch (Exception)
        {
            // Offline, rate limited, or a private repository answering 404 to an anonymous client.
            // Never block dictating over an update check - but never claim to be up to date either.
            // The manager caches the token it was built with, so drop it: setting MUMBLR_GITHUB_TOKEN
            // would otherwise need a restart to take effect.
            manager = null;
            return UpdateCheck.Failed;
        }
    }

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

    /// <summary>
    /// An UpdateManager that can also say whether this build's channel has any release at all.
    /// The source, the channel and the log are protected on the base class, which is the only
    /// reason this subclass exists - a Velopack that moves them breaks the build rather than
    /// quietly returning the wrong answer.
    /// </summary>
    private sealed class ChannelAwareUpdateManager(IUpdateSource source) : UpdateManager(source)
    {
        public async Task<bool> ChannelHasReleasesAsync()
        {
            var feed = await Source.GetReleaseFeed(Log, AppId, Channel).ConfigureAwait(false);
            return feed.Assets is { Length: > 0 };
        }
    }
}
