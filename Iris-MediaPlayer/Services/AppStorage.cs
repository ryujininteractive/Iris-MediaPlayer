using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Iris_MediaPlayer.Services;

/// <summary>
/// Where Iris keeps its data: %LocalAppData%\Iris for the setup (.exe) version, or the package's
/// LocalState folder for the Microsoft Store version. Small settings go in settings.json and larger
/// lists (collections, resume points) in their own JSON files next to it.
///
/// The library window and a standalone player window can run at the same time as separate
/// processes, so settings are re-read whenever the file changed on disk and written atomically.
/// </summary>
internal static class AppStorage
{
    private const string SettingsFileName = "settings.json";

    // The package family of the old MSIX builds; their data is copied over once (see MigrateFromMsix).
    private const string MsixPackageFamilyName = "bf315f38-a664-47e1-967d-769e61d05e48_egdw50hcq0var";
    private static readonly string[] MigratedFiles = ["media-collections.json", "playback-progress.json"];

    private static readonly object Gate = new();
    private static Dictionary<string, string> _settings = new(StringComparer.Ordinal);
    private static DateTime _loadedStamp = DateTime.MinValue;

    public static string DataFolder { get; } = CreateDataFolder();

    public static string GetFilePath(string fileName) => Path.Combine(DataFolder, fileName);

    private static string SettingsPath => GetFilePath(SettingsFileName);

    /// <summary>The setup (.exe) version's data folder, %LocalAppData%\Iris.</summary>
    private static string DesktopDataFolder
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Iris");

    private static string CreateDataFolder()
    {
        // The Store version keeps its data in its package folder. Windows would quietly redirect
        // writes to %LocalAppData% there anyway; using it directly keeps "Open folder" honest.
        var folder = Installation.IsPackaged
            ? Windows.Storage.ApplicationData.Current.LocalFolder.Path
            : DesktopDataFolder;
        Directory.CreateDirectory(folder);
        return folder;
    }

    // ---------- Settings ----------

    public static string? GetString(string key)
    {
        lock (Gate)
        {
            Reload();
            return _settings.TryGetValue(key, out var value) ? value : null;
        }
    }

    public static void SetString(string key, string? value)
    {
        lock (Gate)
        {
            Reload();
            if (value is null)
                _settings.Remove(key);
            else
                _settings[key] = value;
            Save();
        }
    }

    public static double? GetDouble(string key)
        => double.TryParse(GetString(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static void SetDouble(string key, double value) => SetString(key, value.ToString("R", CultureInfo.InvariantCulture));

    public static bool? GetBool(string key) => bool.TryParse(GetString(key), out var value) ? value : null;

    public static void SetBool(string key, bool value) => SetString(key, value ? "true" : "false");

    /// <summary>Re-reads settings.json if another Iris window changed it since we last looked.</summary>
    private static void Reload()
    {
        try
        {
            var path = SettingsPath;
            var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (stamp == _loadedStamp)
                return;

            _settings = stamp == DateTime.MinValue
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(
                    JsonSerializer.Deserialize(File.ReadAllText(path), IrisJsonContext.Default.DictionaryStringString) ?? [],
                    StringComparer.Ordinal);
            _loadedStamp = stamp;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A half-written or unreadable file: keep what we have in memory.
        }
    }

    /// <summary>Writes to a temporary file and swaps it in, so a crash never leaves half a file.</summary>
    private static void Save()
    {
        try
        {
            var path = SettingsPath;
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_settings, IrisJsonContext.Default.DictionaryStringString));
            File.Move(temporary, path, overwrite: true);
            _loadedStamp = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    // ---------- One-time move from the MSIX version ----------

    /// <summary>
    /// Earlier Iris builds were MSIX packages and kept their data in the package's folder.
    /// On the first run without the package, copy library folders, recently played, player
    /// preferences, collections and resume points over, so nothing has to be set up again.
    /// </summary>
    public static void MigrateFromMsix()
    {
        if (File.Exists(SettingsPath))
            return;

        if (Installation.IsPackaged)
        {
            CopyFromDesktopVersion();
            return;
        }

        var packageFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", MsixPackageFamilyName);

        foreach (var fileName in MigratedFiles)
        {
            var source = Path.Combine(packageFolder, "LocalState", fileName);
            var target = GetFilePath(fileName);
            try
            {
                if (File.Exists(source) && !File.Exists(target))
                    File.Copy(source, target);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        // Small settings lived in the package's settings store (settings.dat), which only
        // Windows can read. This works for desktop apps on Windows 10 1809 and later.
        var migrated = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var packageData = Windows.Management.Core.ApplicationDataManager.CreateForPackageFamily(MsixPackageFamilyName);
            foreach (var (key, value) in packageData.LocalSettings.Values)
            {
                var text = value switch
                {
                    string s => s,
                    double d => d.ToString("R", CultureInfo.InvariantCulture),
                    bool b => b ? "true" : "false",
                    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                    _ => null
                };
                if (text is not null)
                    migrated[key] = text;
            }
        }
        catch (Exception)
        {
            // The old package isn't installed or Windows wouldn't share its settings; start fresh.
        }

        lock (Gate)
        {
            _settings = migrated;
            Save();
        }
    }

    /// <summary>
    /// First run of the Store version on a PC that already had the setup (.exe) version: copy its
    /// settings, library folders, collections and resume points, so switching loses nothing.
    /// The two versions keep separate copies from then on.
    /// </summary>
    private static void CopyFromDesktopVersion()
    {
        foreach (var fileName in (string[])[SettingsFileName, .. MigratedFiles])
        {
            var source = Path.Combine(DesktopDataFolder, fileName);
            var target = GetFilePath(fileName);
            try
            {
                if (File.Exists(source) && !File.Exists(target))
                    File.Copy(source, target);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        // No reload needed: Reload() notices settings.json's new timestamp on the next read.
    }
}
