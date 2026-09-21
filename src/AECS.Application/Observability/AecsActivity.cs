using System.Diagnostics;

namespace AECS.Application.Observability;

/// <summary>
/// Tracing surface for AECS executions (Fundação §28 / M0 "OpenTelemetry básico").
/// BCL-only on purpose: Application must not reference vendor SDKs (rule).
/// The OTLP exporter is wired separately in Infrastructure via
/// <c>OtlpTelemetry.Configure()</c>; without a listener/collector this is a no-op.
/// </summary>
public static class AecsActivity
{
    public const string SourceName = "AECS";
    public const string SourceVersion = "1.0.0";

    public static readonly ActivitySource Source = new(SourceName, SourceVersion);

    // Event names from Fundação §28 — stable wire vocabulary.
    public const string TaskCreated = "task.created";
    public const string TaskCompiled = "task.compiled";
    public const string AgentStarted = "agent.started";
    public const string AgentStopped = "agent.stopped";
    public const string BudgetUpdated = "budget.updated";
    public const string FileModified = "file.modified";
    public const string VerificationStarted = "verification.started";
    public const string VerificationCompleted = "verification.completed";
    public const string PatchAccepted = "patch.accepted";
    public const string PatchRejected = "patch.rejected";

    public static Activity? Start(string operationName) =>
        Source.StartActivity(operationName);

    public static void Event(Activity? activity, string name)
    {
        if (activity is null)
        {
            return;
        }

        activity.AddEvent(new ActivityEvent(name));
    }

    public static void Event(Activity? activity, string name, string tagKey, string tagValue)
    {
        if (activity is null)
        {
            return;
        }

        activity.AddEvent(new ActivityEvent(name, tags: new ActivityTagsCollection
        {
            [tagKey] = tagValue
        }));
    }

    public static void Tag(Activity? activity, string key, object? value) =>
        activity?.SetTag(key, value);
}
