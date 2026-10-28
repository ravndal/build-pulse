using System.Security.Cryptography;
using System.Text;

namespace DevOpsWatcher;

/// <summary>
/// Client-side throttling of Azure DevOps calls: one budget for the signed-in user across all endpoints,
/// plus optional budgets per endpoint. History is kept per user, so recreating the client doesn't reset it.
/// Retry-After from the server always pauses the user's calls, even when client limits are off.
/// </summary>
sealed class RateLimiter
{
    /// <summary>Endpoint names used as keys in <see cref="RateLimitSettings.PerEndpoint"/>.</summary>
    public static class Endpoints
    {
        public const string Projects = "projects";
        public const string Definitions = "definitions";
        public const string Builds = "builds";
        public const string Timeline = "timeline";
        public const string Approvals = "approvals";
        public const string ApprovalUpdate = "approvalUpdate";
        public const string PlanEvents = "planEvents";

        public static readonly string[] All = [Projects, Definitions, Builds, Timeline, Approvals, ApprovalUpdate, PlanEvents];
    }

    sealed class Window
    {
        readonly Queue<DateTime> _stamps = new();

        /// <summary>Time until a slot is free under <paramref name="limit"/>; zero if one is free now.</summary>
        public TimeSpan WaitTime(RateLimit? limit, DateTime now)
        {
            if (limit is not { Requests: > 0 }) return TimeSpan.Zero;
            var period = TimeSpan.FromSeconds(Math.Max(1, limit.PerSeconds));
            while (_stamps.Count > 0 && now - _stamps.Peek() >= period) _stamps.Dequeue();
            return _stamps.Count < limit.Requests ? TimeSpan.Zero : _stamps.Peek() + period - now;
        }

        public void Record(DateTime now) => _stamps.Enqueue(now);
    }

    sealed class UserState
    {
        public readonly Window Total = new();
        public readonly Dictionary<string, Window> Endpoints = new(StringComparer.OrdinalIgnoreCase);
        public DateTime BlockedUntil;
    }

    static readonly Lock Gate = new();
    static readonly Dictionary<string, UserState> Users = [];

    readonly RateLimitSettings _settings;
    readonly Dictionary<string, RateLimit> _endpointLimits;
    readonly UserState _user;

    public RateLimiter(string userKey, RateLimitSettings settings)
    {
        _settings = settings;
        _endpointLimits = new(settings.PerEndpoint ?? [], StringComparer.OrdinalIgnoreCase);
        lock (Gate)
        {
            if (!Users.TryGetValue(userKey, out var user)) Users[userKey] = user = new UserState();
            _user = user;
        }
    }

    /// <summary>Stable, non-reversible key for the signed-in identity (server + credential).</summary>
    public static string UserKey(string serverUrl, bool useWindowsAuth, string? pat)
    {
        var identity = useWindowsAuth ? $"windows:{Environment.UserDomainName}\\{Environment.UserName}" : $"pat:{pat}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{serverUrl.Trim().TrimEnd('/').ToLowerInvariant()}|{identity}"));
        return Convert.ToHexString(bytes);
    }

    /// <summary>Waits for a free slot for <paramref name="endpoint"/>, or throws a 429 if that takes longer than MaxWaitSeconds.</summary>
    public async Task AcquireAsync(string endpoint)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(0, _settings.MaxWaitSeconds));
        bool logged = false;
        while (true)
        {
            TimeSpan wait;
            lock (Gate)
            {
                var now = DateTime.UtcNow;
                wait = _user.BlockedUntil - now;
                if (_settings.Enabled)
                {
                    _endpointLimits.TryGetValue(endpoint, out var endpointLimit);
                    if (!_user.Endpoints.TryGetValue(endpoint, out var window))
                        _user.Endpoints[endpoint] = window = new Window();
                    var userWait = _user.Total.WaitTime(_settings.PerUser, now);
                    var endpointWait = window.WaitTime(endpointLimit, now);
                    wait = new[] { wait, userWait, endpointWait }.Max();
                    if (wait <= TimeSpan.Zero)
                    {
                        if (_settings.PerUser is { Requests: > 0 }) _user.Total.Record(now);
                        if (endpointLimit is { Requests: > 0 }) window.Record(now);
                    }
                }
                if (wait <= TimeSpan.Zero) return;
            }

            if (DateTime.UtcNow + wait > deadline)
                throw new PipelineException(429, $"Rate limit reached for '{endpoint}'. Try again in {Math.Ceiling(wait.TotalSeconds)}s.");
            if (!logged)
            {
                Log.Write($"Rate limit: waiting {wait.TotalSeconds:0.#}s for '{endpoint}'");
                logged = true;
            }
            await Task.Delay(wait);
        }
    }

    /// <summary>Honours the server's Retry-After for all of this user's calls.</summary>
    public void BlockFor(TimeSpan delay)
    {
        lock (Gate)
        {
            var until = DateTime.UtcNow + delay;
            if (until > _user.BlockedUntil) _user.BlockedUntil = until;
        }
        Log.Write($"Rate limit: server asked to wait {delay.TotalSeconds:0}s");
    }
}
