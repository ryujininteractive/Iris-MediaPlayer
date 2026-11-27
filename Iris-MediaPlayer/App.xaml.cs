using Iris_MediaPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Windows.ApplicationModel.Activation;

namespace Iris_MediaPlayer;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Opened with video files (from Explorer, "Open with" or as the default app): show only
    /// the standalone player. Launched normally: show the full library.
    /// </summary>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // First run after moving from the MSIX build: bring the old library data across.
        AppStorage.MigrateFromMsix();
        // Before any window: pages are built with the chosen theme's colours from the start.
        Themes.ApplySaved();

        var files = GetFilesToOpen();
        _window = files.Count > 0 ? new PlayerWindow(files) : new MainWindow();
        SetWindowIcon(_window);
        _window.Activate();
    }

    /// <summary>Title bar, taskbar and Alt+Tab icon. The packaged tiles come from the manifest.</summary>
    public static void SetWindowIcon(Window window)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Iris.ico");
        if (File.Exists(iconPath))
            window.AppWindow.SetIcon(iconPath);
    }

    private static List<string> GetFilesToOpen()
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activation.Kind == ExtendedActivationKind.File && activation.Data is IFileActivatedEventArgs fileArgs)
        {
            return fileArgs.Files
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrEmpty(path) && MediaLibraryService.IsSupportedVideoFile(path))
                .ToList();
        }

        // Also accept paths on the command line, e.g. "Iris-MediaPlayer.exe movie.mkv".
        return Environment.GetCommandLineArgs()
            .Skip(1)
            .Where(path => MediaLibraryService.IsSupportedVideoFile(path) && File.Exists(path))
            .ToList();
    }
}
