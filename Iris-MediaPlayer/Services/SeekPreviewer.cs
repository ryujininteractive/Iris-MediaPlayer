using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Iris_MediaPlayer.Services;

/// <summary>
/// Makes the little pictures shown when hovering the seek bar. A second, hidden mpv opens the same
/// video with no window and no sound, jumps to the nearest keyframe of the hovered time and saves
/// a small JPEG of that frame. Only the latest request is worked on, so moving the mouse quickly
/// never builds a queue, and finished pictures are kept so going back over the bar is instant.
/// </summary>
public sealed class SeekPreviewer : IDisposable
{
    private const int FileLoadedEvent = 8;
    private const int EndFileEvent = 7;
    private const int ShutdownEvent = 1;
    private const int PlaybackRestartEvent = 21;
    private const int MaxCachedFrames = 400;
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SeekTimeout = TimeSpan.FromSeconds(5);

    private readonly string _source;
    private readonly int _pictureWidth;
    private readonly double _bucketSeconds;
    private readonly Action<double, byte[]> _frameReady;
    private readonly string _framePath = Path.Combine(Path.GetTempPath(), $"iris-preview-{Guid.NewGuid():N}.jpg");
    private readonly Dictionary<long, byte[]> _cache = [];
    private readonly Queue<long> _cacheOrder = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();
    private readonly Thread _thread;
    private double? _requested;
    private volatile bool _disposed;

    /// <param name="source">The file path or stream address being played.</param>
    /// <param name="durationSeconds">Length of the video; pictures are kept per 1/300 of it.</param>
    /// <param name="pictureWidth">Width of the saved pictures in pixels.</param>
    /// <param name="frameReady">Called on a background thread with the time asked for and a JPEG.</param>
    public SeekPreviewer(string source, double durationSeconds, int pictureWidth, Action<double, byte[]> frameReady)
    {
        _source = source;
        _pictureWidth = pictureWidth / 2 * 2;
        _bucketSeconds = Math.Max(1, durationSeconds / 300);
        _frameReady = frameReady;
        _thread = new Thread(Run) { IsBackground = true, Name = "Iris seek preview" };
        _thread.Start();
    }

    /// <summary>Asks for the picture at <paramref name="seconds"/>; replaces any request not started yet.</summary>
    public void Request(double seconds)
    {
        if (_disposed)
            return;

        var bucket = Bucket(seconds);
        byte[]? cached;
        lock (_gate)
        {
            if (!_cache.TryGetValue(bucket, out cached))
                _requested = seconds;
        }
        if (cached is not null)
            _frameReady(seconds, cached);
        else
            _wake.Set();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _wake.Set();
    }

    private long Bucket(double seconds) => (long)Math.Round(seconds / _bucketSeconds);

    private void Run()
    {
        var handle = IntPtr.Zero;
        try
        {
            handle = Open();
            if (handle == IntPtr.Zero)
                return;

            while (!_disposed)
            {
                _wake.WaitOne();
                while (!_disposed && TakeRequest() is { } seconds)
                {
                    var frame = Capture(handle, seconds);
                    if (frame is null || _disposed)
                        continue;
                    Remember(Bucket(seconds), frame);
                    _frameReady(seconds, frame);
                }
            }
        }
        catch (Exception exception)
        {
            // A missing preview is never worth interrupting playback for.
            Debug.WriteLine($"Seek preview stopped: {exception.Message}");
        }
        finally
        {
            if (handle != IntPtr.Zero)
                MpvNative.mpv_terminate_destroy(handle);
            // _wake is left to the garbage collector: Request or Dispose may still signal it.
            try { File.Delete(_framePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private double? TakeRequest()
    {
        lock (_gate)
        {
            var seconds = _requested;
            _requested = null;
            return seconds;
        }
    }

    private void Remember(long bucket, byte[] frame)
    {
        lock (_gate)
        {
            if (!_cache.TryAdd(bucket, frame))
                return;
            _cacheOrder.Enqueue(bucket);
            if (_cacheOrder.Count > MaxCachedFrames)
                _cache.Remove(_cacheOrder.Dequeue());
        }
    }

    private IntPtr Open()
    {
        var handle = MpvNative.mpv_create();
        if (handle == IntPtr.Zero)
            return IntPtr.Zero;

        foreach (var (name, value) in new[]
                 {
                     ("config", "no"), ("terminal", "no"), ("msg-level", "all=no"), ("load-scripts", "no"),
                     ("ytdl", "no"), ("osc", "no"), ("input-default-bindings", "no"),
                     // No window and no sound: frames are decoded and kept for the screenshot only.
                     ("vo", "null"), ("ao", "null"), ("aid", "no"), ("sid", "no"), ("sub-auto", "no"),
                     ("pause", "yes"), ("idle", "yes"), ("keep-open", "always"),
                     ("hwdec", "auto-copy"), ("hr-seek", "no"),
                     ("vf", $"scale=w={_pictureWidth}:h=-2"),
                     ("screenshot-format", "jpg"), ("screenshot-jpeg-quality", "80"),
                     ("demuxer-max-bytes", "8MiB"), ("demuxer-readahead-secs", "1")
                 })
            MpvNative.mpv_set_option_string(handle, name, value);

        if (MpvNative.mpv_initialize(handle) < 0
            || Command(handle, "loadfile", _source, "replace") < 0
            || !WaitFor(handle, FileLoadedEvent, OpenTimeout))
        {
            MpvNative.mpv_terminate_destroy(handle);
            return IntPtr.Zero;
        }
        // Let the first frame settle, so its "ready" signal isn't mistaken for the first seek's.
        WaitFor(handle, PlaybackRestartEvent, SeekTimeout);
        return handle;
    }

    private byte[]? Capture(IntPtr handle, double seconds)
    {
        var time = seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        if (Command(handle, "seek", time, "absolute+keyframes") < 0 || !WaitFor(handle, PlaybackRestartEvent, SeekTimeout))
            return null;
        if (Command(handle, "screenshot-to-file", _framePath, "video") < 0)
            return null;
        try
        {
            return File.ReadAllBytes(_framePath);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private bool WaitFor(IntPtr handle, int eventId, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout && !_disposed)
        {
            var eventPointer = MpvNative.mpv_wait_event(handle, 0.1);
            if (eventPointer == IntPtr.Zero)
                continue;

            var id = Marshal.PtrToStructure<MpvEvent>(eventPointer).EventId;
            if (id == eventId)
                return true;
            if (id is ShutdownEvent || (id is EndFileEvent && eventId == FileLoadedEvent))
                return false;
        }
        return false;
    }

    private static int Command(IntPtr handle, params string[] values)
    {
        var arguments = new IntPtr[values.Length + 1];
        try
        {
            for (var index = 0; index < values.Length; index++)
                arguments[index] = Marshal.StringToCoTaskMemUTF8(values[index]);
            return MpvNative.mpv_command(handle, arguments);
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
}
