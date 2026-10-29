using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using static BuildPulse.Json;

namespace BuildPulse;

/// <summary>
/// Minimal GitHub Actions REST client for github.com, GitHub Enterprise Cloud (*.ghe.com) and GitHub Enterprise Server.
/// Maps repos to projects, workflows to definitions, runs to builds and jobs to timeline records, using the
/// Azure DevOps status names the rest of the app understands. Environment reviews are the approvals.
/// </summary>
public sealed class GitHubClient : IPipelineSource
{
    const int MaxRepoPages = 10;
    const int MaxCachedUrls = 300;

    readonly HttpClient _http;
    readonly RateLimiter _limiter;
    /// <summary>Last ETag and body per GET URL. A 304 reply is free: it doesn't count against GitHub's rate limit.</summary>
    readonly Dictionary<string, (string ETag, string Body)> _etags = [];
    /// <summary>Reset time of the quota window last logged, and whether that window's low-quota warning went out.</summary>
    long _quotaReset;
    bool _quotaWarned;

    public string ApiUrl { get; }

    public GitHubClient(string serverUrl, string? token, RateLimitSettings? rateLimits = null)
    {
        ApiUrl = ApiBase(serverUrl);
        _limiter = new RateLimiter(RateLimiter.UserKey(ApiUrl, false, token), rateLimits ?? new());
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.Id, (typeof(GitHubClient).Assembly.GetName().Version ?? new Version(0, 0)).ToString(3)));
        if (!string.IsNullOrEmpty(token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>REST base URL for a web URL: api.github.com, api.SUBDOMAIN.ghe.com, or HOST/api/v3 for Enterprise Server.</summary>
    public static string ApiBase(string serverUrl)
    {
        var url = serverUrl.Trim().TrimEnd('/');
        if (url == "") url = ServerConfig.GitHubUrl;
        if (!url.Contains("://")) url = "https://" + url;
        var uri = new Uri(url);
        var host = uri.Host.ToLowerInvariant();
        if (host is "github.com" or "www.github.com" or "api.github.com") return "https://api.github.com";
        if (host.StartsWith("api.", StringComparison.Ordinal)) return url;
        if (host.EndsWith(".ghe.com", StringComparison.Ordinal)) return $"{uri.Scheme}://api.{host}";
        return uri.AbsolutePath.TrimEnd('/').EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase) ? url : url + "/api/v3";
    }

    public async Task<List<ProjectInfo>> GetProjectsAsync()
    {
        var repos = new List<ProjectInfo>();
        for (int page = 1; page <= MaxRepoPages; page++)
        {
            var json = await SendAsync(RateLimiter.Endpoints.Projects, HttpMethod.Get, $"user/repos?per_page=100&sort=full_name&page={page}");
            var items = (json as JsonArray)?.OfType<JsonNode>().ToList() ?? [];
            repos.AddRange(items.Select(r => new ProjectInfo(Str(r.N("id")), Str(r.N("full_name")))));
            if (items.Count < 100) break;
        }
        return repos.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<List<DefinitionInfo>> GetDefinitionsAsync(string project)
    {
        var json = await SendAsync(RateLimiter.Endpoints.Definitions, HttpMethod.Get, $"repos/{Repo(project)}/actions/workflows?per_page=100");
        return Array(json.N("workflows"))
            .Select(w => new DefinitionInfo(Long(w.N("id")), Str(w.N("name")), ""))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>One call per workflow, so busy untracked workflows in the same repo can't push tracked runs out of the page.</summary>
    public async Task<List<BuildInfo>> GetBuildsAsync(string project, IReadOnlyCollection<long> definitionIds, int top)
    {
        int perWorkflow = Math.Clamp(top / Math.Max(1, definitionIds.Count), 8, 100);
        var builds = new List<BuildInfo>();
        foreach (var id in definitionIds)
        {
            var json = await SendAsync(RateLimiter.Endpoints.Builds, HttpMethod.Get,
                $"repos/{Repo(project)}/actions/workflows/{id}/runs?per_page={perWorkflow}");
            builds.AddRange(Array(json.N("workflow_runs")).Select(r => ParseRun(project, r)));
        }
        return builds;
    }

    public async Task<List<TimelineRecord>> GetTimelineAsync(string project, long buildId)
    {
        var json = await SendAsync(RateLimiter.Endpoints.Timeline, HttpMethod.Get,
            $"repos/{Repo(project)}/actions/runs/{buildId}/jobs?filter=latest&per_page=100");
        var records = new List<TimelineRecord>();
        int order = 0;
        foreach (var job in Array(json.N("jobs")))
        {
            var id = Str(job.N("id"));
            var status = Str(job.N("status"));
            records.Add(new TimelineRecord
            {
                Id = id,
                Type = "Job",
                Name = Str(job.N("name")),
                State = status switch
                {
                    "in_progress" or "waiting" => "inProgress",
                    "completed" => "completed",
                    _ => "pending",
                },
                Result = status == "completed" ? MapConclusion(Str(job.N("conclusion"))) : null,
                Order = ++order,
                StartTime = Date(job.N("started_at")),
            });
            // A job waiting on an environment's required reviewers: shown like an Azure DevOps approval checkpoint.
            if (status == "waiting")
                records.Add(new TimelineRecord
                {
                    Id = id + ":review",
                    ParentId = id,
                    Type = "Checkpoint.Approval",
                    Name = "Review",
                    State = "inProgress",
                    StartTime = Date(job.N("created_at")),
                });
        }
        return records;
    }

    public async Task<List<ApprovalInfo>> GetPendingApprovalsAsync(string project)
    {
        var runsJson = await SendAsync(RateLimiter.Endpoints.Approvals, HttpMethod.Get,
            $"repos/{Repo(project)}/actions/runs?status=waiting&per_page=30");
        var approvals = new List<ApprovalInfo>();
        foreach (var run in Array(runsJson.N("workflow_runs")))
        {
            long runId = Long(run.N("id"));
            var json = await SendAsync(RateLimiter.Endpoints.Approvals, HttpMethod.Get,
                $"repos/{Repo(project)}/actions/runs/{runId}/pending_deployments");
            foreach (var pending in Array(json))
            {
                var reviewers = Array(pending.N("reviewers"))
                    .Select(r => r.N("reviewer"))
                    .Select(r => Str(r.N("login")) is { Length: > 0 } login ? login : Str(r.N("name")))
                    .Where(n => n != "")
                    .Distinct()
                    .ToList();
                // Environments with only a wait timer pause the run too, but there's nothing to approve.
                if (reviewers.Count == 0) continue;
                bool canApprove = Bool(pending.N("current_user_can_approve"));
                approvals.Add(new ApprovalInfo
                {
                    Id = $"{runId}:{Long(pending.N("environment").N("id"))}",
                    Project = project,
                    DefinitionId = Long(run.N("workflow_id")),
                    PipelineName = Str(run.N("name")),
                    RunId = runId,
                    RunName = Str(run.N("run_number")),
                    StageName = NullIfEmpty(Str(pending.N("environment").N("name"))),
                    CreatedOn = Date(run.N("updated_at")),
                    Approvers = reviewers,
                    WebUrl = Str(run.N("html_url")),
                    CanApprove = canApprove,
                    BrowserHint = canApprove ? null : "You aren't a required reviewer for this environment.",
                });
            }
        }
        return approvals;
    }

    public async Task UpdateApprovalAsync(string project, string approvalId, bool approve, string comment)
    {
        var parts = approvalId.Split(':');
        if (parts.Length != 2 || !long.TryParse(parts[0], out var runId) || !long.TryParse(parts[1], out var environmentId))
            throw new PipelineException(0, "Unknown approval.");
        var body = new JsonObject
        {
            ["environment_ids"] = new JsonArray(environmentId),
            ["state"] = approve ? "approved" : "rejected",
            ["comment"] = comment,
        };
        await SendAsync(RateLimiter.Endpoints.ApprovalUpdate, HttpMethod.Post,
            $"repos/{Repo(project)}/actions/runs/{runId}/pending_deployments", body);
    }

    public Task CompleteManualValidationAsync(ApprovalInfo validation, bool resume) =>
        throw new PipelineException(0, "GitHub Actions has no Manual validation steps.");

    public void Dispose() => _http.Dispose();

    BuildInfo ParseRun(string project, JsonNode r)
    {
        var status = Str(r.N("status"));
        var pr = Array(r.N("pull_requests")).FirstOrDefault();
        return new BuildInfo
        {
            Id = Long(r.N("id")),
            Number = Str(r.N("run_number")),
            Status = status switch
            {
                "in_progress" or "waiting" => "inProgress",
                "completed" => "completed",
                _ => "notStarted", // queued, requested, pending
            },
            Result = status == "completed" ? MapConclusion(Str(r.N("conclusion"))) : null,
            Project = project,
            DefinitionId = Long(r.N("workflow_id")),
            DefinitionName = Str(r.N("name")),
            // Shown as "PR 123" like Azure DevOps pull request builds.
            Branch = Str(r.N("event")).StartsWith("pull_request", StringComparison.Ordinal) && pr != null
                ? $"refs/pull/{Str(pr.N("number"))}/merge"
                : Str(r.N("head_branch")),
            RequestedFor = Str(r.N("triggering_actor").N("login")) is { Length: > 0 } actor ? actor : Str(r.N("actor").N("login")),
            QueueTime = Date(r.N("created_at")),
            StartTime = Date(r.N("run_started_at")),
            FinishTime = status == "completed" ? Date(r.N("updated_at")) : null,
            WebUrl = Str(r.N("html_url")),
        };
    }

    /// <summary>GitHub conclusion → Azure DevOps result name.</summary>
    static string? MapConclusion(string conclusion) => conclusion switch
    {
        "success" => "succeeded",
        "failure" or "timed_out" or "startup_failure" => "failed",
        "cancelled" => "canceled",
        "skipped" or "neutral" => "skipped",
        "" => null,
        _ => conclusion,
    };

    async Task<JsonNode?> SendAsync(string endpoint, HttpMethod method, string relativeUrl, JsonNode? body = null)
    {
        await _limiter.AcquireAsync(endpoint);
        var url = Https.Require($"{ApiUrl}/{relativeUrl}");
        using var request = new HttpRequestMessage(method, url);
        if (body != null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        bool cacheable = method == HttpMethod.Get;
        if (cacheable && _etags.TryGetValue(url, out var cached))
            request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request);
        }
        catch (TaskCanceledException)
        {
            throw new PipelineException(0, "The request timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new PipelineException(0, ex.Message);
        }

        using (response)
        {
            LogQuota(response);
            if (response.StatusCode == HttpStatusCode.NotModified && cacheable && _etags.TryGetValue(url, out cached))
                return Parse(cached.Body);

            // Secondary limits send Retry-After; the hourly limit sends remaining=0 and a reset time.
            var retryAfter = RetryAfter(response) ?? ResetDelay(response);
            if (retryAfter != null) _limiter.BlockFor(retryAfter.Value);
            if (response.StatusCode == HttpStatusCode.TooManyRequests
                || (response.StatusCode == HttpStatusCode.Forbidden && retryAfter != null))
                throw new PipelineException(429, retryAfter is { } wait
                    ? $"GitHub rate limit reached. Retrying after {Math.Ceiling(wait.TotalSeconds)}s."
                    : "GitHub rate limit reached.");

            var text = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new PipelineException(401, "Authentication failed. Check the server URL and access token.");
            if (!response.IsSuccessStatusCode)
            {
                var message = ErrorMessage(text) ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
                // GitHub answers 404 rather than 403 when the token can't see a repo.
                if (response.StatusCode == HttpStatusCode.NotFound) message += " (or the token has no access)";
                throw new PipelineException((int)response.StatusCode, message);
            }

            if (cacheable && response.Headers.ETag is { } etag)
            {
                // Job and review URLs are per run, so the cache would grow forever.
                if (_etags.Count >= MaxCachedUrls) _etags.Clear();
                _etags[url] = (etag.ToString(), text);
            }
            return Parse(text);
        }
    }

    static JsonNode? Parse(string text) => string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);

    /// <summary>
    /// Logs the hourly quota once per window, and again when it runs low. The quota is shared by every personal
    /// access token of the same user, so usage already high at the first call points at another tool.
    /// </summary>
    void LogQuota(HttpResponseMessage response)
    {
        if (!long.TryParse(Header(response, "x-ratelimit-reset"), out var reset)
            || !int.TryParse(Header(response, "x-ratelimit-limit"), out var limit)
            || !int.TryParse(Header(response, "x-ratelimit-remaining"), out var remaining)) return;
        bool newWindow = reset != _quotaReset;
        bool low = remaining <= limit / 10;
        if (!newWindow && (!low || _quotaWarned)) return;
        if (newWindow) _quotaWarned = false;
        _quotaReset = reset;
        _quotaWarned |= low;
        var resetsAt = DateTimeOffset.FromUnixTimeSeconds(reset).ToLocalTime();
        Log.Write($"GitHub quota for {ApiUrl}: {limit - remaining}/{limit} used, resets {resetsAt:HH:mm:ss}{(low ? " (running low)" : "")}");
    }

    static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    static TimeSpan? ResetDelay(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) || remaining.FirstOrDefault() != "0") return null;
        if (!response.Headers.TryGetValues("x-ratelimit-reset", out var reset) || !long.TryParse(reset.FirstOrDefault(), out var epoch)) return null;
        var wait = DateTimeOffset.FromUnixTimeSeconds(epoch) - DateTimeOffset.UtcNow;
        return wait > TimeSpan.Zero ? wait : null;
    }

    static IEnumerable<JsonNode> Array(JsonNode? json) => (json as JsonArray)?.OfType<JsonNode>() ?? [];

    /// <summary>"owner/repo" with each part escaped but the slash kept.</summary>
    static string Repo(string project) => string.Join("/", project.Split('/').Select(Uri.EscapeDataString));
}
