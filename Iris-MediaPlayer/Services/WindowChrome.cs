using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using Windows.Graphics;
using Windows.UI;

namespace Iris_MediaPlayer.Services;

/// <summary>Title bar and window-mode helpers shared by the library window and the standalone player.</summary>
internal static class WindowChrome
{
    /// <summary>Draws Iris content under the title bar and tints the caption buttons to match.</summary>
    public static void ExtendIntoTitleBar(Window window, UIElement titleBar)
    {
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(titleBar);

        var bar = window.AppWindow.TitleBar;
        bar.PreferredHeightOption = TitleBarHeightOption.Tall;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonHoverBackgroundColor = Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF);
        bar.ButtonPressedBackgroundColor = Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF);
        // The glyph colours follow the theme's text colours, now and after a theme change.
        Themes.ApplyCaptionColors(window);
        void Recolor() => Themes.ApplyCaptionColors(window);
        Themes.Changed += Recolor;
        window.Closed += (_, _) => Themes.Changed -= Recolor;
    }

    public static void SizeAndCenter(Window window, int width, int height)
    {
        var appWindow = window.AppWindow;
        var workArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        width = Math.Min(width, workArea.Width);
        height = Math.Min(height, workArea.Height);
        appWindow.MoveAndResize(new RectInt32(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2,
            width,
            height));
    }
}

/// <summary>
/// Picture-in-picture: a small, borderless, always-on-top window in the bottom-right corner.
/// Leaving it restores the window's previous size, position and maximized state.
/// </summary>
internal sealed class MiniPlayerMode
{
    private const int MiniWidth = 480;
    private const int MiniHeight = 270;
    private const int ScreenMargin = 24;

    private RectInt32 _restoreBounds;
    private bool _wasMaximized;

    public bool IsActive { get; private set; }

    public void Enter(AppWindow appWindow, double scale)
    {
        if (IsActive)
            return;

        if (appWindow.Presenter.Kind != AppWindowPresenterKind.Overlapped)
            appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);

        if (appWindow.Presenter is not OverlappedPresenter presenter)
            return;

        _wasMaximized = presenter.State == OverlappedPresenterState.Maximized;
        if (_wasMaximized)
            presenter.Restore();
        _restoreBounds = new RectInt32(appWindow.Position.X, appWindow.Position.Y, appWindow.Size.Width, appWindow.Size.Height);

        presenter.IsAlwaysOnTop = true;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(true, false);

        var width = (int)Math.Round(MiniWidth * scale);
        var height = (int)Math.Round(MiniHeight * scale);
        var margin = (int)Math.Round(ScreenMargin * scale);
        var workArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        appWindow.MoveAndResize(new RectInt32(
            workArea.X + workArea.Width - width - margin,
            workArea.Y + workArea.Height - height - margin,
            width,
            height));
        IsActive = true;
    }

    public void Exit(AppWindow appWindow)
    {
        if (!IsActive)
            return;

        IsActive = false;
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = false;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
            presenter.SetBorderAndTitleBar(true, true);
            appWindow.MoveAndResize(_restoreBounds);
            if (_wasMaximized)
                presenter.Maximize();
        }
    }
}
