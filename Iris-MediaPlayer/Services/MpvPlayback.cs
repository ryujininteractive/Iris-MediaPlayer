using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace Iris_MediaPlayer.Services;

public enum VideoZoomMode
{
    Fit,
    Fill,
    Stretch
}

/// <summary>Owns a libmpv instance configured to render into a WinUI composition swap chain.</summary>
internal sealed class MpvPlayback : IDisposable
{
    private const int PropertyChangeEvent = 22;
    private const int ShutdownEvent = 1;
    private const int EndFileEvent = 7;
    private const int FileLoadedEvent = 8;
    private const int FlagFormat = (int)MpvPropertyFormat.Flag;
    private const int Int64Format = (int)MpvPropertyFormat.Int64;
    private const int DoubleFormat = (int)MpvPropertyFormat.Double;

    private readonly DispatcherQueue _dispatcher;
    private readonly Thread _eventThread;
    private readonly object _gate = new();
    private IntPtr _handle;
    private bool _disposed;

    public event Action<IntPtr>? SwapChainChanged;
    public event Action<string, object?>? PropertyChanged;

    public IntPtr CurrentSwapChain
    {
        get
        {
            lock (_gate)
            {
                if (_handle == IntPtr.Zero || MpvNative.mpv_get_property(_handle, "display-swapchain", Int64Format, out var value) < 0)
                    return IntPtr.Zero;
                return new IntPtr(value);
            }
        }
    }

    public MpvPlayback(DispatcherQueue dispatcher, int width, int height)
    {
        _dispatcher = dispatcher;
        _handle = MpvNative.mpv_create();
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("mpv could not create a playback context.");

        try
        {
            SetOption("config", "no");
            SetOption("terminal", "no");
            SetOption("vo", "gpu-next");
            SetOption("gpu-context", "d3d11");
            SetOption("d3d11-output-mode", "composition");
            SetOption("d3d11-composition-size", $"{Math.Max(width, 1)}x{Math.Max(height, 1)}");
            SetOption("hwdec", "auto-safe");
            SetOption("force-window", "immediate");
            SetOption("idle", "yes");
            SetOption("keep-open", "yes");
            SetOption("osc", "no");
            SetOption("osd-level", "0");
            SetOption("input-default-bindings", "no");
            // mpv's built-in scripts (stats, console, menus, the youtube-dl hook that runs on every web
            // address…) aren't used by Iris. Options this mpv build doesn't know are skipped.
            foreach (var script in new[] { "ytdl", "load-stats-overlay", "load-console", "load-auto-profiles",
                                           "load-select", "load-positioning", "load-commands", "load-osd-console" })
                TrySetOption(script, "no");
            // For troubleshooting: with IRIS_MPV_LOG set, mpv writes a detailed log next to Iris's data.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IRIS_MPV_LOG")))
                SetOption("log-file", AppStorage.GetFilePath("mpv.log"));
            // Text subtitles sit a little closer to the bottom edge than mpv's default (34).
            SetOption("sub-margin-y", "22");
            // After a seek, show the subtitle line already in progress instead of waiting for the next one,
            // even in files without a subtitle index.
            SetOption("demuxer-mkv-subtitle-preroll", "yes");

            var result = MpvNative.mpv_initialize(_handle);
            ThrowIfError(result, "initialize mpv");

            Observe("display-swapchain", Int64Format);
            Observe("time-pos", DoubleFormat);
            Observe("duration", DoubleFormat);
            Observe("pause", FlagFormat);
            Observe("volume", DoubleFormat);
            Observe("mute", FlagFormat);
            Observe("speed", DoubleFormat);
            Observe("media-title", (int)MpvPropertyFormat.String);
            Observe("idle-active", FlagFormat);
            Observe("chapter", Int64Format);
            // Seconds of video buffered ahead of the playhead; drawn as the lighter part of the seek bar.
            Observe("demuxer-cache-duration", DoubleFormat);
            // With keep-open=yes mpv pauses on the last frame instead of ending the file,
            // so end of playback is reported through this property.
            Observe("eof-reached", FlagFormat);
            // Where the video sits in the window; changes on resize, zoom and new videos.
            Observe("osd-dimensions", (int)MpvPropertyFormat.String);
            // Silence reports from the skip-silence filter (see SetSilenceDetection), and seeks, which
            // end any silence being hurried through.
            Observe($"af-metadata/{SilenceFilterLabel}", (int)MpvPropertyFormat.String);
            Observe("seeking", FlagFormat);

            _eventThread = new Thread(EventLoop)
            {
                IsBackground = true,
                Name = "Iris mpv event loop"
            };
            _eventThread.Start();
        }
        catch
        {
            MpvNative.mpv_terminate_destroy(_handle);
            _handle = IntPtr.Zero;
            throw;
        }
    }

    /// <summary>
    /// Opens a file and plays it, optionally starting at <paramref name="startSeconds"/> (used for resume).
    /// With keep-open, mpv pauses on the last frame of a finished video and stays paused for the next
    /// file, so playback is unpaused here explicitly.
    /// </summary>
    public void LoadFile(string filePath, double startSeconds = 0)
    {
        var start = startSeconds > 0 ? startSeconds.ToString("0.###", CultureInfo.InvariantCulture) : "0";
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfError(MpvNative.mpv_set_property_string(_handle, "start", start), "set start position");
        }
        SetPaused(false);
        RunCommand("loadfile", filePath, "replace");
    }

    public void Stop()
    {
        RunCommand("stop");
    }

    public void SetPaused(bool paused)
    {
        var value = paused ? 1 : 0;
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfError(MpvNative.mpv_set_property_flag(_handle, "pause", FlagFormat, ref value), "set pause");
        }
    }

    /// <summary>Restarts the current file from the beginning and resumes playback.</summary>
    public void Replay()
    {
        Seek(0);
        SetPaused(false);
    }

    public void SetSubtitleDelay(double seconds) => SetDouble("sub-delay", seconds, "set subtitle delay");

    public void SetAudioDelay(double seconds) => SetDouble("audio-delay", seconds, "set audio delay");

    /// <summary>
    /// Where subtitles sit, as a percentage of the window height (100 = bottom). Used to lift them
    /// above the player controls while those are showing.
    /// </summary>
    public void SetSubtitlePosition(double percent) => SetDouble("sub-pos", Math.Floor(Math.Clamp(percent, 0, 150) * 10) / 10, "set subtitle position");

    /// <summary>
    /// Describes what the current subtitles are positioned against, or null without subtitles or video.
    /// Plain text subtitles (SRT, WebVTT…) are laid out in the whole window; picture subtitles (Blu-ray,
    /// DVD) and styled ASS subtitles are laid out inside the video, and sub-pos moves them by a
    /// percentage of the video height.
    /// </summary>
    public SubtitleFrame? GetSubtitleFrame()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero || _disposed)
                return null;
            var codec = GetStringProperty("current-tracks/sub/codec");
            if (codec is null
                || MpvNative.mpv_get_property(_handle, "osd-dimensions/h", Int64Format, out var height) < 0 || height <= 0
                || MpvNative.mpv_get_property(_handle, "osd-dimensions/mt", Int64Format, out var top) < 0
                || MpvNative.mpv_get_property(_handle, "osd-dimensions/mb", Int64Format, out var bottom) < 0)
                return null;

            var isPicture = PictureSubtitleCodecs.Contains(codec);
            var followsVideo = isPicture || codec is "ass" or "ssa";
            return followsVideo
                ? new SubtitleFrame(isPicture, Top: (double)top / height, Height: (double)(height - top - bottom) / height)
                : new SubtitleFrame(isPicture, Top: 0, Height: 1);
        }
    }

    private static readonly HashSet<string> PictureSubtitleCodecs =
        ["hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle", "xsub", "dvb_teletext"];

    /// <summary>Overrides the video aspect ratio, for example "16:9". Pass "-1" to use the file's own ratio.</summary>
    public void SetAspectOverride(string aspect)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfError(MpvNative.mpv_set_property_string(_handle, "video-aspect-override", aspect), "set aspect ratio");
        }
    }

    /// <summary>
    /// How the video fills the window. Fit letterboxes, Fill zooms until the window is covered
    /// (cropping the edges), Stretch ignores the aspect ratio.
    /// </summary>
    public void SetZoomMode(VideoZoomMode mode)
    {
        var keepAspect = mode == VideoZoomMode.Stretch ? 0 : 1;
        var panscan = mode == VideoZoomMode.Fill ? 1.0 : 0.0;
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfError(MpvNative.mpv_set_property_flag(_handle, "keepaspect", FlagFormat, ref keepAspect), "set zoom mode");
            ThrowIfError(MpvNative.mpv_set_property_double(_handle, "panscan", DoubleFormat, ref panscan), "set zoom mode");
        }
    }

    /// <summary>
    /// Crops the video to <paramref name="ratio"/> (width ÷ height), centred, for example to remove
    /// black bars burned into a widescreen film. Pass null to show the whole frame.
    /// </summary>
    public void SetCropRatio(double? ratio)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var crop = string.Empty;
            if (ratio is > 0
                && MpvNative.mpv_get_property(_handle, "width", Int64Format, out var width) >= 0
                && MpvNative.mpv_get_property(_handle, "height", Int64Format, out var height) >= 0
                && width > 0 && height > 0)
            {
                var sourceRatio = (double)width / height;
                long cropWidth = width, cropHeight = height;
                if (sourceRatio > ratio.Value)
                    cropWidth = (long)Math.Round(height * ratio.Value);
                else
                    cropHeight = (long)Math.Round(width / ratio.Value);

                crop = $"{cropWidth}x{cropHeight}+{(width - cropWidth) / 2}+{(height - cropHeight) / 2}";
            }
            ThrowIfError(MpvNative.mpv_set_property_string(_handle, "video-crop", crop), "crop video");
        }
    }

    /// <summary>Loads an external subtitle file and selects it.</summary>
    public void AddSubtitle(string filePath) => RunCommand("sub-add", filePath, "select");

    private void SetDouble(string property, double value, string action)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfError(MpvNative.mpv_set_property_double(_handle, property, DoubleFormat, ref value), action);
        }
    }

    /// <summary>
    /// mpv's current pause state. Property events only arrive when it changes, so a new file that
    /// keeps playing never reports it; read it directly instead.
    /// </summary>
    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return MpvNative.mpv_get_property_flag(_handle, "pause", FlagFormat, out var paused) >= 0 && paused != 0;
            }
        }
    }

    public bool TogglePause()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfError(MpvNative.mpv_get_property_flag(_handle, "pause", FlagFormat, out var current),
                "read pause state");
            var next = current == 0 ? 1 : 0;
            ThrowIfError(MpvNative.mpv_set_property_flag(_handle, "pause", FlagFormat, ref next), "toggle pause");
            return next != 0;
        }
    }

    public void Seek(double positionSeconds)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfError(MpvNative.mpv_set_property_double(_handle, "time-pos", DoubleFormat, ref positionSeconds), "seek");
        }
    }

    public void SetVolume(double volume)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfError(MpvNative.mpv_set_property_double(_handle, "volume", DoubleFormat, ref volume), "set volume");
        }
    }

    public void SetMuted(bool muted)
    {
        var value = muted ? 1 : 0;
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfError(MpvNative.mpv_set_property_flag(_handle, "mute", FlagFormat, ref value), "set mute");
        }
    }

    public void SetSpeed(double speed) => SetDouble("speed", speed, "set playback speed");

    public const string SilenceFilterLabel = "iris-silence";

    /// <summary>
    /// Turns FFmpeg's silence detector on or off in the audio chain. While on, mpv reports
    /// "lavfi.silence_start" once the sound has stayed below -35 dB for half a second and
    /// "lavfi.silence_end" when it comes back, through the af-metadata/iris-silence property.
    /// </summary>
    public void SetSilenceDetection(bool on)
    {
        var filters = on ? $"@{SilenceFilterLabel}:lavfi=[silencedetect=noise=-35dB:duration=0.5]" : string.Empty;
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfError(MpvNative.mpv_set_property_string(_handle, "af", filters), "set skip silence");
        }
    }

    /// <summary>Subtitle text size as a multiple of mpv's default (1 = normal).</summary>
    public void SetSubtitleScale(double scale) => SetDouble("sub-scale", Math.Clamp(scale, 0.3, 4), "set subtitle size");

    /// <summary>
    /// Tells mpv which languages to pick automatically for the next file. <paramref name="subtitles"/>
    /// and <paramref name="audio"/> are comma-separated language codes ("en,eng"), or empty to
    /// leave mpv's own choice. <paramref name="subtitlesOff"/> starts without subtitles.
    /// </summary>
    public void SetPreferredLanguages(string subtitles, string audio, bool subtitlesOff)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (subtitlesOff)
            {
                ThrowIfError(MpvNative.mpv_set_property_string(_handle, "sid", "no"), "turn off subtitles");
            }
            else if (subtitles.Length > 0)
            {
                ThrowIfError(MpvNative.mpv_set_property_string(_handle, "slang", subtitles), "set subtitle language");
                ThrowIfError(MpvNative.mpv_set_property_string(_handle, "sid", "auto"), "select subtitles automatically");
            }

            if (audio.Length > 0)
            {
                ThrowIfError(MpvNative.mpv_set_property_string(_handle, "alang", audio), "set audio language");
                ThrowIfError(MpvNative.mpv_set_property_string(_handle, "aid", "auto"), "select audio automatically");
            }
        }
    }

    public void SetAudioTrack(long trackId) => SetTrack("aid", trackId);

    public void SetSubtitleTrack(long trackId) => SetTrack("sid", trackId);

    /// <summary>The file's chapters in order, or an empty list when it has none.</summary>
    public IReadOnlyList<MpvChapter> GetChapters()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (MpvNative.mpv_get_property(_handle, "chapter-list/count", Int64Format, out var count) < 0 || count <= 0)
                return Array.Empty<MpvChapter>();

            var chapters = new List<MpvChapter>((int)count);
            for (var index = 0; index < count; index++)
            {
                var prefix = $"chapter-list/{index}/";
                if (MpvNative.mpv_get_property_double(_handle, prefix + "time", DoubleFormat, out var time) < 0)
                    continue;
                chapters.Add(new MpvChapter(index, Math.Max(0, time), GetStringProperty(prefix + "title")));
            }
            return chapters;
        }
    }

    public IReadOnlyList<MpvTrack> GetTracks(string requestedType)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (MpvNative.mpv_get_property(_handle, "track-list/count", Int64Format, out var count) < 0)
                return Array.Empty<MpvTrack>();

            var tracks = new List<MpvTrack>();
            for (long index = 0; index < count; index++)
            {
                var prefix = $"track-list/{index}/";
                var type = GetStringProperty(prefix + "type");
                if (type is null || !string.Equals(type, requestedType, StringComparison.Ordinal))
                    continue;

                if (MpvNative.mpv_get_property(_handle, prefix + "id", Int64Format, out var id) < 0)
                    continue;

                var selected = MpvNative.mpv_get_property_flag(_handle, prefix + "selected", FlagFormat, out var flag) >= 0
                               && flag != 0;
                tracks.Add(new MpvTrack(id, type, GetStringProperty(prefix + "title"),
                    GetStringProperty(prefix + "lang"), selected, GetStringProperty(prefix + "external-filename")));
            }
            return tracks;
        }
    }

    /// <summary>Selects a track by id; a negative id turns the track type off ("no").</summary>
    private void SetTrack(string property, long trackId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (trackId < 0)
                ThrowIfError(MpvNative.mpv_set_property_string(_handle, property, "no"), $"turn off {property}");
            else
                ThrowIfError(MpvNative.mpv_set_property(_handle, property, Int64Format, ref trackId), $"select {property} track");
        }
    }

    private string? GetStringProperty(string name)
    {
        if (MpvNative.mpv_get_property_string(_handle, name, (int)MpvPropertyFormat.String, out var value) < 0
            || value == IntPtr.Zero)
            return null;
        try
        {
            return Marshal.PtrToStringUTF8(value);
        }
        finally
        {
            MpvNative.mpv_free(value);
        }
    }

    public void Resize(int width, int height)
    {
        if (width < 1 || height < 1)
            return;

        lock (_gate)
        {
            if (_handle == IntPtr.Zero || _disposed)
                return;
            ThrowIfError(MpvNative.mpv_set_property_string(_handle, "d3d11-composition-size", $"{width}x{height}"), "resize video output");
        }
    }

    private void TrySetOption(string name, string value) => MpvNative.mpv_set_option_string(_handle, name, value);

    private void SetOption(string name, string value)
    {
        ThrowIfError(MpvNative.mpv_set_option_string(_handle, name, value), $"set mpv option '{name}'");
    }

    private void Observe(string name, int format)
    {
        ThrowIfError(MpvNative.mpv_observe_property(_handle, 1, name, format), $"observe mpv property '{name}'");
    }

    private void RunCommand(params string[] values)
    {
        var arguments = new IntPtr[values.Length + 1];
        try
        {
            for (var index = 0; index < values.Length; index++)
                arguments[index] = Marshal.StringToCoTaskMemUTF8(values[index]);

            lock (_gate)
            {
                ThrowIfDisposed();
                ThrowIfError(MpvNative.mpv_command(_handle, arguments), values[0]);
            }
        }
        finally
        {
            foreach (var argument in arguments)
            {
                if (argument != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(argument);
            }
        }
    }

    private void EventLoop()
    {
        while (true)
        {
            IntPtr handle;
            lock (_gate)
            {
                if (_handle == IntPtr.Zero)
                    return;
                handle = _handle;
            }

            var eventPointer = MpvNative.mpv_wait_event(handle, 0.5);

            if (eventPointer == IntPtr.Zero)
                continue;

            var mpvEvent = Marshal.PtrToStructure<MpvEvent>(eventPointer);
            if (mpvEvent.EventId == ShutdownEvent)
                return;

            if (mpvEvent.EventId == FileLoadedEvent)
            {
                _dispatcher.TryEnqueue(() => PropertyChanged?.Invoke("file-loaded", true));
                continue;
            }

            if (mpvEvent.EventId == EndFileEvent && mpvEvent.Data != IntPtr.Zero)
            {
                var endFile = Marshal.PtrToStructure<MpvEventEndFile>(mpvEvent.Data);
                if (endFile.Reason == 4)
                    _dispatcher.TryEnqueue(() => PropertyChanged?.Invoke("playback-error", endFile.Error));
                else if (endFile.Reason == 0)
                    _dispatcher.TryEnqueue(() => PropertyChanged?.Invoke("playback-ended", true));
                continue;
            }

            if (mpvEvent.EventId != PropertyChangeEvent || mpvEvent.Data == IntPtr.Zero)
                continue;

            var property = Marshal.PtrToStructure<MpvEventProperty>(mpvEvent.Data);
            var name = Marshal.PtrToStringUTF8(property.Name);
            if (string.IsNullOrEmpty(name) || property.Data == IntPtr.Zero)
                continue;

            object? value = ReadPropertyValue(property);
            if (name == "display-swapchain")
            {
                var swapChain = value is long address ? new IntPtr(address) : IntPtr.Zero;
                _dispatcher.TryEnqueue(() => SwapChainChanged?.Invoke(swapChain));
            }
            else
            {
                _dispatcher.TryEnqueue(() => PropertyChanged?.Invoke(name, value));
            }
        }
    }

    private static object? ReadPropertyValue(MpvEventProperty property)
    {
        return property.Format switch
        {
            FlagFormat => Marshal.ReadInt32(property.Data) != 0,
            Int64Format => Marshal.ReadInt64(property.Data),
            DoubleFormat => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(property.Data)),
            (int)MpvPropertyFormat.String => Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(property.Data)),
            _ => null
        };
    }

    private static void ThrowIfError(int result, string action)
    {
        if (result < 0)
            throw new InvalidOperationException($"Could not {action}: {MpvNative.GetErrorMessage(result)}");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed || _handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(MpvPlayback));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed || _handle == IntPtr.Zero)
                return;
            _disposed = true;
            MpvNative.mpv_command_string(_handle, "quit");
        }

        _eventThread.Join(TimeSpan.FromSeconds(3));
        lock (_gate)
        {
            var handle = _handle;
            _handle = IntPtr.Zero;
            if (handle != IntPtr.Zero)
                MpvNative.mpv_terminate_destroy(handle);
        }
    }
}

/// <summary>
/// The area subtitles are laid out in, as fractions of the window height. <see cref="IsPicture"/> is
/// true for picture subtitles (Blu-ray/DVD), whose size and position are drawn into the video.
/// </summary>
public sealed record SubtitleFrame(bool IsPicture, double Top, double Height)
{
    public double Bottom => Top + Height;
}
