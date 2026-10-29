using System.Security;
using Microsoft.Win32;

namespace DevOpsWatcher;

/// <summary>Start at login: the HKCU Run key on Windows, a LaunchAgent on macOS.</summary>
static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "DevOpsWatcher";
    const string AgentLabel = "no.ravndal.devopswatcher";

    static string AgentPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", AgentLabel + ".plist");

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
}
