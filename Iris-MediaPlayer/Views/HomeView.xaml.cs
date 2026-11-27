using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services;
using Iris_MediaPlayer.Views.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Iris_MediaPlayer.Views;

/// <summary>
/// Home page: a hero that rotates through up to three featured videos (the ones to resume first,
/// then the newest), then rows for Continue watching, Recently added and Recently played.
/// </summary>
public sealed partial class HomeView : UserControl
{
    private const int RowSize = 16;
    private const int MaxFeatured = 3;
    private static readonly TimeSpan HeroInterval = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan HeroCrossfade = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan HeroDrift = TimeSpan.FromSeconds(22);

    private readonly DispatcherQueueTimer _heroTimer;
    private readonly List<Button> _dots = [];
    private LibraryStore? _store;
    private VideoMenu? _menu;
    private List<Featured> _featured = [];
    private int _featuredIndex;
    private bool _frontIsA = true;
    private bool _hasScanned;
    private bool _isActive = true;
    private bool _animateRows = true;
    private int _heroVersion;

    public HomeView()
    {
        InitializeComponent();
        Services.Themes.TrackFades(this);
        _heroTimer = DispatcherQueue.GetForCurrentThread()!.CreateTimer();
        _heroTimer.Interval = HeroInterval;
        _heroTimer.Tick += (_, _) => ShowFeatured((_featuredIndex + 1) % Math.Max(1, _featured.Count), animate: true);
        Motion.SetOpacity(BackdropLayerB, 0);
    }

    public event EventHandler<PlayRequest>? PlayRequested;
    public event EventHandler? OpenVideoRequested;
    public event EventHandler? AddFolderRequested;

    /// <summary>The hero's Open folder button: show this video's folder in the library.</summary>
    public event EventHandler<MediaItem>? OpenFolderRequested;

    /// <summary>False while another section is shown, so the hero stops rotating in the background.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            UpdateHeroTimer();
        }
    }

    public void Initialize(LibraryStore store)
    {
        _store = store;
        _menu = new VideoMenu(store, this);
        _menu.PlayRequested += (_, request) => PlayRequested?.Invoke(this, request);
        store.LibraryChanged += () =>
        {
            _hasScanned = true;
            Refresh();
        };
        store.RecentMediaChanged += Refresh;
        store.ContinueWatchingChanged += Refresh;
        // "Hide Continue watching" in Settings takes effect straight away.
        AppSettings.Changed += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        if (_store is null)
            return;

        // Hidden for good in Settings: no row and no "Continue watching" hero slides. Progress is still saved.
        IReadOnlyList<MediaItem> continueWatching = AppSettings.HideContinueWatching ? [] : _store.ContinueWatching;
        var recentlyAdded = _store.GetRecentlyAdded(RowSize);
        IReadOnlyList<MediaItem> recentlyPlayed = AppSettings.HideRecentlyPlayed ? [] : _store.RecentMedia;

        ShowRow(ContinueSection, ContinueItems, continueWatching);
        ShowRow(AddedSection, AddedItems, recentlyAdded);
        ShowRow(RecentSection, RecentItems, recentlyPlayed);

        UpdateFeatured(BuildFeatured(continueWatching, recentlyAdded, recentlyPlayed));

        // Cards only fade up the first time the rows fill, not on every progress update.
        if (_animateRows && (continueWatching.Count > 0 || recentlyAdded.Count > 0))
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _animateRows = false);
    }

    private static void ShowRow(FrameworkElement section, ItemsRepeater items, IReadOnlyList<MediaItem> media)
    {
        items.ItemsSource = media;
        section.Visibility = media.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RowItems_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (_animateRows)
            Motion.EnterFromBelow(args.Element, TimeSpan.FromMilliseconds(80 + Math.Min(args.Index, 8) * 50), 16);
    }

    // ---------- Hero ----------

    private static List<Featured> BuildFeatured(
        IReadOnlyList<MediaItem> continueWatching,
        IReadOnlyList<MediaItem> recentlyAdded,
        IReadOnlyList<MediaItem> recentlyPlayed)
    {
        var featured = new List<Featured>();
        void Add(IEnumerable<MediaItem> source, string eyebrow, int limit)
        {
            foreach (var media in source)
            {
                if (featured.Count >= limit)
                    return;
                if (featured.All(item => !string.Equals(item.Media.FilePath, media.FilePath, StringComparison.OrdinalIgnoreCase)))
                    featured.Add(new Featured(media, eyebrow, IsResume: media.WatchProgress > 0));
            }
        }

        Add(continueWatching, "CONTINUE WATCHING", 2);
        Add(recentlyAdded, "RECENTLY ADDED", MaxFeatured);
        Add(recentlyPlayed, "RECENTLY PLAYED", MaxFeatured);
        return featured;
    }

    private void UpdateFeatured(List<Featured> featured)
    {
        if (featured.Count == 0)
        {
            _featured = [];
            Hero.Visibility = Visibility.Collapsed;
            UpdateHeroTimer();
            UpdateWelcome();
            return;
        }

        WelcomeState.Visibility = Visibility.Collapsed;
        Hero.Visibility = Visibility.Visible;

        // Progress updates arrive often; only restart the hero when the set of videos changes.
        var sameSet = featured.Count == _featured.Count
                      && featured.Zip(_featured).All(pair => ReferenceEquals(pair.First.Media, pair.Second.Media));
        _featured = featured;
        if (sameSet)
        {
            UpdateHeroText(_featured[_featuredIndex]);
            return;
        }

        BuildDots();
        ShowFeatured(0, animate: false);
        UpdateHeroTimer();
    }

    private void UpdateHeroTimer()
    {
        _heroTimer.Stop();
        if (_isActive && _featured.Count > 1)
            _heroTimer.Start();
    }

    private async void ShowFeatured(int index, bool animate)
    {
        if (index < 0 || index >= _featured.Count)
            return;

        _featuredIndex = index;
        var featured = _featured[index];
        var version = ++_heroVersion;

        UpdateHeroText(featured);
        UpdateDots();
        if (animate)
            AnimateHeroText();

        // Crossfade: the hidden layer gets the new artwork, then the two layers swap.
        var incomingLayer = animate ? (_frontIsA ? BackdropLayerB : BackdropLayerA) : (_frontIsA ? BackdropLayerA : BackdropLayerB);
        var incomingImage = incomingLayer == BackdropLayerA ? BackdropImageA : BackdropImageB;
        var outgoingLayer = incomingLayer == BackdropLayerA ? BackdropLayerB : BackdropLayerA;
        incomingImage.Source = featured.Media.Thumbnail;

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

        var backdrop = await _store!.GetBackdropAsync(featured.Media);
        if (version == _heroVersion && backdrop is not null)
            incomingImage.Source = backdrop;

        // A user who picks a dot gets the full interval before the next change.
        if (animate)
            UpdateHeroTimer();
    }

    private void UpdateHeroText(Featured featured)
    {
        var media = featured.Media;
        HeroEyebrow.Text = featured.Eyebrow;
        HeroTitle.Text = media.Title;

        HeroQuality.Text = media.QualityLabel;
        HeroQualityBadge.Visibility = string.IsNullOrEmpty(media.QualityLabel) ? Visibility.Collapsed : Visibility.Visible;
        var meta = new List<string> { media.FolderName, media.FileFormat };
        if (!string.IsNullOrEmpty(media.DurationLabel))
            meta.Add(media.DurationLabel);
        HeroMeta.Text = string.Join("  ·  ", meta);

        var hasProgress = media.WatchProgress > 0;
        HeroProgressPanel.Visibility = hasProgress ? Visibility.Visible : Visibility.Collapsed;
        HeroProgressFill.Width = 240 * media.WatchProgress;
        HeroRemaining.Text = media.WatchRemaining;
        HeroPlayText.Text = hasProgress ? "Resume" : "Play";

        // Only library videos have a folder to open.
        HeroFolderButton.Visibility = _store?.AllMedia.Contains(media) == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AnimateHeroText()
    {
        var delay = 0;
        foreach (var child in HeroText.Children.Where(child => child.Visibility == Visibility.Visible))
        {
            Motion.EnterFromBelow(child, TimeSpan.FromMilliseconds(delay), 16);
            delay += 60;
        }
    }

    private void BuildDots()
    {
        HeroDots.Children.Clear();
        _dots.Clear();
        if (_featured.Count < 2)
            return;

        for (var index = 0; index < _featured.Count; index++)
        {
            var dot = new Button
            {
                Style = (Style)Application.Current.Resources["IrisButtonBaseStyle"],
                Width = 8,
                Height = 8,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                Tag = index
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dot, $"Show featured video {index + 1}");
            dot.Click += (sender, _) => ShowFeatured((int)((Button)sender).Tag, animate: true);
            HeroDots.Children.Add(dot);
            _dots.Add(dot);
        }
    }

    private void UpdateDots()
    {
        for (var index = 0; index < _dots.Count; index++)
        {
            var isCurrent = index == _featuredIndex;
            _dots[index].Background = isCurrent
                ? (Brush)Application.Current.Resources["IrisTextBrush"]
                : new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x47, 0xFF, 0xFF, 0xFF));
            AnimateWidth(_dots[index], isCurrent ? 28 : 8);
        }
    }

    private static void AnimateWidth(FrameworkElement element, double width)
    {
        var animation = new DoubleAnimation
        {
            To = width,
            Duration = Motion.Slow,
            EnableDependentAnimation = true,
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 }
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Width");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void Hero_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The drifting artwork is larger than the hero; keep it inside.
        Hero.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
    }

    private void Backdrop_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Restart the drift with the new centre point (it zooms toward the upper right, where artwork usually is).
        if (sender is FrameworkElement image && e.NewSize.Width > 0)
            Motion.StartDrift(image, 1.06f, 1.16f, HeroDrift, new Vector2(0.72f, 0.38f));
    }

    private void UpdateWelcome()
    {
        // Avoid flashing "no videos" while the first scan is still running.
        if (_store is null || (!_hasScanned && _store.FolderPaths.Count > 0))
        {
            WelcomeState.Visibility = Visibility.Collapsed;
            return;
        }

        if (_store.FolderPaths.Count == 0)
        {
            WelcomeState.Show("", "Welcome to Iris",
                "Add a folder of videos and Iris will build your library. You can also open a single file to play it right away.",
                "Add folder", "Open video");
        }
        else
        {
            WelcomeState.Show("", "No videos found yet",
                "Iris didn’t find any videos in your folders. Add another folder, or open a single file to play it.",
                "Add folder", "Open video");
        }
        WelcomeState.Visibility = Visibility.Visible;
    }

    private void HeroPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_featuredIndex < _featured.Count)
            PlayRequested?.Invoke(this, new PlayRequest(_featured[_featuredIndex].Media));
    }

    private void HeroFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_featuredIndex < _featured.Count)
            OpenFolderRequested?.Invoke(this, _featured[_featuredIndex].Media);
    }

    private void WelcomeAction_Click(object? sender, EventArgs e) => AddFolderRequested?.Invoke(this, EventArgs.Empty);

    private void WelcomeSecondaryAction_Click(object? sender, EventArgs e) => OpenVideoRequested?.Invoke(this, EventArgs.Empty);

    // ---------- Rows ----------

    private void MediaCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string filePath } && _store?.ResolveMedia(filePath) is { } media)
        {
            MediaCard.PrepareToPlay(sender);
            PlayRequested?.Invoke(this, new PlayRequest(media));
        }
    }

    private void ContinueCard_Click(object sender, RoutedEventArgs e)
    {
        // Use the Continue watching instance so its saved progress is the one resumed.
        if (sender is not Button { Tag: string filePath } || _store is null)
            return;

        var media = _store.ContinueWatching.FirstOrDefault(item =>
                        string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    ?? _store.ResolveMedia(filePath);
        if (media is null)
            return;

        MediaCard.PrepareToPlay(sender);
        PlayRequested?.Invoke(this, new PlayRequest(media));
    }

    /// <summary>Right-click (or the menu key) on a card opens the video menu.</summary>
    private void Card_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not Button { Tag: string filePath } button || _store is null || _menu is null)
            return;

        // Prefer the Continue watching instance so Play resumes from its saved progress.
        var media = _store.ContinueWatching.FirstOrDefault(item =>
                        string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    ?? _store.ResolveMedia(filePath);
        if (media is not null)
            _menu.Show(button, media, args);
    }

    /// <summary>The Continue watching menu adds "Remove from Continue watching" to the usual video menu.</summary>
    private void ContinueCard_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not Button { Tag: string filePath } button || _store is null || _menu is null)
            return;

        var media = _store.ContinueWatching.FirstOrDefault(item =>
                        string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    ?? _store.ResolveMedia(filePath);
        if (media is null)
            return;

        var remove = new MenuFlyoutItem { Text = "Remove from Continue watching", Icon = new FontIcon { Glyph = "\uE738" } };
        remove.Click += (_, _) => _store.RemoveFromContinueWatching(media);
        _menu.Show(button, media, args, remove);
    }

    /// <summary>Clears the whole Continue watching row after asking. Videos aren't deleted.</summary>
    private async void ClearContinueWatching_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null)
            return;

        var dialog = new ContentDialog
        {
            Title = "Clear Continue watching?",
            Content = new TextBlock
            {
                Text = "Every video leaves this row and starts from the beginning next time. Nothing is deleted.",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            RequestedTheme = ElementTheme.Dark
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            _store.ClearContinueWatching();
    }

    /// <summary>The Recently played menu adds "Remove from Recently played" to the usual video menu.</summary>
    private void RecentCard_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not Button { Tag: string filePath } button || _store is null || _menu is null)
            return;

        var media = _store.RecentMedia.FirstOrDefault(item =>
                        string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    ?? _store.ResolveMedia(filePath);
        if (media is null)
            return;

        var remove = new MenuFlyoutItem { Text = "Remove from Recently played", Icon = new FontIcon { Glyph = "\uE738" } };
        remove.Click += (_, _) => _store.RemoveFromRecent(media);
        _menu.Show(button, media, args, remove);
    }

    /// <summary>Clears the whole Recently played row after asking. Videos aren't deleted.</summary>
    private async void ClearRecentlyPlayed_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null)
            return;

        var dialog = new ContentDialog
        {
            Title = "Clear Recently played?",
            Content = new TextBlock
            {
                Text = "Every video leaves this row. Continue watching and your videos stay as they are.",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            RequestedTheme = ElementTheme.Dark
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            _store.ClearRecent();
    }

    private void ScrollRowBack_Click(object sender, RoutedEventArgs e) => ScrollRow(sender, -1);

    private void ScrollRowForward_Click(object sender, RoutedEventArgs e) => ScrollRow(sender, 1);

    private void ScrollRow(object sender, int direction)
    {
        if (sender is FrameworkElement { Tag: string name } && FindName(name) is ScrollViewer scroller)
            scroller.ChangeView(scroller.HorizontalOffset + direction * scroller.ViewportWidth * 0.8, null, null);
    }

    private sealed record Featured(MediaItem Media, string Eyebrow, bool IsResume);
}
