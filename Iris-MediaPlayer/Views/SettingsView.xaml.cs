using Iris_MediaPlayer.Services;
using Iris_MediaPlayer.Services.Jellyfin;
using Iris_MediaPlayer.Services.Stash;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Windows.System;

namespace Iris_MediaPlayer.Views;

/// <summary>
/// The Settings page. Every preference is a row (label and caption on the left, control on the
/// right) in a titled group. Rows are built in code because they are all the same shape: a helper
/// per control type keeps this file about what each setting is, not about layout. Values are read
/// and written through <see cref="AppSettings"/>, <see cref="PlayerSettings"/> and the
/// <see cref="LibraryStore"/>, so the rest of the app picks them up without restarting.
/// </summary>
public sealed partial class SettingsView : UserControl
{
    private const string SiteUrl = "https://www.ryujininteractive.com";

    private LibraryStore? _store;
    private AppUpdater? _updater;
    private StackPanel? _currentGroup;
    private StackPanel _folderList = new();
    private TextBlock _scanStatus = new();
    private TextBlock _updateStatus = new();
    private Button _restartButton = new();
    private StackPanel _jellyfinRows = new();
    private bool _isBuilt;

    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>The Add folder button needs the window for its picker, so MainWindow handles it.</summary>
    public event EventHandler? AddFolderRequested;

    /// <summary>"Connect" in the Jellyfin section: MainWindow opens the Jellyfin page.</summary>
    public event EventHandler? OpenJellyfinRequested;

    /// <summary>"Restart" after an update was downloaded from this page.</summary>
    public event EventHandler? RestartRequested;

    internal void Initialize(LibraryStore store, AppUpdater updater)
    {
        _store = store;
        _updater = updater;
        Build();
        store.LibraryChanged += RebuildFolderList;
        JellyfinSession.Changed += () => DispatcherQueue.TryEnqueue(RebuildJellyfinRows);
        StashSession.Changed += () => DispatcherQueue.TryEnqueue(RebuildJellyfinRows);
    }

    // ---------- Page ----------

    private void Build()
    {
        if (_isBuilt)
            return;
        _isBuilt = true;

        BeginSection("Library");
        AddFolderRow();
        AddRow("Rescan now", "Look through your folders again for new, moved or removed videos.", RescanControl());
        AddRow("Watch folders for changes", "Pick up new videos on their own while Iris is open.",
            Toggle(AppSettings.WatchFolders, on =>
            {
                AppSettings.WatchFolders = on;
                _store?.ApplyWatchSetting();
            }));

        BeginSection("Media servers");
        _jellyfinRows = new StackPanel();
        _currentGroup!.Children.Add(_jellyfinRows);
        RebuildJellyfinRows();

        BeginSection("Home");
        AddRow("Hide Continue watching", "Remove the row and its slides from Home. Iris still remembers where you stopped.",
            Toggle(AppSettings.HideContinueWatching, on => AppSettings.HideContinueWatching = on));
        AddRow("Hide Recently played", "Remove the row and its slides from Home, so what you've watched doesn't show.",
            Toggle(AppSettings.HideRecentlyPlayed, on => AppSettings.HideRecentlyPlayed = on));

        BeginSection("Playback");
        AddRow("Autoplay next video", "Start the next video in the folder when one ends.",
            Toggle(PlayerSettings.AutoplayNext, on => PlayerSettings.AutoplayNext = on));
        AddRow("Seek bar preview", "Show a picture of the moment when you hover the seek bar.",
            Toggle(PlayerSettings.SeekPreview, on => PlayerSettings.SeekPreview = on));
        AddRow("Seek bar preview size", "How big the picture is.",
            Choice([("Small", 192), ("Medium", 288), ("Large", 384)], PlayerSettings.SeekPreviewWidth,
                value => PlayerSettings.SeekPreviewWidth = value));
        AddRow("Autoplay countdown", "Seconds before the next video starts.",
            Choice([("3 seconds", 3), ("5 seconds", 5), ("10 seconds", 10)], PlayerSettings.AutoplayCountdownSeconds,
                value => PlayerSettings.AutoplayCountdownSeconds = value));
        AddRow("Resume", "What happens with a video you stopped part-way.",
            Choice([("Always resume", ResumeMode.Always), ("Ask me", ResumeMode.Ask), ("Never", ResumeMode.Never)],
                PlayerSettings.Resume, value => PlayerSettings.Resume = value));
        AddRow("Default zoom", "How video fills the window.",
            Choice([("Fit", VideoZoomMode.Fit), ("Fill screen", VideoZoomMode.Fill), ("Stretch", VideoZoomMode.Stretch)],
                PlayerSettings.ZoomMode, value => PlayerSettings.ZoomMode = value));
        AddRow("Default speed", "The speed every video starts at.",
            Choice([("0.5×", 0.5), ("0.75×", 0.75), ("1×", 1d), ("1.25×", 1.25), ("1.5×", 1.5), ("2×", 2d)],
                PlayerSettings.DefaultSpeed, value => PlayerSettings.DefaultSpeed = value));
        AddRow("Arrow keys skip", "Left and Right arrow.",
            Choice([("5 seconds", 5), ("10 seconds", 10), ("15 seconds", 15)], PlayerSettings.SmallSkipSeconds,
                value => PlayerSettings.SmallSkipSeconds = value));
        AddRow("J and L skip", "J, L and the skip buttons in the player.",
            Choice([("10 seconds", 10), ("15 seconds", 15), ("30 seconds", 30), ("60 seconds", 60)], PlayerSettings.LargeSkipSeconds,
                value => PlayerSettings.LargeSkipSeconds = value));

        BeginSection("Subtitles and audio");
        AddRow("Preferred subtitle language", "Picked automatically when a video has it.",
            Choice(SubtitleLanguages, PlayerSettings.SubtitleLanguage, value => PlayerSettings.SubtitleLanguage = value));
        AddRow("Preferred audio language", "Picked automatically when a video has it.",
            Choice(AudioLanguages, PlayerSettings.AudioLanguage, value => PlayerSettings.AudioLanguage = value));
        AddRow("Subtitle size", null,
            Choice([("Small", 0.8), ("Normal", 1d), ("Large", 1.3), ("Extra large", 1.7)], PlayerSettings.SubtitleScale,
                value => PlayerSettings.SubtitleScale = value));
        AddRow("Subtitle position", "How high subtitles sit above the bottom edge.",
            Choice([("Bottom", 0d), ("A little higher", 5d), ("Higher", 10d), ("Highest", 18d)], PlayerSettings.SubtitleLift,
                value => PlayerSettings.SubtitleLift = value));

        BeginSection("Player");
        AddRow("Hide controls after", "How long the mouse can rest before the controls fade away.",
            Choice([("2 seconds", 2d), ("3 seconds", 3d), ("5 seconds", 5d), ("10 seconds", 10d)], PlayerSettings.ControlsHideSeconds,
                value => PlayerSettings.ControlsHideSeconds = value));
        AddRow("Mouse-wheel volume step", "Volume change for each notch of the wheel.",
            Choice([("2%", 2), ("5%", 5), ("10%", 10)], PlayerSettings.WheelVolumeStep,
                value => PlayerSettings.WheelVolumeStep = value));

        BeginSection("Updates");
        AddRow("Current version", null, ValueText(VersionLabel));
        if (Installation.IsPackaged)
        {
            AddRow("Updates", "The Microsoft Store keeps Iris up to date.", ButtonControl("Open the Store", false, async _ =>
                await Launcher.LaunchUriAsync(new Uri("ms-windows-store://downloadsandupdates"))));
        }
        else
        {
            AddRow("Check for updates now", "Looks at ryujininteractive.com and downloads a newer version if there is one.", UpdateControl());
            AddRow("Update automatically", "Look for new versions after Iris starts, and download them quietly.",
                Toggle(AppSettings.AutoUpdate, on => AppSettings.AutoUpdate = on));
        }

        BeginSection("Storage");
        AddRow("Where Iris keeps its data", AppStorage.DataFolder, ButtonControl("Open folder", false, async _ =>
            await Launcher.LaunchFolderPathAsync(AppStorage.DataFolder)));
        AddRow("Clear thumbnail cache", "Throws away the thumbnails and backdrops Iris is holding and makes them again.",
            ClearCacheControl());
        AddRow("Reset library", "Forget your folders, collections, Recently played and Continue watching. Your videos stay where they are.",
            ButtonControl("Reset library…", false, async button => await ResetLibraryAsync(button)));

        BeginSection("About");
        AddRow("Iris", $"Version {VersionLabel} · by Ryujin Interactive", ButtonControl("Visit the website", false, async _ =>
            await Launcher.LaunchUriAsync(new Uri(SiteUrl))));
        AddRow("Credits", "Playback by mpv, the open-source media player."
                          + (Installation.IsPackaged ? " " : " Installer and updates by Velopack. ")
                          + "Full licenses are in THIRD-PARTY-NOTICES.md next to Iris.", null);

        RebuildFolderList();
    }

    // ---------- Library ----------

    private void AddFolderRow()
    {
        var addButton = ButtonControl("Add folder", true, _ =>
        {
            AddFolderRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        });
        AddRow("Library folders", "Iris looks for videos in these folders and their subfolders.", addButton);
        _folderList = new StackPanel { Spacing = 0 };
        _currentGroup!.Children.Add(_folderList);
    }

    /// <summary>The server and account rows, or a "Connect" row when not signed in.</summary>
    private void RebuildJellyfinRows()
    {
        var group = _currentGroup;
        _currentGroup = _jellyfinRows;
        _jellyfinRows.Children.Clear();
        try
        {
            if (!JellyfinSession.IsSignedIn)
            {
                AddRow("Jellyfin", "Not connected. Stream films and shows from your Jellyfin server.",
                    ButtonControl("Connect", true, _ => OpenServer(MediaServerKind.Jellyfin)));
            }
            else
            {
                AddRow($"Jellyfin · {JellyfinSession.ServerName ?? "Jellyfin"}", $"{JellyfinSession.Address} · signed in as {JellyfinSession.UserName}",
                    ButtonControl("Sign out", false, async button =>
                    {
                        button.IsEnabled = false;
                        await JellyfinSession.SignOutAsync();
                    }));
            }

            if (!StashSession.IsConnected)
            {
                AddRow("Stash", "Not connected. Browse and play your Stash library.",
                    ButtonControl("Connect", true, _ => OpenServer(MediaServerKind.Stash)));
            }
            else
            {
                AddRow($"Stash · {StashSession.ServerName}", StashSession.Address,
                    ButtonControl("Disconnect", false, _ =>
                    {
                        StashSession.Disconnect();
                        return Task.CompletedTask;
                    }));
            }

            AddRow("Plex", "Coming in a later version.", null);
        }
        finally
        {
            _currentGroup = group;
        }
    }

    /// <summary>Opens the media-servers section on the connect screen for <paramref name="kind"/>.</summary>
    private Task OpenServer(MediaServerKind kind)
    {
        OpenJellyfinRequested?.Invoke(this, EventArgs.Empty);
        MediaServers.RequestAdd(kind);
        return Task.CompletedTask;
    }

    private void RebuildFolderList()
    {
        if (_store is null)
            return;

        _folderList.Children.Clear();
        if (_store.FolderPaths.Count == 0)
        {
            _folderList.Children.Add(Padded(new TextBlock
            {
                Text = "No folders yet.",
                Style = (Style)Application.Current.Resources["IrisCaptionTextStyle"]
            }));
            return;
        }

        foreach (var path in _store.FolderPaths.ToList())
        {
            var count = _store.Folders.FirstOrDefault(folder => PathUtils.IsSamePath(folder.Path, path))?.MediaCountLabel;
            var remove = new Button
            {
                Content = "Remove",
                Style = (Style)Application.Current.Resources["IrisOutlineButtonStyle"],
                VerticalAlignment = VerticalAlignment.Center
            };
            remove.Click += async (_, _) => await _store.RemoveFolderAsync(path);

            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = PathUtils.GetFolderName(path) is { Length: > 0 } name ? name : path,
                FontSize = 14,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            text.Children.Add(new TextBlock
            {
                Text = count is null ? path : $"{path} · {count}",
                Style = (Style)Application.Current.Resources["IrisCaptionTextStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var row = new Grid { ColumnSpacing = 16, Padding = new Thickness(36, 8, 20, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(text);
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            _folderList.Children.Add(row);
        }
    }

    private UIElement RescanControl()
    {
        _scanStatus = new TextBlock
        {
            Style = (Style)Application.Current.Resources["IrisCaptionTextStyle"],
            VerticalAlignment = VerticalAlignment.Center
        };
        var button = ButtonControl("Rescan now", false, async button =>
        {
            if (_store is null)
                return;
            button.IsEnabled = false;
            _scanStatus.Text = "Scanning…";
            await _store.RefreshAsync(showBusy: false);
            _scanStatus.Text = $"{_store.AllMedia.Count:N0} videos found";
            button.IsEnabled = true;
        });
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _scanStatus, button } };
    }

    // ---------- Updates ----------

    /// <summary>The version as shown in Settings, with "(MSIX)" on the Microsoft Store version so the two are easy to tell apart.</summary>
    private static string VersionLabel => Installation.IsPackaged ? $"{CurrentVersion} (MSIX)" : CurrentVersion;

    private static string CurrentVersion
    {
        get
        {
            var informational = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            // "1.0.5+abc123" -> "1.0.5"
            if (!string.IsNullOrWhiteSpace(informational))
                return informational.Split('+')[0];
            return Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";
        }
    }

    private UIElement UpdateControl()
    {
        _updateStatus = new TextBlock
        {
            Style = (Style)Application.Current.Resources["IrisCaptionTextStyle"],
            VerticalAlignment = VerticalAlignment.Center
        };
        _restartButton = new Button
        {
            Content = "Restart to update",
            Visibility = Visibility.Collapsed,
            Style = (Style)Application.Current.Resources["IrisPrimarySmallButtonStyle"]
        };
        _restartButton.Click += (_, _) => RestartRequested?.Invoke(this, EventArgs.Empty);

        var check = ButtonControl("Check now", false, async button =>
        {
            if (_updater is null)
                return;
            button.IsEnabled = false;
            _updateStatus.Text = "Checking…";
            var result = await _updater.CheckNowAsync();
            _updateStatus.Text = result switch
            {
                UpdateCheckResult.UpToDate => "You have the latest version.",
                UpdateCheckResult.Ready => $"Iris {_updater.ReadyVersion} is ready.",
                UpdateCheckResult.NotInstalled => "Updates only work in the installed version of Iris.",
                _ => "Couldn’t reach the website. Check your connection and try again."
            };
            _restartButton.Visibility = result == UpdateCheckResult.Ready ? Visibility.Visible : Visibility.Collapsed;
            button.IsEnabled = true;
        });
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { _updateStatus, _restartButton, check }
        };
    }

    // ---------- Storage ----------

    private UIElement ClearCacheControl()
    {
        var status = new TextBlock
        {
            Style = (Style)Application.Current.Resources["IrisCaptionTextStyle"],
            VerticalAlignment = VerticalAlignment.Center
        };
        var button = ButtonControl("Clear cache", false, _ =>
        {
            _store?.ClearImageCache();
            status.Text = "Cleared";
            return Task.CompletedTask;
        });
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { status, button } };
    }

    private async Task ResetLibraryAsync(Button owner)
    {
        if (_store is null)
            return;

        var dialog = new ContentDialog
        {
            Title = "Reset library?",
            Content = new TextBlock
            {
                Text = "Iris forgets your library folders, collections, Recently played and Continue watching. "
                       + "Your videos are not deleted, and your settings are kept.",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            RequestedTheme = ElementTheme.Dark
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        owner.IsEnabled = false;
        await _store.ResetLibraryAsync();
        owner.IsEnabled = true;
    }

    // ---------- Language lists ----------

    // mpv matches either the two-letter or the three-letter code, so both are listed.
    private static readonly (string Label, string Code)[] Languages =
    [
        ("English", "en,eng"), ("Japanese", "ja,jpn"), ("Spanish", "es,spa"), ("French", "fr,fre,fra"),
        ("German", "de,ger,deu"), ("Italian", "it,ita"), ("Portuguese", "pt,por"), ("Russian", "ru,rus"),
        ("Korean", "ko,kor"), ("Chinese", "zh,chi,zho"), ("Hindi", "hi,hin"), ("Arabic", "ar,ara")
    ];

    private static readonly List<(string, string)> SubtitleLanguages =
        [("Automatic", string.Empty), ("No subtitles", PlayerSettings.SubtitlesOff), .. Languages.Select(l => (l.Label, l.Code))];

    private static readonly List<(string, string)> AudioLanguages =
        [("Automatic", string.Empty), .. Languages.Select(l => (l.Label, l.Code))];

    // ---------- Row building ----------

    private void BeginSection(string title)
    {
        Sections.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["IrisSectionTitleTextStyle"],
            Margin = new Thickness(0, Sections.Children.Count == 0 ? 0 : 22, 0, 4)
        });

        _currentGroup = new StackPanel();
        Sections.Children.Add(new Border
        {
            Child = _currentGroup,
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(0, 6, 0, 6),
            BorderThickness = new Thickness(1),
            Background = (Brush)Application.Current.Resources["IrisSurfaceBrush"],
            BorderBrush = (Brush)Application.Current.Resources["IrisStrokeSubtleBrush"]
        });
    }

    private void AddRow(string label, string? caption, UIElement? control)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = label, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(caption))
        {
            text.Children.Add(new TextBlock
            {
                Text = caption,
                Style = (Style)Application.Current.Resources["IrisCaptionTextStyle"],
                TextWrapping = TextWrapping.Wrap
            });
        }

        var row = new Grid { ColumnSpacing = 24, Padding = new Thickness(20, 12, 20, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(text);
        if (control is FrameworkElement element)
        {
            element.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(element, 1);
            row.Children.Add(element);
        }
        _currentGroup!.Children.Add(row);
    }

    private static UIElement Padded(UIElement child)
        => new Border { Padding = new Thickness(36, 8, 20, 8), Child = child };

    private static TextBlock ValueText(string text) => new()
    {
        Text = text,
        Foreground = (Brush)Application.Current.Resources["IrisTextSecondaryBrush"],
        FontSize = 14
    };

    private static ToggleSwitch Toggle(bool isOn, Action<bool> changed)
    {
        var toggle = new ToggleSwitch { IsOn = isOn, MinWidth = 0, OnContent = "", OffContent = "" };
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return toggle;
    }

    private static Button ButtonControl(string text, bool primary, Func<Button, Task> click)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)Application.Current.Resources[primary ? "IrisPrimarySmallButtonStyle" : "IrisOutlineButtonStyle"]
        };
        button.Click += async (_, _) => await click(button);
        return button;
    }

    /// <summary>
    /// A drop-down of fixed options. If the saved value isn't one of them (it was edited by hand),
    /// the nearest number is shown without changing what is saved until the user picks one.
    /// </summary>
    private static ComboBox Choice<T>(IReadOnlyList<(string Label, T Value)> options, T current, Action<T> changed)
    {
        var combo = new ComboBox { MinWidth = 180, ItemsSource = options.Select(option => option.Label).ToList() };
        var index = options.ToList().FindIndex(option => IsSame(option.Value, current));
        if (index < 0 && current is int or double)
        {
            var target = Convert.ToDouble(current, CultureInfo.InvariantCulture);
            index = options
                .Select((option, i) => (i, distance: Math.Abs(Convert.ToDouble(option.Value, CultureInfo.InvariantCulture) - target)))
                .OrderBy(pair => pair.distance).First().i;
        }
        combo.SelectedIndex = Math.Max(index, 0);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
                changed(options[combo.SelectedIndex].Value);
        };
        return combo;
    }

    private static bool IsSame<T>(T first, T second)
        => first is double a && second is double b ? Math.Abs(a - b) < 0.001 : EqualityComparer<T>.Default.Equals(first, second);
}
