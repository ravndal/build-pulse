using System.Text.RegularExpressions;

namespace BuildPulse;

/// <summary>What the user connects to; finer than <see cref="ServerKind"/> because each asks for different values.</summary>
public enum ServerTarget { AzureDevOps, AzureDevOpsServer, GitHub, GitHubEnterprise }

/// <summary>Adding or editing a server, shared by the tray app's and the terminal app's settings.</summary>
static partial class ServerTargets
{
    public const string AzureDevOpsHost = "https://dev.azure.com";

    public static readonly ServerTarget[] All = [ServerTarget.AzureDevOps, ServerTarget.AzureDevOpsServer, ServerTarget.GitHub, ServerTarget.GitHubEnterprise];

    public static ServerKind KindOf(ServerTarget target) =>
        target is ServerTarget.GitHub or ServerTarget.GitHubEnterprise ? ServerKind.GitHub : ServerKind.AzureDevOps;

    public static string Name(ServerTarget target) => target switch
    {
        ServerTarget.AzureDevOps => "Azure DevOps",
        ServerTarget.AzureDevOpsServer => "Azure DevOps Server",
        ServerTarget.GitHub => "GitHub",
        _ => "GitHub Enterprise",
    };

    /// <summary>The line under the name when picking a target.</summary>
    public static string Detail(ServerTarget target) => target switch
    {
        ServerTarget.AzureDevOps => "dev.azure.com organization",
        ServerTarget.AzureDevOpsServer => "On-premises collection URL",
        ServerTarget.GitHub => "github.com",
        _ => "Your GitHub server's URL",
    };

    public static ServerTarget Of(ServerConfig server)
    {
        if (server.Kind == ServerKind.GitHub)
            return server.Url == "" || server.Url.TrimEnd('/').Equals(ServerConfig.GitHubUrl, StringComparison.OrdinalIgnoreCase)
                ? ServerTarget.GitHub : ServerTarget.GitHubEnterprise;
        return server.Url == "" || OrganizationOf(server.Url) != null ? ServerTarget.AzureDevOps : ServerTarget.AzureDevOpsServer;
    }

    /// <summary>The token's required permissions, for the hint under the token field.</summary>
    public static string TokenHint(ServerKind kind) => kind == ServerKind.GitHub
        ? "Fine-grained token with repository permissions Actions (Read) and Deployments (Read & write, to approve), "
          + "or a classic token with the repo scope."
        : "PAT scopes: Build (Read & execute) and Project and Team (Read).";

    /// <summary>
    /// The organization of an Azure DevOps Services URL (dev.azure.com/org/... or org.visualstudio.com),
    /// a bare organization name, or null for anything else.
    /// </summary>
    public static string? OrganizationOf(string text)
    {
        text = text.Trim().TrimEnd('/');
        if (OrgName().IsMatch(text)) return text;
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
            return uri.Segments.Length > 1 && OrgName().IsMatch(uri.Segments[1].TrimEnd('/')) ? uri.Segments[1].TrimEnd('/') : null;
        if (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
            return uri.Host[..uri.Host.IndexOf('.')];
        return null;
    }

    /// <summary>
    /// The server URL for what was entered: the organization for Azure DevOps, the URL for the on-premises and
    /// Enterprise targets. Returns null with <paramref name="error"/> set when it's missing or not valid.
    /// </summary>
    public static string? UrlFor(ServerTarget target, string organization, string url, out string error)
    {
        error = "";
        switch (target)
        {
            case ServerTarget.AzureDevOps:
                organization = organization.Trim();
                if (organization == "") { error = "Enter your organization."; return null; }
                if (OrganizationOf(organization) is not { } org) { error = "That isn't a valid organization name."; return null; }
                return $"{AzureDevOpsHost}/{org}";
            case ServerTarget.GitHub:
                return ServerConfig.GitHubUrl;
            default:
                url = url.Trim().TrimEnd('/');
                if (url == "") { error = "Enter the server URL."; return null; }
                if (!url.Contains("://")) url = "https://" + url;
                if (!Uri.TryCreate(url, UriKind.Absolute, out _)) { error = "That isn't a valid URL."; return null; }
                if (Https.Parse(url) == null) { error = "The server must use https."; return null; }
                return url;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]*$")]
    private static partial Regex OrgName();
}
