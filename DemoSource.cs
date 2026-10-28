namespace DevOpsWatcher;

/// <summary>
/// In-memory fake used by <c>--demo</c>: a CI build finishing, a deployment that stops for approval,
/// and a build that fails. Timings are relative to app start.
/// </summary>
sealed class DemoSource : IPipelineSource
{
    public const string Url = "https://dev.azure.com/demo";
    const string Project = "Demo";
    const string ApprovalId = "6f1c2a9e-0000-4000-8000-000000000001";
    static readonly string[] Names = ["", "web-api CI", "web-api deploy", "mobile-app"];
    static readonly string[] People = ["Ola Nordmann", "Kari Nordmann", "Per Hansen"];

    const string ValidationId = "6f1c2a9e-0000-4000-8000-000000000002";

    readonly DateTime _t0 = DateTime.Now;
    DateTime? _decidedAt, _validatedAt;
    bool _rejected, _validationRejected;

    public static AppSettings CreateSettings() => new()
    {
        ServerUrl = Url,
        UseWindowsAuth = true,
        PollSeconds = 10,
        EnableManualValidationActions = true,
        Pipelines = Enumerable.Range(1, 3).Select(i => new TrackedPipeline { Project = Project, DefinitionId = i, Name = Names[i] }).ToList(),
    };

    double T => (DateTime.Now - _t0).TotalSeconds;

    public Task<List<BuildInfo>> GetBuildsAsync(string project, IReadOnlyCollection<int> definitionIds, int top)
    {
        var list = new List<BuildInfo>();
        string[] results = ["succeeded", "succeeded", "failed", "succeeded", "partiallySucceeded", "succeeded", "canceled"];
        for (int i = 0; i < results.Length; i++)
        {
            var finish = _t0.AddMinutes(-(i + 1) * 37);
            list.Add(Make(100 + i, i % 3 + 1, "completed", results[i], finish.AddMinutes(-6 - i), finish));
        }

        double t = T;
        list.Add(t < 40
            ? Make(200, 1, "inProgress", null, _t0.AddSeconds(-90), null)
            : Make(200, 1, "completed", "succeeded", _t0.AddSeconds(-90), _t0.AddSeconds(40)));

        if (t >= 10)
        {
            var start = _t0.AddSeconds(10);
            if (_rejected) list.Add(Make(201, 2, "completed", "failed", start, _decidedAt));
            else if (_decidedAt is { } d && DateTime.Now >= d.AddSeconds(20)) list.Add(Make(201, 2, "completed", "succeeded", start, d.AddSeconds(20)));
            else list.Add(Make(201, 2, "inProgress", null, start, null));
        }

        if (t >= 60)
        {
            var start = _t0.AddSeconds(60);
            if (_validationRejected) list.Add(Make(202, 3, "completed", "failed", start, _validatedAt));
            else if (_validatedAt is { } v && DateTime.Now >= v.AddSeconds(10)) list.Add(Make(202, 3, "completed", "succeeded", start, v.AddSeconds(10)));
            else list.Add(Make(202, 3, "inProgress", null, start, null));
        }
        return Task.FromResult(list.Where(b => definitionIds.Contains(b.DefinitionId)).ToList());
    }

    public Task<List<TimelineRecord>> GetTimelineAsync(string project, int buildId)
    {
        double t = T;
        var records = new List<TimelineRecord>();
        void Stage(string id, string name, int order, string state) =>
            records.Add(new TimelineRecord { Id = id, Type = "Stage", Name = name, Order = order, State = state, Result = state == "completed" ? "succeeded" : null });

        switch (buildId)
        {
            case 200:
                Stage("s1", "Build", 1, "completed");
                Stage("s2", "Test", 2, "inProgress");
                break;
            case 201:
                Stage("s1", "Build", 1, t < 25 ? "inProgress" : "completed");
                Stage("s2", "Deploy to Test", 2, t < 25 ? "pending" : t < 45 ? "inProgress" : "completed");
                Stage("s3", "Deploy to Prod", 3, _decidedAt != null ? "inProgress" : "pending");
                if (t >= 45 && _decidedAt == null)
                {
                    records.Add(new TimelineRecord { Id = "cp", ParentId = "s3", Type = "Checkpoint", State = "inProgress" });
                    records.Add(new TimelineRecord { Id = ApprovalId, ParentId = "cp", Type = "Checkpoint.Approval", State = "inProgress", StartTime = _t0.AddSeconds(45) });
                }
                break;
            case 202:
                Stage("s1", "Build", 1, t < 70 ? "inProgress" : "completed");
                Stage("s2", "Store release", 2, t < 70 ? "pending" : "inProgress");
                if (t >= 70 && _validatedAt == null)
                {
                    records.Add(new TimelineRecord { Id = "job-mv", ParentId = "s2", Type = "Job", Name = "Sign-off", State = "inProgress" });
                    records.Add(new TimelineRecord
                    {
                        Id = ValidationId, ParentId = "job-mv", Type = "Task", Name = "Confirm store listing",
                        TaskName = "ManualValidation", State = "inProgress", StartTime = _t0.AddSeconds(70),
                    });
                }
                break;
        }
        return Task.FromResult(records);
    }

    public Task<List<ApprovalInfo>> GetPendingApprovalsAsync(string project)
    {
        if (T < 45 || _decidedAt != null) return Task.FromResult(new List<ApprovalInfo>());
        return Task.FromResult(new List<ApprovalInfo>
        {
            new()
            {
                Id = ApprovalId,
                Project = Project,
                DefinitionId = 2,
                PipelineName = Names[2],
                RunId = 201,
                RunName = $"{_t0:yyyyMMdd}.2",
                CreatedOn = _t0.AddSeconds(45),
                Approvers = ["Release managers"],
                Instructions = "Check the smoke tests in Test before promoting to production.",
                WebUrl = RunUrl(201),
            },
        });
    }

    public async Task UpdateApprovalAsync(string project, string approvalId, bool approve, string comment)
    {
        await Task.Delay(500);
        _decidedAt = DateTime.Now;
        _rejected = !approve;
    }

    public async Task CompleteManualValidationAsync(ApprovalInfo validation, bool resume)
    {
        await Task.Delay(800);
        _validatedAt = DateTime.Now;
        _validationRejected = !resume;
    }

    BuildInfo Make(int id, int def, string status, string? result, DateTime start, DateTime? finish) => new()
    {
        Id = id,
        Number = $"{start:yyyyMMdd}.{id % 10 + 1}",
        Status = status,
        Result = result,
        Project = Project,
        DefinitionId = def,
        DefinitionName = Names[def],
        Branch = def == 3 ? "refs/pull/482/merge" : "refs/heads/main",
        RequestedFor = People[id % People.Length],
        QueueTime = start.AddSeconds(-5),
        StartTime = start,
        FinishTime = finish,
        WebUrl = RunUrl(id),
        ProjectId = "00000000-0000-4000-8000-0000000000aa",
        PlanId = $"00000000-0000-4000-8000-000000000{id}",
    };

    static string RunUrl(int id) => $"{Url}/{Project}/_build/results?buildId={id}";

    public void Dispose() { }
}
