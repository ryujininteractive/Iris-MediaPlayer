using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Iris_MediaPlayer.Services;

/// <summary>The audio and subtitle tracks the user picked for one video.</summary>
public sealed class TrackChoice
{
    /// <summary>mpv audio track id; null keeps mpv's own choice.</summary>
    public long? Audio { get; set; }

    /// <summary>mpv subtitle track id, -1 for subtitles off; null keeps mpv's own choice.</summary>
    public long? Subtitle { get; set; }

    /// <summary>A subtitle file the user added, loaded again next time.</summary>
    public string? SubtitleFile { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// Remembers the audio and subtitle tracks chosen for each video, so reopening it doesn't mean
/// choosing them again. Kept apart from the resume points, which are dropped once a video is watched.
/// </summary>
internal static class TrackChoiceStore
{
    /// <summary>Oldest choices are forgotten past this many videos.</summary>
    private const int MaximumEntries = 3000;

    private static readonly object Gate = new();

    public static TrackChoice? Get(string key)
    {
        lock (Gate)
            return Load().TryGetValue(key, out var choice) ? choice : null;
    }

    public static void SetAudio(string key, long trackId) => Update(key, choice => choice.Audio = trackId);

    /// <param name="file">The added subtitle file the track came from, or null for one inside the video.</param>
    public static void SetSubtitle(string key, long trackId, string? file) => Update(key, choice =>
    {
        choice.Subtitle = trackId;
        choice.SubtitleFile = trackId >= 0 ? file : null;
    });

    public static void Remove(string key)
    {
        lock (Gate)
        {
            var entries = Load();
            if (entries.Remove(key))
                Persist(entries);
        }
    }

    private static void Update(string key, Action<TrackChoice> change)
    {
        if (string.IsNullOrEmpty(key))
            return;

        lock (Gate)
        {
            // The library and a standalone player window can run as separate processes, so start from disk.
            var entries = Load();
            if (!entries.TryGetValue(key, out var choice))
                entries[key] = choice = new TrackChoice();
            change(choice);
            choice.UpdatedUtc = DateTime.UtcNow;

            if (entries.Count > MaximumEntries)
            {
                foreach (var old in entries.OrderBy(pair => pair.Value.UpdatedUtc).Take(entries.Count - MaximumEntries).ToList())
                    entries.Remove(old.Key);
            }
            Persist(entries);
        }
    }

    private static Dictionary<string, TrackChoice> Load()
    {
        var entries = new Dictionary<string, TrackChoice>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = GetFilePath();
            if (File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), IrisJsonContext.Default.DictionaryStringTrackChoice) is { } saved)
            {
                foreach (var (key, choice) in saved)
                    entries[key] = choice;
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return entries;
    }

    private static void Persist(Dictionary<string, TrackChoice> entries)
    {
        try
        {
            File.WriteAllText(GetFilePath(), JsonSerializer.Serialize(entries, IrisJsonContext.Default.DictionaryStringTrackChoice));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string GetFilePath() => AppStorage.GetFilePath("track-choices.json");
}
