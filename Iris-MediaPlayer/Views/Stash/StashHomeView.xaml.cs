using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services;
using Iris_MediaPlayer.Services.Stash;
using Iris_MediaPlayer.Views.Jellyfin;
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

namespace Iris_MediaPlayer.Views.Stash;

/// <summary>
/// Stash's Home: a hero for what to watch, then rows for Continue watching, Recently added, Most
/// played, performers and studios. Scenes open their page; performers and studios open their scenes.
/// </summary>
public sealed partial class StashHomeView : UserControl
{
    private const int RowSize = 16;
    private static readonly TimeSpan HeroInterval = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan HeroCrossfade = TimeSpan.FromMilliseconds(900);

    private readonly DispatcherQueueTimer _heroTimer;
    private StashClient? _client;
    private List<StashItem> _featured = [];
    private int _featuredIndex;
    private bool _frontIsA = true;

    public StashHomeView()
    {
        InitializeComponent();
        Services.Themes.TrackFades(this);
        _heroTimer = DispatcherQueue.GetForCurrentThread()!.CreateTimer();
        _heroTimer.Interval = HeroInterval;
        _heroTimer.Tick += (_, _) => ShowFeatured((_featuredIndex + 1) % Math.Max(1, _featured.Count), animate: true);
        Motion.SetOpacity(BackdropLayerB, 0);
        Unloaded += (_, _) => _heroTimer.Stop();
    }

    /// <summary>A card was clicked: a scene, performer or studio.</summary>
    public event EventHandler<StashItem>? ItemInvoked;

    public event EventHandler<PlayRequest>? PlayRequested;

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

    public async Task LoadAsync(StashClient client, CancellationToken cancel)
    {
        _client = client;
        ErrorText.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = Rows.Children.Count == 0;
        try
        {
            // Each row loads on its own; one failing doesn't empty the page.
            var resume = Load(() => client.FindScenesAsync(StashSceneFilter.InProgress, StashSceneSort.RecentlyPlayed, null, null, 1, RowSize, cancel));
            var recent = Load(() => client.FindScenesAsync(StashSceneFilter.All, StashSceneSort.RecentlyAdded, null, null, 1, RowSize, cancel));
            var played = Load(() => client.FindScenesAsync(StashSceneFilter.All, StashSceneSort.MostPlayed, null, null, 1, RowSize, cancel));
            var performers = Load(() => client.FindAsync(StashKind.Performer, null, 1, 20, popularFirst: true, cancel));
            var studios = Load(() => client.FindAsync(StashKind.Studio, null, 1, 20, popularFirst: true, cancel));
            await Task.WhenAll(resume, recent, played, performers, studios);
            if (cancel.IsCancellationRequested)
                return;

            Rows.Children.Clear();
            AddRow("Continue watching", resume.Result, "WideTemplate");
            AddRow("Recently added", recent.Result, "WideTemplate");
            AddRow("Most played", played.Result.Where(scene => scene.PlayCount > 0).ToList(), "WideTemplate");
            AddRow("Performers", performers.Result, "PosterTemplate");
            AddRow("Studios", studios.Result, "LogoTemplate");
            BuildHero(resume.Result.Concat(recent.Result));

            if (Rows.Children.Count == 0)
            {
                ErrorText.Text = "Stash didn't send any scenes. If your library has some, check the API key in Settings.";
                ErrorText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private static async Task<IReadOnlyList<StashItem>> Load(Func<Task<StashPage>> load)
    {
        try
        {
            return (await load()).Items;
        }
        catch (StashException)
        {
            return [];
        }
    }

    private void AddRow(string title, IReadOnlyList<StashItem> items, string template)
    {
        if (items.Count == 0 || _client is null)
            return;
        StashUi.LoadCardImages(_client, items);

        var scroller = new ScrollViewer
        {
            Padding = new Thickness(48, 0, 40, 0),
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var repeater = new ItemsRepeater
        {
            ItemsSource = items,
            ItemTemplate = (DataTemplate)Resources[template],
            Layout = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 4 }
        };
        repeater.ElementPrepared += (_, args) => Motion.EnterFromBelow(args.Element, TimeSpan.FromMilliseconds(60 + Math.Min(args.Index, 8) * 45), 16);
        scroller.Content = repeater;

        var header = new Grid { Margin = new Thickness(56, 0, 40, 0) };
        header.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["IrisSectionTitleTextStyle"], VerticalAlignment = VerticalAlignment.Center });
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
        var button = new Button { Style = (Style)Application.Current.Resources["IrisOutlineIconButtonStyle"], Content = new FontIcon { Glyph = glyph, FontSize = 12 } };
        ToolTipService.SetToolTip(button, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: StashItem item })
            ItemInvoked?.Invoke(this, item);
    }

    // ---------- Hero ----------

    private void BuildHero(IEnumerable<StashItem> candidates)
    {
        _featured = candidates.Where(scene => scene.ScreenshotPath is not null).GroupBy(scene => scene.Id).Select(group => group.First()).Take(5).ToList();
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
        var scene = _featured[index];

        var incomingLayer = animate ? (_frontIsA ? BackdropLayerB : BackdropLayerA) : (_frontIsA ? BackdropLayerA : BackdropLayerB);
        var incomingImage = incomingLayer == BackdropLayerA ? BackdropImageA : BackdropImageB;
        var outgoingLayer = incomingLayer == BackdropLayerA ? BackdropLayerB : BackdropLayerA;
        incomingImage.Source = JellyfinUi.Load(_client.GetImageUrl(scene.ScreenshotPath), decodeWidth: 1920);
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

        var started = scene.ResumeSeconds > 0;
        HeroEyebrow.Text = (started ? "CONTINUE WATCHING" : "RECENTLY ADDED") + (scene.Studio is { } studio ? $" · {studio.Name.ToUpperInvariant()}" : string.Empty);
        HeroTitle.Text = scene.Name;
        StashSceneView.FillMeta(HeroMeta, scene, withEndTime: false);
        HeroPeople.Text = scene.Performers.Count > 0 ? string.Join(", ", scene.Performers.Take(4).Select(person => person.Name)) : string.Empty;
        HeroPeople.Visibility = scene.Performers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HeroPlayText.Text = started ? "Resume" : "Play";
        HeroProgressPanel.Visibility = scene.ProgressVisibility;
        HeroProgressFill.Width = 240 * scene.Progress;
        HeroRemaining.Text = started ? StashItem.FormatTime(scene.DurationSeconds - scene.ResumeSeconds) + " left" : string.Empty;

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
                BorderThickness = new Thickness(0), Tag = index, Style = (Style)Application.Current.Resources["IrisButtonBaseStyle"]
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dot, $"Show featured scene {index + 1}");
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
            dot.Background = new SolidColorBrush(on ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF4, 0xF4, 0xF6) : Microsoft.UI.ColorHelper.FromArgb(0x47, 0xFF, 0xFF, 0xFF));
        }
    }

    private void HeroPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not null && _featuredIndex < _featured.Count)
            PlayRequested?.Invoke(this, StashUi.Request(_client, _featured[_featuredIndex], [_featured[_featuredIndex]]));
    }

    private void HeroMore_Click(object sender, RoutedEventArgs e)
    {
        if (_featuredIndex < _featured.Count)
            ItemInvoked?.Invoke(this, _featured[_featuredIndex]);
    }

    private void Hero_SizeChanged(object sender, SizeChangedEventArgs e)
        => Hero.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };

    private void Backdrop_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Image image && e.NewSize.Width > 0)
            Motion.StartDrift(image, 1.04f, 1.12f, TimeSpan.FromSeconds(24), new Vector2(0.6f, 0.4f));
    }
}
