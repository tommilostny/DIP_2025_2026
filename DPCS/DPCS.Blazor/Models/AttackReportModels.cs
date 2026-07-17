namespace DPCS.Blazor.Models;

public sealed class AttackReportSummary
{
    public int TotalWorkUnits { get; set; }
    public int CompletedWorkUnits { get; set; }
    public int TimedOutWorkUnits { get; set; }
    public int RetriedWorkUnits { get; set; }
    public double CompletedRatePercent { get; set; }
    public double TimeoutRatePercent { get; set; }
    public double MeanDurationSeconds { get; set; }
    public double P95DurationSeconds { get; set; }
    public List<AgentReportSummary> ByAgent { get; set; } = [];
    public List<ChunkReportSummary> Chunks { get; set; } = [];
}

public sealed class AgentReportSummary
{
    public string AgentKey { get; set; } = string.Empty;
    public int TotalWorkUnits { get; set; }
    public int CompletedWorkUnits { get; set; }
    public int TimedOutWorkUnits { get; set; }
    public double MeanDurationSeconds { get; set; }
}

public sealed class ChunkReportSummary
{
    public string Mode { get; set; } = string.Empty;
    public ChunkDisplaySummary Display { get; set; } = new();
    public int AttemptCount { get; set; }
    public List<string> Agents { get; set; } = [];
    public string FinalOutcome { get; set; } = string.Empty;
    public double MeanDurationSeconds { get; set; }
    public int RecoveredCount { get; set; }
    public List<ChunkAttemptSummary> Attempts { get; set; } = [];
}

public sealed class ChunkAttemptSummary
{
    public string RequestId { get; set; } = string.Empty;
    public string AgentKey { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public double ProcessingDurationSeconds { get; set; }
    public int RecoveredCount { get; set; }
    public DateTime AssignedAtUtc { get; set; }
}

public sealed class ChunkDisplaySummary
{
    public string ModeLabel { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string PrimaryRange { get; set; } = string.Empty;
    public string SecondaryTitle { get; set; } = string.Empty;
    public string SecondaryRange { get; set; } = string.Empty;
}