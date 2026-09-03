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
    private readonly string repositoryUrl;
    private UpdateManager? manager;
    private UpdateInfo? pending;

    public UpdateService(string repositoryUrl = "https://github.com/0xMMA/mumblr") =>
        this.repositoryUrl = repositoryUrl;

    /// <summary>Null until an update was found and downloaded.</summary>
    public string? AvailableVersion { get; private set; }

    public bool HasUpdate => pending is not null;

    /// <summary>Looks for a newer release and downloads it. Returns the version, or null.</summary>
    public async Task<string?> CheckAsync()
    {
        try
        {
            manager ??= new UpdateManager(new GithubSource(repositoryUrl, accessToken: null, prerelease: false));

            // A plain `dotnet run` or an unzipped build without the Velopack layout cannot update.
            if (!manager.IsInstalled)
                return null;

            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
                return null;

            await manager.DownloadUpdatesAsync(update).ConfigureAwait(false);

            pending = update;
            AvailableVersion = update.TargetFullRelease.Version.ToString();
            return AvailableVersion;
        }
        catch (Exception)
        {
            // Offline, rate limited, or no releases yet. Never block dictating over an update check.
            return null;
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
