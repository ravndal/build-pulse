using System.Diagnostics;
using System.Text.RegularExpressions;

namespace BuildPulse;

public enum TrayState { Unconfigured, Idle, Running, Approval, Error }

public enum Glyph { None, Check, Cross, Bang, Spinner, Ring, Dash, Dots }

static partial class Fmt
{
    public static string Ago(DateTime? time)
    {
        if (time is not { } t) return "";
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }

    public static string Duration(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes}m";
        if (d.TotalMinutes >= 1) return $"{(int)d.TotalMinutes}m {d.Seconds}s";
        return $"{d.Seconds}s";
    }

    public static string Branch(string refName)
    {
        if (PullRequestRef().Match(refName) is { Success: true } m) return $"PR {m.Groups[1].Value}";
        foreach (var prefix in new[] { "refs/heads/", "refs/tags/" })
            if (refName.StartsWith(prefix, StringComparison.Ordinal)) return refName[prefix.Length..];
        return refName;
    }

    public static string BuildLine(BuildInfo b)
    {
        var number = b.Number != "" && char.IsDigit(b.Number[0]) ? $"#{b.Number}" : b.Number;
        var branch = Branch(b.Branch);
        if (branch.Equals(b.Number, StringComparison.OrdinalIgnoreCase)) branch = "";
        return string.Join(" · ", new[] { number, branch, b.RequestedFor }.Where(s => s != ""));
    }

    [GeneratedRegex(@"^refs/pull/(\d+)/")]
    private static partial Regex PullRequestRef();
}

static class Shell
{
    /// <summary>
    /// Opens a web page in the browser. Only absolute https URLs get through: links come from server responses,
    /// and the shell would just as happily run a file path or hand off to any registered protocol.
    /// </summary>
    public static void Open(string? url)
    {
        if (Https.Parse(url) is not { } uri) { if (!string.IsNullOrEmpty(url)) Log.Write($"Refused to open non-https link: {url}"); return; }
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* no browser registered */ }
    }
}

static class Https
{
    /// <summary>The URL when it's absolute https without embedded credentials; null otherwise.</summary>
    public static Uri? Parse(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo == "" ? uri : null;

    /// <summary>Throws unless the URL is https; tokens and API responses must never travel in the clear.</summary>
    public static string Require(string url) =>
        Parse(url) != null ? url : throw new InvalidOperationException($"{url} doesn't use https. Only https servers are supported.");
}

