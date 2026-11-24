using Microsoft.Win32;
using System;
using System.Runtime.InteropServices;
using Velopack;

namespace Iris_MediaPlayer.Services;

/// <summary>
/// Install, update and uninstall steps for the Velopack setup (IrisSetup.exe).
/// Velopack installs Iris for the current user into %LocalAppData%\IrisMediaPlayer and creates
/// the Start menu and desktop shortcuts itself; this class adds what Velopack doesn't: Iris in
/// "Open with" and Settings › Apps › Default apps for video files.
/// </summary>
internal static class Installation
{
    /// <summary>
    /// Where releases are published (the Iris-1 folder of the Cloudflare R2 bucket behind the
    /// website). Iris reads releases.win.json here to find updates; build-installer.ps1 uploads to it.
    /// </summary>
    public const string UpdatesUrl = "https://www.ryujininteractive.com/Iris-1/";

    private const string ProgId = "Iris.Video";
    private const string ExeName = "Iris-MediaPlayer.exe";
    private const string CapabilitiesKey = @"Software\Ryujin Interactive\Iris\Capabilities";

    // Keep in step with MediaLibraryService.SupportedVideoExtensions.
    private static readonly string[] VideoExtensions =
        [".avi", ".flv", ".m2ts", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg", ".mpg", ".mts", ".ogv", ".ts", ".webm", ".wmv"];

    /// <summary>
    /// True when Iris runs from its MSIX package (the Microsoft Store version). The Store installs
    /// and updates the package and its manifest registers the file types, so Velopack stays out of it.
    /// </summary>
    public static bool IsPackaged { get; } = DetectPackage();

    private static bool DetectPackage()
    {
        uint length = 0;
        // ERROR_INSUFFICIENT_BUFFER means there is a package name to read; APPMODEL_ERROR_NO_PACKAGE means none.
        return GetCurrentPackageFullName(ref length, null) == ERROR_INSUFFICIENT_BUFFER;
    }

    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);

    /// <summary>
    /// Call first thing in Main. During install, update or uninstall Velopack runs the matching
    /// hook below and exits the process; on a normal launch it returns straight away (and applies
    /// an update that was downloaded earlier but not yet installed). Does nothing in the Store version.
    /// </summary>
    public static void RunVelopackHooks()
    {
        if (IsPackaged)
            return;

        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => RegisterFileTypes())
            .OnAfterUpdateFastCallback(_ => RegisterFileTypes())
            .OnBeforeUninstallFastCallback(_ => UnregisterFileTypes())
            .Run();
    }

    // ---------- "Open with" and Default apps, for this user only (no admin needed) ----------

    private static void RegisterFileTypes()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Iris couldn’t find its own path.");
        var command = $"\"{exe}\" \"%1\"";
        var icon = $"\"{exe}\",0";

        using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progId.SetValue(null, "Video file");
            progId.SetValue("FriendlyTypeName", "Video file");
            using var defaultIcon = progId.CreateSubKey("DefaultIcon");
            defaultIcon.SetValue(null, icon);
            using var open = progId.CreateSubKey(@"shell\open");
            open.SetValue("FriendlyAppName", "Iris");
            using var openCommand = progId.CreateSubKey(@"shell\open\command");
            openCommand.SetValue(null, command);
        }

        using (var application = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{ExeName}"))
        {
            application.SetValue("FriendlyAppName", "Iris");
            using var openCommand = application.CreateSubKey(@"shell\open\command");
            openCommand.SetValue(null, command);
            using var supportedTypes = application.CreateSubKey("SupportedTypes");
            foreach (var extension in VideoExtensions)
                supportedTypes.SetValue(extension, string.Empty);
        }

        foreach (var extension in VideoExtensions)
        {
            using var openWith = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}\OpenWithProgids");
            openWith.SetValue(ProgId, string.Empty);
        }

        using (var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
        {
            capabilities.SetValue("ApplicationName", "Iris");
            capabilities.SetValue("ApplicationDescription", "Play your local video library.");
            capabilities.SetValue("ApplicationIcon", icon);
            using var associations = capabilities.CreateSubKey("FileAssociations");
            foreach (var extension in VideoExtensions)
                associations.SetValue(extension, ProgId);
        }

        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            registered.SetValue("Iris", CapabilitiesKey);

        NotifyShell();
    }

    private static void UnregisterFileTypes()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\Applications\{ExeName}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Ryujin Interactive\Iris", throwOnMissingSubKey: false);

        foreach (var extension in VideoExtensions)
        {
            using var openWith = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}\OpenWithProgids", writable: true);
            openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
        }

        using (var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
            registered?.DeleteValue("Iris", throwOnMissingValue: false);

        NotifyShell();
    }

    /// <summary>Tells Explorer the file associations changed, so "Open with" updates right away.</summary>
    private static void NotifyShell() => SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
