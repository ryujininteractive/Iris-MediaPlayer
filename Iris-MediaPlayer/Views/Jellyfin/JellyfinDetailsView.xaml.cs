using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services;
using Iris_MediaPlayer.Services.Jellyfin;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Iris_MediaPlayer.Views.Jellyfin;

/// <summary>
/// The page for one film or show: backdrop, poster, logo, tagline, ratings, genres, Play/Resume,
/// trailer, watched and favorite, the overview, which version, audio and subtitles to play, credits,
/// seasons with their episodes, and the cast.
/// </summary>
public sealed partial class JellyfinDetailsView : UserControl
{
    private static readonly TimeSpan BackdropDrift = TimeSpan.FromSeconds(26);

    private JellyfinClient? _client;
    private JellyfinItem? _item;
    /// <summary>What was asked for, so "Try again" works even when the first load failed.</summary>
    private JellyfinItem? _requested;
    private IReadOnlyList<JellyfinItem> _seasons = [];
    private IReadOnlyList<JellyfinItem> _episodes = [];
    private JellyfinItem? _nextUp;
    private CancellationTokenSource? _loading;
    private bool _isFillingPickers;
    private bool _audioChosen;
    private bool _subtitleChosen;

    public JellyfinDetailsView()
    {
        InitializeComponent();
        Services.Themes.TrackFades(this);
    }

    /// <summary>Play something from this page; the queue is the season's episodes for shows.</summary>
    public event EventHandler<PlayRequest>? PlayRequested;

    /// <summary>The title shown, for the Back button's label.</summary>
    public string? Title => _item?.Name;

    /// <summary>
    /// Shows <paramref name="item"/>. With <paramref name="quiet"/> (coming back from the player) the
    /// page refreshes in place: same scroll position and season, no entrance animation.
    /// </summary>
    public async Task ShowAsync(JellyfinClient client, JellyfinItem item, bool quiet = false)
    {
        var keepSeason = quiet ? SeasonTabs.SelectedIndex : -1;
        _client = client;
        _requested = item;
        _loading?.Cancel();
        var cancel = _loading = new CancellationTokenSource();
        Message.Visibility = Visibility.Collapsed;
        if (!quiet)
        {
            Body.Visibility = Visibility.Collapsed;
            BackdropImage.Source = null;
            LoadingRing.IsActive = true;
            PageScroller.ChangeView(null, 0, null, disableAnimation: true);
        }
        try
        {
            var full = await client.GetItemAsync(item.Id, cancel.Token);
            if (cancel.IsCancellationRequested)
                return;
            _item = full;
            Fill(full);
            Body.Visibility = Visibility.Visible;
            if (!quiet)
            {
                Motion.EnterFromBelow(PosterColumn, TimeSpan.Zero, 24);
                var delay = 40;
                foreach (var child in Info.Children.Where(child => child.Visibility == Visibility.Visible))
                {
                    Motion.EnterFromBelow(child, TimeSpan.FromMilliseconds(delay), 16);
                    delay += 40;
                }
            }

            if (full.Type == "Series")
                await LoadSeasonsAsync(full, keepSeason, cancel.Token);
        }
        catch (JellyfinException exception) when (!cancel.IsCancellationRequested)
        {
            Message.Show("", "Couldn't load this", exception.Message, "Try again");
            Message.Visibility = Visibility.Visible;
        }
        finally
        {
            if (ReferenceEquals(_loading, cancel))
                LoadingRing.IsActive = false;
        }
    }

    // ---------- Filling the page ----------

    private void Fill(JellyfinItem item)
    {
        var client = _client!;
        BackdropImage.Source = JellyfinUi.Load(client.GetImageUrl(item, JellyfinImage.Backdrop, JellyfinUi.BackdropWidth),
            decodeWidth: JellyfinUi.BackdropWidth);
        PosterImage.Source = JellyfinUi.Load(client.GetImageUrl(item, JellyfinImage.Poster, 560), decodeHeight: 560);
        PosterGlyph.Glyph = item.Glyph;

        var logoUrl = client.GetImageUrl(item, JellyfinImage.Logo, JellyfinUi.LogoWidth);
        LogoImage.Source = JellyfinUi.Load(logoUrl, decodeWidth: JellyfinUi.LogoWidth);
        LogoImage.Visibility = logoUrl is null ? Visibility.Collapsed : Visibility.Visible;
        TitleText.Text = item.Name;
        TitleText.Visibility = logoUrl is null ? Visibility.Visible : Visibility.Collapsed;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LogoImage, item.Name);

        TaglineText.Text = item.Tagline ?? string.Empty;
        TaglineText.Visibility = string.IsNullOrEmpty(item.Tagline) ? Visibility.Collapsed : Visibility.Visible;
        JellyfinUi.FillMeta(MetaPanel, item, withGenres: false, withEndTime: true);
        if (item.Type == "Series" && item.Status is { } status)
            MetaPanel.Children.Add(StatusText(status));

        GenrePanel.Children.Clear();
        foreach (var genre in item.Genres.Take(4))
        {
            GenrePanel.Children.Add(new Border
            {
                Height = 30, Padding = new Thickness(13, 0, 13, 0), CornerRadius = new CornerRadius(15),
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                BorderBrush = (Brush)Application.Current.Resources["IrisStrokeBrush"], BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = genre, FontSize = 13, VerticalAlignment = VerticalAlignment.Center }
            });
        }
        GenrePanel.Visibility = item.Genres.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var started = item.IsPlayable && item.ResumeSeconds > 0;
        PlayText.Text = item.Type == "Series" ? "Play" : started ? $"Resume from {FormatClock(item.ResumeSeconds)}" : "Play";
        StartOverButton.Visibility = started ? Visibility.Visible : Visibility.Collapsed;
        TrailerButton.Visibility = item.TrailerUrl is null ? Visibility.Collapsed : Visibility.Visible;
        ProgressPanel.Visibility = started ? item.ProgressVisibility : Visibility.Collapsed;
        ProgressFill.Width = 300 * item.Progress;
        RemainingText.Text = started ? JellyfinItem.FormatRunTime(item.RunTimeSeconds - item.ResumeSeconds) + " left" : string.Empty;
        UpdateMarks();

        OverviewText.Text = item.Overview ?? string.Empty;
        OverviewText.Visibility = string.IsNullOrEmpty(item.Overview) ? Visibility.Collapsed : Visibility.Visible;

        FillBadges(item);
        FillPickers(item);
        FillCredits(item);
        FillCast(item);
        EpisodesSection.Visibility = item.Type == "Series" ? Visibility.Visible : Visibility.Collapsed;
    }

    private static TextBlock StatusText(string status) => new()
    {
        Text = status == "Continuing" ? "● Continuing" : "Ended",
        FontSize = 14,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = status == "Continuing"
            ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x5B, 0xD6, 0x9A))
            : (Brush)Application.Current.Resources["IrisTextTertiaryBrush"]
    };

    private void FillBadges(JellyfinItem item)
    {
        Badges.Children.Clear();
        var source = item.MediaSources.FirstOrDefault();
        if (source is null)
            return;
        if (source.Video is { } video)
        {
            if (video.ResolutionLabel is { Length: > 0 } resolution and not "SD")
                Badges.Children.Add(JellyfinUi.Badge(resolution == "4K" ? "4K" : resolution.ToUpperInvariant()));
            if (video.RangeLabel.Length > 0)
                Badges.Children.Add(JellyfinUi.Badge(video.RangeLabel == "Dolby Vision" ? "DOLBY VISION" : video.RangeLabel.ToUpperInvariant()));
        }
        if (source.AudioStreams.Any(stream => stream.Label.Contains("Atmos", StringComparison.OrdinalIgnoreCase)))
            Badges.Children.Add(JellyfinUi.Badge("ATMOS"));
        if (source.SubtitleStreams.Any())
            Badges.Children.Add(JellyfinUi.Badge("CC"));
    }

    private void FillPickers(JellyfinItem item)
    {
        _isFillingPickers = true;
        _audioChosen = _subtitleChosen = false;
        Pickers.Visibility = item.IsPlayable && item.MediaSources.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        VersionBox.ItemsSource = item.MediaSources.Select(source => source.Label).ToList();
        VersionBox.SelectedIndex = item.MediaSources.Count > 0 ? 0 : -1;
        VersionPicker.Visibility = item.MediaSources.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        FillTrackPickers();
        _isFillingPickers = false;
    }

    private JellyfinMediaSource? SelectedSource
        => _item is { } item && VersionBox.SelectedIndex >= 0 && VersionBox.SelectedIndex < item.MediaSources.Count
            ? item.MediaSources[VersionBox.SelectedIndex] : null;

    /// <summary>Audio and subtitle lists for the chosen version. External subtitle files aren't offered: direct play can't load them yet.</summary>
    private void FillTrackPickers()
    {
        var source = SelectedSource;
        var audio = source?.AudioStreams.ToList() ?? [];
        AudioBox.ItemsSource = audio;
        var defaultAudio = audio.FindIndex(stream => stream.Index == source?.DefaultAudioIndex);
        if (defaultAudio < 0)
            defaultAudio = Math.Max(0, audio.FindIndex(stream => stream.IsDefault));
        AudioBox.SelectedIndex = audio.Count > 0 ? defaultAudio : -1;
        AudioBox.IsEnabled = audio.Count > 1;

        var subtitles = new List<object> { "Off" };
        subtitles.AddRange(source?.SubtitleStreams.Where(stream => !stream.IsExternal) ?? []);
        SubtitleBox.ItemsSource = subtitles;
        var defaultSubtitle = subtitles.FindIndex(entry => entry is JellyfinStream stream && stream.Index == source?.DefaultSubtitleIndex);
        SubtitleBox.SelectedIndex = defaultSubtitle > 0 ? defaultSubtitle : 0;
        SubtitleBox.IsEnabled = subtitles.Count > 1;
    }

    private void FillCredits(JellyfinItem item)
    {
        Credits.Children.Clear();
        AddCredit("Director", item.People.Where(person => person.Type == "Director").Select(person => person.Name));
        AddCredit("Writers", item.People.Where(person => person.Type == "Writer").Select(person => person.Name));
        AddCredit(item.Type == "Series" ? "Network" : "Studio", item.Studios);
        Credits.Visibility = Credits.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddCredit(string label, IEnumerable<string> names)
    {
        var list = names.Distinct().Take(3).ToList();
        if (list.Count == 0)
            return;
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = (Brush)Application.Current.Resources["IrisTextTertiaryBrush"] });
        panel.Children.Add(new TextBlock { Text = string.Join(", ", list), FontSize = 13 });
        Credits.Children.Add(panel);
    }

    private void FillCast(JellyfinItem item)
    {
        var cast = item.People.Where(person => person.Type is "Actor" or "GuestStar").Take(30).ToList();
        foreach (var person in cast)
            person.Image = JellyfinUi.Load(_client!.GetPersonImageUrl(person, JellyfinUi.FaceHeight), decodeHeight: JellyfinUi.FaceHeight);
        CastItems.ItemsSource = cast;
        CastSection.Visibility = cast.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMarks()
    {
        if (_item is not { } item)
            return;
        var accent = (Brush)Application.Current.Resources["IrisAccentLightBrush"];
        var normal = (Brush)Application.Current.Resources["IrisTextBrush"];
        WatchedGlyph.Foreground = item.IsPlayed ? accent : normal;
        ToolTipService.SetToolTip(WatchedButton, item.IsPlayed ? "Mark as unwatched" : "Mark as watched");
        FavoriteGlyph.Glyph = item.IsFavorite ? "" : "";
        FavoriteGlyph.Foreground = item.IsFavorite ? accent : normal;
        ToolTipService.SetToolTip(FavoriteButton, item.IsFavorite ? "Remove from favorites" : "Add to favorites");
    }

    private static string FormatClock(double seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    // ---------- Shows ----------

    private async Task LoadSeasonsAsync(JellyfinItem series, int keepSeason, CancellationToken cancel)
    {
        var client = _client!;
        var seasonsTask = client.GetSeasonsAsync(series.Id, cancel);
        var nextUpTask = client.GetNextUpAsync(1, series.Id, cancel);
        _seasons = await seasonsTask;
        try
        {
            _nextUp = (await nextUpTask).FirstOrDefault();
        }
        catch (JellyfinException)
        {
            _nextUp = null;
        }
        if (cancel.IsCancellationRequested)
            return;

        PlayText.Text = _nextUp is { } next ? $"{(next.ResumeSeconds > 0 ? "Resume" : "Play")} {next.EpisodeCode}".Trim() : "Play";
        SeasonTabs.Items = _seasons.Select(season => season.Name).ToList();
        var start = keepSeason >= 0 && keepSeason < _seasons.Count ? keepSeason
            : _nextUp?.SeasonId is { } nextSeason ? Math.Max(0, _seasons.ToList().FindIndex(season => season.Id == nextSeason)) : 0;
        if (_seasons.Count > 0)
        {
            SeasonTabs.SelectedIndex = start;
            await LoadEpisodesAsync(start);
        }
    }

    private async void SeasonTabs_SelectionChanged(object? sender, int index) => await LoadEpisodesAsync(index);

    private async Task LoadEpisodesAsync(int seasonIndex)
    {
        if (_client is null || _item is null || seasonIndex < 0 || seasonIndex >= _seasons.Count)
            return;
        var season = _seasons[seasonIndex];
        EpisodesRing.IsActive = true;
        EpisodeItems.ItemsSource = null;
        try
        {
            var episodes = await _client.GetEpisodesAsync(_item.Id, season.Id);
            if (SeasonTabs.SelectedIndex != seasonIndex)
                return;
            JellyfinUi.LoadCardImages(_client, episodes, wide: true);
            _episodes = episodes;
            EpisodeItems.ItemsSource = episodes;
            var left = episodes.Count(episode => !episode.IsPlayed);
            SeasonSummary.Text = $"{episodes.Count} episode{(episodes.Count == 1 ? string.Empty : "s")}"
                                 + (left == 0 ? " · all watched" : left == episodes.Count ? string.Empty : $" · {left} left");
        }
        catch (JellyfinException exception)
        {
            SeasonSummary.Text = exception.Message;
        }
        finally
        {
            EpisodesRing.IsActive = false;
        }
    }

    private void Episodes_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        => Motion.EnterFromBelow(args.Element, TimeSpan.FromMilliseconds(Math.Min(args.Index, 10) * 40), 14);

    private void Episode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: JellyfinItem episode })
            PlayEpisode(episode, _episodes);
    }

    private void PlayEpisode(JellyfinItem episode, IReadOnlyList<JellyfinItem> season)
    {
        if (_client is null)
            return;
        var queue = season.Count > 0 ? season.Select(entry => JellyfinUi.ToMediaItem(_client, entry)).ToList() : [];
        var media = queue.FirstOrDefault(entry => entry.FilePath == JellyfinUi.Key(episode)) ?? JellyfinUi.ToMediaItem(_client, episode);
        PlayRequested?.Invoke(this, new PlayRequest(media, queue.Count > 0 ? queue : [media]));
    }

    // ---------- Actions ----------

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_item is not { } item || _client is null)
            return;
        if (item.Type == "Series")
        {
            var episode = _nextUp;
            if (episode is null && _seasons.Count > 0)
                episode = (await _client.GetEpisodesAsync(item.Id, _seasons[0].Id)).FirstOrDefault();
            if (episode is null)
                return;
            var season = episode.SeasonId is { } seasonId ? await _client.GetEpisodesAsync(item.Id, seasonId) : [];
            JellyfinUi.LoadCardImages(_client, season, wide: true);
            PlayEpisode(season.FirstOrDefault(entry => entry.Id == episode.Id) ?? episode, season);
            return;
        }
        PlayItem(item, fromStart: false);
    }

    private void StartOver_Click(object sender, RoutedEventArgs e)
    {
        if (_item is { } item)
            PlayItem(item, fromStart: true);
    }

    private void PlayItem(JellyfinItem item, bool fromStart)
    {
        if (_client is null)
            return;
        var source = SelectedSource;
        long? audio = _audioChosen && AudioBox.SelectedItem is JellyfinStream audioStream ? source?.GetMpvTrackId(audioStream) : null;
        long? subtitle = !_subtitleChosen ? null
            : SubtitleBox.SelectedItem is JellyfinStream subtitleStream ? source?.GetMpvTrackId(subtitleStream) : -1;
        var resume = item.ResumeSeconds;
        if (fromStart)
            item.ResumeSeconds = 0;
        item.Image ??= PosterImage.Source;
        var media = JellyfinUi.ToMediaItem(_client, item, null, source, audio, subtitle);
        item.ResumeSeconds = resume;
        PlayRequested?.Invoke(this, new PlayRequest(media, [media]));
    }

    private async void Trailer_Click(object sender, RoutedEventArgs e)
    {
        if (_item?.TrailerUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            await Windows.System.Launcher.LaunchUriAsync(uri);
    }

    private async void Watched_Click(object sender, RoutedEventArgs e)
    {
        if (_item is not { } item || _client is null)
            return;
        item.IsPlayed = !item.IsPlayed;
        UpdateMarks();
        Motion.Pulse(WatchedGlyph);
        try
        {
            await _client.SetPlayedAsync(item.Id, item.IsPlayed);
            if (item.Type == "Series" && SeasonTabs.SelectedIndex >= 0)
                await LoadEpisodesAsync(SeasonTabs.SelectedIndex);
        }
        catch (JellyfinException)
        {
            item.IsPlayed = !item.IsPlayed;
            UpdateMarks();
        }
    }

    private async void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (_item is not { } item || _client is null)
            return;
        item.IsFavorite = !item.IsFavorite;
        UpdateMarks();
        Motion.Pulse(FavoriteGlyph);
        try
        {
            await _client.SetFavoriteAsync(item.Id, item.IsFavorite);
        }
        catch (JellyfinException)
        {
            item.IsFavorite = !item.IsFavorite;
            UpdateMarks();
        }
    }

    private void Version_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isFillingPickers)
            return;
        _isFillingPickers = true;
        FillTrackPickers();
        _isFillingPickers = false;
    }

    private void Audio_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isFillingPickers)
            _audioChosen = true;
    }

    private void Subtitle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isFillingPickers)
            _subtitleChosen = true;
    }

    private async void Message_ActionClick(object? sender, EventArgs e)
    {
        if (_client is not null && _requested is not null)
            await ShowAsync(_client, _requested);
    }

    // ---------- Backdrop ----------

    private void Backdrop_SizeChanged(object sender, SizeChangedEventArgs e)
        => Backdrop.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };

    private void BackdropImage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0)
            Motion.StartDrift(BackdropImage, 1.03f, 1.1f, BackdropDrift, new Vector2(0.7f, 0.35f));
    }
}
