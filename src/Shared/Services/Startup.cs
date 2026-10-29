using System.Security;
using Microsoft.Win32;

namespace BuildPulse;

/// <summary>Start at login: the HKCU Run key on Windows, a LaunchAgent on macOS.</summary>
static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = AppInfo.Id;
    const string LegacyValueName = AppInfo.LegacyId;
    const string AgentLabel = "no.ravndal.buildpulse";
    const string LegacyAgentLabel = "no.ravndal.devopswatcher";

    static string AgentPath => AgentPathFor(AgentLabel);

    static string AgentPathFor(string label) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", label + ".plist");

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static bool IsEnabled
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) != null;
            }
            return OperatingSystem.IsMacOS() && File.Exists(AgentPath);
        }
    }

    public static void Set(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (!enabled) { File.Delete(AgentPath); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(AgentPath)!);
            File.WriteAllText(AgentPath, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                  <key>Label</key><string>{AgentLabel}</string>
                  <key>ProgramArguments</key><array><string>{SecurityElement.Escape(Environment.ProcessPath)}</string></array>
                  <key>RunAtLoad</key><true/>
                </dict>
                </plist>
                """);
        }
    }

    /// <summary>Replaces a DevOpsWatcher start-at-login entry with one under the new name, pointing at this exe.</summary>
    public static void MigrateLegacy()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(LegacyValueName) == null) return;
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            Set(true);
        }
        else if (OperatingSystem.IsMacOS())
        {
            var legacy = AgentPathFor(LegacyAgentLabel);
            if (!File.Exists(legacy)) return;
            File.Delete(legacy);
            Set(true);
        }
    }
}
