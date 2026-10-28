namespace DevOpsWatcher;

public sealed record ProjectInfo(string Id, string Name);

public sealed record DefinitionInfo(int Id, string Name, string Path)
{
    public string DisplayName => Path is "" or "\\" ? Name : $"{Path.Trim('\\')}\\{Name}";
}

public sealed class BuildInfo
{
    public int Id { get; init; }
    public string Number { get; init; } = "";
    public string Status { get; init; } = "";
    public string? Result { get; init; }
    public string Project { get; init; } = "";
    public int DefinitionId { get; init; }
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

public enum ApprovalKind { Approval, ManualValidation }

public sealed class ApprovalInfo
{
    public ApprovalKind Kind { get; init; } = ApprovalKind.Approval;
    public string Id { get; init; } = "";
    public string Project { get; init; } = "";
    public int DefinitionId { get; init; }
    public string PipelineName { get; set; } = "";
    public int RunId { get; init; }
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
}

public interface IPipelineSource : IDisposable
{
    Task<List<BuildInfo>> GetBuildsAsync(string project, IReadOnlyCollection<int> definitionIds, int top);
    Task<List<TimelineRecord>> GetTimelineAsync(string project, int buildId);
    Task<List<ApprovalInfo>> GetPendingApprovalsAsync(string project);
    Task UpdateApprovalAsync(string project, string approvalId, bool approve, string comment);
    /// <summary>Experimental: resumes or rejects a waiting ManualValidation task. Throws if the step is still waiting afterwards.</summary>
    Task CompleteManualValidationAsync(ApprovalInfo validation, bool resume);
}
