using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services;
using Iris_MediaPlayer.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System;
using System.Linq;
using System.Numerics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Iris_MediaPlayer;

/// <summary>
/// App shell: title bar, navigation rail, switching between sections, file pickers, drag and drop,
/// fullscreen and the mini player. Each section's own UI and logic lives in its view under Views/.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly LibraryStore _store;
    private readonly MiniPlayerMode _miniPlayer = new();
    private Button? _selectedRailButton;
    private UIElement _sectionBeforePlayer;
    private Button? _railBeforePlayer;
    private bool _isPlayerFullscreen;
    private readonly AppUpdater _updater = new();

    public MainWindow()
    {
        InitializeComponent();
        Activated += (_, e) => PlayerPage.SetWindowActive(e.WindowActivationState != WindowActivationState.Deactivated);
        WindowChrome.ExtendIntoTitleBar(this, AppTitleBar);
        _sectionBeforePlayer = HomePage;

        _store = new LibraryStore(DispatcherQueue);
        // Only a scan the user asked for (adding a folder) greys out the rail. The startup scan below
        // passes showBusy: false, so Iris stays usable while it runs.
        _store.ScanningChanged += isScanning => Rail.IsHitTestVisible = !isScanning;

        HomePage.Initialize(_store);
        HomePage.PlayRequested += (_, request) => StartPlayback(request);
        HomePage.OpenVideoRequested += (_, _) => PickAndPlayVideo();
        HomePage.AddFolderRequested += (_, _) => PickAndAddFolder();
        HomePage.OpenFolderRequested += (_, media) =>
        {
            ShowLibrary();
            LibraryPage.ShowFolder(media.LibraryRootPath, media.FolderPath);
        };

        LibraryPage.Initialize(_store);
        LibraryPage.PlayRequested += (_, request) => StartPlayback(request);
        LibraryPage.AddFolderRequested += (_, _) => PickAndAddFolder();

        ServersPage.PlayRequested += (_, request) => StartPlayback(request);

        SettingsPage.Initialize(_store, _updater);
        SettingsPage.AddFolderRequested += (_, _) => PickAndAddFolder();
        SettingsPage.RestartRequested += (_, _) => RestartToUpdate();
        SettingsPage.OpenJellyfinRequested += (_, _) => ShowJellyfin();

        PlayerPage.Initialize(_store);
        PlayerPage.BackRequested += (_, _) => ReturnFromPlayer();
        PlayerPage.FullscreenToggleRequested += (_, _) => SetPlayerFullscreen(!_isPlayerFullscreen);
        PlayerPage.MiniPlayerToggleRequested += (_, _) => SetMiniPlayer(!_miniPlayer.IsActive);
        PlayerPage.MiniPlayerCloseRequested += (_, _) =>
        {
            SetMiniPlayer(false);
            ReturnFromPlayer();
        };
        PlayerPage.MediaChanged += (_, media) => TitleBarText.Text = media.Title;

        Rail.Loaded += async (_, _) =>
        {
            SelectRail(HomeRailButton, animate: false);
            await _store.RefreshAsync(showBusy: false);
            _ = CheckForUpdateAsync();
        };
        // Settings sits at the bottom of the rail, so its plate position changes with the window height.
        Rail.SizeChanged += (_, _) =>
        {
            if (_selectedRailButton is not null)
                SelectRail(_selectedRailButton, animate: false);
        };
        Closed += MainWindow_Closed;
    }

    // ---------- Navigation ----------

    private void RailButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        switch (button.Tag as string)
        {
            case "Library":
                ShowLibrary();
                break;
            case "Jellyfin":
                ShowJellyfin();
                break;
            case "Settings":
                SelectRail(button);
                ShowSection(SettingsPage);
                break;
            default:
                SelectRail(HomeRailButton);
                ShowSection(HomePage);
                break;
        }
    }

    private void ShowLibrary()
    {
        SelectRail(LibraryRailButton);
        ShowSection(LibraryPage);
        _ = _store.RefreshAsync(showBusy: false);
    }

    private void ShowJellyfin()
    {
        SelectRail(JellyfinRailButton);
        ShowSection(ServersPage);
        ServersPage.Activate();
    }

    private void ShowPlaceholder(string glyph, string title, string description)
    {
        PlaceholderPage.SetContent(glyph, title, description);
        ShowSection(PlaceholderPage);
    }

    /// <summary>Moves the rail's selection plate and indicator to <paramref name="button"/>.</summary>
    private void SelectRail(Button button, bool animate = true)
    {
        _selectedRailButton = button;
        foreach (var item in new[] { HomeRailButton, LibraryRailButton, JellyfinRailButton, SettingsRailButton })
        {
            item.Foreground = (Brush)Application.Current.Resources[item == button ? "IrisTextBrush" : "IrisTextTertiaryBrush"];
            AutomationPropertiesHelper.SetIsSelected(item, item == button);
        }

        if (Rail.ActualHeight <= 0)
            return;

        var top = (float)button.TransformToVisual(Rail).TransformPoint(new Windows.Foundation.Point(0, 0)).Y
                  - (float)Rail.Padding.Top;
        var offset = new Vector3(0, top, 0);
        if (animate)
        {
            Motion.Translate(RailSelection, offset, Motion.Slow);
            Motion.Translate(RailIndicator, offset, Motion.Slow);
        }
        else
        {
            Motion.SetTranslation(RailSelection, offset);
            Motion.SetTranslation(RailIndicator, offset);
        }
    }

    /// <summary>Shows exactly one of the section views.</summary>
    private void ShowSection(UIElement section)
    {
        if (section != PlayerPage && PlayerPage.Visibility == Visibility.Visible)
            LeavePlayerMode();

        HomePage.Visibility = section == HomePage ? Visibility.Visible : Visibility.Collapsed;
        LibraryPage.Visibility = section == LibraryPage ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderPage.Visibility = section == PlaceholderPage ? Visibility.Visible : Visibility.Collapsed;
        ServersPage.Visibility = section == ServersPage ? Visibility.Visible : Visibility.Collapsed;
        if (section != ServersPage)
            ServersPage.Deactivate();
        SettingsPage.Visibility = section == SettingsPage ? Visibility.Visible : Visibility.Collapsed;
        PlayerPage.Visibility = section == PlayerPage ? Visibility.Visible : Visibility.Collapsed;
        TopBar.Visibility = section == HomePage || section == PlaceholderPage ? Visibility.Visible : Visibility.Collapsed;
        HomePage.IsActive = section == HomePage;
    }

    // ---------- Search ----------

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        SearchShortcutHint.Visibility = string.IsNullOrEmpty(sender.Text) && sender.FocusState == FocusState.Unfocused
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;

        sender.ItemsSource = _store.Search(sender.Text.Trim()).Take(8).ToList();
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is MediaItem media)
        {
            StartPlayback(new PlayRequest(media));
            return;
        }

        // Enter with no suggestion chosen shows every match in the library.
        if (string.IsNullOrWhiteSpace(sender.Text))
            return;
        LibraryPage.SearchText = sender.Text;
        ShowLibrary();
    }

    private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        SearchShortcutHint.Visibility = Visibility.Collapsed;
        AnimateSearchWidth(500);
    }

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        SearchShortcutHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        AnimateSearchWidth(440);
    }

    private void AnimateSearchWidth(double width)
    {
        var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = width,
            Duration = Motion.Slow,
            EnableDependentAnimation = true,
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.ExponentialEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
                Exponent = 6
            }
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, SearchHost);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Width");
        var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void SearchAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TopBar.Visibility == Visibility.Visible)
        {
            SearchBox.Focus(FocusState.Keyboard);
            args.Handled = true;
        }
        else if (LibraryPage.Visibility == Visibility.Visible)
        {
            LibraryPage.FocusFilter();
            args.Handled = true;
        }
    }

    private void OpenAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        PickAndPlayVideo();
        args.Handled = true;
    }

    private void OpenVideo_Click(object sender, RoutedEventArgs e) => PickAndPlayVideo();

    // ---------- Opening files and folders ----------

    private async void PickAndAddFolder()
    {
        string? folder;
        try
        {
            folder = FileDialogs.PickFolder(WinRT.Interop.WindowNative.GetWindowHandle(this));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            await ShowPickerFailedAsync("folder");
            return;
        }
        if (folder is not null)
            await _store.AddFolderAsync(folder);
    }

    private async void PickAndPlayVideo()
    {
        string? path;
        try
        {
            path = FileDialogs.PickFile(WinRT.Interop.WindowNative.GetWindowHandle(this), "Videos",
                MediaLibraryService.SupportedVideoExtensions);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            await ShowPickerFailedAsync("video");
            return;
        }
        if (path is null)
            return;

        var media = _store.OpenFile(path);
        if (media is null)
        {
            PlayerPage.ShowStatusMessage("Iris couldn’t access that video file.");
            return;
        }

        StartPlayback(new PlayRequest(media));
    }

    /// <summary>The Open dialog failing is rare; explains it instead of crashing.</summary>
    private async System.Threading.Tasks.Task ShowPickerFailedAsync(string what)
    {
        if (Content?.XamlRoot is not { } root)
            return;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"Couldn’t open that {what}",
            Content = what == "video"
                ? "Windows couldn’t open the file chooser. Try again, or drag the video onto Iris."
                : "Windows couldn’t open the folder chooser. Try again.",
            CloseButtonText = "OK"
        };
        try
        {
            await dialog.ShowAsync();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another dialog is already open.
        }
    }

    private void App_DragEnter(object sender, DragEventArgs e) => ShowDropTarget(e);

    private void App_DragOver(object sender, DragEventArgs e) => ShowDropTarget(e);

    private void App_DragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private async void App_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;

        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        try
        {
            var files = (await e.DataView.GetStorageItemsAsync()).OfType<StorageFile>().ToList();

            // A subtitle dropped onto a playing video is added to it.
            var subtitle = files.FirstOrDefault(item => MediaLibraryService.IsSupportedSubtitleFile(item.Path));
            if (subtitle is not null && PlayerPage.Visibility == Visibility.Visible)
            {
                PlayerPage.AddSubtitle(subtitle.Path);
                return;
            }

            var file = files.FirstOrDefault(item => MediaLibraryService.IsSupportedVideoFile(item.Path));
            if (file is null)
                return;

            var media = _store.OpenFile(file.Path);
            if (media is not null)
                StartPlayback(new PlayRequest(media));
        }
        catch (Exception exception)
        {
            PlayerPage.ShowStatusMessage($"Could not open the dropped video: {exception.Message}");
        }
    }

    private void ShowDropTarget(DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = PlayerPage.Visibility == Visibility.Visible
                ? "Drop a video to play, or subtitles to add them"
                : "Drop to play in Iris";
            e.DragUIOverride.IsCaptionVisible = true;
            DropOverlay.Visibility = _miniPlayer.IsActive ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
            DropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- Player, fullscreen and mini player ----------

    private void StartPlayback(PlayRequest request)
    {
        if (PlayerPage.Visibility != Visibility.Visible)
        {
            (_sectionBeforePlayer, _railBeforePlayer) =
                LibraryPage.Visibility == Visibility.Visible ? ((UIElement)LibraryPage, LibraryRailButton)
                : ServersPage.Visibility == Visibility.Visible ? (ServersPage, JellyfinRailButton)
                : (HomePage, HomeRailButton);
        }

        ShowSection(PlayerPage);
        EnterPlayerMode();
        PlayerPage.Play(request.Media, request.Queue ?? _store.GetFolderQueue(request.Media));
    }

    private void ReturnFromPlayer()
    {
        // ShowSection leaves player mode: fullscreen, mini player and playback all stop there.
        ShowSection(_sectionBeforePlayer);
        SelectRail(_railBeforePlayer ?? HomeRailButton, animate: false);
        if (_sectionBeforePlayer == ServersPage)
        {
            ServersPage.Activate();
            ServersPage.RefreshAfterPlayback();
        }
    }

    /// <summary>The player covers the rail and the content frame; the title bar stays.</summary>
    private void EnterPlayerMode()
    {
        Rail.Visibility = Visibility.Collapsed;
        Grid.SetColumn(ContentSurface, 0);
        Grid.SetColumnSpan(ContentSurface, 2);
        ContentSurface.CornerRadius = new CornerRadius(0);
        ContentSurface.BorderThickness = new Thickness(0);
        UpdateContentClip();
    }

    private void LeavePlayerMode()
    {
        if (_isPlayerFullscreen)
            SetPlayerFullscreen(false);
        if (_miniPlayer.IsActive)
            SetMiniPlayer(false);

        PlayerPage.Stop();
        TitleBarText.Text = "Iris";
        Rail.Visibility = Visibility.Visible;
        Grid.SetColumn(ContentSurface, 1);
        Grid.SetColumnSpan(ContentSurface, 1);
        ContentSurface.CornerRadius = new CornerRadius(16, 0, 0, 0);
        ContentSurface.BorderThickness = new Thickness(1, 1, 0, 0);
        UpdateContentClip();
    }

    private void SetPlayerFullscreen(bool enabled)
    {
        if (_isPlayerFullscreen == enabled)
            return;
        if (enabled && _miniPlayer.IsActive)
            SetMiniPlayer(false);

        AppWindow.SetPresenter(enabled ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        _isPlayerFullscreen = enabled;
        SetTitleBarVisible(!enabled);
        PlayerPage.SetFullscreen(enabled);
    }

    private void SetMiniPlayer(bool enabled)
    {
        if (_miniPlayer.IsActive == enabled)
            return;

        if (enabled)
        {
            if (_isPlayerFullscreen)
                SetPlayerFullscreen(false);
            _miniPlayer.Enter(AppWindow, Content.XamlRoot?.RasterizationScale ?? 1);
        }
        else
        {
            _miniPlayer.Exit(AppWindow);
        }

        SetTitleBarVisible(!enabled);
        PlayerPage.SetMiniMode(enabled);
    }

    private void SetTitleBarVisible(bool visible)
    {
        TitleBarRow.Height = visible ? new GridLength((double)Application.Current.Resources["IrisTitleBarHeight"]) : new GridLength(0);
        AppTitleBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ContentRoot_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateContentClip();

    /// <summary>Clips the pages to the content surface's rounded top-left corner.</summary>
    private void UpdateContentClip()
    {
        var visual = ElementCompositionPreview.GetElementVisual(ContentRoot);
        if (ContentSurface.CornerRadius.TopLeft <= 0 || ContentRoot.ActualWidth <= 0 || ContentRoot.ActualHeight <= 0)
        {
            visual.Clip = null;
            return;
        }

        var radius = new Vector2(15f);
        visual.Clip = visual.Compositor.CreateRectangleClip(
            0, 0, (float)ContentRoot.ActualWidth, (float)ContentRoot.ActualHeight,
            radius, Vector2.Zero, Vector2.Zero, Vector2.Zero);
    }

    // ---------- Updates ----------

    /// <summary>Looks for a new release shortly after startup and offers it once it has downloaded.</summary>
    private async System.Threading.Tasks.Task CheckForUpdateAsync()
    {
        if (!AppSettings.AutoUpdate)
            return;

        await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(5));
        if (!AppSettings.AutoUpdate || !await _updater.DownloadUpdateAsync())
            return;

        UpdateTitle.Text = $"Iris {_updater.ReadyVersion} is ready";
        UpdateCard.Visibility = Visibility.Visible;
        Motion.EnterFromBelow(UpdateCard, TimeSpan.Zero, 16);
    }

    private void UpdateLater_Click(object sender, RoutedEventArgs e) => UpdateCard.Visibility = Visibility.Collapsed;

    private void UpdateRestart_Click(object sender, RoutedEventArgs e) => RestartToUpdate();

    private void RestartToUpdate()
    {
        // Save the resume point of anything playing before Iris closes.
        PlayerPage.Stop();
        _updater.RestartToUpdate();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _store.Dispose();
        PlayerPage.Shutdown();
    }
}

/// <summary>Marks the current rail item for screen readers.</summary>
internal static class AutomationPropertiesHelper
{
    public static void SetIsSelected(UIElement element, bool selected)
        => Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(element, selected ? "Current page" : string.Empty);
}
