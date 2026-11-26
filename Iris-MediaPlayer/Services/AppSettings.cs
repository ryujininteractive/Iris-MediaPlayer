using System;

namespace Iris_MediaPlayer.Services;

/// <summary>How a video that was stopped part-way starts the next time.</summary>
internal enum ResumeMode
{
    /// <summary>Pick up where it stopped, with a "Start over" button for a few seconds.</summary>
    Always,
    /// <summary>Start from the beginning and offer a "Resume" button for a few seconds.</summary>
    Ask,
    /// <summary>Always start from the beginning.</summary>
    Never
}

/// <summary>What the player does at an intro, recap or end credits.</summary>
internal enum SkipMode
{
    /// <summary>Show a "Skip intro" button while it plays.</summary>
    Button,
    /// <summary>Jump past it without asking.</summary>
    Automatic,
    /// <summary>Play it like the rest of the video.</summary>
    Off
}

/// <summary>
/// App-wide preferences that aren't about the player itself (library and Home). Like
/// <see cref="PlayerSettings"/> they live in settings.json through <see cref="AppStorage"/>.
/// Setters raise <see cref="Changed"/> so open pages (Home) can update without a restart.
/// </summary>
internal static class AppSettings
{
    private const string HideContinueWatchingKey = "HideContinueWatching";
    private const string HideRecentlyPlayedKey = "HideRecentlyPlayed";
    private const string WatchFoldersKey = "WatchLibraryFolders";
    private const string AutoUpdateKey = "AutoUpdate";

    /// <summary>Raised on the thread that changed a setting (the UI thread for the Settings page).</summary>
    public static event Action? Changed;

    /// <summary>Hides the Continue watching row on Home (progress is still remembered).</summary>
    public static bool HideContinueWatching
    {
        get => AppStorage.GetBool(HideContinueWatchingKey) ?? false;
        set => Set(HideContinueWatchingKey, value);
    }

    /// <summary>Hides the Recently played row on Home and its slides (the list is still kept).</summary>
    public static bool HideRecentlyPlayed
    {
        get => AppStorage.GetBool(HideRecentlyPlayedKey) ?? false;
        set => Set(HideRecentlyPlayedKey, value);
    }

    /// <summary>Rescans library folders on their own when files are added, removed or renamed.</summary>
    public static bool WatchFolders
    {
        get => AppStorage.GetBool(WatchFoldersKey) ?? true;
        set => Set(WatchFoldersKey, value);
    }

    /// <summary>Looks for and downloads new versions in the background after startup.</summary>
    public static bool AutoUpdate
    {
        get => AppStorage.GetBool(AutoUpdateKey) ?? true;
        set => Set(AutoUpdateKey, value);
    }

    private static void Set(string key, bool value)
    {
        AppStorage.SetBool(key, value);
        Changed?.Invoke();
    }
}
