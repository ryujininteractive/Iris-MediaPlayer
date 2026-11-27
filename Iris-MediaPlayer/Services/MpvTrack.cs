using System.Linq;

namespace Iris_MediaPlayer.Services;

/// <param name="ExternalFile">For a track loaded from its own file (an added subtitle): that file's path.</param>
internal sealed record MpvTrack(long Id, string Type, string? Title, string? Language, bool IsSelected, string? ExternalFile = null)
{
    public string DisplayName
    {
        get
        {
            var details = string.Join(" · ", new[] { Language, Title }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct());
            return string.IsNullOrWhiteSpace(details) ? $"Track {Id}" : details;
        }
    }
}
