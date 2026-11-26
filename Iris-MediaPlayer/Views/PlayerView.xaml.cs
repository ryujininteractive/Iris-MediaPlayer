using Iris_MediaPlayer.Models;
using Iris_MediaPlayer.Services;
using Iris_MediaPlayer.Views.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.UI.Core;
using VirtualKey = Windows.System.VirtualKey;

namespace Iris_MediaPlayer.Views;

/// <summary>
/// The video player. Owns the mpv instance, the video surface, the play queue and every
/// playback control: floating glass bars that hide while the video plays, a settings sheet,
/// the Up next card and the mini player overlay. The window decides fullscreen, mini player
/// and navigation; this view asks for them through events.
/// </summary>
public sealed partial class PlayerView : UserControl
{
    private const double ProgressSaveIntervalSeconds = 5;
    private const double PreviousRestartThresholdSeconds = 5;
    private const double SheetWidth = 384;
    private const double DelayLimitSeconds = 30;
    // mpv's default subtitle size reads too large; the Settings page's "Normal" (1.0) maps to this.
    private const double SubtitleBaseScale = 0.8;
    // How far below their authored spot picture subtitles sit, in percent of the video height.
    private const double PictureSubtitleDrop = 3;

    private const string PlayGlyph = "";
    private const string PauseGlyph = "";
    private const string VolumeGlyph = "";
    private const string MutedGlyph = "";
    private const string CheckGlyph = "";

    private static readonly double[] Speeds = [0.5, 0.75, 1, 1.25, 1.5, 2];
    private static readonly string[] SpeedLabels = ["0.5×", "0.75×", "1×", "1.25×", "1.5×", "2×"];
    private static readonly VideoZoomMode[] ZoomModes = [VideoZoomMode.Fit, VideoZoomMode.Fill, VideoZoomMode.Stretch];
    private static readonly TimeSpan SeekThrottle = TimeSpan.FromMilliseconds(90);

    private readonly DispatcherQueueTimer _controlsHideTimer;
    private readonly DispatcherQueueTimer _singleTapTimer;
    private readonly DispatcherQueueTimer _resumeBannerTimer;
    private readonly DispatcherQueueTimer _osdTimer;
    private readonly DispatcherQueueTimer _autoplayTimer;
    private readonly DispatcherQueueTimer _posterTimer;

    private LibraryStore? _store;
    private MpvPlayback? _mpv;
    private IntPtr _boundSwapChain;

    // Seek bar preview: started on the first hover of each video
    private SeekPreviewer? _seekPreviewer;
    private double _seekPreviewX;
    private int _seekPreviewWidth;

    // Skip intro: parts to offer skipping, from the server or chapter names
    private IReadOnlyList<SkipSegment> _serverSkipSegments = [];
    private IReadOnlyList<SkipSegment> _skipSegments = [];
    private readonly HashSet<SkipSegment> _autoSkipped = [];
    private SkipSegment? _offeredSkip;
    private int _skipSegmentsVersion;

    // Skip silence: mpv reports quiet parts and the player plays them fast
    private const double SilenceSpeed = 4;
    private bool _isSkipSilenceOn;
    private bool _isHurryingSilence;
    private double _silenceEndsAt = -1;
    private double _normalSpeed = 1;

    // Queue and current file
    private IReadOnlyList<MediaItem> _queue = [];
    private int _queueIndex;
    private MediaItem? _currentMedia;
    private double _resumedFrom;

    /// <summary>With "Ask" resume: the saved point offered through the banner's Resume button.</summary>
    private double _offeredResumeAt;

    // Playback state reported by mpv
    private bool _isFileLoaded;
    private bool _isAtEnd;
    private bool _isPaused = true;
    private bool _isMuted;
    private double _durationSeconds;
    private double _positionSeconds;
    private double _bufferedSeconds;
    private double _lastSavedPosition;

    // Tracks and per-file settings
    private IReadOnlyList<MpvTrack> _audioTracks = [];
    private IReadOnlyList<MpvTrack> _subtitleTracks = [];
    private long _selectedAudioId = -1;
    private long _selectedSubtitleId = -1;
    private long _lastSubtitleId = -1;
    private double _subtitleDelay;
    private IReadOnlyList<MpvChapter> _chapters = [];
    private int _currentChapter = -1;
    private double _audioDelay;

    // UI state
    private bool _isInitializing = true;
    private bool _isUpdatingFileSettings;
    private bool _isFullscreen;
    private bool _isMini;
    private bool _isSheetOpen;
    private bool _controlsVisible = true;
    private bool _isPointerOverBars;
    private bool _isPointerInside;
    private bool _isCursorHidden;
    private bool _isDraggingSeek;
    private double _dragPosition;
    private DateTime _lastDragSeekUtc;
    private VideoZoomMode _zoomMode;
    private int _autoplaySecondsLeft;
    private DateTime _lastDoubleTapUtc;
    private Storyboard? _ringStoryboard;

    public PlayerView()
    {
        var savedVolume = PlayerSettings.Volume;
        InitializeComponent();

        var dispatcher = DispatcherQueue.GetForCurrentThread()!;
        _controlsHideTimer = CreateTimer(dispatcher, TimeSpan.FromSeconds(PlayerSettings.ControlsHideSeconds), ControlsHideTimer_Tick);
        _singleTapTimer = CreateTimer(dispatcher, TimeSpan.FromMilliseconds(250), (_, _) => TogglePause());
        _resumeBannerTimer = CreateTimer(dispatcher, TimeSpan.FromSeconds(8), (_, _) => HideResumeBanner());
        _osdTimer = CreateTimer(dispatcher, TimeSpan.FromSeconds(1.5), (_, _) => OsdBanner.Visibility = Visibility.Collapsed);
        _autoplayTimer = CreateTimer(dispatcher, TimeSpan.FromSeconds(1), AutoplayTimer_Tick, repeating: true);
        _posterTimer = CreateTimer(dispatcher, TimeSpan.FromMilliseconds(350), (_, _) => Motion.Fade(PosterFrame, 0, Motion.Slow));

        ZoomSegments.Items = ["Fit", "Fill screen", "Stretch"];
        SpeedSegments.Items = SpeedLabels;
        SpeedSegments.SelectedIndex = Array.IndexOf(Speeds, 1d);

        VolumeSlider.Value = savedVolume;
        AutoplayToggle.IsOn = PlayerSettings.AutoplayNext;
        SetZoomMode(PlayerSettings.ZoomMode);
        _isInitializing = false;

        Motion.SetOpacity(PulseBadge, 0);
        Motion.SetScale(SeekKnob, 0);
        MiniOverlay.PointerPressed += MiniSurface_PointerPressed;
        SizeChanged += (_, e) =>
        {
            PlayerRoot.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
            UpdateSubtitlePosition();
        };

        // Preview so player shortcuts win over focused buttons and sliders inside the player.
        PreviewKeyDown += Player_PreviewKeyDown;
    }

    /// <summary>The user asked to leave the player (Back button or Escape).</summary>
    public event EventHandler? BackRequested;

    /// <summary>The user pressed F, double-clicked the video or used the fullscreen button.</summary>
    public event EventHandler? FullscreenToggleRequested;

    /// <summary>The picture-in-picture button, I, or the mini player's expand button.</summary>
    public event EventHandler? MiniPlayerToggleRequested;

    /// <summary>The mini player's close button.</summary>
    public event EventHandler? MiniPlayerCloseRequested;

    /// <summary>A different video started (next/previous, autoplay or a new request).</summary>
    public event EventHandler<MediaItem>? MediaChanged;

    private bool _isStandalone;

    /// <summary>
    /// True when the player is its own window (a file opened from Explorer) rather than a page
    /// inside the library. There is nowhere to go back to, so the Back button is hidden and
    /// Escape only leaves fullscreen.
    /// </summary>
    public bool IsStandalone
    {
        get => _isStandalone;
        set
        {
            _isStandalone = value;
            BackButton.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private bool HasPrevious => _queueIndex > 0;
    private bool HasNext => _queueIndex < _queue.Count - 1;
    private MediaItem? NextMedia => HasNext ? _queue[_queueIndex + 1] : null;

    // ---------- Public API used by the window ----------

    public void Initialize(LibraryStore store)
    {
        _store = store;
        _videoMenu = new VideoMenu(store, this);
    }

    private VideoMenu? _videoMenu;

    /// <summary>Plays <paramref name="media"/>; next/previous move through <paramref name="queue"/>.</summary>
    public void Play(MediaItem media, IReadOnlyList<MediaItem> queue)
    {
        var index = queue.ToList().FindIndex(item => string.Equals(item.FilePath, media.FilePath, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            _queue = [media];
            index = 0;
        }
        else
        {
            _queue = queue;
        }

        PlayQueueItem(index, fromCard: true);
    }

    /// <summary>Saves progress and stops playback. Called when leaving the player.</summary>
    public void Stop()
    {
        SaveProgress();
        StopRemote();
        StopSeekPreview();
        HideEndPanel();
        HideResumeBanner();
        SetSheetOpen(false);
        _singleTapTimer.Stop();
        _controlsHideTimer.Stop();
        _isFileLoaded = false;
        SetCursorHidden(false);
        TryMpv(mpv => mpv.Stop());
    }

    public void ShowStatusMessage(string message)
    {
        PlaybackProgress.IsActive = false;
        PlaybackStatusText.Text = message;
        PlayerStatus.Visibility = Visibility.Visible;
    }

    /// <summary>Loads an external subtitle file into the current video.</summary>
    public void AddSubtitle(string filePath)
    {
        if (_mpv is null || !_isFileLoaded)
        {
            ShowOsd("Start a video before adding subtitles");
            return;
        }

        try
        {
            _mpv.AddSubtitle(filePath);
            RefreshTrackOptions();
            UpdateSubtitlePosition();
            ShowOsd($"Subtitles: {Path.GetFileName(filePath)}");
        }
        catch (Exception exception)
        {
            ShowOsd($"Couldn’t load subtitles: {exception.Message}");
        }
    }

    /// <summary>Updates the fullscreen icon. The window switches the presenter.</summary>
    public void SetFullscreen(bool enabled)
    {
        _isFullscreen = enabled;
        EnterFullscreenIcon.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        ExitFullscreenIcon.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(FullscreenButton, enabled ? "Exit fullscreen (F)" : "Fullscreen (F)");
        ShowControlsTemporarily();
    }

    /// <summary>Switches between the full player and the small picture-in-picture overlay.</summary>
    public void SetMiniMode(bool enabled)
    {
        _isMini = enabled;
        var full = enabled ? Visibility.Collapsed : Visibility.Visible;
        PlayerHeaderBar.Visibility = full;
        PlayerControlsBar.Visibility = full;
        NoticeArea.Visibility = full;
        Scrims.Visibility = full;
        MiniOverlay.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        UpdateSubtitlePosition();
        if (enabled)
        {
            SetSheetOpen(false);
            _controlsHideTimer.Stop();
            SetCursorHidden(false);
            UpdateMiniChrome(animate: false);
            UpdateMiniProgress();
        }
        else
        {
            _controlsVisible = false;
            ShowControlsTemporarily();
        }
        DispatcherQueue.TryEnqueue(() => Focus(FocusState.Programmatic));
    }

    /// <summary>Saves progress and releases mpv and the swap chain. Call once when the window closes.</summary>
    public void Shutdown()
    {
        SaveProgress();
        StopRemote();
        StopSeekPreview();
        foreach (var timer in new[] { _controlsHideTimer, _singleTapTimer, _resumeBannerTimer, _osdTimer, _autoplayTimer, _posterTimer })
            timer.Stop();
        SetCursorHidden(false);

        if (_mpv is not null)
        {
            _mpv.SwapChainChanged -= Mpv_SwapChainChanged;
            _mpv.PropertyChanged -= Mpv_PropertyChanged;
        }
        if (_boundSwapChain != IntPtr.Zero)
            SwapChainPanelInterop.SetSwapChain(VideoPanel, IntPtr.Zero);
        _mpv?.Dispose();
        _mpv = null;
    }

    // ---------- Queue ----------

    private void PlayQueueItem(int index, bool fromCard = false)
    {
        SaveProgress();
        StopRemote();
        StopSeekPreview();
        HideEndPanel();
        HideResumeBanner();

        _queueIndex = index;
        _currentMedia = _queue[index];
        // Settings › Playback › Resume decides whether the saved point is used, offered or ignored.
        // Streamed videos resume from where the server says; local files from Iris's own record.
        var savedPosition = _currentMedia.IsRemote
            ? _currentMedia.ServerResumeSeconds
            : _store?.GetResumePosition(_currentMedia) ?? 0;
        var resumeMode = PlayerSettings.Resume;
        _resumedFrom = resumeMode == ResumeMode.Always ? savedPosition : 0;
        _offeredResumeAt = resumeMode == ResumeMode.Ask ? savedPosition : 0;
        if (_currentMedia.StartAtSeconds is { } startAt)
        {
            _resumedFrom = startAt;
            _offeredResumeAt = 0;
        }

        _isFileLoaded = false;
        _isAtEnd = false;
        _isPaused = true;
        _durationSeconds = 0;
        _positionSeconds = 0;
        _bufferedSeconds = 0;
        _lastSavedPosition = 0;

        PlayerTitle.Text = _currentMedia.Title;
        MiniTitle.Text = _currentMedia.Title;
        UpdateDetails();
        MediaChanged?.Invoke(this, _currentMedia);
        PlaybackStatusText.Text = "Starting mpv…";
        PlaybackProgress.IsActive = true;
        PlayerStatus.Visibility = Visibility.Visible;
        PlayPauseButton.IsEnabled = false;
        LoadSubtitleButton.IsEnabled = false;
        SetPlayPauseGlyph(paused: true);
        UpdateTimes();
        UpdateQueueControls();
        ResetPerFileSettings();
        AutoplayToggle.IsOn = PlayerSettings.AutoplayNext;
        if (PlayerSettings.ZoomMode != _zoomMode)
            SetZoomMode(PlayerSettings.ZoomMode);
        ShowPoster(_currentMedia, fromCard);
        ShowControlsTemporarily();

        if (_mpv is not null)
            LoadCurrentMedia();
        else if (VideoHost.ActualWidth > 0 && VideoHost.ActualHeight > 0)
            InitializePlayer();
        // Otherwise VideoHost_SizeChanged starts mpv once the view has been laid out.

        // The view may have only just become visible, so focus after layout.
        DispatcherQueue.TryEnqueue(() => Focus(FocusState.Programmatic));
    }

    private void PlayNext()
    {
        if (HasNext)
            PlayQueueItem(_queueIndex + 1);
    }

    /// <summary>Restarts the current video if it has been playing a while; otherwise goes back one.</summary>
    private void PlayPrevious()
    {
        if (HasPrevious && _positionSeconds <= PreviousRestartThresholdSeconds)
        {
            PlayQueueItem(_queueIndex - 1);
            return;
        }

        if (_isFileLoaded)
        {
            HideEndPanel();
            TryMpv(mpv =>
            {
                if (_isAtEnd)
                    mpv.Replay();
                else
                    mpv.Seek(0);
            });
        }
    }

    private void UpdateQueueControls()
    {
        PreviousButton.IsEnabled = HasPrevious || _isFileLoaded;
        NextButton.IsEnabled = HasNext;
        ToolTipService.SetToolTip(NextButton, NextMedia is { } next ? $"Next: {next.Title} (Shift+N)" : "Next (Shift+N)");
        UpdateDetails();
    }

    /// <summary>"Folder · 3 of 12 · 4K" under the title.</summary>
    private void UpdateDetails()
    {
        if (_currentMedia is null)
        {
            PlayerDetails.Text = string.Empty;
            return;
        }

        var parts = new List<string> { _currentMedia.FolderName };
        if (_queue.Count > 1)
            parts.Add($"{_queueIndex + 1} of {_queue.Count}");
        if (!string.IsNullOrEmpty(_currentMedia.QualityLabel))
            parts.Add(_currentMedia.QualityLabel);
        if (_currentChapter >= 0 && _currentChapter < _chapters.Count)
            parts.Add(_chapters[_currentChapter].DisplayName);
        PlayerDetails.Text = string.Join("  ·  ", parts);
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => PlayPrevious();

    private void Next_Click(object sender, RoutedEventArgs e) => PlayNext();

    // ---------- Poster (card to player) ----------

    private void ShowPoster(MediaItem media, bool fromCard)
    {
        _posterTimer.Stop();
        if (media.Thumbnail is null)
        {
            PosterFrame.Visibility = Visibility.Collapsed;
            return;
        }

        PosterImage.Source = media.Thumbnail;
        PosterFrame.Visibility = Visibility.Visible;
        Motion.SetOpacity(PosterFrame, 1);
        SizePoster();

        if (!fromCard)
            return;
        try
        {
            ConnectedAnimationService.GetForCurrentView().GetAnimation(MediaCard.PlayerAnimationKey)?.TryStart(PosterFrame);
        }
        catch (Exception)
        {
            // The animation is decoration; playback continues without it.
        }
    }

    /// <summary>The poster is the largest 16:9 frame that fits, like the video will be.</summary>
    private void SizePoster()
    {
        var width = VideoHost.ActualWidth;
        var height = VideoHost.ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var posterWidth = Math.Min(width, height * 16 / 9);
        PosterFrame.Width = posterWidth;
        PosterFrame.Height = posterWidth * 9 / 16;
    }

    // ---------- mpv lifecycle ----------

    private void VideoHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        SizePoster();
        var scale = VideoPanel.XamlRoot?.RasterizationScale ?? 1.0;
        var width = Math.Max(1, (int)Math.Round(e.NewSize.Width * scale));
        var height = Math.Max(1, (int)Math.Round(e.NewSize.Height * scale));

        if (_mpv is null)
        {
            if (_currentMedia is not null && e.NewSize.Width > 0 && e.NewSize.Height > 0)
                InitializePlayer(width, height);
            return;
        }

        _mpv.Resize(width, height);
    }

    private void InitializePlayer(int? width = null, int? height = null)
    {
        if (_mpv is not null)
            return;

        try
        {
            var scale = VideoPanel.XamlRoot?.RasterizationScale ?? 1.0;
            var outputWidth = width ?? Math.Max(1, (int)Math.Round(VideoHost.ActualWidth * scale));
            var outputHeight = height ?? Math.Max(1, (int)Math.Round(VideoHost.ActualHeight * scale));
            var dispatcher = DispatcherQueue.GetForCurrentThread()
                             ?? throw new InvalidOperationException("The Windows UI dispatcher is unavailable.");

            _mpv = new MpvPlayback(dispatcher, outputWidth, outputHeight);
            _mpv.SwapChainChanged += Mpv_SwapChainChanged;
            _mpv.PropertyChanged += Mpv_PropertyChanged;

            var swapChain = _mpv.CurrentSwapChain;
            if (swapChain != IntPtr.Zero)
                Mpv_SwapChainChanged(swapChain);

            _mpv.SetVolume(VolumeSlider.Value);
            _mpv.SetZoomMode(_zoomMode);
            LoadCurrentMedia();
        }
        catch (Exception exception)
        {
            _mpv?.Dispose();
            _mpv = null;
            ShowStatusMessage($"Could not start mpv: {exception.Message}");
        }
    }

    /// <summary>
    /// Applies the Settings page's preferences that mpv needs told about: default speed, subtitle
    /// size and the preferred subtitle and audio languages. Runs before each file loads, so
    /// changes made in Settings take effect on the next video.
    /// </summary>
    private void ApplyPreferences()
    {
        TryMpv(mpv =>
        {
            mpv.SetSpeed(PlayerSettings.DefaultSpeed);
            _normalSpeed = PlayerSettings.DefaultSpeed;
            _isHurryingSilence = false;
            _silenceEndsAt = -1;
            SetSkipSilence(PlayerSettings.SkipSilence);
            mpv.SetSubtitleScale(PlayerSettings.SubtitleScale * SubtitleBaseScale);
            mpv.SetPreferredLanguages(
                PlayerSettings.SubtitleLanguage == PlayerSettings.SubtitlesOff ? string.Empty : PlayerSettings.SubtitleLanguage,
                PlayerSettings.AudioLanguage,
                PlayerSettings.SubtitleLanguage == PlayerSettings.SubtitlesOff);
        });
    }

    private void LoadCurrentMedia()
    {
        if (_mpv is null || _currentMedia is null)
            return;

        ApplyPreferences();

        try
        {
            _mpv.LoadFile(_currentMedia.StreamUrl ?? _currentMedia.FilePath, _resumedFrom);
        }
        catch (Exception exception)
        {
            ShowStatusMessage($"Could not open this video: {exception.Message}");
        }
    }

    private void Mpv_SwapChainChanged(IntPtr swapChain)
    {
        _boundSwapChain = swapChain;
        SwapChainPanelInterop.SetSwapChain(VideoPanel, swapChain);
    }

    private void Mpv_PropertyChanged(string name, object? value)
    {
        switch (name)
        {
            case "file-loaded":
                OnFileLoaded();
                break;
            case "time-pos" when value is double position:
                _positionSeconds = position;
                UpdateTimes();
                SaveProgressIfDue();
                if (_isHurryingSilence && _silenceEndsAt >= 0 && position >= _silenceEndsAt)
                    EndSilenceHurry();
                UpdateSkipSegment();
                break;
            case "duration" when value is double duration:
                _durationSeconds = Math.Max(0, duration);
                UpdateTimes();
                UpdateChapterMarks();
                RefreshSkipSegments();
                break;
            case $"af-metadata/{MpvPlayback.SilenceFilterLabel}" when value is string report:
                OnSilenceReport(report);
                break;
            case "seeking" when value is true:
                // Silence is measured from where playback is; after a jump it starts over.
                EndSilenceHurry();
                break;
            case "chapter" when value is long chapter:
                _currentChapter = (int)chapter;
                UpdateDetails();
                BuildChapterRows();
                break;
            case "demuxer-cache-duration" when value is double buffered:
                _bufferedSeconds = Math.Max(0, buffered);
                UpdateSeekVisuals();
                break;
            case "pause" when value is bool paused:
                _isPaused = paused;
                SetPlayPauseGlyph(paused);
                if (paused)
                {
                    SaveProgress();
                    SetControlsVisible(true);
                }
                else
                {
                    SaveProgress();
                    ShowControlsTemporarily();
                }
                UpdateMiniChrome(animate: true);
                break;
            case "osd-dimensions":
                // The video moved or resized inside the window (resize, zoom, a new video).
                UpdateSubtitlePosition();
                break;
            case "eof-reached" when value is bool atEnd:
                OnEndReachedChanged(atEnd);
                break;
            case "volume" when value is double volume:
                // mpv's report only moves the slider. Sending it back would echo: reports arrive one
                // change late, so the two values bounce back and forth and swamp mpv (it then takes
                // many seconds to show the first frame).
                _isShowingMpvVolume = true;
                try
                {
                    VolumeSlider.Value = Math.Clamp(volume, VolumeSlider.Minimum, VolumeSlider.Maximum);
                }
                finally
                {
                    _isShowingMpvVolume = false;
                }
                break;
            case "mute" when value is bool muted:
                _isMuted = muted;
                MuteIcon.Glyph = muted ? MutedGlyph : VolumeGlyph;
                break;
            case "speed" when value is double speed:
                // Skip silence's own speed-up isn't the viewer's speed choice.
                if (Math.Abs(speed - SilenceSpeed) > 0.001)
                {
                    _normalSpeed = speed;
                    UpdatePlaybackSpeedSelection(speed);
                }
                break;
            case "playback-error" when value is int error:
                ShowStatusMessage($"mpv could not play this file (error {error}).");
                PlayPauseButton.IsEnabled = false;
                PosterFrame.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void OnFileLoaded()
    {
        _isFileLoaded = true;
        SyncPauseState();
        PlaybackProgress.IsActive = false;
        PlayerStatus.Visibility = Visibility.Collapsed;
        PlayPauseButton.IsEnabled = true;
        SeekBackwardButton.IsEnabled = true;
        SeekForwardButton.IsEnabled = true;
        MuteButton.IsEnabled = true;
        LoadSubtitleButton.IsEnabled = true;
        ApplyStartTracks();
        if (_currentMedia?.Remote is { } remote)
        {
            remote.ReportStarted(_resumedFrom, _isPaused);
            _remoteStarted = true;
        }
        RefreshTrackOptions();
        LoadChapters();
        LoadServerSkipSegments();
        UpdateQueueControls();
        _posterTimer.Stop();
        _posterTimer.Start();
        ShowControlsTemporarily();
        UpdateSubtitlePosition();

        // Recently played and Continue watching are for files on this PC; server videos sync in a later version.
        if (_currentMedia is { IsRemote: false })
            _store?.RecordRecent(_currentMedia);
        if (_resumedFrom > 0)
            ShowResumeBanner();
        else if (_offeredResumeAt > 0)
            ShowResumeOffer();
    }

    // ---------- Progress and resume ----------

    private void SaveProgressIfDue()
    {
        if (Math.Abs(_positionSeconds - _lastSavedPosition) >= ProgressSaveIntervalSeconds)
            SaveProgress();
    }

    private void SaveProgress()
    {
        // Server videos keep their resume point on the server.
        if (_currentMedia?.Remote is { } remote)
        {
            ReportRemote(remote);
            return;
        }
        if (_store is null || _currentMedia is null || _currentMedia.IsRemote || !_isFileLoaded || _durationSeconds <= 0)
            return;

        var position = _isAtEnd ? _durationSeconds : _positionSeconds;
        _store.SaveProgress(_currentMedia, position, _durationSeconds);
        _lastSavedPosition = _positionSeconds;
    }

    private bool _remoteStarted;

    /// <summary>Start, progress and (at the end) stop reports for a server video.</summary>
    private void ReportRemote(IRemotePlayback remote)
    {
        if (!_isFileLoaded)
            return;

        var position = Math.Max(0, _positionSeconds);
        if (_isAtEnd)
        {
            if (_remoteStarted)
                remote.ReportStopped(Math.Max(_durationSeconds, position));
            _remoteStarted = false;
        }
        else if (!_remoteStarted)
        {
            remote.ReportStarted(position, _isPaused);
            _remoteStarted = true;
        }
        else
        {
            remote.ReportProgress(position, _isPaused);
        }
        _lastSavedPosition = _positionSeconds;
    }

    /// <summary>Tells the server playback stopped (leaving the player or switching video).</summary>
    private void StopRemote()
    {
        if (_currentMedia?.Remote is { } remote && _remoteStarted && _isFileLoaded)
            remote.ReportStopped(_isAtEnd ? Math.Max(_durationSeconds, _positionSeconds) : _positionSeconds);
        _remoteStarted = false;
    }

    private void ShowResumeBanner()
    {
        ResumeBannerText.Text = $"Resumed from {FormatTime(_resumedFrom)}";
        StartOverButton.Visibility = Visibility.Visible;
        ResumeOfferButton.Visibility = Visibility.Collapsed;
        ResumeBanner.Visibility = Visibility.Visible;
        Motion.EnterFromBelow(ResumeBanner, TimeSpan.Zero, 16);
        _resumeBannerTimer.Stop();
        _resumeBannerTimer.Start();
    }

    /// <summary>"Ask" resume: the video started from the beginning; offer to jump to where it stopped.</summary>
    private void ShowResumeOffer()
    {
        ResumeBannerText.Text = $"You stopped at {FormatTime(_offeredResumeAt)}";
        StartOverButton.Visibility = Visibility.Collapsed;
        ResumeOfferButton.Visibility = Visibility.Visible;
        ResumeBanner.Visibility = Visibility.Visible;
        Motion.EnterFromBelow(ResumeBanner, TimeSpan.Zero, 16);
        _resumeBannerTimer.Stop();
        _resumeBannerTimer.Start();
    }

    private void ResumeOffer_Click(object sender, RoutedEventArgs e)
    {
        var position = _offeredResumeAt;
        HideResumeBanner();
        TryMpv(mpv => mpv.Seek(position));
        Focus(FocusState.Programmatic);
    }

    private void HideResumeBanner()
    {
        _resumeBannerTimer.Stop();
        ResumeBanner.Visibility = Visibility.Collapsed;
    }

    private void StartOver_Click(object sender, RoutedEventArgs e)
    {
        HideResumeBanner();
        TryMpv(mpv => mpv.Seek(0));
        Focus(FocusState.Programmatic);
    }

    // ---------- End of video and autoplay ----------

    private void OnEndReachedChanged(bool atEnd)
    {
        if (!_isFileLoaded)
            return;

        _isAtEnd = atEnd;
        if (!atEnd)
        {
            HideEndPanel();
            return;
        }

        SaveProgress();
        HideResumeBanner();
        _controlsHideTimer.Stop();
        SetControlsVisible(true);

        if (NextMedia is not null && PlayerSettings.AutoplayNext)
            ShowAutoplayCountdown();
        else
            ShowReplayPanel();
    }

    private void ShowAutoplayCountdown()
    {
        _autoplaySecondsLeft = PlayerSettings.AutoplayCountdownSeconds;
        ShowUpNextCard(NextMedia!, "UP NEXT", countdown: true);
        UpNextPlayButton.Visibility = Visibility.Visible;
        UpNextCancelButton.Visibility = Visibility.Visible;
        UpNextReplayButton.Visibility = Visibility.Collapsed;
        _autoplayTimer.Stop();
        _autoplayTimer.Start();
    }

    private void ShowReplayPanel()
    {
        _autoplayTimer.Stop();
        var next = NextMedia;
        ShowUpNextCard(next ?? _currentMedia!, next is null ? "FINISHED" : "UP NEXT", countdown: false);
        UpNextPlayButton.Visibility = next is null ? Visibility.Collapsed : Visibility.Visible;
        UpNextCancelButton.Visibility = Visibility.Collapsed;
        UpNextReplayButton.Visibility = Visibility.Visible;
    }

    private void ShowUpNextCard(MediaItem media, string heading, bool countdown)
    {
        var wasVisible = UpNextCard.Visibility == Visibility.Visible;
        UpNextHeading.Text = heading;
        UpNextTitle.Text = media.Title;
        UpNextImage.Source = media.Thumbnail;
        UpNextRing.Visibility = countdown ? Visibility.Visible : Visibility.Collapsed;
        UpNextCard.Visibility = Visibility.Visible;
        HideSkipSegmentButton();
        if (!wasVisible)
            Motion.EnterFromBelow(UpNextCard, TimeSpan.Zero, 16);

        _ringStoryboard?.Stop();
        if (!countdown)
            return;

        // The ring empties over the countdown, like a timer running down.
        var animation = new DoubleAnimation
        {
            From = 0,
            To = UpNextRingStroke.StrokeDashArray.FirstOrDefault(),
            Duration = TimeSpan.FromSeconds(PlayerSettings.AutoplayCountdownSeconds),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(animation, UpNextRingStroke);
        Storyboard.SetTargetProperty(animation, "StrokeDashOffset");
        _ringStoryboard = new Storyboard();
        _ringStoryboard.Children.Add(animation);
        _ringStoryboard.Begin();
    }

    private void HideEndPanel()
    {
        _autoplayTimer.Stop();
        _ringStoryboard?.Stop();
        UpNextCard.Visibility = Visibility.Collapsed;
    }

    private void AutoplayTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _autoplaySecondsLeft--;
        if (_autoplaySecondsLeft <= 0)
            PlayNext();
    }

    private void EndPlayNext_Click(object sender, RoutedEventArgs e) => PlayNext();

    private void EndCancel_Click(object sender, RoutedEventArgs e)
    {
        ShowReplayPanel();
        Focus(FocusState.Programmatic);
    }

    private void EndReplay_Click(object sender, RoutedEventArgs e) => Replay();

    private void Replay()
    {
        HideEndPanel();
        TryMpv(mpv => mpv.Replay());
        Focus(FocusState.Programmatic);
    }

    // ---------- Play, seek and volume ----------

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePause();

    private void TogglePause()
    {
        if (_mpv is null || !_isFileLoaded)
            return;

        if (_isAtEnd)
        {
            Replay();
            return;
        }

        try
        {
            _isPaused = _mpv.TogglePause();
            SetPlayPauseGlyph(_isPaused);
            // The pulse shows what just happened: the play symbol when playback resumes.
            PulseIcon.Glyph = _isPaused ? PauseGlyph : PlayGlyph;
            if (!_isMini)
                Motion.Pulse(PulseBadge);
        }
        catch (Exception exception)
        {
            ShowStatusMessage($"Could not toggle playback: {exception.Message}");
        }
    }

    private void SetPlayPauseGlyph(bool paused)
    {
        PlayPauseIcon.Glyph = paused ? PlayGlyph : PauseGlyph;
        MiniPlayPauseIcon.Glyph = PlayPauseIcon.Glyph;
        var label = paused ? "Play" : "Pause";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlayPauseButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MiniPlayPauseButton, label);
    }

    private static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            seconds = 0;
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private double DisplayedPosition => _isDraggingSeek ? _dragPosition : _positionSeconds;

    private void UpdateTimes()
    {
        var position = DisplayedPosition;
        CurrentTimeText.Text = FormatTime(position);
        RemainingTimeText.Text = "-" + FormatTime(Math.Max(0, _durationSeconds - position));
        MiniElapsedText.Text = CurrentTimeText.Text;
        MiniRemainingText.Text = RemainingTimeText.Text;
        UpdateSeekVisuals();
        UpdateMiniProgress();
    }

    private void UpdateSeekVisuals()
    {
        var width = SeekTrack.ActualWidth;
        if (width <= 0)
            return;

        var played = _durationSeconds > 0 ? Math.Clamp(DisplayedPosition / _durationSeconds, 0, 1) : 0;
        var buffered = _durationSeconds > 0 ? Math.Clamp((_positionSeconds + _bufferedSeconds) / _durationSeconds, 0, 1) : 0;
        SeekPlayed.Width = width * played;
        SeekBuffered.Width = width * Math.Max(played, buffered);
        Canvas.SetLeft(SeekKnob, width * played - SeekKnob.Width / 2);
    }

    private void UpdateMiniProgress()
    {
        if (!_isMini)
            return;
        var played = _durationSeconds > 0 ? Math.Clamp(_positionSeconds / _durationSeconds, 0, 1) : 0;
        MiniProgressFill.Width = MiniProgressTrack.ActualWidth * played;
    }

    private void SeekTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateSeekVisuals();
        UpdateChapterMarks();
    }

    private double PositionAt(PointerRoutedEventArgs e)
    {
        var width = Math.Max(1, SeekTrack.ActualWidth);
        var x = e.GetCurrentPoint(SeekTrack).Position.X;
        return Math.Clamp(x / width, 0, 1) * _durationSeconds;
    }

    private bool CanSeek => _isFileLoaded && _durationSeconds > 0;

    private void SeekTrack_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!CanSeek)
            return;
        SeekRail.Height = 7;
        UpdateChapterMarks();
        Motion.Scale(SeekKnob, 1, Motion.Fast, decelerate: true);
    }

    private void SeekTrack_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingSeek)
            return;
        SeekRail.Height = 4;
        UpdateChapterMarks();
        Motion.Scale(SeekKnob, 0, Motion.Fast, decelerate: true);
        SeekPreviewBadge.Visibility = Visibility.Collapsed;
    }

    private void SeekTrack_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!CanSeek)
            return;

        SeekTrack.CapturePointer(e.Pointer);
        _isDraggingSeek = true;
        _dragPosition = PositionAt(e);
        UpdateTimes();
        UpdateSeekPreview(e);
        e.Handled = true;
    }

    private void SeekTrack_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!CanSeek)
            return;

        UpdateSeekPreview(e);
        if (!_isDraggingSeek)
            return;

        _dragPosition = PositionAt(e);
        UpdateTimes();
        if (DateTime.UtcNow - _lastDragSeekUtc >= SeekThrottle)
        {
            _lastDragSeekUtc = DateTime.UtcNow;
            var target = _dragPosition;
            TryMpv(mpv => mpv.Seek(target));
        }
    }

    private void SeekTrack_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingSeek)
            return;

        _dragPosition = PositionAt(e);
        var target = _dragPosition;
        _positionSeconds = target;
        _isDraggingSeek = false;
        SeekTrack.ReleasePointerCapture(e.Pointer);
        TryMpv(mpv => mpv.Seek(target));
        UpdateTimes();
        e.Handled = true;
    }

    private void SeekTrack_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingSeek)
            return;
        _isDraggingSeek = false;
        UpdateTimes();
    }

    private void UpdateSeekPreview(PointerRoutedEventArgs e)
    {
        var width = SeekTrack.ActualWidth;
        var x = Math.Clamp(e.GetCurrentPoint(SeekTrack).Position.X, 0, width);
        var time = width > 0 ? x / width * _durationSeconds : 0;
        SeekPreviewText.Text = FormatTime(time);
        var chapter = ChapterAt(time);
        SeekPreviewChapter.Text = chapter?.DisplayName ?? string.Empty;
        SeekPreviewChapter.Visibility = chapter is null ? Visibility.Collapsed : Visibility.Visible;
        SeekPreviewBadge.Visibility = Visibility.Visible;
        _seekPreviewX = x;
        PlaceSeekPreview();
        RequestSeekPreviewFrame(time);
    }

    /// <summary>Puts the badge above the pointer, kept inside the seek bar's ends.</summary>
    private void PlaceSeekPreview()
    {
        var width = SeekTrack.ActualWidth;
        SeekPreviewBadge.UpdateLayout();
        Canvas.SetTop(SeekPreviewBadge, -SeekPreviewBadge.ActualHeight - 12);
        var badgeWidth = SeekPreviewBadge.ActualWidth > 0 ? SeekPreviewBadge.ActualWidth : 52;
        Canvas.SetLeft(SeekPreviewBadge, Math.Clamp(_seekPreviewX - badgeWidth / 2, 0, Math.Max(0, width - badgeWidth)));
    }

    private void RequestSeekPreviewFrame(double seconds)
    {
        if (!PlayerSettings.SeekPreview || _currentMedia is null || _durationSeconds <= 0)
        {
            if (_seekPreviewer is not null)
                StopSeekPreview();
            return;
        }

        // A size changed in Settings mid-video starts a fresh previewer with sharper or smaller pictures.
        var width = PlayerSettings.SeekPreviewWidth;
        if (_seekPreviewer is not null && width != _seekPreviewWidth)
            StopSeekPreview();

        if (_seekPreviewer is null)
        {
            _seekPreviewWidth = width;
            SeekPreviewFrame.Width = width;
            // Pictures are made 1.5× the on-screen size so they stay sharp on high-DPI screens.
            SeekPreviewer? previewer = null;
            previewer = new SeekPreviewer(_currentMedia.StreamUrl ?? _currentMedia.FilePath, _durationSeconds, width * 3 / 2,
                (_, jpeg) => DispatcherQueue.TryEnqueue(() => ShowSeekPreviewFrame(previewer!, jpeg)));
            _seekPreviewer = previewer;
        }
        _seekPreviewer.Request(seconds);
    }

    private async void ShowSeekPreviewFrame(SeekPreviewer from, byte[] jpeg)
    {
        // Ignore pictures from a previous video, or arriving after the pointer left the bar.
        if (from != _seekPreviewer || SeekPreviewBadge.Visibility != Visibility.Visible)
            return;

        try
        {
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(jpeg);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (from != _seekPreviewer)
                return;

            SeekPreviewImage.Source = bitmap;
            if (SeekPreviewFrame.Visibility != Visibility.Visible)
            {
                SeekPreviewFrame.Visibility = Visibility.Visible;
                PlaceSeekPreview();
            }
        }
        catch (Exception)
        {
            // A broken picture just means no preview this time.
        }
    }

    private void StopSeekPreview()
    {
        _seekPreviewer?.Dispose();
        _seekPreviewer = null;
        SeekPreviewImage.Source = null;
        SeekPreviewFrame.Visibility = Visibility.Collapsed;
    }

    private void SeekBackward_Click(object sender, RoutedEventArgs e) => SeekBy(-PlayerSettings.LargeSkipSeconds);

    private void SeekForward_Click(object sender, RoutedEventArgs e) => SeekBy(PlayerSettings.LargeSkipSeconds);

    private void SeekBy(double seconds)
    {
        if (_mpv is null || !CanSeek)
            return;

        var position = Math.Clamp(_positionSeconds + seconds, 0, _durationSeconds);
        _positionSeconds = position;
        UpdateTimes();
        TryMpv(mpv => mpv.Seek(position));
    }

    private bool _isShowingMpvVolume;

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing || _isShowingMpvVolume)
            return;

        TryMpv(mpv => mpv.SetVolume(e.NewValue));
        PlayerSettings.Volume = e.NewValue;
    }

    private void ChangeVolume(double delta)
    {
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, VolumeSlider.Minimum, VolumeSlider.Maximum);
        ShowOsd($"Volume {VolumeSlider.Value:0}%");
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void ToggleMute()
    {
        if (_mpv is null)
            return;
        TryMpv(mpv => mpv.SetMuted(!_isMuted));
        ShowOsd(_isMuted ? "Unmuted" : "Muted");
    }

    // ---------- Settings sheet ----------

    private void SettingsToggle_Click(object sender, RoutedEventArgs e) => SetSheetOpen(!_isSheetOpen);

    private void CloseSettings_Click(object sender, RoutedEventArgs e)
    {
        SetSheetOpen(false);
        Focus(FocusState.Programmatic);
    }

    /// <summary>Slides the settings sheet in or out (333 ms, decelerate).</summary>
    private void SetSheetOpen(bool open)
    {
        SettingsToggle.IsChecked = open;
        if (_isSheetOpen == open)
            return;

        _isSheetOpen = open;
        var hidden = new Vector3((float)(SheetWidth * 1.04), 0, 0);
        if (open)
        {
            if (SettingsSheet.Visibility != Visibility.Visible)
            {
                SettingsSheet.Visibility = Visibility.Visible;
                Motion.SetTranslation(SettingsSheet, hidden);
            }
            SettingsSheet.IsHitTestVisible = true;
            Motion.Translate(SettingsSheet, Vector3.Zero, Motion.Slow);
            SetControlsVisible(true);
        }
        else
        {
            SettingsSheet.IsHitTestVisible = false;
            _isPointerOverBars = false;
            if (SettingsSheet.Visibility == Visibility.Visible)
                Motion.Translate(SettingsSheet, hidden, Motion.Slow);
            ShowControlsTemporarily();
        }
    }

    private void ResetPerFileSettings()
    {
        _isUpdatingFileSettings = true;
        AspectRatioCombo.SelectedIndex = 0;
        CropCombo.SelectedIndex = 0;
        _isUpdatingFileSettings = false;
        _audioDelay = 0;
        _subtitleDelay = 0;
        _audioTracks = [];
        _subtitleTracks = [];
        _selectedAudioId = -1;
        _selectedSubtitleId = -1;
        _lastSubtitleId = -1;
        _chapters = [];
        _currentChapter = -1;
        _skipSegmentsVersion++;
        _serverSkipSegments = [];
        _skipSegments = [];
        _autoSkipped.Clear();
        HideSkipSegmentButton();
        UpdateDelayTexts();
        BuildTrackRows();
        BuildChapterRows();
        UpdateChapterMarks();

        TryMpv(mpv =>
        {
            mpv.SetAudioDelay(0);
            mpv.SetSubtitleDelay(0);
            mpv.SetAspectOverride("-1");
            mpv.SetCropRatio(null);
        });
    }

    // Zoom is a lasting preference; crop and aspect ratio are per video.

    private void SetZoomMode(VideoZoomMode mode)
    {
        _zoomMode = mode;
        if (!_isInitializing)
            PlayerSettings.ZoomMode = mode;

        ZoomSegments.SelectedIndex = Array.IndexOf(ZoomModes, mode);
        ToolTipService.SetToolTip(ZoomButton, $"Zoom: {GetZoomLabel(mode)} (C)");
        TryMpv(mpv => mpv.SetZoomMode(mode));
    }

    private void CycleZoomMode()
    {
        var next = ZoomModes[(Array.IndexOf(ZoomModes, _zoomMode) + 1) % ZoomModes.Length];
        SetZoomMode(next);
        ShowOsd($"Zoom: {GetZoomLabel(next)}");
    }

    private static string GetZoomLabel(VideoZoomMode mode) => mode switch
    {
        VideoZoomMode.Fill => "Fill screen",
        VideoZoomMode.Stretch => "Stretch",
        _ => "Fit"
    };

    private void ZoomButton_Click(object sender, RoutedEventArgs e) => CycleZoomMode();

    private void ZoomSegments_SelectionChanged(object? sender, int index)
    {
        if (index >= 0 && index < ZoomModes.Length)
            SetZoomMode(ZoomModes[index]);
    }

    private void SpeedSegments_SelectionChanged(object? sender, int index)
    {
        if (index >= 0 && index < Speeds.Length)
        {
            var speed = Speeds[index];
            _normalSpeed = speed;
            // While skip silence hurries, the new speed starts when the sound comes back.
            if (!_isHurryingSilence)
            {
                TryMpv(mpv => mpv.SetSpeed(speed));
                UpdateSpeedLabel(speed);
            }
        }
    }

    private void UpdatePlaybackSpeedSelection(double speed)
    {
        var index = Array.FindIndex(Speeds, option => Math.Abs(option - speed) < 0.001);
        if (index >= 0)
            SpeedSegments.SelectedIndex = index;
        UpdateSpeedLabel(speed);
    }

    /// <summary>The bar shows the speed only when it isn't normal.</summary>
    private void UpdateSpeedLabel(double speed)
    {
        if (_isHurryingSilence)
        {
            SpeedLabel.Text = "Skipping silence";
            SpeedLabel.Visibility = Visibility.Visible;
            return;
        }
        var isNormal = Math.Abs(speed - 1) < 0.001;
        SpeedLabel.Text = $"{speed.ToString("0.##", CultureInfo.CurrentCulture)}×";
        SpeedLabel.Visibility = isNormal ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CropCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingFileSettings || CropCombo.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio))
            return;
        TryMpv(mpv => mpv.SetCropRatio(ratio > 0 ? ratio : null));
    }

    private void AspectRatioCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingFileSettings || AspectRatioCombo.SelectedItem is not ComboBoxItem { Tag: string aspect })
            return;
        TryMpv(mpv => mpv.SetAspectOverride(aspect));
    }

    private void SubtitleDelayDown_Click(object sender, RoutedEventArgs e) => AdjustSubtitleDelay(-0.1, showOsd: false);

    private void SubtitleDelayUp_Click(object sender, RoutedEventArgs e) => AdjustSubtitleDelay(0.1, showOsd: false);

    private void AudioDelayDown_Click(object sender, RoutedEventArgs e) => AdjustAudioDelay(-0.1);

    private void AudioDelayUp_Click(object sender, RoutedEventArgs e) => AdjustAudioDelay(0.1);

    private void AdjustSubtitleDelay(double delta, bool showOsd = true)
    {
        if (!_isFileLoaded)
            return;

        _subtitleDelay = Math.Clamp(Math.Round(_subtitleDelay + delta, 1), -DelayLimitSeconds, DelayLimitSeconds);
        var delay = _subtitleDelay;
        TryMpv(mpv => mpv.SetSubtitleDelay(delay));
        UpdateDelayTexts();
        if (showOsd)
            ShowOsd($"Subtitle delay {FormatDelay(delay)}");
    }

    private void AdjustAudioDelay(double delta)
    {
        if (!_isFileLoaded)
            return;

        _audioDelay = Math.Clamp(Math.Round(_audioDelay + delta, 1), -DelayLimitSeconds, DelayLimitSeconds);
        var delay = _audioDelay;
        TryMpv(mpv => mpv.SetAudioDelay(delay));
        UpdateDelayTexts();
    }

    private void UpdateDelayTexts()
    {
        SubtitleDelayText.Text = FormatDelay(_subtitleDelay);
        AudioDelayText.Text = FormatDelay(_audioDelay);
    }

    private static string FormatDelay(double seconds)
        => $"{seconds.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture)} s";

    private void AutoplayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isInitializing)
            PlayerSettings.AutoplayNext = AutoplayToggle.IsOn;
    }

    private void LoadSubtitle_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot?.ContentIslandEnvironment is not { } environment)
            return;

        string? path = null;
        try
        {
            path = FileDialogs.PickFile(Microsoft.UI.Win32Interop.GetWindowFromWindowId(environment.AppWindowId), "Subtitles",
                MediaLibraryService.SupportedSubtitleExtensions);
        }
        catch (COMException)
        {
            ShowOsd("Windows couldn’t open the file chooser. Try dropping the subtitle onto the player.");
        }
        if (path is not null)
            AddSubtitle(path);
        Focus(FocusState.Programmatic);
    }

    // ---------- Tracks ----------

    /// <summary>The audio and subtitle tracks picked before playing (Jellyfin details page).</summary>
    private void ApplyStartTracks()
    {
        if (_currentMedia?.StartAudioTrack is { } audio)
            TryMpv(mpv => mpv.SetAudioTrack(audio));
        if (_currentMedia?.StartSubtitleTrack is { } subtitle)
            TryMpv(mpv => mpv.SetSubtitleTrack(subtitle));
    }

    private void RefreshTrackOptions()
    {
        if (_mpv is null)
            return;

        _audioTracks = _mpv.GetTracks("audio");
        _subtitleTracks = _mpv.GetTracks("sub");
        _selectedAudioId = _audioTracks.FirstOrDefault(track => track.IsSelected)?.Id ?? -1;
        _selectedSubtitleId = _subtitleTracks.FirstOrDefault(track => track.IsSelected)?.Id ?? -1;
        if (_selectedSubtitleId >= 0)
            _lastSubtitleId = _selectedSubtitleId;
        BuildTrackRows();
    }

    private void BuildTrackRows()
    {
        SubtitleRows.Children.Clear();
        SubtitleRows.Children.Add(CreateSheetRow("Off", _selectedSubtitleId < 0, () => SelectSubtitle(-1)));
        foreach (var track in _subtitleTracks)
        {
            var id = track.Id;
            SubtitleRows.Children.Add(CreateSheetRow(track.DisplayName, id == _selectedSubtitleId, () => SelectSubtitle(id)));
        }

        AudioRows.Children.Clear();
        if (_audioTracks.Count == 0)
        {
            AudioRows.Children.Add(new TextBlock
            {
                Text = _isFileLoaded ? "No audio tracks" : "Audio tracks appear when the video starts",
                Margin = new Thickness(12, 8, 12, 8),
                Style = (Style)Application.Current.Resources["IrisCaptionTextStyle"]
            });
        }
        foreach (var track in _audioTracks)
        {
            var id = track.Id;
            AudioRows.Children.Add(CreateSheetRow(track.DisplayName, id == _selectedAudioId, () => SelectAudio(id)));
        }

        SubtitlesToggle.IsEnabled = _subtitleTracks.Count > 0;
        SubtitlesToggle.IsChecked = _selectedSubtitleId >= 0;
        var subtitleIconBrush = (Brush)Application.Current.Resources[_selectedSubtitleId >= 0 ? "IrisAccentLightBrush" : "IrisPlayerIconBrush"];
        SubtitlesIconFrame.Stroke = subtitleIconBrush;
        SubtitlesIconText.Stroke = subtitleIconBrush;
    }

    private Button CreateSheetRow(string text, bool isSelected, Action select, string? trailing = null)
    {
        var content = new Grid();
        content.Children.Add(new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, trailing is null ? 24 : 88, 0)
        });
        if (trailing is not null)
        {
            content.Children.Add(new TextBlock
            {
                Text = trailing,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, isSelected ? 24 : 0, 0),
                Style = (Style)Application.Current.Resources["IrisCaptionTextStyle"]
            });
        }
        if (isSelected)
        {
            content.Children.Add(new FontIcon
            {
                Glyph = CheckGlyph,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = (Brush)Application.Current.Resources["IrisAccentLightBrush"]
            });
        }

        var row = new Button
        {
            Style = (Style)Application.Current.Resources["IrisSheetRowButtonStyle"],
            Content = content
        };
        if (isSelected)
        {
            row.Background = (Brush)Application.Current.Resources["IrisAccentSoftBrush"];
            row.Foreground = (Brush)Application.Current.Resources["IrisTextBrush"];
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, isSelected ? $"{text}, selected" : text);
        row.Click += (_, _) => select();
        return row;
    }

    private void SelectSubtitle(long trackId)
    {
        TryMpv(mpv => mpv.SetSubtitleTrack(trackId));
        _selectedSubtitleId = trackId;
        if (trackId >= 0)
            _lastSubtitleId = trackId;
        BuildTrackRows();
        // Text and picture subtitles are positioned differently.
        UpdateSubtitlePosition();
    }

    private void SelectAudio(long trackId)
    {
        TryMpv(mpv => mpv.SetAudioTrack(trackId));
        _selectedAudioId = trackId;
        BuildTrackRows();
    }

    private void SubtitlesToggle_Click(object sender, RoutedEventArgs e) => ToggleSubtitles();

    /// <summary>Turns subtitles off, or back on with the last track used (or the first one).</summary>
    private void ToggleSubtitles()
    {
        if (_subtitleTracks.Count == 0)
        {
            SubtitlesToggle.IsChecked = false;
            return;
        }

        if (_selectedSubtitleId >= 0)
        {
            SelectSubtitle(-1);
            ShowOsd("Subtitles off");
            return;
        }

        var track = _subtitleTracks.FirstOrDefault(item => item.Id == _lastSubtitleId) ?? _subtitleTracks[0];
        SelectSubtitle(track.Id);
        ShowOsd($"Subtitles: {track.DisplayName}");
    }

    // ---------- Chapters ----------

    private void LoadChapters()
    {
        try
        {
            _chapters = _mpv?.GetChapters() ?? [];
        }
        catch (ObjectDisposedException)
        {
            _chapters = [];
        }

        // A single chapter is just the whole film; treat it as no chapters.
        if (_chapters.Count == 1)
            _chapters = [];
        BuildChapterRows();
        UpdateChapterMarks();
        UpdateDetails();
        RefreshSkipSegments();
    }

    // ---------- Skip intro ----------

    /// <summary>Asks the media server (Jellyfin) for the intros and credits it has marked.</summary>
    private async void LoadServerSkipSegments()
    {
        if (_currentMedia?.Remote is not { } remote)
            return;

        var version = _skipSegmentsVersion;
        IReadOnlyList<SkipSegment> segments;
        try
        {
            segments = await remote.GetSkipSegmentsAsync();
        }
        catch (Exception)
        {
            return;
        }
        if (version != _skipSegmentsVersion)
            return;
        _serverSkipSegments = segments;
        RefreshSkipSegments();
    }

    /// <summary>
    /// The server's segments when it has any (they're measured from the audio and video), otherwise the
    /// ones found from chapter names.
    /// </summary>
    private void RefreshSkipSegments()
    {
        _skipSegments = _serverSkipSegments.Count > 0
            ? _serverSkipSegments
            : _durationSeconds > 0
                ? SkipSegment.FromChapters(_chapters.Select(chapter => (chapter.Time, chapter.Title)).ToList(), _durationSeconds)
                : [];
        UpdateSkipSegment();
    }

    /// <summary>Shows the Skip button inside an intro, or skips it straight away when set to automatic.</summary>
    private void UpdateSkipSegment()
    {
        var mode = PlayerSettings.SkipIntro;
        var current = mode == SkipMode.Off || !CanSeek || _isAtEnd
            ? null
            // The last second isn't worth a button.
            : _skipSegments.FirstOrDefault(segment => _positionSeconds >= segment.Start && _positionSeconds < segment.End - 1);

        if (current is not null && mode == SkipMode.Automatic)
        {
            HideSkipSegmentButton();
            // Once per part: going back into it means it's wanted.
            if (!_isDraggingSeek && _autoSkipped.Add(current))
            {
                SkipPast(current);
                ShowOsd(current.SkippedText);
            }
            return;
        }

        if (current is null || UpNextCard.Visibility == Visibility.Visible)
        {
            HideSkipSegmentButton();
            return;
        }
        if (current == _offeredSkip)
            return;

        _offeredSkip = current;
        SkipSegmentText.Text = current.ButtonText;
        if (SkipSegmentButton.Visibility != Visibility.Visible)
        {
            SkipSegmentButton.Opacity = 0;
            SkipSegmentButton.Visibility = Visibility.Visible;
            Motion.Fade(SkipSegmentButton, 1, Motion.Slow);
        }
    }

    private void HideSkipSegmentButton()
    {
        _offeredSkip = null;
        SkipSegmentButton.Visibility = Visibility.Collapsed;
    }

    private void SkipSegment_Click(object sender, RoutedEventArgs e)
    {
        if (_offeredSkip is { } segment)
            SkipPast(segment);
    }

    /// <summary>Jumps to the end of <paramref name="segment"/>; credits that run to the end finish the video.</summary>
    private void SkipPast(SkipSegment segment)
    {
        var target = Math.Min(segment.End, _durationSeconds);
        HideSkipSegmentButton();
        _positionSeconds = target;
        TryMpv(mpv => mpv.Seek(target));
        UpdateTimes();
    }

    // ---------- Skip silence ----------

    /// <summary>Turns skip silence on or off for this player (and remembers it).</summary>
    private void SetSkipSilence(bool on)
    {
        _isSkipSilenceOn = on;
        SkipSilenceToggle.IsOn = on;
        if (!on)
            EndSilenceHurry();
        TryMpv(mpv => mpv.SetSilenceDetection(on));
    }

    private void SkipSilenceToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (SkipSilenceToggle.IsOn == _isSkipSilenceOn)
            return;
        PlayerSettings.SkipSilence = SkipSilenceToggle.IsOn;
        SetSkipSilence(SkipSilenceToggle.IsOn);
    }

    /// <summary>
    /// mpv's silence detector reports where the last silence started and ended (both keys stay once
    /// seen, so the newer one tells whether it's quiet now). Quiet parts are played at
    /// <see cref="SilenceSpeed"/>. The end is reported early, as audio is decoded ahead of what's
    /// heard, so the hurry carries on until playback reaches the reported end.
    /// </summary>
    private void OnSilenceReport(string report)
    {
        if (!_isSkipSilenceOn || string.IsNullOrWhiteSpace(report))
            return;

        double? start = null, end = null;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(report);
            start = ReadSeconds(json.RootElement, "lavfi.silence_start");
            end = ReadSeconds(json.RootElement, "lavfi.silence_end");
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        var isQuiet = start is not null && (end is null || start > end);
        if (isQuiet && !_isHurryingSilence)
        {
            if (_isPaused || !_isFileLoaded)
                return;
            _isHurryingSilence = true;
            _silenceEndsAt = -1;
            TryMpv(mpv => mpv.SetSpeed(SilenceSpeed));
            UpdateSpeedLabel(SilenceSpeed);
        }
        else if (!isQuiet && _isHurryingSilence)
        {
            // A little before the sound returns, so the first word isn't rushed. An end far from
            // here (odd timestamps) isn't trusted; normal speed comes back straight away instead.
            var resumeAt = (end ?? 0) - 0.25;
            if (resumeAt > _positionSeconds && resumeAt - _positionSeconds < 10)
                _silenceEndsAt = resumeAt;
            else
                EndSilenceHurry();
        }

        static double? ReadSeconds(System.Text.Json.JsonElement root, string key)
            => root.TryGetProperty(key, out var value)
               && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                ? seconds
                : null;
    }

    private void EndSilenceHurry()
    {
        if (!_isHurryingSilence)
            return;
        _isHurryingSilence = false;
        _silenceEndsAt = -1;
        var speed = _normalSpeed;
        TryMpv(mpv => mpv.SetSpeed(speed));
        UpdateSpeedLabel(speed);
    }

    /// <summary>The chapter playing at <paramref name="seconds"/>, or null when the file has none.</summary>
    private MpvChapter? ChapterAt(double seconds)
        => _chapters.LastOrDefault(chapter => chapter.Time <= seconds + 0.01) ?? _chapters.FirstOrDefault();

    /// <summary>Lists the chapters in the settings sheet, with the current one highlighted.</summary>
    private void BuildChapterRows()
    {
        ChapterRows.Children.Clear();
        ChaptersSection.Visibility = _chapters.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var chapter in _chapters)
        {
            var target = chapter;
            ChapterRows.Children.Add(CreateSheetRow(chapter.DisplayName, chapter.Index == _currentChapter,
                () => SeekToChapter(target), FormatTime(chapter.Time)));
        }
    }

    /// <summary>Cuts a thin gap into the seek bar at the start of each chapter.</summary>
    private void UpdateChapterMarks()
    {
        ChapterMarks.Children.Clear();
        var width = SeekTrack.ActualWidth;
        if (_chapters.Count == 0 || _durationSeconds <= 0 || width <= 0)
            return;

        var railHeight = SeekRail.Height;
        var top = (SeekTrack.ActualHeight - railHeight) / 2;
        var gap = (Brush)Application.Current.Resources["IrisChapterGapBrush"];
        foreach (var chapter in _chapters)
        {
            if (chapter.Time <= 0.5 || chapter.Time >= _durationSeconds - 0.5)
                continue;

            var mark = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 2, Height = railHeight, Fill = gap };
            Canvas.SetLeft(mark, width * chapter.Time / _durationSeconds - 1);
            Canvas.SetTop(mark, top);
            ChapterMarks.Children.Add(mark);
        }
    }

    private void SeekToChapter(MpvChapter chapter)
    {
        if (!CanSeek)
            return;

        _positionSeconds = chapter.Time;
        UpdateTimes();
        TryMpv(mpv => mpv.Seek(chapter.Time));
        ShowOsd(chapter.DisplayName);
    }

    /// <summary>
    /// Page Down goes to the next chapter. Page Up goes back to the start of the current one,
    /// or to the previous one when pressed within a few seconds of a chapter starting.
    /// </summary>
    private void JumpChapter(int direction)
    {
        if (_chapters.Count == 0 || !CanSeek)
            return;

        var current = ChapterAt(_positionSeconds)!;
        MpvChapter target;
        if (direction > 0)
        {
            var next = _chapters.FirstOrDefault(chapter => chapter.Time > _positionSeconds + 0.5);
            if (next is null)
            {
                ShowOsd("Last chapter");
                return;
            }
            target = next;
        }
        else
        {
            var currentIndex = _chapters.ToList().IndexOf(current);
            target = _positionSeconds - current.Time > 3 || currentIndex <= 0
                ? current
                : _chapters[currentIndex - 1];
        }

        SeekToChapter(target);
        ShowControlsTemporarily();
    }

    // ---------- Volume with the mouse wheel ----------

    /// <summary>Scrolling over the video changes the volume, by the step chosen in Settings per notch.</summary>
    private void Player_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // Leave scrolling alone where it means something else: the settings sheet.
        if (e.Handled || (_isSheetOpen && PointerHelper.IsInside(SettingsSheet, e)))
            return;

        var delta = e.GetCurrentPoint(PlayerRoot).Properties.MouseWheelDelta;
        if (delta == 0)
            return;

        if (_isMuted && delta > 0)
            TryMpv(mpv => mpv.SetMuted(false));
        ChangeVolume(Math.Sign(delta) * PlayerSettings.WheelVolumeStep * Math.Max(1, Math.Abs(delta) / 120));
        e.Handled = true;
    }

    // ---------- Top bar ----------

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
        => FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);

    private void MiniPlayer_Click(object sender, RoutedEventArgs e) => MiniPlayerToggleRequested?.Invoke(this, EventArgs.Empty);

    private void MiniClose_Click(object sender, RoutedEventArgs e) => MiniPlayerCloseRequested?.Invoke(this, EventArgs.Empty);

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        if (_currentMedia is { IsRemote: false } media)
        {
            menu.Items.Add(CreateMenuItem("Show in File Explorer", "", async () =>
                await Windows.System.Launcher.LaunchFolderPathAsync(media.FolderPath)));
        }
        if (_currentMedia is { IsRemote: false } current && _videoMenu is not null)
        {
            menu.Items.Add(_videoMenu.CreateCollectionMenu(current));
            menu.Items.Add(_videoMenu.CreateDetailsItem(current));
        }
        menu.Items.Add(CreateMenuItem("Load subtitle file…", "", () => LoadSubtitle_Click(this, new RoutedEventArgs())));
        menu.Items.Add(CreateMenuItem("Playback settings", "", () => SetSheetOpen(true)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateMenuItem("Mini player", "", () => MiniPlayerToggleRequested?.Invoke(this, EventArgs.Empty)));
        menu.ShowAt(MoreButton, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }

    private static MenuFlyoutItem CreateMenuItem(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    // ---------- Mouse, keyboard and auto-hiding controls ----------

    private void VideoHost_Tapped(object sender, TappedRoutedEventArgs e)
    {
        Focus(FocusState.Programmatic);

        // With the sheet open, a click on the video just closes it.
        if (_isSheetOpen)
        {
            SetSheetOpen(false);
            return;
        }

        // A double-click raises Tapped as well; ignore taps that belong to one.
        if ((DateTime.UtcNow - _lastDoubleTapUtc).TotalMilliseconds < 400)
            return;

        // Wait briefly so a double-click toggles fullscreen without also pausing.
        _singleTapTimer.Stop();
        _singleTapTimer.Start();
    }

    private void VideoHost_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _singleTapTimer.Stop();
        _lastDoubleTapUtc = DateTime.UtcNow;
        if (!_isMini)
            FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    private void VideoHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isMini)
            MiniSurface_PointerPressed(sender, e);
    }

    /// <summary>The mini player has no title bar, so dragging anywhere on it moves the window.</summary>
    private void MiniSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_isMini || e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || XamlRoot?.ContentIslandEnvironment is not { } environment)
            return;

        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(environment.AppWindowId);
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
        e.Handled = true;
    }

    private void RestoreKeyboardFocus()
    {
        if (Visibility != Visibility.Visible || XamlRoot is null)
            return;
        if (FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused && IsInsidePlayer(focused))
            return;
        Focus(FocusState.Programmatic);
    }

    private bool IsInsidePlayer(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, this))
                return true;
        }
        return false;
    }

    private void Player_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Visibility != Visibility.Visible || IsModifierDown(VirtualKey.Control) || IsModifierDown(VirtualKey.Menu))
            return;

        // Let text and list controls in the sheet keep their own keys.
        if (_isSheetOpen && e.OriginalSource is ComboBox or ComboBoxItem)
            return;

        e.Handled = HandlePlayerKey(e.Key, IsModifierDown(VirtualKey.Shift));
    }

    private bool HandlePlayerKey(VirtualKey key, bool shift)
    {
        switch (key)
        {
            case VirtualKey.Space:
            case VirtualKey.K:
                TogglePause();
                break;
            case VirtualKey.Left:
                SeekBy(-PlayerSettings.SmallSkipSeconds);
                ShowControlsTemporarily();
                break;
            case VirtualKey.Right:
                SeekBy(PlayerSettings.SmallSkipSeconds);
                ShowControlsTemporarily();
                break;
            case VirtualKey.J:
                SeekBy(-PlayerSettings.LargeSkipSeconds);
                ShowControlsTemporarily();
                break;
            case VirtualKey.L:
                SeekBy(PlayerSettings.LargeSkipSeconds);
                ShowControlsTemporarily();
                break;
            case VirtualKey.Up:
                ChangeVolume(5);
                break;
            case VirtualKey.Down:
                ChangeVolume(-5);
                break;
            case VirtualKey.M:
                ToggleMute();
                break;
            case VirtualKey.V:
                ToggleSubtitles();
                break;
            case VirtualKey.I:
                MiniPlayerToggleRequested?.Invoke(this, EventArgs.Empty);
                break;
            case VirtualKey.F:
            case VirtualKey.F11:
                if (!_isMini)
                    FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);
                break;
            case VirtualKey.Escape:
                // Escape closes the sheet, then leaves mini player or fullscreen, then (inside the library) the player.
                if (_isSheetOpen)
                    SetSheetOpen(false);
                else if (_isMini)
                    MiniPlayerToggleRequested?.Invoke(this, EventArgs.Empty);
                else if (_isFullscreen)
                    FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);
                else if (!_isStandalone)
                    BackRequested?.Invoke(this, EventArgs.Empty);
                else
                    return false;
                break;
            case VirtualKey.N when shift:
                PlayNext();
                break;
            case VirtualKey.P when shift:
                PlayPrevious();
                break;
            case VirtualKey.C:
                CycleZoomMode();
                break;
            case VirtualKey.PageDown:
                JumpChapter(1);
                break;
            case VirtualKey.PageUp:
                JumpChapter(-1);
                break;
            case VirtualKey.Z:
                AdjustSubtitleDelay(-0.1);
                break;
            case VirtualKey.X:
                AdjustSubtitleDelay(0.1);
                break;
            case VirtualKey.S when _offeredSkip is { } segment:
                SkipPast(segment);
                break;
            default:
                return false;
        }
        return true;
    }

    private static bool IsModifierDown(VirtualKey key)
        => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void Player_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (Visibility != Visibility.Visible)
            return;
        if (_isMini)
        {
            if (!_isPointerInside)
            {
                _isPointerInside = true;
                UpdateMiniChrome(animate: true);
            }
            return;
        }
        ShowControlsTemporarily();
    }

    private void Player_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerInside = true;
        if (_isMini)
            UpdateMiniChrome(animate: true);
    }

    private void Player_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Exits from child elements bubble up here too; only react when the pointer really left.
        // Leaving through a window edge reports a last position just inside, so ignore the outer pixels.
        if (PointerHelper.IsInside(PlayerRoot, e, edge: 3))
            return;
        _isPointerOverBars = false;
        _isPointerInside = false;
        // The hidden cursor is per thread, so give it back as soon as the pointer leaves the video.
        SetCursorHidden(false);
        if (_isMini)
            UpdateMiniChrome(animate: true);
    }

    private void Bars_PointerEntered(object sender, PointerRoutedEventArgs e) => _isPointerOverBars = true;

    private void Bars_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // The controls strip touches the window's bottom edge; leaving the window through it
        // reports a position still on the strip, so ignore the outer pixels.
        if (sender is FrameworkElement bar && !PointerHelper.IsInside(bar, e, edge: 3))
            _isPointerOverBars = false;
    }

    /// <summary>
    /// Called by the window when it gains or loses focus. Switching to another app ends any hover,
    /// so the controls hide as usual instead of waiting for the pointer to come back.
    /// </summary>
    public void SetWindowActive(bool active)
    {
        if (active)
        {
            // Windows doesn't hand keyboard focus back to the player when the window is reactivated,
            // so the shortcuts went dead until the video was clicked. Take it back, unless focus is
            // already somewhere inside the player (the settings sheet, for example).
            DispatcherQueue.TryEnqueue(RestoreKeyboardFocus);
            return;
        }
        _isPointerOverBars = false;
        _isPointerInside = false;
        SetCursorHidden(false);
        if (_controlsVisible && !_isMini)
        {
            _controlsHideTimer.Stop();
            _controlsHideTimer.Start();
        }
    }

    /// <summary>Shows the bars; while the video plays they hide again after a few idle seconds.</summary>
    private void ShowControlsTemporarily()
    {
        if (_isMini)
            return;
        SetControlsVisible(true);
        _controlsHideTimer.Stop();
        _controlsHideTimer.Interval = TimeSpan.FromSeconds(PlayerSettings.ControlsHideSeconds);
        _controlsHideTimer.Start();
    }

    /// <summary>Reads the real pause state from mpv (see <see cref="MpvPlayback.IsPaused"/>).</summary>
    private void SyncPauseState()
    {
        if (_mpv is null)
            return;
        try
        {
            _isPaused = _mpv.IsPaused;
            SetPlayPauseGlyph(_isPaused);
        }
        catch (ObjectDisposedException) { }
    }

    private void ControlsHideTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        SyncPauseState();
        if (_isMini || !_controlsVisible)
            return;

        // Keep the controls up while they're in use, the video is paused or has ended,
        // and look again shortly so they still hide once that's over.
        if (_isPointerOverBars || _isSheetOpen || _isDraggingSeek || _isPaused || !_isFileLoaded
            || UpNextCard.Visibility == Visibility.Visible)
        {
            _controlsHideTimer.Start();
            return;
        }
        SetControlsVisible(false);
    }

    /// <summary>Fades the bars and scrims and slides the bars 10 px (333 ms).</summary>
    private void SetControlsVisible(bool visible)
    {
        if (_controlsVisible == visible)
            return;
        _controlsVisible = visible;

        var opacity = visible ? 1f : 0f;
        Motion.Fade(PlayerHeaderBar, opacity, Motion.Slow);
        Motion.Fade(PlayerControlsBar, opacity, Motion.Slow);
        Motion.Fade(Scrims, opacity, Motion.Slow);
        Motion.Translate(PlayerHeaderBar, new Vector3(0, visible ? 0 : -10, 0), Motion.Slow);
        Motion.Translate(PlayerControlsBar, new Vector3(0, visible ? 0 : 10, 0), Motion.Slow);
        PlayerHeaderBar.IsHitTestVisible = visible;
        PlayerControlsBar.IsHitTestVisible = visible;
        UpdateSubtitlePosition();
        if (!visible)
            _isPointerOverBars = false;
        SetCursorHidden(!visible && _isPointerInside);
    }

    /// <summary>
    /// Lifts subtitles above the controls while they show, and drops them back to the bottom when
    /// the controls hide (and in the mini player, whose controls are an overlay).
    /// </summary>
    private void UpdateSubtitlePosition()
    {
        if (_mpv is null || !_isFileLoaded)
            return;

        // Plain text subtitles are placed in the whole window; picture (Blu-ray/DVD) and ASS subtitles
        // inside the video, and sub-pos moves those by a percentage of the video height, not the window's.
        var frame = _mpv.GetSubtitleFrame() ?? new SubtitleFrame(false, 0, 1);
        if (frame.Height <= 0)
            return;

        // At rest, picture subtitles drop a little towards the bottom of the video (mpv stops them at the edge).
        var percent = 100d - PlayerSettings.SubtitleLift + (frame.IsPicture ? PictureSubtitleDrop : 0);
        var height = PlayerRoot.ActualHeight;
        if (_controlsVisible && !_isMini && height > 0)
        {
            // The strip's top padding is only the fade; keep subtitles just above the time row.
            var covered = (PlayerControlsBar.ActualHeight - PlayerControlsBar.Padding.Top + 16) / height;
            // How far the bottom of the subtitle area reaches into the controls, as a share of the window.
            var overlap = frame.Bottom - (1 - covered);
            if (overlap > 0)
                percent = Math.Min(percent, 100 * (1 - overlap / frame.Height));
        }

        try
        {
            _mpv.SetSubtitlePosition(percent);
        }
        catch (Exception)
        {
            // Older mpv builds may reject the value; subtitles then just stay where they are.
        }
    }

    /// <summary>The mini player's controls show while the pointer is over it or the video is paused.</summary>
    private void UpdateMiniChrome(bool animate)
    {
        if (!_isMini)
            return;

        var show = _isPointerInside || _isPaused;
        if (animate)
            Motion.Fade(MiniChrome, show ? 1 : 0, Motion.Normal);
        else
            Motion.SetOpacity(MiniChrome, show ? 1 : 0);
        MiniProgressTrack.Height = _isPointerInside ? 5 : 3;
        UpdateMiniProgress();
    }

    private void ShowOsd(string message)
    {
        OsdText.Text = message;
        OsdBanner.Visibility = _isMini ? Visibility.Collapsed : Visibility.Visible;
        _osdTimer.Stop();
        _osdTimer.Start();
    }

    private void PlayerControlsBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        UpdateSubtitlePosition();

        // Progressively drop the least important controls so the seek bar and play button keep room.
        VolumeSlider.Visibility = width < 820 ? Visibility.Collapsed : Visibility.Visible;

        var narrow = width < 640 ? Visibility.Collapsed : Visibility.Visible;
        SeekBackwardButton.Visibility = narrow;
        SeekForwardButton.Visibility = narrow;
        SeekDivider.Visibility = narrow;
        ZoomButton.Visibility = narrow;

        var tiny = width < 480 ? Visibility.Collapsed : Visibility.Visible;
        PreviousButton.Visibility = tiny;
        VolumeDivider.Visibility = tiny;
        RemainingTimeText.Visibility = tiny;
    }

    // ---------- Helpers ----------

    /// <summary>Runs an mpv call if mpv is running, showing failures instead of crashing the app.</summary>
    private void TryMpv(Action<MpvPlayback> action)
    {
        if (_mpv is null)
            return;

        try
        {
            action(_mpv);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException exception)
        {
            ShowOsd(exception.Message);
        }
    }

    private static DispatcherQueueTimer CreateTimer(
        DispatcherQueue dispatcher,
        TimeSpan interval,
        Windows.Foundation.TypedEventHandler<DispatcherQueueTimer, object> tick,
        bool repeating = false)
    {
        var timer = dispatcher.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = repeating;
        timer.Tick += tick;
        return timer;
    }

    private void SetCursorHidden(bool hidden)
    {
        // ShowCursor keeps a per-thread counter, so only call it when the state actually changes.
        if (_isCursorHidden == hidden)
            return;
        _isCursorHidden = hidden;
        ShowCursor(!hidden);
    }

    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private static readonly IntPtr HTCAPTION = new(2);

    [DllImport("user32.dll")]
    private static extern int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
}
