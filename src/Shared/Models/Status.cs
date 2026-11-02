namespace BuildPulse;

/// <summary>A status color, independent of the UI that paints it.</summary>
public enum Tone { Green, Blue, Orange, Red, Amber, Gray }

/// <summary>Status mapping shared by the tray app and the terminal app: which color and glyph a state gets.</summary>
static class Status
{
    public static (Tone Tone, Glyph Glyph) ForState(TrayState state) => state switch
    {
        TrayState.Approval => (Tone.Orange, Glyph.Bang),
        TrayState.Running => (Tone.Blue, Glyph.Spinner),
        TrayState.Idle => (Tone.Green, Glyph.Check),
        TrayState.Error => (Tone.Red, Glyph.Cross),
        _ => (Tone.Gray, Glyph.Dots),
    };

    public static (Tone Tone, Glyph Glyph) ForBuild(BuildInfo b, bool awaitingApproval)
    {
        if (awaitingApproval) return (Tone.Orange, Glyph.Bang);
        if (b.Status == "inProgress") return (Tone.Blue, Glyph.Spinner);
        if (b.IsActive) return (Tone.Blue, Glyph.Ring);
        return ForResult(b.Result);
    }

    public static (Tone Tone, Glyph Glyph) ForResult(string? result) => result switch
    {
        "succeeded" => (Tone.Green, Glyph.Check),
        "failed" => (Tone.Red, Glyph.Cross),
        "partiallySucceeded" => (Tone.Amber, Glyph.Bang),
        _ => (Tone.Gray, Glyph.Dash),
    };

    public static (Tone Tone, Glyph Glyph) ForStage(StageStatus s) => s.State switch
    {
        "inProgress" => s.Waiting ? (Tone.Orange, Glyph.Bang) : (Tone.Blue, Glyph.Spinner),
        "completed" => s.Result switch
        {
            "succeeded" => (Tone.Green, Glyph.Check),
            "succeededWithIssues" => (Tone.Amber, Glyph.Bang),
            "failed" or "abandoned" => (Tone.Red, Glyph.Cross),
            _ => (Tone.Gray, Glyph.Dash),   // skipped, canceled
        },
        _ => (Tone.Gray, Glyph.Ring),       // pending
    };

    public static string ResultText(string? result) => result switch
    {
        "succeeded" => "Succeeded",
        "failed" => "Failed",
        "partiallySucceeded" => "Partially succeeded",
        "canceled" => "Canceled",
        _ => "Finished",
    };
}
