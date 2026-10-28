using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevOpsWatcher;

sealed record UpdateInfo(Version Version, string ReleaseUrl, string FilePath);

/// <summary>Checks GitHub releases for a newer DevOpsWatcher.exe, downloads it and swaps it in place.</summary>
static class Updater
{
    const string Repo = "ravndal/devops-pipline-watcher";
    const string AssetName = "DevOpsWatcher.exe";
    public const string UpdatedArg = "--updated";

    static readonly string DownloadDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevOpsWatcher", "update");

    public static Version CurrentVersion { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    /// <summary>Only the single-file release exe can replace itself; a dotnet build output can't.</summary>
    public static bool IsSupported =>
        Environment.ProcessPath != null && !File.Exists(Path.Combine(AppContext.BaseDirectory, "DevOpsWatcher.dll"));

    sealed class Release
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<Asset> Assets { get; set; } = [];
    }

    sealed class Asset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
        /// <summary>"sha256:&lt;hex&gt;", computed by GitHub on upload.</summary>
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }

    /// <summary>Returns a downloaded, verified update, or null when already up to date.</summary>
    public static async Task<UpdateInfo?> CheckAndDownloadAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DevOpsWatcher", CurrentVersion.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        var release = JsonSerializer.Deserialize<Release>(json);
        if (release == null || release.Draft || release.Prerelease) return null;
        if (!Version.TryParse(release.TagName.TrimStart('v', 'V'), out var latest) || Normalize(latest) <= Normalize(CurrentVersion))
            return null;

        var asset = release.Assets.FirstOrDefault(a => a.Name.Equals(AssetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Release {release.TagName} has no {AssetName}.");

        Directory.CreateDirectory(DownloadDir);
        var target = Path.Combine(DownloadDir, $"DevOpsWatcher-{latest}.exe");
        if (!File.Exists(target) || !HashMatches(target, asset.Digest))
        {
            var partial = target + ".part";
            await using (var stream = await http.GetStreamAsync(asset.DownloadUrl, ct))
            await using (var file = File.Create(partial))
                await stream.CopyToAsync(file, ct);
            if (!HashMatches(partial, asset.Digest))
            {
                File.Delete(partial);
                throw new InvalidOperationException($"Downloaded {AssetName} for {release.TagName} failed the checksum.");
            }
            File.Move(partial, target, overwrite: true);
        }

        foreach (var old in Directory.EnumerateFiles(DownloadDir).Where(f => !f.Equals(target, StringComparison.OrdinalIgnoreCase)))
            TryDelete(old);

        return new UpdateInfo(latest, release.HtmlUrl, target);
    }

    /// <summary>Puts the new exe in place of the running one and starts it. The caller exits afterwards.</summary>
    public static void Install(UpdateInfo update)
    {
        var exe = Environment.ProcessPath!;
        var backup = exe + ".old";
        // Windows lets a running exe be renamed but not overwritten.
        File.Move(exe, backup, overwrite: true);
        try
        {
            File.Copy(update.FilePath, exe);
        }
        catch
        {
            File.Move(backup, exe);
            throw;
        }
        Process.Start(new ProcessStartInfo(exe, UpdatedArg) { UseShellExecute = false });
    }

    /// <summary>Removes what an earlier update left behind.</summary>
    public static void CleanUp()
    {
        if (Environment.ProcessPath is { } exe) TryDelete(exe + ".old");
        if (Directory.Exists(DownloadDir))
            foreach (var file in Directory.EnumerateFiles(DownloadDir))
                if (!Version.TryParse(Path.GetFileNameWithoutExtension(file).Replace("DevOpsWatcher-", ""), out var v)
                    || Normalize(v) <= Normalize(CurrentVersion))
                    TryDelete(file);
    }

    static bool HashMatches(string path, string? digest)
    {
        // Older releases have no digest; the download is still over HTTPS from GitHub.
        if (digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return true;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(digest[7..], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Treats 1.2 and 1.2.0.0 as equal.</summary>
    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* still locked or gone; next start tries again */ }
    }
}
