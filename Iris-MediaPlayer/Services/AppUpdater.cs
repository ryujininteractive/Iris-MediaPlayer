using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Iris_MediaPlayer.Services;

/// <summary>What "Check for updates now" found.</summary>
internal enum UpdateCheckResult
{
    /// <summary>Iris wasn't installed by the setup (running from Visual Studio, say), so it can't update itself.</summary>
    NotInstalled,
    UpToDate,
    /// <summary>A newer version has been downloaded and waits for a restart.</summary>
    Ready,
    /// <summary>The website couldn't be reached.</summary>
    Failed
}

/// <summary>
/// Keeps an installed Iris up to date from the release folder on ryujininteractive.com. The new version is
/// downloaded quietly in the background; the user chooses when to restart into it, and if they
/// never do, it is installed the next time Iris starts.
/// Does nothing when Iris wasn't installed by IrisSetup.exe (running from Visual Studio, say).
/// </summary>
internal sealed class AppUpdater
{
    // Not created at all in the Store version, where Velopack never runs.
    private readonly UpdateManager? _manager = Installation.IsPackaged ? null : new(new SimpleWebSource(Installation.UpdatesUrl));
    private UpdateInfo? _ready;

    /// <summary>The version that is downloaded and waiting, or null.</summary>
    public string? ReadyVersion => _ready?.TargetFullRelease.Version.ToString();

    /// <summary>Installed by the setup (.exe). The Store version is updated by the Store instead.</summary>
    public bool IsInstalled => _manager?.IsInstalled == true;

    /// <summary>
    /// The Settings page's "Check for updates now": like <see cref="DownloadUpdateAsync"/> but says why
    /// nothing is ready. A version that is already downloaded counts as ready.
    /// </summary>
    public async Task<UpdateCheckResult> CheckNowAsync()
    {
        if (!IsInstalled)
            return UpdateCheckResult.NotInstalled;
        if (_ready is not null)
            return UpdateCheckResult.Ready;

        try
        {
            var update = await _manager!.CheckForUpdatesAsync();
            if (update is null)
                return UpdateCheckResult.UpToDate;

            await _manager!.DownloadUpdatesAsync(update);
            _ready = update;
            return UpdateCheckResult.Ready;
        }
        catch (Exception)
        {
            return UpdateCheckResult.Failed;
        }
    }

    /// <summary>Checks the website and downloads a newer release. Returns true when one is ready to install.</summary>
    public async Task<bool> DownloadUpdateAsync()
    {
        if (!IsInstalled)
            return false;

        try
        {
            var update = await _manager!.CheckForUpdatesAsync();
            if (update is null)
                return false;

            await _manager!.DownloadUpdatesAsync(update);
            _ready = update;
            return true;
        }
        catch (Exception)
        {
            // Offline, GitHub unreachable or rate-limited: try again next launch.
            return false;
        }
    }

    /// <summary>Closes Iris, installs the downloaded version and starts it again.</summary>
    public void RestartToUpdate()
    {
        if (_ready is not null && _manager is not null)
            _manager.ApplyUpdatesAndRestart(_ready.TargetFullRelease);
    }
}
