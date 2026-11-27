using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services;
using Iris_MediaPlayer.Services.Stash;
using Iris_MediaPlayer.Views.Jellyfin;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Iris_MediaPlayer.Views.Stash;

/// <summary>
/// One Stash scene: screenshot backdrop, studio, title, rating, date, length and quality, Play/Resume
/// and Start over, the description, performers, tags, markers (play from that moment) and the file.
/// </summary>
public sealed partial class StashSceneView : UserControl
{
    private StashClient? _client;
    private StashItem? _scene;
    private StashItem? _requested;
    private CancellationTokenSource? _loading;

    public StashSceneView()
    {
        InitializeComponent();
        Services.Themes.TrackFades(this);
    }

    public event EventHandler<PlayRequest>? PlayRequested;

    /// <summary>A performer, studio or tag was clicked: show its scenes.</summary>
    public event EventHandler<StashScope>? ScopeRequested;

    public string? Title => _scene?.Name;

    public async Task ShowAsync(StashClient client, StashItem scene, bool quiet = false)
    {
        _client = client;
        _requested = scene;
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
            var full = await client.FindSceneAsync(scene.Id, cancel.Token);
            if (cancel.IsCancellationRequested)
                return;
            full.Image = scene.Image;
            _scene = full;
            Fill(full);
            Body.Visibility = Visibility.Visible;
            if (!quiet)
            {
                var delay = 0;
                foreach (var child in Header.Children.Where(child => child.Visibility == Visibility.Visible))
                {
                    Motion.EnterFromBelow(child, TimeSpan.FromMilliseconds(delay), 16);
                    delay += 40;
                }
            }
        }
        catch (StashException exception) when (!cancel.IsCancellationRequested)
        {
            Message.Show("", "Couldn't load this scene", exception.Message, "Try again");
            Message.Visibility = Visibility.Visible;
        }
        finally
        {
            if (ReferenceEquals(_loading, cancel))
                LoadingRing.IsActive = false;
        }
    }

    private void Fill(StashItem scene)
    {
        var client = _client!;
        BackdropImage.Source = JellyfinUi.Load(client.GetImageUrl(scene.ScreenshotPath), decodeWidth: 1920);
        StudioLink.Visibility = scene.Studio is null ? Visibility.Collapsed : Visibility.Visible;
        StudioText.Text = scene.Studio?.Name.ToUpperInvariant() ?? string.Empty;
        TitleText.Text = scene.Name;
        FillMeta(MetaPanel, scene, withEndTime: true);

        var started = scene.ResumeSeconds > 0;
        PlayText.Text = started ? $"Resume from {Clock(scene.ResumeSeconds)}" : "Play";
        StartOverButton.Visibility = started ? Visibility.Visible : Visibility.Collapsed;
        ProgressPanel.Visibility = started ? scene.ProgressVisibility : Visibility.Collapsed;
        ProgressFill.Width = 300 * scene.Progress;
        RemainingText.Text = started ? StashItem.FormatTime(scene.DurationSeconds - scene.ResumeSeconds) + " left" : string.Empty;
        DetailsText.Text = scene.Details ?? string.Empty;
        DetailsText.Visibility = string.IsNullOrWhiteSpace(scene.Details) ? Visibility.Collapsed : Visibility.Visible;

        foreach (var performer in scene.Performers)
            performer.Image ??= JellyfinUi.Load(client.GetImageUrl(performer.ImagePath), decodeHeight: StashUi.FaceHeight);
        PerformerItems.ItemsSource = scene.Performers;
        PerformersSection.Visibility = scene.Performers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        TagPanel.Children.Clear();
        foreach (var tag in scene.Tags)
        {
            var chip = new Button
            {
                Content = tag.Name, Tag = tag, Height = 32, Padding = new Thickness(14, 0, 14, 0), FontSize = 13,
                Style = (Style)Application.Current.Resources["IrisOutlineButtonStyle"]
            };
            chip.Click += (_, _) => ScopeRequested?.Invoke(this, new StashScope(StashKind.Tag, tag.Id, tag.Name));
            TagPanel.Children.Add(chip);
        }
        TagsSection.Visibility = scene.Tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        MarkerItems.ItemsSource = scene.Markers;
        MarkersSection.Visibility = scene.Markers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        FilePanel.Children.Clear();
        AddFact("Resolution", scene.Width > 0 ? $"{scene.Width} × {scene.Height}" : null);
        AddFact("Video", scene.VideoCodec?.ToUpperInvariant());
        AddFact("Audio", scene.AudioCodec?.ToUpperInvariant());
        AddFact("Frame rate", scene.FrameRate > 0 ? $"{scene.FrameRate:0.##} fps" : null);
        AddFact("Size", scene.Size > 0 ? scene.Size >= 1_000_000_000 ? $"{scene.Size / 1_000_000_000d:0.0} GB" : $"{scene.Size / 1_000_000d:0} MB" : null);
        PathText.Text = scene.FilePath ?? string.Empty;
    }

    private void AddFact(string label, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = (Brush)Application.Current.Resources["IrisTextTertiaryBrush"] });
        panel.Children.Add(new TextBlock { Text = value, FontSize = 13 });
        FilePanel.Children.Add(panel);
    }

    /// <summary>Rating, date, length, quality, plays and (on the scene page) when it would end.</summary>
    public static void FillMeta(Panel panel, StashItem scene, bool withEndTime)
    {
        panel.Children.Clear();
        var muted = (Brush)Application.Current.Resources["IrisTextMutedBrush"];
        if (scene.Stars is { } stars)
        {
            var rated = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            rated.Children.Add(new FontIcon { Glyph = "", FontSize = 13, Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF5, 0xC4, 0x52)) });
            rated.Children.Add(new TextBlock { Text = stars.ToString("0.#", CultureInfo.CurrentCulture), FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(rated);
        }
        if (scene.DateText.Length > 0)
            panel.Children.Add(Text(scene.DateText, muted));
        if (scene.DurationSeconds > 0)
            panel.Children.Add(Text(StashItem.FormatTime(scene.DurationSeconds), muted));
        if (scene.ResolutionLabel is { Length: > 0 } resolution and not "SD")
            panel.Children.Add(JellyfinUi.Badge(resolution));
        if (scene.PlayCount > 0)
            panel.Children.Add(Text(scene.PlayCount == 1 ? "Played once" : $"Played {scene.PlayCount}×", muted));
        if (withEndTime && scene.DurationSeconds > 0)
            panel.Children.Add(Text($"Ends at {DateTime.Now.AddSeconds(scene.DurationSeconds - scene.ResumeSeconds):HH:mm}",
                (Brush)Application.Current.Resources["IrisTextTertiaryBrush"]));
    }

    private static TextBlock Text(string text, Brush brush) => new() { Text = text, FontSize = 14, Foreground = brush, VerticalAlignment = VerticalAlignment.Center };

    private static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    // ---------- Actions ----------

    private void Play_Click(object sender, RoutedEventArgs e) => Play(null);

    private void StartOver_Click(object sender, RoutedEventArgs e) => Play(0);

    private void Marker_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: StashMarker marker })
            Play(Math.Max(0.01, marker.Seconds));
    }

    private void Play(double? startAt)
    {
        if (_client is null || _scene is null)
            return;
        // Start over passes 0: the player then starts at the beginning whatever the resume setting says.
        PlayRequested?.Invoke(this, StashUi.Request(_client, _scene, [_scene], startAt));
    }

    private void Performer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: StashItem performer })
            ScopeRequested?.Invoke(this, new StashScope(StashKind.Performer, performer.Id, performer.Name));
    }

    private void Studio_Click(object sender, RoutedEventArgs e)
    {
        if (_scene?.Studio is { } studio)
            ScopeRequested?.Invoke(this, new StashScope(StashKind.Studio, studio.Id, studio.Name));
    }

    private async void Message_ActionClick(object? sender, EventArgs e)
    {
        if (_client is not null && _requested is not null)
            await ShowAsync(_client, _requested);
    }

    private void Backdrop_SizeChanged(object sender, SizeChangedEventArgs e)
        => Backdrop.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };

    private void BackdropImage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0)
            Motion.StartDrift(BackdropImage, 1.03f, 1.1f, TimeSpan.FromSeconds(26), new Vector2(0.6f, 0.4f));
    }
}
