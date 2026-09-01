namespace AECS.Domain.Models;

public static class ProactiveAlertSchema
{
    public const string PolicyVersion = "aecs.alert-policy/v1";
    public const string AlertVersion = "aecs.proactive-alert/v1";
    public const string DeliveryVersion = "aecs.alert-delivery/v1";
    public const string MetricsVersion = "aecs.alert-effectiveness/v1";
}

public enum ProactiveAlertKind
{
    Security,
    Scope,
    Budget,
    RepeatedFailure,
    Regression,
    HumanReview
}

public enum ProactiveAlertSeverity
{
    Info,
    Warning,
    High,
    Critical
}

public enum ProactiveAlertStatus
{
    Pending,
    Delivered,
    Read,
    Actioned,
    Expired,
    DeliveryFailed
}

public enum ProactiveAlertLifecycleKind
{
    Created,
    DeliveryAttempted,
    Delivered,
    DeliveryFailed,
    Deduplicated,
    Escalated,
    Read,
    Actioned,
    Expired
}

public enum AlertDeathCriterionDecision
{
    InsufficientSample,
    Continue,
    Disable
}

public sealed class AlertChannelAuthorization
{
    public bool AllowCode { get; init; }
    public bool AllowSecrets { get; init; }
}

public sealed class AlertDeathCriteria
{
    public int MinimumDeliveredAlerts { get; init; } = 20;
    public decimal MinimumActionRate { get; init; } = 0.10m;
    public decimal MaximumIgnoredRate { get; init; } = 0.70m;
}

public sealed class ProactiveAlertPolicy
{
    public string SchemaVersion { get; init; } = ProactiveAlertSchema.PolicyVersion;
    public string Id { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public string Recipient { get; init; } = string.Empty;
    public int CooldownMinutes { get; init; } = 60;
    public int LookbackHours { get; init; } = 24;
    public int RepeatedFailureThreshold { get; init; } = 2;
    public int DefaultDeadlineMinutes { get; init; } = 60;
    public List<ProactiveAlertKind> Events { get; init; } = Enum
        .GetValues<ProactiveAlertKind>()
        .ToList();
    public Dictionary<ProactiveAlertKind, ProactiveAlertSeverity> SeverityOverrides { get; init; } = [];
    public Dictionary<ProactiveAlertKind, int> DeadlineMinutes { get; init; } = [];
    public AlertChannelAuthorization ChannelAuthorization { get; init; } = new();
    public AlertDeathCriteria DeathCriteria { get; init; } = new();
}

public sealed class ProactiveAlertLifecycleEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ProactiveAlertLifecycleKind Kind { get; init; }
    public DateTime OccurredAt { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public bool GrantsApproval { get; init; }
}

public sealed class ProactiveAlert
{
    public string SchemaVersion { get; init; } = ProactiveAlertSchema.AlertVersion;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string DeduplicationKey { get; init; } = string.Empty;
    public string PolicyId { get; init; } = string.Empty;
    public string RepositoryPath { get; init; } = string.Empty;
    public string Recipient { get; init; } = string.Empty;
    public ProactiveAlertKind Kind { get; init; }
    public ProactiveAlertSeverity Severity { get; set; }
    public ProactiveAlertStatus Status { get; set; }
    public string TaskId { get; init; } = string.Empty;
    public Guid RunId { get; init; }
    public Guid LatestEvidenceId { get; set; }
    public List<Guid> EvidenceIds { get; init; } = [];
    public string EvidenceUri { get; set; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string RecommendedAction { get; init; } = string.Empty;
    public int OccurrenceCount { get; set; } = 1;
    public DateTime DeadlineAt { get; set; }
    public DateTime? LastDeliveryAttemptAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; set; }
    public List<ProactiveAlertLifecycleEvent> Lifecycle { get; init; } = [];
}

public sealed class ProactiveAlertDelivery
{
    public string SchemaVersion { get; init; } = ProactiveAlertSchema.DeliveryVersion;
    public Guid AlertId { get; init; }
    public string PolicyId { get; init; } = string.Empty;
    public string Recipient { get; init; } = string.Empty;
    public ProactiveAlertKind Kind { get; init; }
    public ProactiveAlertSeverity Severity { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string RecommendedAction { get; init; } = string.Empty;
    public string EvidenceUri { get; init; } = string.Empty;
    public DateTime DeadlineAt { get; init; }
    public bool ContainsCode { get; init; }
    public bool ContainsSecrets { get; init; }
}

public sealed class ProactiveAlertSinkCapabilities
{
    public bool AcceptsCode { get; init; }
    public bool AcceptsSecrets { get; init; }
}

public sealed class ProactiveAlertDeliveryResult
{
    public bool Succeeded { get; init; }
    public DateTime FinishedAt { get; init; }
    public string Message { get; init; } = string.Empty;
    public string Receipt { get; init; } = string.Empty;
}

public sealed class ProactiveAlertEvaluationReport
{
    public string SchemaVersion { get; init; } = "aecs.alert-evaluation/v1";
    public bool PolicyEnabled { get; init; }
    public int EvidenceRecordsEvaluated { get; init; }
    public int CandidatesDetected { get; init; }
    public int AlertsCreated { get; init; }
    public int AlertsDelivered { get; init; }
    public int AlertsDeduplicated { get; init; }
    public int AlertsEscalated { get; init; }
    public int DeliveryFailures { get; init; }
    public int AlertsExpired { get; init; }
    public List<Guid> AlertIds { get; init; } = [];
    public List<string> Diagnostics { get; init; } = [];
}

public sealed class ProactiveAlertEffectivenessReport
{
    public string SchemaVersion { get; init; } = ProactiveAlertSchema.MetricsVersion;
    public string PolicyId { get; init; } = string.Empty;
    public int DeliveredAlerts { get; init; }
    public int ActionedAlerts { get; init; }
    public int IgnoredAlerts { get; init; }
    public decimal ActionRate { get; init; }
    public decimal IgnoredRate { get; init; }
    public AlertDeathCriterionDecision Decision { get; init; }
    public string Reason { get; init; } = string.Empty;
}
