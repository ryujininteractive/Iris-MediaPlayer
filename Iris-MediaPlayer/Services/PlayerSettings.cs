using System;

namespace Iris_MediaPlayer.Services;

/// <summary>Player preferences that persist between launches (stored by <see cref="AppStorage"/>).</summary>
internal static class PlayerSettings
{
    private const string VolumeKey = "PlayerVolume";
    private const string AutoplayNextKey = "PlayerAutoplayNext";
    private const string ZoomModeKey = "PlayerZoomMode";

    public static double Volume
    {
        get => AppStorage.GetDouble(VolumeKey) is { } volume ? Math.Clamp(volume, 0, 100) : 100;
        set => AppStorage.SetDouble(VolumeKey, Math.Clamp(value, 0, 100));
    }

    /// <summary>Kept between videos and launches, like most players' "fill screen" setting.</summary>
    public static VideoZoomMode ZoomMode
    {
        get => Enum.TryParse<VideoZoomMode>(AppStorage.GetString(ZoomModeKey), out var mode) ? mode : VideoZoomMode.Fit;
        set => AppStorage.SetString(ZoomModeKey, value.ToString());
    }

    public static bool AutoplayNext
    {
        get => AppStorage.GetBool(AutoplayNextKey) ?? true;
        set => AppStorage.SetBool(AutoplayNextKey, value);
    }

    // ---------- Playback ----------

    /// <summary>Show a picture of the moment when hovering the seek bar.</summary>
    public static bool SeekPreview
    {
        get => AppStorage.GetBool("PlayerSeekPreview") ?? true;
        set => AppStorage.SetBool("PlayerSeekPreview", value);
    }

    /// <summary>Width of the seek bar picture on screen, in pixels.</summary>
    public static int SeekPreviewWidth
    {
        get => Math.Clamp((int)(AppStorage.GetDouble("PlayerSeekPreviewWidth") ?? 288), 160, 480);
        set => AppStorage.SetDouble("PlayerSeekPreviewWidth", Math.Clamp(value, 160, 480));
    }

    public static int AutoplayCountdownSeconds
    {
        get => Math.Clamp((int)(AppStorage.GetDouble("PlayerAutoplayCountdown") ?? 5), 1, 30);
        set => AppStorage.SetDouble("PlayerAutoplayCountdown", Math.Clamp(value, 1, 30));
    }

    public static ResumeMode Resume
    {
        get => Enum.TryParse<ResumeMode>(AppStorage.GetString("PlayerResumeMode"), out var mode) ? mode : ResumeMode.Always;
        set => AppStorage.SetString("PlayerResumeMode", value.ToString());
    }

    /// <summary>Speed every new player starts at; the speed buttons in the player still change it per session.</summary>
    public static double DefaultSpeed
    {
        get => Math.Clamp(AppStorage.GetDouble("PlayerDefaultSpeed") ?? 1, 0.25, 4);
        set => AppStorage.SetDouble("PlayerDefaultSpeed", Math.Clamp(value, 0.25, 4));
    }

    /// <summary>Seconds skipped by the Left and Right arrow keys.</summary>
    public static int SmallSkipSeconds
    {
        get => Math.Clamp((int)(AppStorage.GetDouble("PlayerSmallSkip") ?? 5), 1, 120);
        set => AppStorage.SetDouble("PlayerSmallSkip", Math.Clamp(value, 1, 120));
    }

    /// <summary>Seconds skipped by J and L and the skip buttons in the player.</summary>
    public static int LargeSkipSeconds
    {
        get => Math.Clamp((int)(AppStorage.GetDouble("PlayerLargeSkip") ?? 10), 1, 300);
        set => AppStorage.SetDouble("PlayerLargeSkip", Math.Clamp(value, 1, 300));
    }

    // ---------- Subtitles and audio ----------

    /// <summary>
    /// Preferred subtitle language as mpv language codes ("en,eng"), empty for mpv's own choice,
    /// or <see cref="SubtitlesOff"/> to start without subtitles.
    /// </summary>
    public static string SubtitleLanguage
    {
        get => AppStorage.GetString("PlayerSubtitleLanguage") ?? string.Empty;
        set => AppStorage.SetString("PlayerSubtitleLanguage", string.IsNullOrEmpty(value) ? null : value);
    }

    public const string SubtitlesOff = "off";

    public static string AudioLanguage
    {
        get => AppStorage.GetString("PlayerAudioLanguage") ?? string.Empty;
        set => AppStorage.SetString("PlayerAudioLanguage", string.IsNullOrEmpty(value) ? null : value);
    }

    /// <summary>Subtitle size chosen in Settings (1 = normal); the player scales it down a little, see PlayerView.SubtitleBaseScale.</summary>
    public static double SubtitleScale
    {
        get => Math.Clamp(AppStorage.GetDouble("PlayerSubtitleScale") ?? 1, 0.5, 3);
        set => AppStorage.SetDouble("PlayerSubtitleScale", Math.Clamp(value, 0.5, 3));
    }

    /// <summary>How far subtitles sit above the bottom edge, as a percentage of the window height.</summary>
    public static double SubtitleLift
    {
        get => Math.Clamp(AppStorage.GetDouble("PlayerSubtitleLift") ?? 0, 0, 30);
        set => AppStorage.SetDouble("PlayerSubtitleLift", Math.Clamp(value, 0, 30));
    }

    // ---------- Player ----------

    /// <summary>Seconds of no mouse movement before the controls hide.</summary>
    public static double ControlsHideSeconds
    {
        get => Math.Clamp(AppStorage.GetDouble("PlayerControlsHideSeconds") ?? 3, 1, 30);
        set => AppStorage.SetDouble("PlayerControlsHideSeconds", Math.Clamp(value, 1, 30));
    }

    /// <summary>Volume percentage per mouse-wheel notch.</summary>
    public static int WheelVolumeStep
    {
        get => Math.Clamp((int)(AppStorage.GetDouble("PlayerWheelVolumeStep") ?? 5), 1, 25);
        set => AppStorage.SetDouble("PlayerWheelVolumeStep", Math.Clamp(value, 1, 25));
    }
}
