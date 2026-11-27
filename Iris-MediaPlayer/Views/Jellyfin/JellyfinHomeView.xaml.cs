using Iris_MediaPlayer.Services;
using Iris_MediaPlayer.Services.Jellyfin;
using Microsoft.UI.Dispatching;
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
/// Jellyfin's Home: a hero for what to watch next, then rows for Continue watching, Next up and the
/// newest films and shows in each library. Cards and buttons raise events; the Jellyfin page decides
/// whether that opens a details page, a library or the player.
/// </summary>
public sealed partial class JellyfinHomeView : UserControl
{
    private const int MaxFeatured = 5;
    private static readonly TimeSpan HeroInterval = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan HeroCrossfade = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan HeroDrift = TimeSpan.FromSeconds(24);

    private readonly DispatcherQueueTimer _heroTimer;
    private JellyfinClient? _client;
    private List<JellyfinItem> _featured = [];
    private int _featuredIndex;
    private bool _frontIsA = true;

    public JellyfinHomeView()
    {
        InitializeComponent();
        Services.Themes.TrackFades(this);
        _heroTimer = DispatcherQueue.GetForCurrentThread()!.CreateTimer();
        _heroTimer.Interval = HeroInterval;
        _heroTimer.Tick += (_, _) => ShowFeatured((_featuredIndex + 1) % Math.Max(1, _featured.Count), animate: true);
        Motion.SetOpacity(BackdropLayerB, 0);
        Unloaded += (_, _) => _heroTimer.Stop();
    }

    /// <summary>A card was clicked (film, show, episode…).</summary>
    public event EventHandler<JellyfinItem>? ItemInvoked;

    /// <summary>The hero's Play button.</summary>
    public event EventHandler<JellyfinItem>? PlayRequested;

    /// <summary>The hero's "More info": the details page for this film or show.</summary>
    public event EventHandler<JellyfinItem>? DetailsRequested;

    /// <summary>"See all" on a library's row.</summary>
    public event EventHandler<JellyfinItem>? LibraryRequested;

    /// <summary>Whether the page is on screen; the hero only rotates while it is.</summary>
    public bool IsActive
    {
        set
        {
            if (value && _featured.Count > 1)
                _heroTimer.Start();
            else
                _heroTimer.Stop();
        }
    }

    public async Task LoadAsync(JellyfinClient client, IReadOnlyList<JellyfinItem> libraries, CancellationToken cancel)
    {
        _client = client;
        ErrorText.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = Rows.Children.Count == 0;
        try
        {
            var resumeTask = client.GetResumeAsync(16, cancel);
            var nextUpTask = libraries.Any(library => library.CollectionType == "tvshows") ? client.GetNextUpAsync(16, null, cancel) : null;
            var latestTasks = libraries
                .Where(library => library.CollectionType is "movies" or "tvshows" or "homevideos" or null)
                .Select(library => (Library: library, Task: client.GetLatestAsync(library.Id, 16, cancel)))
                .ToList();

            var resume = await resumeTask;
            var nextUp = nextUpTask is null ? [] : await nextUpTask;
            var latest = new List<(JellyfinItem Library, IReadOnlyList<JellyfinItem> Items)>();
            foreach (var (library, task) in latestTasks)
                latest.Add((library, await task));
            if (cancel.IsCancellationRequested)
                return;

            BuildRows(resume, nextUp, latest);
            BuildHero(resume, latest.SelectMany(entry => entry.Items));
        }
        catch (JellyfinException exception) when (!cancel.IsCancellationRequested)
        {
            ErrorText.Text = exception.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    // ---------- Rows ----------

    private void BuildRows(IReadOnlyList<JellyfinItem> resume, IReadOnlyList<JellyfinItem> nextUp,
        List<(JellyfinItem Library, IReadOnlyList<JellyfinItem> Items)> latest)
    {
        Rows.Children.Clear();
        AddRow("Continue watching", resume, wide: true, library: null);
        // Next up leaves out what's already in Continue watching.
        AddRow("Next up", nextUp.Where(item => resume.All(started => started.Id != item.Id)).ToList(), wide: true, library: null);
        foreach (var (library, items) in latest)
            AddRow(library.CollectionType == "tvshows" ? $"New in {library.Name}" : $"Latest {library.Name.ToLowerInvariant()}", items,
                wide: library.CollectionType == "homevideos", library);
    }

    private void AddRow(string title, IReadOnlyList<JellyfinItem> items, bool wide, JellyfinItem? library)
    {
        if (items.Count == 0 || _client is null)
            return;
        JellyfinUi.LoadCardImages(_client, items, wide);

        var scroller = new ScrollViewer
        {
            Padding = new Thickness(wide ? 48 : 50, 0, 40, 0),
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var repeater = new ItemsRepeater
        {
            ItemsSource = items,
            ItemTemplate = (DataTemplate)Resources[wide ? "WideTemplate" : "PosterTemplate"],
            Layout = new StackLayout { Orientation = Orientation.Horizontal, Spacing = wide ? 4 : 2 }
        };
        repeater.ElementPrepared += (_, args) => Motion.EnterFromBelow(args.Element, TimeSpan.FromMilliseconds(60 + Math.Min(args.Index, 8) * 45), 16);
        scroller.Content = repeater;

        var header = new Grid { Margin = new Thickness(56, 0, 40, 0) };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["IrisSectionTitleTextStyle"] });
        if (library is not null)
        {
            var seeAll = new HyperlinkButton { Content = "See all", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 2, 0, 0) };
            seeAll.Click += (_, _) => LibraryRequested?.Invoke(this, library);
            heading.Children.Add(seeAll);
        }
        header.Children.Add(heading);

        var arrows = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        arrows.Children.Add(ArrowButton("", "Scroll left", () => scroller.ChangeView(scroller.HorizontalOffset - scroller.ViewportWidth * 0.8, null, null)));
        arrows.Children.Add(ArrowButton("", "Scroll right", () => scroller.ChangeView(scroller.HorizontalOffset + scroller.ViewportWidth * 0.8, null, null)));
        header.Children.Add(arrows);

        var row = new StackPanel { Spacing = 6 };
        row.Children.Add(header);
        row.Children.Add(scroller);
        Rows.Children.Add(row);
    }

    private static Button ArrowButton(string glyph, string name, Action click)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["IrisOutlineIconButtonStyle"],
            Content = new FontIcon { Glyph = glyph, FontSize = 12 }
        };
        ToolTipService.SetToolTip(button, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: JellyfinItem item })
            ItemInvoked?.Invoke(this, item);
    }

    // ---------- Hero ----------

    private void BuildHero(IReadOnlyList<JellyfinItem> resume, IEnumerable<JellyfinItem> latest)
    {
        if (_client is null)
            return;
        _featured = resume.Concat(latest)
            .Where(item => _client.GetImageUrl(item, JellyfinImage.Backdrop, JellyfinUi.BackdropWidth) is not null)
            .GroupBy(item => item.SeriesId ?? item.Id)
            .Select(group => group.First())
            .Take(MaxFeatured)
            .ToList();

        Hero.Visibility = _featured.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Rows.Margin = new Thickness(0, _featured.Count > 0 ? -44 : 96, 0, 0);
        BuildDots();
        if (_featured.Count > 0)
            ShowFeatured(0, animate: false);
        IsActive = true;
    }

    private void ShowFeatured(int index, bool animate)
    {
        if (_client is null || index < 0 || index >= _featured.Count)
            return;
        _featuredIndex = index;
        var item = _featured[index];

        var incomingLayer = animate ? (_frontIsA ? BackdropLayerB : BackdropLayerA) : (_frontIsA ? BackdropLayerA : BackdropLayerB);
        var incomingImage = incomingLayer == BackdropLayerA ? BackdropImageA : BackdropImageB;
        var outgoingLayer = incomingLayer == BackdropLayerA ? BackdropLayerB : BackdropLayerA;
        incomingImage.Source = JellyfinUi.Load(_client.GetImageUrl(item, JellyfinImage.Backdrop, JellyfinUi.BackdropWidth), decodeWidth: JellyfinUi.BackdropWidth);
        if (animate)
        {
            _frontIsA = incomingLayer == BackdropLayerA;
            Motion.Fade(incomingLayer, 1, HeroCrossfade);
            Motion.Fade(outgoingLayer, 0, HeroCrossfade);
        }
        else
        {
            Motion.SetOpacity(incomingLayer, 1);
            Motion.SetOpacity(outgoingLayer, 0);
        }

        UpdateHeroText(item);
        if (animate)
        {
            var delay = 0;
            foreach (var child in HeroText.Children.Where(child => child.Visibility == Visibility.Visible))
            {
                Motion.EnterFromBelow(child, TimeSpan.FromMilliseconds(delay), 16);
                delay += 60;
            }
        }
        UpdateDots();
    }

    private void UpdateHeroText(JellyfinItem item)
    {
        var started = item.ResumeSeconds > 0;
        HeroEyebrow.Text = (started ? "CONTINUE WATCHING" : item.Type == "Series" ? "NEW SHOW" : "NEW ON YOUR SERVER")
                           + (item.Type == "Episode" && item.EpisodeCode.Length > 0 ? $" · {item.EpisodeCode}" : string.Empty);

        var logoUrl = _client?.GetImageUrl(item, JellyfinImage.Logo, JellyfinUi.LogoWidth);
        HeroLogo.Source = JellyfinUi.Load(logoUrl, decodeWidth: JellyfinUi.LogoWidth);
        HeroLogo.Visibility = logoUrl is null ? Visibility.Collapsed : Visibility.Visible;
        HeroTitle.Text = item.Type == "Episode" ? item.SeriesName ?? item.Name : item.Name;
        HeroTitle.Visibility = logoUrl is null ? Visibility.Visible : Visibility.Collapsed;

        JellyfinUi.FillMeta(HeroMeta, item, withGenres: true, withEndTime: false);
        HeroOverview.Text = item.Overview ?? string.Empty;
        HeroOverview.Visibility = string.IsNullOrEmpty(item.Overview) ? Visibility.Collapsed : Visibility.Visible;
        HeroPlayText.Text = started ? "Resume" : item.Type == "Series" ? "Open" : "Play";
        HeroProgressPanel.Visibility = item.ProgressVisibility;
        HeroProgressFill.Width = 240 * item.Progress;
        HeroRemaining.Text = started ? JellyfinItem.FormatRunTime(item.RunTimeSeconds - item.ResumeSeconds) + " left" : string.Empty;
        UpdateFavoriteGlyph(item);
    }

    private void UpdateFavoriteGlyph(JellyfinItem item)
    {
        HeroFavoriteGlyph.Glyph = item.IsFavorite ? "" : "";
        HeroFavoriteGlyph.Foreground = item.IsFavorite
            ? (Brush)Application.Current.Resources["IrisAccentLightBrush"]
            : (Brush)Application.Current.Resources["IrisTextBrush"];
    }

    private void BuildDots()
    {
        HeroDots.Children.Clear();
        if (_featured.Count < 2)
            return;
        for (var index = 0; index < _featured.Count; index++)
        {
            var dot = new Button
            {
                Width = 8, Height = 8, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0, CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(0), Tag = index,
                Style = (Style)Application.Current.Resources["IrisButtonBaseStyle"]
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dot, $"Show featured title {index + 1}");
            dot.Click += (sender, _) =>
            {
                ShowFeatured((int)((Button)sender).Tag, animate: true);
                if (_featured.Count > 1) { _heroTimer.Stop(); _heroTimer.Start(); }
            };
            HeroDots.Children.Add(dot);
        }
        UpdateDots();
    }

    private void UpdateDots()
    {
        for (var index = 0; index < HeroDots.Children.Count; index++)
        {
            if (HeroDots.Children[index] is not Button dot)
                continue;
            var on = index == _featuredIndex;
            dot.Width = on ? 28 : 8;
            dot.Background = new SolidColorBrush(on
                ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF4, 0xF4, 0xF6)
                : Microsoft.UI.ColorHelper.FromArgb(0x47, 0xFF, 0xFF, 0xFF));
        }
    }

    private JellyfinItem? CurrentFeatured => _featuredIndex >= 0 && _featuredIndex < _featured.Count ? _featured[_featuredIndex] : null;

    private void HeroPlay_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentFeatured is not { } item)
            return;
        if (item.IsPlayable)
            PlayRequested?.Invoke(this, item);
        else
            ItemInvoked?.Invoke(this, item);
    }

    private void HeroMore_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentFeatured is not { } item)
            return;
        // An episode's "More info" is its show.
        DetailsRequested?.Invoke(this, item.Type == "Episode" && item.SeriesId is { } seriesId
            ? new JellyfinItem { Id = seriesId, Name = item.SeriesName ?? item.Name, Type = "Series", IsFolder = true }
            : item);
    }

    private async void HeroFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentFeatured is not { } item || _client is null)
            return;
        item.IsFavorite = !item.IsFavorite;
        UpdateFavoriteGlyph(item);
        Motion.Pulse(HeroFavoriteGlyph);
        try
        {
            await _client.SetFavoriteAsync(item.Id, item.IsFavorite);
        }
        catch (JellyfinException)
        {
            item.IsFavorite = !item.IsFavorite;
            UpdateFavoriteGlyph(item);
        }
    }

    private void Hero_SizeChanged(object sender, SizeChangedEventArgs e)
        => Hero.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };

    private void Backdrop_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Image image && e.NewSize.Width > 0)
            Motion.StartDrift(image, 1.04f, 1.12f, HeroDrift, new Vector2(0.7f, 0.35f));
    }
}
