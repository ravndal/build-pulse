namespace BuildPulse;

/// <summary>The app's names, and the move from the DevOpsWatcher names that installs from before the rename still have.</summary>
static class AppInfo
{
    public const string Name = "Build Pulse";
    /// <summary>Folder, executable, mutex and start-at-login name.</summary>
    public const string Id = "BuildPulse";
    public const string LegacyId = "DevOpsWatcher";

    /// <summary>Settings and log: %APPDATA%\BuildPulse on Windows.</summary>
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Id);

    static string LegacyDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyId);

    /// <summary>
    /// Moves settings and the start-at-login entry over from the DevOpsWatcher names. Runs at start in the
    /// single instance, after an old DevOpsWatcher process has exited, and before anything reads settings.
    /// </summary>
    public static void MigrateLegacy()
    {
        if (Directory.Exists(LegacyDataDir) && !Directory.Exists(DataDir))
        {
            try { Directory.Move(LegacyDataDir, DataDir); }
            catch (Exception ex)
            {
                // Something still holds a file open; settings are what matter, so copy those.
                try
                {
                    Directory.CreateDirectory(DataDir);
                    File.Copy(Path.Combine(LegacyDataDir, "settings.json"), Path.Combine(DataDir, "settings.json"));
                }
                catch { /* nothing to copy */ }
                Log.Write($"Couldn't move {LegacyDataDir}: {ex.Message}");
            }
        }

        // Leftovers from the old updater, which downloaded into %LOCALAPPDATA%\DevOpsWatcher\update.
        var legacyLocal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyId);
        try
        {
            if (Directory.Exists(Path.Combine(legacyLocal, "update"))) Directory.Delete(Path.Combine(legacyLocal, "update"), recursive: true);
            if (Directory.Exists(legacyLocal) && !Directory.EnumerateFileSystemEntries(legacyLocal).Any()) Directory.Delete(legacyLocal);
        }
        catch { /* next start tries again */ }

        try { Startup.MigrateLegacy(); }
        catch (Exception ex) { Log.Write($"Couldn't move the start-at-login entry: {ex.Message}"); }
    }
}
