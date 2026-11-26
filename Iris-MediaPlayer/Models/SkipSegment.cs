using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Iris_MediaPlayer.Models;

public enum SkipKind
{
    Intro,
    Recap,
    Credits,
    Preview
}

/// <summary>
/// A part of a video people usually skip: an opening, a "previously on" recap, the end credits or a
/// preview of the next episode. Comes from a media server (Jellyfin media segments) or from the
/// file's chapter names.
/// </summary>
public sealed record SkipSegment(SkipKind Kind, double Start, double End)
{
    public string ButtonText => Kind switch
    {
        SkipKind.Recap => "Skip recap",
        SkipKind.Credits => "Skip credits",
        SkipKind.Preview => "Skip preview",
        _ => "Skip intro"
    };

    public string SkippedText => Kind switch
    {
        SkipKind.Recap => "Skipped recap",
        SkipKind.Credits => "Skipped credits",
        SkipKind.Preview => "Skipped preview",
        _ => "Skipped intro"
    };

    /// <summary>Too short to be worth a button, or not a real range.</summary>
    public bool IsUsable => End - Start >= 5;

    // Chapter names used by encoders, release groups and Blu-ray authoring for these parts.
    private static readonly Regex IntroName = new(@"^(intro|introduction|opening|opening credits|opening titles|opening theme|op|op\d*|title sequence|main titles?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RecapName = new(@"^(recap|previously|previously on.*|prologue recap)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CreditsName = new(@"^(credits|end credits|closing credits|ending|ending credits|ending theme|ed|ed\d*|outro)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PreviewName = new(@"^(preview|next episode|next episode preview|next time|coming up next)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Finds intros, recaps, credits and previews by chapter name: a chapter called "Opening" runs
    /// until the next chapter starts (or the video ends).
    /// </summary>
    public static IReadOnlyList<SkipSegment> FromChapters(IReadOnlyList<(double Time, string? Title)> chapters, double duration)
    {
        var segments = new List<SkipSegment>();
        for (var index = 0; index < chapters.Count; index++)
        {
            var title = chapters[index].Title?.Trim().TrimEnd('.', ':') ?? string.Empty;
            SkipKind? kind = IntroName.IsMatch(title) ? SkipKind.Intro
                : RecapName.IsMatch(title) ? SkipKind.Recap
                : CreditsName.IsMatch(title) ? SkipKind.Credits
                : PreviewName.IsMatch(title) ? SkipKind.Preview
                : null;
            if (kind is null)
                continue;

            var end = index + 1 < chapters.Count ? chapters[index + 1].Time : duration;
            var segment = new SkipSegment(kind.Value, Math.Max(0, chapters[index].Time), end);
            if (segment.IsUsable)
                segments.Add(segment);
        }
        return segments;
    }
}
