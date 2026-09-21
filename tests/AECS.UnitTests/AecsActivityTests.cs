using System.Diagnostics;
using AECS.Application.Observability;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class AecsActivityTests
{
    [Fact]
    public void Start_EmitsActivity_AndEvents_ToListener()
    {
        var recorded = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AecsActivity.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => recorded.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        using (var activity = AecsActivity.Start("aecs.execution"))
        {
            AecsActivity.Tag(activity, "aecs.task_id", "T-OTEL");
            AecsActivity.Event(activity, AecsActivity.TaskCreated);
            AecsActivity.Event(activity, AecsActivity.PatchAccepted, "aecs.task_id", "T-OTEL");
        }

        recorded.Should().ContainSingle();
        var root = recorded.Single();
        root.OperationName.Should().Be("aecs.execution");
        root.GetTagItem("aecs.task_id").Should().Be("T-OTEL");
        root.Events.Select(e => e.Name).Should().Contain(
        [
            AecsActivity.TaskCreated,
            AecsActivity.PatchAccepted
        ]);
    }

    [Fact]
    public void EventNames_MatchFoundationVocabulary()
    {
        AecsActivity.TaskCreated.Should().Be("task.created");
        AecsActivity.TaskCompiled.Should().Be("task.compiled");
        AecsActivity.AgentStarted.Should().Be("agent.started");
        AecsActivity.AgentStopped.Should().Be("agent.stopped");
        AecsActivity.BudgetUpdated.Should().Be("budget.updated");
        AecsActivity.FileModified.Should().Be("file.modified");
        AecsActivity.VerificationStarted.Should().Be("verification.started");
        AecsActivity.VerificationCompleted.Should().Be("verification.completed");
        AecsActivity.PatchAccepted.Should().Be("patch.accepted");
        AecsActivity.PatchRejected.Should().Be("patch.rejected");
    }
}
