using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace DevOpsWatcher;

public sealed class TrackedPipeline
{
    public string Project { get; set; } = "";
    public long DefinitionId { get; set; }
    public string Name { get; set; } = "";

    public string Key => $"{Project.ToLowerInvariant()}|{DefinitionId}";
}

[JsonConverter(typeof(JsonStringEnumConverter<ServerKind>))]
public enum ServerKind { AzureDevOps, GitHub }

/// <summary>One server to watch and the pipelines tracked on it.</summary>
public sealed class ServerConfig
{
    public const string GitHubUrl = "https://github.com";

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public ServerKind Kind { get; set; }
    public string Url { get; set; } = "";
    /// <summary>Azure DevOps Server only.</summary>
    public bool UseWindowsAuth { get; set; }
    /// <summary>PAT encrypted with DPAPI for the current Windows user.</summary>
    public string? ProtectedPat { get; set; }
    public List<TrackedPipeline> Pipelines { get; set; } = [];

    [JsonIgnore]
    public string? Pat
    {
        get => Dpapi.Unprotect(ProtectedPat);
        set => ProtectedPat = Dpapi.Protect(value);
    }

    [JsonIgnore]
    public bool HasCredentials => (UseWindowsAuth && Kind == ServerKind.AzureDevOps) || ProtectedPat != null;

    [JsonIgnore]
    public string KindName => KindNameOf(Kind);

    /// <summary>The URL without scheme, e.g. "dev.azure.com/yourorg" or "github.com".</summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var url = Url.Trim().TrimEnd('/');
            int scheme = url.IndexOf("://", StringComparison.Ordinal);
            return scheme >= 0 ? url[(scheme + 3)..] : url;
        }
    }

    public static string KindNameOf(ServerKind kind) => kind == ServerKind.GitHub ? "GitHub Actions" : "Azure DevOps";

    public IPipelineSource CreateSource(RateLimitSettings rateLimits, bool demo = false)
    {
        if (demo && Url == DemoSource.Url) return new DemoSource();
        return Kind == ServerKind.GitHub
            ? new GitHubClient(Url, Pat, rateLimits)
            : new AzDoClient(Url, UseWindowsAuth, Pat, rateLimits);
    }

    public ServerConfig Clone() => new()
    {
        Id = Id,
        Kind = Kind,
        Url = Url,
        UseWindowsAuth = UseWindowsAuth,
        ProtectedPat = ProtectedPat,
        Pipelines = Pipelines.Select(p => new TrackedPipeline { Project = p.Project, DefinitionId = p.DefinitionId, Name = p.Name }).ToList(),
    };
}

public sealed class RateLimit
{
    /// <summary>Max requests per window; 0 or less means unlimited.</summary>
    public int Requests { get; set; }
    public int PerSeconds { get; set; } = 60;
}

public sealed class RateLimitSettings
{
    /// <summary>Client-side limits on/off. Server Retry-After is honoured either way.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Budget for the signed-in user across all endpoints.</summary>
    public RateLimit? PerUser { get; set; } = new() { Requests = 120, PerSeconds = 60 };
    /// <summary>Budgets per endpoint (see <see cref="RateLimiter.Endpoints"/>); endpoints not listed are only bound by PerUser.</summary>
    public Dictionary<string, RateLimit>? PerEndpoint { get; set; } = new()
    {
        [RateLimiter.Endpoints.Timeline] = new() { Requests = 60, PerSeconds = 60 },
    };
    /// <summary>Longest a call waits for a free slot before failing with a rate-limit error.</summary>
    public int MaxWaitSeconds { get; set; } = 30;
}

public sealed class AppSettings
{
    public List<ServerConfig> Servers { get; set; } = [];
    public int PollSeconds { get; set; } = 20;
    public bool ShowStatusPopups { get; set; } = true;
    /// <summary>Install new releases from GitHub automatically when no approval is waiting.</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>Experimental: Resume/Reject ManualValidation steps from the popup via an undocumented use of plan events.</summary>
    public bool EnableManualValidationActions { get; set; }
    public RateLimitSettings RateLimits { get; set; } = new();

    // Single-server settings from before Servers existed; moved into Servers on load and not written back.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ServerUrl { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? UseWindowsAuth { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ProtectedPat { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<TrackedPipeline>? Pipelines { get; set; }

    [JsonIgnore]
    public bool IsConfigured => Servers.Any(s => s.Url != "" && s.HasCredentials && s.Pipelines.Count > 0);

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevOpsWatcher", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJsonContext.Default.AppSettings) ?? new();
                settings.MigrateLegacy();
                return settings;
            }
        }
        catch
        {
            // Corrupt settings: start fresh rather than crash.
        }
        return new();
    }

    void MigrateLegacy()
    {
        if (!string.IsNullOrEmpty(ServerUrl) && Servers.Count == 0)
            Servers.Add(new ServerConfig
            {
                Kind = ServerKind.AzureDevOps,
                Url = ServerUrl,
                UseWindowsAuth = UseWindowsAuth ?? false,
                ProtectedPat = ProtectedPat,
                Pipelines = Pipelines ?? [],
            });
        ServerUrl = null;
        UseWindowsAuth = null;
        ProtectedPat = null;
        Pipelines = null;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsJsonContext.Default.AppSettings));
    }
}

/// <summary>Source-generated serializer, so settings survive trimming.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
partial class SettingsJsonContext : JsonSerializerContext;

static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "DevOpsWatcher";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>Windows DPAPI (CurrentUser scope) without an extra NuGet dependency.</summary>
static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr handle);

    const int UiForbidden = 0x1;

    public static string? Protect(string? plain) =>
        string.IsNullOrEmpty(plain) ? null : Convert.ToBase64String(Run(Encoding.UTF8.GetBytes(plain), protect: true));

    public static string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        try { return Encoding.UTF8.GetString(Run(Convert.FromBase64String(cipher), protect: false)); }
        catch { return null; }
    }

    static byte[] Run(byte[] input, bool protect)
    {
        var dataIn = new DataBlob { cbData = input.Length, pbData = Marshal.AllocHGlobal(input.Length) };
        var dataOut = new DataBlob();
        try
        {
            Marshal.Copy(input, 0, dataIn.pbData, input.Length);
            bool ok = protect
                ? CryptProtectData(ref dataIn, "DevOpsWatcher", 0, 0, 0, UiForbidden, ref dataOut)
                : CryptUnprotectData(ref dataIn, 0, 0, 0, 0, UiForbidden, ref dataOut);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[dataOut.cbData];
            Marshal.Copy(dataOut.pbData, result, 0, dataOut.cbData);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(dataIn.pbData);
            if (dataOut.pbData != 0) LocalFree(dataOut.pbData);
        }
    }
}
