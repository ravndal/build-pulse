namespace DevOpsWatcher;

public sealed record ProjectInfo(string Id, string Name);

public sealed record DefinitionInfo(long Id, string Name, string Path)
{
    public string DisplayName => Path is "" or "\\" ? Name : $"{Path.Trim('\\')}\\{Name}";
}

public sealed class BuildInfo
{
    /// <summary>The <see cref="ServerConfig.Id"/> this run came from; set by the poller.</summary>
    public string ServerId { get; set; } = "";
    public long Id { get; init; }
    public string Number { get; init; } = "";
    public string Status { get; init; } = "";
    public string? Result { get; init; }
    public string Project { get; init; } = "";
    public long DefinitionId { get; init; }
    public string DefinitionName { get; init; } = "";
    public string Branch { get; init; } = "";
    public string RequestedFor { get; init; } = "";
    public DateTime? QueueTime { get; init; }
    public DateTime? StartTime { get; init; }
    public DateTime? FinishTime { get; init; }
    public string WebUrl { get; init; } = "";
    /// <summary>Project GUID and orchestration plan id; needed to post events to a running plan.</summary>
    public string ProjectId { get; init; } = "";
    public string PlanId { get; init; } = "";

    public bool IsActive => Status is "inProgress" or "notStarted" or "postponed" or "cancelling";

    /// <summary>Unique across servers (run ids are only unique per server).</summary>
    public string Key => $"{ServerId}|{Id}";
    public string DefinitionKey => $"{ServerId}|{Project}|{DefinitionId}";
}

public sealed class TimelineRecord
{
    public string Id { get; init; } = "";
    public string? ParentId { get; init; }
    public string Type { get; init; } = "";
    public string Name { get; init; } = "";
    public string State { get; init; } = "";
    public string? Result { get; init; }
    public int Order { get; init; }
    public DateTime? StartTime { get; init; }
    /// <summary>Task reference name (e.g. "ManualValidation"), independent of the step's display name.</summary>
    public string? TaskName { get; init; }
}

/// <summary>One stage of a run (or job, for pipelines without named stages) as shown in the dashboard.</summary>
public sealed record StageStatus(string Name, string State, string? Result, bool Waiting)
{
    public bool IsCurrent => State == "inProgress";
}

public enum ApprovalKind { Approval, ManualValidation }

public sealed class ApprovalInfo
{
    public ApprovalKind Kind { get; init; } = ApprovalKind.Approval;
    /// <summary>The <see cref="ServerConfig.Id"/> this approval came from; set by the poller.</summary>
    public string ServerId { get; set; } = "";
    public string Id { get; init; } = "";
    public string Project { get; init; } = "";
    public long DefinitionId { get; init; }
    public string PipelineName { get; set; } = "";
    public long RunId { get; init; }
    public string RunName { get; set; } = "";
    public string? StageName { get; set; }
    public string Instructions { get; init; } = "";
    public DateTime? CreatedOn { get; init; }
    public List<string> Approvers { get; init; } = [];
    public string WebUrl { get; set; } = "";
    /// <summary>False when the approval was inferred from the build timeline and must be handled in the browser.</summary>
    public bool CanApprove { get; init; } = true;
    /// <summary>Shown instead of the approve controls when <see cref="CanApprove"/> is false.</summary>
    public string? BrowserHint { get; init; }

    // ManualValidation only: where the waiting task lives.
    public string ProjectId { get; init; } = "";
    public string PlanId { get; init; } = "";
    public string JobId { get; init; } = "";

    public string Key => $"{ServerId}|{Id}";
    /// <summary>Matches <see cref="BuildInfo.Key"/> of the run this approval belongs to.</summary>
    public string RunKey => $"{ServerId}|{RunId}";
}

/// <summary>
/// A CI server. "Project" is an Azure DevOps project or a GitHub "owner/repo"; a "definition" is a
/// pipeline or a workflow; a "build" is a run.
/// </summary>
public interface IPipelineSource : IDisposable
{
    Task<List<ProjectInfo>> GetProjectsAsync();
    Task<List<DefinitionInfo>> GetDefinitionsAsync(string project);
    Task<List<BuildInfo>> GetBuildsAsync(string project, IReadOnlyCollection<long> definitionIds, int top);
    Task<List<TimelineRecord>> GetTimelineAsync(string project, long buildId);
    Task<List<ApprovalInfo>> GetPendingApprovalsAsync(string project);
    Task UpdateApprovalAsync(string project, string approvalId, bool approve, string comment);
    /// <summary>Experimental: resumes or rejects a waiting ManualValidation task. Throws if the step is still waiting afterwards.</summary>
    Task CompleteManualValidationAsync(ApprovalInfo validation, bool resume);
}
