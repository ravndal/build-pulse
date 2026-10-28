using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using static DevOpsWatcher.Json;

namespace DevOpsWatcher;

/// <summary>
/// Minimal Azure DevOps REST client. Works against dev.azure.com and Azure DevOps Server
/// (base URL = organization or collection URL).
/// </summary>
public sealed class AzDoClient : IPipelineSource
{
    const string Api = "api-version=6.0";
    // The approvals API went GA in 7.1; older servers only know the preview versions.
    static readonly string[] ApprovalApiVersions = ["7.1", "7.1-preview.1", "7.0-preview.1", "6.0-preview.1"];

    readonly HttpClient _http;
    readonly RateLimiter _limiter;
    string? _approvalApiVersion;

    public string BaseUrl { get; }

    public AzDoClient(string baseUrl, bool useWindowsAuth, string? pat, RateLimitSettings? rateLimits = null)
    {
        BaseUrl = baseUrl.Trim().TrimEnd('/');
        _limiter = new RateLimiter(RateLimiter.UserKey(BaseUrl, useWindowsAuth, pat), rateLimits ?? new());
        _http = new HttpClient(new HttpClientHandler { UseDefaultCredentials = useWindowsAuth })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!useWindowsAuth && !string.IsNullOrEmpty(pat))
        {
            var token = Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
    }

    public async Task<List<ProjectInfo>> GetProjectsAsync()
    {
        var json = await SendAsync(RateLimiter.Endpoints.Projects, HttpMethod.Get, $"_apis/projects?$top=1000&{Api}");
        return Items(json)
            .Select(p => new ProjectInfo(Str(p.N("id")), Str(p.N("name"))))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<List<DefinitionInfo>> GetDefinitionsAsync(string project)
    {
        var json = await SendAsync(RateLimiter.Endpoints.Definitions, HttpMethod.Get, $"{Esc(project)}/_apis/build/definitions?$top=5000&{Api}");
        return Items(json)
            .Select(d => new DefinitionInfo(Long(d.N("id")), Str(d.N("name")), Str(d.N("path"))))
            .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<List<BuildInfo>> GetBuildsAsync(string project, IReadOnlyCollection<long> definitionIds, int top)
    {
        var defs = string.Join(",", definitionIds);
        var json = await SendAsync(RateLimiter.Endpoints.Builds, HttpMethod.Get,
            $"{Esc(project)}/_apis/build/builds?definitions={defs}&$top={top}&queryOrder=queueTimeDescending&{Api}");
        return Items(json).Select(ParseBuild).ToList();
    }

    public async Task<List<TimelineRecord>> GetTimelineAsync(string project, long buildId)
    {
        var json = await SendAsync(RateLimiter.Endpoints.Timeline, HttpMethod.Get, $"{Esc(project)}/_apis/build/builds/{buildId}/timeline?{Api}");
        if (json.N("records") is not JsonArray records) return [];
        return records.OfType<JsonNode>().Select(r => new TimelineRecord
        {
            Id = Str(r.N("id")),
            ParentId = NullIfEmpty(Str(r.N("parentId"))),
            Type = Str(r.N("type")),
            Name = Str(r.N("name")),
            State = Str(r.N("state")),
            Result = NullIfEmpty(Str(r.N("result"))),
            Order = Int(r.N("order")),
            StartTime = Date(r.N("startTime")),
            TaskName = NullIfEmpty(Str(r.N("task").N("name"))),
        }).ToList();
    }

    public async Task<List<ApprovalInfo>> GetPendingApprovalsAsync(string project)
    {
        var json = await ApprovalRequestAsync(v =>
            SendAsync(RateLimiter.Endpoints.Approvals, HttpMethod.Get, $"{Esc(project)}/_apis/pipelines/approvals?state=pending&$expand=steps&api-version={v}"));
        return Items(json).Select(a =>
        {
            var pipeline = a.N("pipeline");
            var owner = pipeline.N("owner");
            long runId = Long(owner.N("id"));
            var approvers = (a.N("steps") as JsonArray)?.OfType<JsonNode>()
                .Select(s => Str(s.N("assignedApprover").N("displayName")))
                .Where(n => n != "")
                .Distinct()
                .ToList() ?? [];
            return new ApprovalInfo
            {
                Id = Str(a.N("id")),
                Project = project,
                DefinitionId = Long(pipeline.N("id")),
                PipelineName = Str(pipeline.N("name")),
                RunId = runId,
                RunName = Str(owner.N("name")),
                Instructions = Str(a.N("instructions")),
                CreatedOn = Date(a.N("createdOn")),
                Approvers = approvers,
                WebUrl = RunUrl(project, runId),
            };
        }).ToList();
    }

    public async Task UpdateApprovalAsync(string project, string approvalId, bool approve, string comment)
    {
        var body = new JsonArray(new JsonObject
        {
            ["approvalId"] = approvalId,
            ["status"] = approve ? "approved" : "rejected",
            ["comment"] = comment,
        });
        await ApprovalRequestAsync(v =>
            SendAsync(RateLimiter.Endpoints.ApprovalUpdate, HttpMethod.Patch, $"{Esc(project)}/_apis/pipelines/approvals?api-version={v}", body.DeepClone()));
    }

    /// <summary>
    /// Undocumented for ManualValidation: posts the documented plan event "TaskCompleted" for the waiting task,
    /// then re-reads the timeline to confirm Azure DevOps actually moved on.
    /// </summary>
    public async Task CompleteManualValidationAsync(ApprovalInfo validation, bool resume)
    {
        if (validation.ProjectId == "" || validation.PlanId == "" || validation.JobId == "")
            throw new PipelineException(0, "Missing plan details for this run. Use Open instead.");

        var body = new JsonObject
        {
            ["name"] = "TaskCompleted",
            ["taskId"] = validation.Id,
            ["jobId"] = validation.JobId,
            ["result"] = resume ? "succeeded" : "failed",
        };
        await SendAsync(RateLimiter.Endpoints.PlanEvents, HttpMethod.Post,
            $"{validation.ProjectId}/_apis/distributedtask/hubs/build/plans/{validation.PlanId}/events?api-version=7.1", body);

        for (int attempt = 0; attempt < 4; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            var timeline = await GetTimelineAsync(validation.Project, validation.RunId);
            var record = timeline.FirstOrDefault(r => r.Id.Equals(validation.Id, StringComparison.OrdinalIgnoreCase));
            if (record == null || record.State != "inProgress") return;
        }
        throw new PipelineException(0, "Azure DevOps accepted the request, but the step is still waiting. Use Open → Review instead.");
    }

    public string RunUrl(string project, long buildId) => $"{BaseUrl}/{Esc(project)}/_build/results?buildId={buildId}";

    public void Dispose() => _http.Dispose();

    async Task<JsonNode?> ApprovalRequestAsync(Func<string, Task<JsonNode?>> send)
    {
        if (_approvalApiVersion != null) return await send(_approvalApiVersion);
        PipelineException? last = null;
        foreach (var version in ApprovalApiVersions)
        {
            try
            {
                var result = await send(version);
                _approvalApiVersion = version;
                return result;
            }
            catch (PipelineException ex) when (ex.StatusCode is 400 or 404)
            {
                last = ex;
            }
        }
        throw last!;
    }

    async Task<JsonNode?> SendAsync(string endpoint, HttpMethod method, string relativeUrl, JsonNode? body = null)
    {
        await _limiter.AcquireAsync(endpoint);
        using var request = new HttpRequestMessage(method, $"{BaseUrl}/{relativeUrl}");
        if (body != null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

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
            // Azure DevOps sends Retry-After when it delays or blocks this user's requests.
            var retryAfter = RetryAfter(response);
            if (retryAfter != null) _limiter.BlockFor(retryAfter.Value);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new PipelineException(429, retryAfter is { } wait
                    ? $"Azure DevOps is throttling requests. Retrying after {Math.Ceiling(wait.TotalSeconds)}s."
                    : "Azure DevOps is throttling requests.");

            var text = await response.Content.ReadAsStringAsync();
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            // A bad PAT on dev.azure.com yields 203 + an HTML sign-in page rather than a 401.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NonAuthoritativeInformation
                || (response.IsSuccessStatusCode && text.Length > 0 && !mediaType.Contains("json")))
                throw new PipelineException(401, "Authentication failed. Check the server URL and access token.");
            if (!response.IsSuccessStatusCode)
                throw new PipelineException((int)response.StatusCode,
                    ErrorMessage(text) ?? $"{(int)response.StatusCode} {response.ReasonPhrase}");
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
    }

    BuildInfo ParseBuild(JsonNode b)
    {
        var project = Str(b.N("project").N("name"));
        var id = Long(b.N("id"));
        var web = Str(b.N("_links").N("web").N("href"));
        return new BuildInfo
        {
            Id = id,
            Number = Str(b.N("buildNumber")),
            Status = Str(b.N("status")),
            Result = Str(b.N("result")) is "" or "none" ? null : Str(b.N("result")),
            Project = project,
            DefinitionId = Long(b.N("definition").N("id")),
            DefinitionName = Str(b.N("definition").N("name")),
            Branch = Str(b.N("sourceBranch")),
            RequestedFor = Str(b.N("requestedFor").N("displayName")),
            QueueTime = Date(b.N("queueTime")),
            StartTime = Date(b.N("startTime")),
            FinishTime = Date(b.N("finishTime")),
            WebUrl = web != "" ? web : RunUrl(project, id),
            ProjectId = Str(b.N("project").N("id")),
            PlanId = Str(b.N("orchestrationPlan").N("planId")),
        };
    }

    static IEnumerable<JsonNode> Items(JsonNode? json) =>
        (json.N("value") as JsonArray)?.OfType<JsonNode>() ?? [];

    static string Esc(string s) => Uri.EscapeDataString(s);
}
