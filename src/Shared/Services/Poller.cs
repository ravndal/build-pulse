namespace BuildPulse;

/// <summary>What one server returned in one poll.</summary>
sealed record ServerPoll(
    List<BuildInfo> Builds,
    List<ApprovalInfo> Approvals,
    Dictionary<string, string> Stages,
    Dictionary<string, IReadOnlyList<StageStatus>> StageStatuses,
    List<string> Warnings);

/// <summary>UI-free polling of one server: runs, pending approvals and, for running builds, their stages. Shared by the Windows and Avalonia apps.</summary>
static class Poller
{
    public const string WaitingForApproval = "Waiting for approval";

    /// <param name="manualValidationActions">Lets the popup resume or reject ManualValidation steps (experimental).</param>
    public static async Task<ServerPoll> PollServerAsync(ServerConfig server, IPipelineSource source, bool manualValidationActions)
    {
        var builds = new List<BuildInfo>();
        var approvals = new List<ApprovalInfo>();
        var warnings = new List<string>();
        bool approvalsApiFailed = false;

        foreach (var group in server.Pipelines.GroupBy(p => p.Project, StringComparer.OrdinalIgnoreCase))
        {
            var ids = group.Select(p => p.DefinitionId).Distinct().ToList();
            builds.AddRange(await source.GetBuildsAsync(group.Key, ids, Math.Clamp(ids.Count * 8, 25, 200)));
            try
            {
                var pending = await source.GetPendingApprovalsAsync(group.Key);
                approvals.AddRange(pending.Where(a => ids.Contains(a.DefinitionId)));
            }
            catch (PipelineException ex)
            {
                approvalsApiFailed = true;
                warnings.Add($"Approvals unavailable: {ex.Message}");
                Log.Write($"Approvals query failed for '{group.Key}': {ex.StatusCode} {ex.Message}");
            }
        }

        builds = builds.GroupBy(b => b.Id).Select(g => g.First()).ToList();
        foreach (var b in builds) b.ServerId = server.Id;
        foreach (var a in approvals) a.ServerId = server.Id;

        // Timelines of running builds give the current stage and the stage an approval is waiting in.
        var stages = new Dictionary<string, string>();
        var stageStatuses = new Dictionary<string, IReadOnlyList<StageStatus>>();
        foreach (var build in builds.Where(b => b.IsActive))
        {
            List<TimelineRecord> timeline;
            try { timeline = await source.GetTimelineAsync(build.Project, build.Id); }
            catch (PipelineException ex)
            {
                Log.Write($"Timeline failed for build {build.Id}: {ex.StatusCode} {ex.Message}");
                continue;
            }

            if (CurrentStage(timeline) is { } stage) stages[build.Key] = stage;
            if (StageStatuses(timeline) is { Count: > 0 } statuses) stageStatuses[build.Key] = statuses;
            foreach (var approval in approvals.Where(a => a.RunId == build.Id))
            {
                approval.StageName ??= StageOf(timeline, timeline.FirstOrDefault(r => r.Id == approval.Id));
                if (approval.RunName == "") approval.RunName = build.Number;
                if (approval.PipelineName == "") approval.PipelineName = build.DefinitionName;
            }

            if (approvalsApiFailed)
            {
                // Fall back to the timeline so the tray still flags runs waiting for approval.
                foreach (var checkpoint in timeline.Where(r => r.Type == "Checkpoint.Approval" && r.State == "inProgress"))
                    approvals.Add(new ApprovalInfo
                    {
                        ServerId = server.Id,
                        Id = checkpoint.Id,
                        Project = build.Project,
                        DefinitionId = build.DefinitionId,
                        PipelineName = build.DefinitionName,
                        RunId = build.Id,
                        RunName = build.Number,
                        StageName = StageOf(timeline, checkpoint),
                        CreatedOn = checkpoint.StartTime,
                        WebUrl = build.WebUrl,
                        CanApprove = false,
                        BrowserHint = "Approve this run in the browser (your token can't read approvals).",
                    });
            }

            // ManualValidation steps in agentless jobs pause the run like an approval but aren't in the approvals API.
            foreach (var task in timeline.Where(IsWaitingManualValidation))
                approvals.Add(new ApprovalInfo
                {
                    ServerId = server.Id,
                    Kind = ApprovalKind.ManualValidation,
                    ProjectId = build.ProjectId,
                    PlanId = build.PlanId,
                    JobId = task.ParentId ?? "",
                    Id = task.Id,
                    Project = build.Project,
                    DefinitionId = build.DefinitionId,
                    PipelineName = build.DefinitionName,
                    RunId = build.Id,
                    RunName = build.Number,
                    StageName = StageOf(timeline, task) is { } s ? $"{s} · {task.Name}" : task.Name,
                    CreatedOn = task.StartTime,
                    WebUrl = build.WebUrl,
                    CanApprove = manualValidationActions && build.PlanId != "" && task.ParentId != null,
                    BrowserHint = "This is a Manual validation step. Open the run and choose Review to resume or reject it.",
                });
        }

        foreach (var approval in approvals.Where(a => a.WebUrl == ""))
            approval.WebUrl = builds.FirstOrDefault(b => b.Id == approval.RunId)?.WebUrl ?? "";

        return new ServerPoll(builds, approvals, stages, stageStatuses, warnings);
    }

    static string? CurrentStage(List<TimelineRecord> timeline)
    {
        var waiting = timeline.FirstOrDefault(r => r.Type == "Checkpoint.Approval" && r.State == "inProgress")
            ?? timeline.FirstOrDefault(IsWaitingManualValidation);
        if (waiting != null)
            return StageOf(timeline, waiting) is { } s ? $"{WaitingForApproval} · {s}" : WaitingForApproval;

        var stage = timeline.Where(r => r.Type == "Stage" && r.State == "inProgress" && r.Name != "__default")
            .OrderBy(r => r.Order).LastOrDefault();
        if (stage != null) return stage.Name;

        return timeline.Where(r => r.Type == "Job" && r.State == "inProgress").OrderBy(r => r.Order).FirstOrDefault()?.Name;
    }

    static List<StageStatus> StageStatuses(List<TimelineRecord> timeline)
    {
        var byId = timeline.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        // Every ancestor of a waiting approval or validation is shown as waiting.
        var waiting = new HashSet<string>();
        foreach (var record in timeline.Where(r => (r.Type == "Checkpoint.Approval" && r.State == "inProgress") || IsWaitingManualValidation(r)))
            for (var r = record; r != null && waiting.Add(r.Id);)
                r = r.ParentId != null && byId.TryGetValue(r.ParentId, out var parent) ? parent : null;

        var stages = timeline.Where(r => r.Type == "Stage" && r.Name != "__default").ToList();
        // Pipelines without named stages only have the implicit one; show their jobs instead.
        if (stages.Count == 0) stages = timeline.Where(r => r.Type == "Job").ToList();
        return stages.OrderBy(r => r.Order)
            .Select(r => new StageStatus(r.Name, r.State, r.Result, waiting.Contains(r.Id)))
            .ToList();
    }

    static bool IsWaitingManualValidation(TimelineRecord r) =>
        r.Type == "Task" && r.State == "inProgress"
        && (r.TaskName ?? r.Name).Equals("ManualValidation", StringComparison.OrdinalIgnoreCase);

    static string? StageOf(List<TimelineRecord> timeline, TimelineRecord? record)
    {
        var byId = timeline.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        for (int depth = 0; record != null && depth < 10; depth++)
        {
            if (record.Type == "Stage") return record.Name == "__default" ? null : record.Name;
            record = record.ParentId != null && byId.TryGetValue(record.ParentId, out var parent) ? parent : null;
        }
        return null;
    }
}
