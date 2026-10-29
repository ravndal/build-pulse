using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildPulse;

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
    /// <summary>Azure DevOps Services only: when set, setup lists just this project of the organization.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Project { get; set; }
    /// <summary>PAT protected by <see cref="Secrets"/>: DPAPI-encrypted on Windows, a Keychain reference on macOS.</summary>
    public string? ProtectedPat { get; set; }
    public List<TrackedPipeline> Pipelines { get; set; } = [];

    [JsonIgnore]
    public string? Pat
    {
        get => Secrets.Unprotect(ProtectedPat);
        set => ProtectedPat = Secrets.Protect(value, ProtectedPat);
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
        Project = Project,
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
    /// <summary>Dashboard panels (see <c>DashboardForm.Group.Key</c>) the user collapsed to status icons.</summary>
    public List<string> CollapsedPanels { get; set; } = [];
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

    static string FilePath => Path.Combine(AppInfo.DataDir, "settings.json");

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
