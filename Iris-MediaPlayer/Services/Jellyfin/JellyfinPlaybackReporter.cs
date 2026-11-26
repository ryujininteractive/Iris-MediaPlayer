using Iris_MediaPlayer.Models;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Iris_MediaPlayer.Services.Jellyfin;

/// <summary>
/// Reports one Jellyfin video's playback to the server (Sessions/Playing, …/Progress, …/Stopped), the
/// way Jellyfin's own apps do. The server then keeps the resume point, shows the video in Continue
/// watching on every device, and marks it watched near the end.
/// </summary>
public sealed class JellyfinPlaybackReporter(JellyfinClient client, string itemId, string mediaSourceId, int? audioIndex, int? subtitleIndex)
    : IRemotePlayback
{
    private static Task _pending = Task.CompletedTask;
    private static readonly object Gate = new();
    private readonly string _playSessionId = Guid.NewGuid().ToString("N");

    /// <summary>Finishes once every report sent so far has been answered (used before reloading pages).</summary>
    public static Task WhenSentAsync()
    {
        lock (Gate)
            return _pending;
    }

    public void ReportStarted(double positionSeconds, bool isPaused) => Send("/Sessions/Playing", positionSeconds, isPaused);

    public void ReportProgress(double positionSeconds, bool isPaused) => Send("/Sessions/Playing/Progress", positionSeconds, isPaused);

    public void ReportStopped(double positionSeconds) => Send("/Sessions/Playing/Stopped", positionSeconds, isPaused: false);

    public Task<System.Collections.Generic.IReadOnlyList<SkipSegment>> GetSkipSegmentsAsync() => client.GetSkipSegmentsAsync(itemId);

    private void Send(string path, double positionSeconds, bool isPaused)
    {
        var body = BuildBody(positionSeconds, isPaused);
        lock (Gate)
        {
            // One after another, so the server never sees progress before the start.
            _pending = _pending.ContinueWith(async _ =>
            {
                try
                {
                    await client.PostAsync(path, body);
                }
                catch (JellyfinException)
                {
                }
            }, TaskScheduler.Default).Unwrap();
        }
    }

    private string BuildBody(double positionSeconds, bool isPaused)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("ItemId", itemId);
            json.WriteString("MediaSourceId", mediaSourceId);
            json.WriteString("PlaySessionId", _playSessionId);
            json.WriteNumber("PositionTicks", (long)Math.Max(0, positionSeconds * 10_000_000));
            json.WriteBoolean("IsPaused", isPaused);
            json.WriteBoolean("CanSeek", true);
            json.WriteString("PlayMethod", "DirectPlay");
            if (audioIndex is { } audio)
                json.WriteNumber("AudioStreamIndex", audio);
            if (subtitleIndex is { } subtitle)
                json.WriteNumber("SubtitleStreamIndex", subtitle);
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
