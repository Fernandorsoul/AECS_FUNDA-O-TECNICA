using AECS.Application.ProactiveAlerts;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.Repositories;
using FluentAssertions;
using System.Text.Json;

namespace AECS.UnitTests;

public sealed class ProactiveAlertServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "aecs-proactive-alert-tests",
        Guid.NewGuid().ToString("N"));
    private readonly DateTime _initialNow = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    public ProactiveAlertServiceTests() => Directory.CreateDirectory(RepositoryPath);

    private string RepositoryPath => Path.Combine(_root, "repository");

    [Fact]
    public async Task Evaluate_DerivesAllRequiredEventsWithoutSensitiveContent()
    {
        var fixture = Fixture();
        fixture.Evidence.Add(AllEventsEvidence("TASK-ALL", _initialNow));

        var report = await fixture.Service.EvaluateAsync(
            Policy(),
            CancellationToken.None);

        report.AlertsCreated.Should().Be(6);
        report.AlertsDelivered.Should().Be(6);
        fixture.Sink.Deliveries.Select(item => item.Kind).Should().BeEquivalentTo(
            Enum.GetValues<ProactiveAlertKind>());
        fixture.Sink.Deliveries.Should().OnlyContain(item =>
            !item.ContainsCode && !item.ContainsSecrets &&
            item.EvidenceUri.StartsWith("aecs://evidence/", StringComparison.Ordinal) &&
            !item.Summary.Contains("secret-value", StringComparison.Ordinal));
        fixture.Alerts.Items.Should().OnlyContain(item =>
            item.Lifecycle.All(entry => !entry.GrantsApproval));
    }

    [Fact]
    public async Task Evaluate_RepeatedEventWithinCooldownIsDeduplicated()
    {
        var fixture = Fixture();
        fixture.Evidence.Add(HumanReviewEvidence("TASK-REPEAT", _initialNow));
        var policy = Policy(ProactiveAlertKind.HumanReview);

        await fixture.Service.EvaluateAsync(policy, CancellationToken.None);
        var second = await fixture.Service.EvaluateAsync(policy, CancellationToken.None);

        second.AlertsCreated.Should().Be(0);
        second.AlertsDeduplicated.Should().Be(1);
        fixture.Sink.Deliveries.Should().ContainSingle();
        fixture.Alerts.Items.Should().ContainSingle().Which.OccurrenceCount.Should().Be(2);
    }

    [Fact]
    public async Task Evaluate_HigherSeverityBypassesCooldownAndRedelivers()
    {
        var fixture = Fixture();
        var policy = Policy(ProactiveAlertKind.RepeatedFailure);
        fixture.Evidence.Add(RepeatedFailureEvidence("TASK-ESCALATE", _initialNow, failures: 2));
        await fixture.Service.EvaluateAsync(policy, CancellationToken.None);
        fixture.Clock.Now = _initialNow.AddMinutes(1);
        fixture.Evidence.Add(RepeatedFailureEvidence(
            "TASK-ESCALATE",
            fixture.Clock.Now,
            failures: 4));

        var report = await fixture.Service.EvaluateAsync(policy, CancellationToken.None);

        report.AlertsEscalated.Should().Be(1);
        fixture.Sink.Deliveries.Should().HaveCount(2);
        var alert = fixture.Alerts.Items.Should().ContainSingle().Which;
        alert.Severity.Should().Be(ProactiveAlertSeverity.High);
        alert.EvidenceIds.Should().HaveCount(2);
        alert.Lifecycle.Should().Contain(entry =>
            entry.Kind == ProactiveAlertLifecycleKind.Escalated);
    }

    [Fact]
    public async Task Evaluate_SinkFailureIsRecordedAndDoesNotFailOpen()
    {
        var fixture = Fixture(sinkSucceeds: false);
        fixture.Evidence.Add(HumanReviewEvidence("TASK-SINK", _initialNow));

        var report = await fixture.Service.EvaluateAsync(
            Policy(ProactiveAlertKind.HumanReview),
            CancellationToken.None);

        report.DeliveryFailures.Should().Be(1);
        report.AlertsDelivered.Should().Be(0);
        fixture.Alerts.Items.Should().ContainSingle()
            .Which.Status.Should().Be(ProactiveAlertStatus.DeliveryFailed);
        fixture.Alerts.Items[0].Lifecycle.Should().Contain(entry =>
            entry.Kind == ProactiveAlertLifecycleKind.DeliveryFailed);

        fixture.Clock.Now = _initialNow.AddMinutes(61);
        fixture.Sink.Succeeds = true;
        var retry = await fixture.Service.EvaluateAsync(
            Policy(ProactiveAlertKind.HumanReview),
            CancellationToken.None);
        retry.AlertsDelivered.Should().Be(1);
        fixture.Alerts.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Evaluate_DisabledPolicyDoesNotReadEvidenceOrCallSink()
    {
        var fixture = Fixture();
        fixture.Evidence.Add(HumanReviewEvidence("TASK-DISABLED", _initialNow));
        var disabled = Policy(ProactiveAlertKind.HumanReview, enabled: false);

        var report = await fixture.Service.EvaluateAsync(disabled, CancellationToken.None);

        report.PolicyEnabled.Should().BeFalse();
        fixture.Evidence.QueryCount.Should().Be(0);
        fixture.Sink.Deliveries.Should().BeEmpty();
        fixture.Alerts.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Lifecycle_ReadActionAndExpirationNeverGrantApprovalAndFeedDeathCriterion()
    {
        var actioned = Fixture();
        var policy = Policy(
            ProactiveAlertKind.HumanReview,
            deadlineMinutes: 10,
            deathCriteria: new AlertDeathCriteria
            {
                MinimumDeliveredAlerts = 1,
                MinimumActionRate = 0.5m,
                MaximumIgnoredRate = 0.5m
            });
        actioned.Evidence.Add(HumanReviewEvidence("TASK-ACTION", _initialNow));
        await actioned.Service.EvaluateAsync(policy, CancellationToken.None);
        var alertId = actioned.Alerts.Items.Single().Id;
        await actioned.Service.MarkReadAsync(alertId, "reviewer", CancellationToken.None);
        var alert = await actioned.Service.MarkActionedAsync(
            alertId,
            "reviewer",
            "ticket/AECS-123",
            CancellationToken.None);
        var successful = await actioned.Service.MeasureAsync(policy, CancellationToken.None);

        alert.Status.Should().Be(ProactiveAlertStatus.Actioned);
        alert.Lifecycle.Should().OnlyContain(entry => !entry.GrantsApproval);
        successful.Decision.Should().Be(AlertDeathCriterionDecision.Continue);
        successful.ActionRate.Should().Be(1);

        var ignored = Fixture();
        ignored.Evidence.Add(HumanReviewEvidence("TASK-IGNORED", _initialNow));
        await ignored.Service.EvaluateAsync(policy, CancellationToken.None);
        ignored.Evidence.Clear();
        ignored.Clock.Now = _initialNow.AddMinutes(11);
        var expiration = await ignored.Service.EvaluateAsync(policy, CancellationToken.None);
        var ineffective = await ignored.Service.MeasureAsync(policy, CancellationToken.None);

        expiration.AlertsExpired.Should().Be(1);
        ignored.Alerts.Items.Single().Status.Should().Be(ProactiveAlertStatus.Expired);
        ineffective.IgnoredAlerts.Should().Be(1);
        ineffective.Decision.Should().Be(AlertDeathCriterionDecision.Disable);
    }

    [Fact]
    public async Task LocalJsonSinkPersistsSafeEnvelopeAndRefusesCodeEvenWhenPolicyAllowsIt()
    {
        var sinkRoot = Path.Combine(_root, "local-alerts");
        var sink = new LocalJsonProactiveAlertSink(sinkRoot, RepositoryPath);
        var unsafeDelivery = Delivery(containsCode: true);

        var refused = await sink.DeliverAsync(
            unsafeDelivery,
            new AlertChannelAuthorization { AllowCode = true },
            CancellationToken.None);
        var accepted = await sink.DeliverAsync(
            Delivery(),
            new AlertChannelAuthorization(),
            CancellationToken.None);

        refused.Succeeded.Should().BeFalse();
        accepted.Succeeded.Should().BeTrue();
        Directory.GetFiles(Path.Combine(sinkRoot, "inbox"), "*.json")
            .Should().ContainSingle();
    }

    [Theory]
    [InlineData("\"unknown\":true", typeof(JsonException))]
    [InlineData("\"enabled\":true", typeof(InvalidDataException))]
    public void PolicyJson_RejectsUnknownOrDuplicateProperties(
        string extraProperty,
        Type exceptionType)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, $$"""
        {
          "schemaVersion": "aecs.alert-policy/v1",
          "id": "policy/parser-v1",
          "enabled": false,
          "recipient": "local",
          {{extraProperty}}
        }
        """);

        var act = () => ProactiveAlertPolicyJson.Load(path);

        act.Should().Throw<Exception>().Which.GetType().Should().Be(exceptionType);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private TestFixture Fixture(bool sinkSucceeds = true)
    {
        var evidence = new InMemoryEvidenceSource(RepositoryPath);
        var alerts = new InMemoryAlertStore();
        var sink = new RecordingSink(sinkSucceeds);
        var clock = new MutableClock { Now = _initialNow };
        return new TestFixture(
            evidence,
            alerts,
            sink,
            clock,
            new ProactiveAlertService(
                evidence,
                evidence,
                alerts,
                sink,
                RepositoryPath,
                "test-principal",
                () => clock.Now));
    }

    private static ProactiveAlertPolicy Policy(
        ProactiveAlertKind? only = null,
        bool enabled = true,
        int deadlineMinutes = 60,
        AlertDeathCriteria? deathCriteria = null) => new()
        {
            Id = "policy/test-v1",
            Enabled = enabled,
            Recipient = "local-operator",
            CooldownMinutes = 60,
            LookbackHours = 24,
            RepeatedFailureThreshold = 2,
            DefaultDeadlineMinutes = deadlineMinutes,
            DeathCriteria = deathCriteria ?? new AlertDeathCriteria(),
            Events = only.HasValue ? [only.Value] : Enum.GetValues<ProactiveAlertKind>().ToList()
        };

    private ExecutionEvidence AllEventsEvidence(string taskId, DateTime createdAt)
    {
        var evidence = HumanReviewEvidence(taskId, createdAt);
        return Copy(evidence,
            budget: new ExecutionBudgetEvidence { Exhausted = true },
            attempts:
            [
                Attempt(1),
                Attempt(2)
            ],
            baseline:
            [
                Gate("Build", VerificationStatus.Pass, Severity.Info)
            ],
            candidate:
            [
                Gate("SecurityScan", VerificationStatus.Fail, Severity.Critical, "secret-value"),
                Gate("Scope", VerificationStatus.Fail, Severity.Error),
                Gate("Build", VerificationStatus.Fail, Severity.Error)
            ],
            agentResult: new AgentRunResult { FailureKind = AgentFailureKind.BudgetExceeded });
    }

    private ExecutionEvidence HumanReviewEvidence(string taskId, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(),
        TaskContract = new TaskContract { Id = taskId, Objective = "Test objective" },
        AgentRun = new AgentRun { Id = Guid.NewGuid(), TaskId = taskId, Model = "mock" },
        AgentResult = new AgentRunResult(),
        Baseline = new BaselineSnapshot
        {
            Commit = "abc123",
            Branch = "main",
            RepositoryPath = RepositoryPath
        },
        CandidateChangeSet = new CandidateChangeSet
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            DiffHash = "sha256:test"
        },
        FinalDecision = new FinalDecisionRecord
        {
            Decision = TaskDecision.HumanReviewRequired,
            State = TaskState.HumanReviewRequired,
            Reason = "review"
        },
        CreatedAt = createdAt
    };

    private ExecutionEvidence RepeatedFailureEvidence(
        string taskId,
        DateTime createdAt,
        int failures)
    {
        var evidence = HumanReviewEvidence(taskId, createdAt);
        return Copy(
            evidence,
            attempts: Enumerable.Range(1, failures).Select(Attempt).ToList(),
            finalDecision: new FinalDecisionRecord
            {
                Decision = TaskDecision.Rejected,
                State = TaskState.AgentFailed,
                Reason = "failed"
            });
    }

    private static ExecutionEvidence Copy(
        ExecutionEvidence source,
        ExecutionBudgetEvidence? budget = null,
        List<AgentAttemptEvidence>? attempts = null,
        List<VerificationResult>? baseline = null,
        List<VerificationResult>? candidate = null,
        AgentRunResult? agentResult = null,
        FinalDecisionRecord? finalDecision = null) => new()
        {
            Id = source.Id,
            TaskContract = source.TaskContract,
            AgentRun = source.AgentRun,
            AgentResult = agentResult ?? source.AgentResult,
            AgentAttempts = attempts ?? source.AgentAttempts,
            BudgetUsage = budget ?? source.BudgetUsage,
            Baseline = source.Baseline,
            BaselineVerificationResults = baseline ?? source.BaselineVerificationResults,
            CandidateChangeSet = source.CandidateChangeSet,
            VerificationResults = candidate ?? source.VerificationResults,
            FinalDecision = finalDecision ?? source.FinalDecision,
            CreatedAt = source.CreatedAt
        };

    private static AgentAttemptEvidence Attempt(int number) => new()
    {
        AttemptNumber = number,
        Success = false,
        FailureKind = AgentFailureKind.Transient
    };

    private static VerificationResult Gate(
        string verifier,
        VerificationStatus status,
        Severity severity,
        string message = "sanitized") => new()
        {
            Verifier = verifier,
            Status = status,
            Severity = severity,
            Message = message
        };

    private static ProactiveAlertDelivery Delivery(bool containsCode = false) => new()
    {
        AlertId = Guid.NewGuid(),
        PolicyId = "policy/test-v1",
        Recipient = "local",
        Kind = ProactiveAlertKind.HumanReview,
        Severity = ProactiveAlertSeverity.Warning,
        Title = "Review",
        Summary = "Review required",
        RecommendedAction = "Inspect evidence",
        EvidenceUri = "aecs://evidence/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        DeadlineAt = DateTime.UtcNow.AddMinutes(10),
        ContainsCode = containsCode
    };

    private sealed record TestFixture(
        InMemoryEvidenceSource Evidence,
        InMemoryAlertStore Alerts,
        RecordingSink Sink,
        MutableClock Clock,
        ProactiveAlertService Service);

    private sealed class MutableClock
    {
        public DateTime Now { get; set; }
    }

    private sealed class RecordingSink : IProactiveAlertSink
    {
        public RecordingSink(bool succeeds) => Succeeds = succeeds;

        public bool Succeeds { get; set; }
        public string Name => "recording";
        public ProactiveAlertSinkCapabilities Capabilities { get; } = new();
        public List<ProactiveAlertDelivery> Deliveries { get; } = [];

        public Task<ProactiveAlertDeliveryResult> DeliverAsync(
            ProactiveAlertDelivery delivery,
            AlertChannelAuthorization authorization,
            CancellationToken cancellationToken)
        {
            Deliveries.Add(delivery);
            return Task.FromResult(new ProactiveAlertDeliveryResult
            {
                Succeeded = Succeeds,
                FinishedAt = new DateTime(2026, 9, 1, 12, 0, 1, DateTimeKind.Utc)
            });
        }
    }

    private sealed class InMemoryAlertStore : IProactiveAlertStore
    {
        public List<ProactiveAlert> Items { get; } = [];
        public void EnsureRepositoryIsolation(string repositoryPath) { }

        public Task<IReadOnlyList<ProactiveAlert>> ListAsync(
            string repositoryPath,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProactiveAlert>>(Items.ToList());

        public Task<ProactiveAlert?> LoadAsync(
            string repositoryPath,
            Guid alertId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Items.SingleOrDefault(item => item.Id == alertId));

        public Task SaveAsync(ProactiveAlert alert, CancellationToken cancellationToken)
        {
            var index = Items.FindIndex(item => item.Id == alert.Id);
            if (index < 0)
                Items.Add(alert);
            else
                Items[index] = alert;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryEvidenceSource(string repositoryPath) :
        IExecutionEvidenceStore,
        IEvidenceGraphSource
    {
        private readonly Dictionary<Guid, ExecutionEvidence> _items = [];
        public int QueryCount { get; private set; }

        public void Add(ExecutionEvidence evidence) => _items[evidence.Id] = evidence;
        public void Clear() => _items.Clear();
        public void EnsureRepositoryIsolation(string repository) { }

        public Task<string> SaveAsync(
            ExecutionEvidence evidence,
            CancellationToken cancellationToken)
        {
            Add(evidence);
            return Task.FromResult(evidence.Id.ToString("N"));
        }

        public Task<ExecutionEvidence?> LoadAsync(
            Guid evidenceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_items.GetValueOrDefault(evidenceId));

        public Task AppendPromotionAsync(
            Guid evidenceId,
            CandidatePromotionEvidence promotion,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AppendReplayAsync(
            Guid evidenceId,
            ExecutionReplayEvidence replay,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<EvidenceGraphQueryResult> QueryEvidenceGraphsAsync(
            EvidenceGraphQuery query,
            EvidenceReadScope scope,
            CancellationToken cancellationToken)
        {
            QueryCount++;
            return Task.FromResult(new EvidenceGraphQueryResult
            {
                Items = _items.Values
                    .OrderByDescending(item => item.CreatedAt)
                    .Select(Summary)
                    .ToList()
            });
        }

        public Task<EvidenceGraph?> LoadEvidenceGraphAsync(
            Guid evidenceId,
            EvidenceReadScope scope,
            CancellationToken cancellationToken)
        {
            var evidence = _items.GetValueOrDefault(evidenceId);
            return Task.FromResult(evidence is null ? null : new EvidenceGraph
            {
                EvidenceId = evidence.Id,
                RepositoryPath = repositoryPath,
                Principal = scope.Principal,
                Summary = Summary(evidence)
            });
        }

        private static EvidenceGraphSummary Summary(ExecutionEvidence evidence) => new()
        {
            EvidenceId = evidence.Id,
            TaskId = evidence.TaskContract.Id,
            RunId = evidence.AgentRun.Id,
            CandidateId = evidence.CandidateChangeSet.Id,
            BaselineCommit = evidence.Baseline.Commit,
            Decision = evidence.FinalDecision.Decision,
            DiffHash = evidence.CandidateChangeSet.DiffHash,
            CreatedAt = evidence.CreatedAt,
            UpdatedAt = evidence.CreatedAt
        };
    }
}
