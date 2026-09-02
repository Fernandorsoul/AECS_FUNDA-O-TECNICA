using System.Security.Cryptography;
using System.Text;
using AECS.Application.EvidenceGraph;
using AECS.Application.Verification;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.ProactiveAlerts;

public sealed class ProactiveAlertService
{
    private readonly IExecutionEvidenceStore _evidenceStore;
    private readonly EvidenceGraphService _graphs;
    private readonly IProactiveAlertStore _alerts;
    private readonly IProactiveAlertSink _sink;
    private readonly string _repositoryPath;
    private readonly string _principal;
    private readonly Func<DateTime> _clock;

    public ProactiveAlertService(
        IExecutionEvidenceStore evidenceStore,
        IEvidenceGraphSource graphSource,
        IProactiveAlertStore alertStore,
        IProactiveAlertSink sink,
        string repositoryPath,
        string principal,
        Func<DateTime>? clock = null)
    {
        _evidenceStore = evidenceStore ?? throw new ArgumentNullException(nameof(evidenceStore));
        _graphs = new EvidenceGraphService(
            graphSource ?? throw new ArgumentNullException(nameof(graphSource)));
        _alerts = alertStore ?? throw new ArgumentNullException(nameof(alertStore));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        _repositoryPath = NormalizePath(repositoryPath);
        _principal = principal.Trim();
        _clock = clock ?? (() => DateTime.UtcNow);
        _evidenceStore.EnsureRepositoryIsolation(_repositoryPath);
        _alerts.EnsureRepositoryIsolation(_repositoryPath);
    }

    public async Task<ProactiveAlertEvaluationReport> EvaluateAsync(
        ProactiveAlertPolicy policy,
        CancellationToken cancellationToken)
    {
        ProactiveAlertPolicyValidator.Validate(policy);
        if (!policy.Enabled)
        {
            return new ProactiveAlertEvaluationReport
            {
                PolicyEnabled = false,
                Diagnostics = ["Alert policy is disabled; no evidence was evaluated and no sink was called."]
            };
        }

        var now = _clock();
        var existing = (await _alerts.ListAsync(_repositoryPath, cancellationToken)).ToList();
        var expired = await ExpireDueAsync(existing, now, cancellationToken);
        var graphResult = await _graphs.ListAsync(
            new EvidenceGraphQuery { Limit = 500 },
            Scope(),
            cancellationToken);
        var summaries = graphResult.Items
            .Where(item => item.CreatedAt >= now.AddHours(-policy.LookbackHours))
            .OrderBy(item => item.CreatedAt)
            .ToList();
        var diagnostics = graphResult.Diagnostics.ToList();
        var candidates = new List<AlertCandidate>();
        var evaluated = 0;
        foreach (var summary in summaries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var authenticated = await _graphs.ShowAsync(
                    summary.EvidenceId,
                    Scope(),
                    cancellationToken);
                if (authenticated is null)
                {
                    diagnostics.Add($"Evidence {summary.EvidenceId:N} disappeared before alert evaluation.");
                    continue;
                }
                var evidence = await _evidenceStore.LoadAsync(
                    summary.EvidenceId,
                    cancellationToken);
                if (evidence is null || !PathsEqual(
                        evidence.Baseline.RepositoryPath,
                        _repositoryPath))
                {
                    diagnostics.Add($"Evidence {summary.EvidenceId:N} failed repository-scoped loading.");
                    continue;
                }
                evaluated++;
                candidates.AddRange(DeriveCandidates(evidence, policy));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                diagnostics.Add(
                    $"Evidence {summary.EvidenceId:N} failed closed during alert derivation.");
            }
        }

        var created = 0;
        var delivered = 0;
        var deduplicated = 0;
        var escalated = 0;
        var failures = 0;
        var alertIds = new List<Guid>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = DeduplicationKey(policy.Id, candidate);
            var current = existing
                .Where(item => item.PolicyId == policy.Id &&
                    item.DeduplicationKey == key &&
                    item.Status is not (ProactiveAlertStatus.Actioned or ProactiveAlertStatus.Expired))
                .OrderByDescending(item => item.UpdatedAt)
                .FirstOrDefault();
            if (current is not null &&
                now - (current.LastDeliveryAttemptAt ?? current.CreatedAt) <
                    TimeSpan.FromMinutes(policy.CooldownMinutes))
            {
                MergeEvidence(current, candidate.EvidenceId);
                current.OccurrenceCount++;
                current.UpdatedAt = now;
                if (candidate.Severity > current.Severity)
                {
                    current.Severity = candidate.Severity;
                    current.Status = ProactiveAlertStatus.Pending;
                    current.DeadlineAt = Deadline(policy, candidate.Kind, now);
                    current.Lifecycle.Add(Event(
                        ProactiveAlertLifecycleKind.Escalated,
                        now,
                        _principal,
                        "Severity increased; a new delivery was requested."));
                    escalated++;
                    if (await DeliverAsync(current, policy, now, diagnostics, cancellationToken))
                        delivered++;
                    else
                        failures++;
                }
                else
                {
                    current.Lifecycle.Add(Event(
                        ProactiveAlertLifecycleKind.Deduplicated,
                        now,
                        _principal,
                        "Matching event was merged during policy cooldown."));
                    await _alerts.SaveAsync(current, cancellationToken);
                    deduplicated++;
                }
                alertIds.Add(current.Id);
                continue;
            }

            var alert = CreateAlert(candidate, policy, key, now);
            existing.Add(alert);
            await _alerts.SaveAsync(alert, cancellationToken);
            created++;
            alertIds.Add(alert.Id);
            if (await DeliverAsync(alert, policy, now, diagnostics, cancellationToken))
                delivered++;
            else
                failures++;
        }

        return new ProactiveAlertEvaluationReport
        {
            PolicyEnabled = true,
            EvidenceRecordsEvaluated = evaluated,
            CandidatesDetected = candidates.Count,
            AlertsCreated = created,
            AlertsDelivered = delivered,
            AlertsDeduplicated = deduplicated,
            AlertsEscalated = escalated,
            DeliveryFailures = failures,
            AlertsExpired = expired,
            AlertIds = alertIds.Distinct().ToList(),
            Diagnostics = diagnostics
        };
    }

    public async Task<IReadOnlyList<ProactiveAlert>> ListAsync(
        CancellationToken cancellationToken) =>
        (await _alerts.ListAsync(_repositoryPath, cancellationToken))
            .OrderByDescending(item => item.UpdatedAt)
            .ToList();

    public async Task<ProactiveAlert> MarkReadAsync(
        Guid alertId,
        string actor,
        CancellationToken cancellationToken)
    {
        ValidateActor(actor);
        var alert = await RequiredAlertAsync(alertId, cancellationToken);
        if (await ExpireIfDueAsync(alert, _clock(), cancellationToken))
            throw new InvalidOperationException("An expired alert cannot be marked as read.");
        if (alert.Status == ProactiveAlertStatus.Read)
            return alert;
        if (alert.Status != ProactiveAlertStatus.Delivered)
            throw new InvalidOperationException("Only a delivered alert can be marked as read.");
        var now = _clock();
        alert.Status = ProactiveAlertStatus.Read;
        alert.UpdatedAt = now;
        alert.Lifecycle.Add(Event(
            ProactiveAlertLifecycleKind.Read,
            now,
            actor.Trim(),
            "Alert was read; no approval was inferred."));
        await _alerts.SaveAsync(alert, cancellationToken);
        return alert;
    }

    public async Task<ProactiveAlert> MarkActionedAsync(
        Guid alertId,
        string actor,
        string actionReference,
        CancellationToken cancellationToken)
    {
        ValidateActor(actor);
        if (string.IsNullOrWhiteSpace(actionReference) || actionReference.Length > 256 ||
            actionReference.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Action reference must contain 1-256 printable characters.",
                nameof(actionReference));
        }
        var alert = await RequiredAlertAsync(alertId, cancellationToken);
        if (await ExpireIfDueAsync(alert, _clock(), cancellationToken))
            throw new InvalidOperationException("An expired alert cannot be actioned.");
        if (alert.Status == ProactiveAlertStatus.Actioned)
            return alert;
        if (alert.Status is not (ProactiveAlertStatus.Delivered or ProactiveAlertStatus.Read))
            throw new InvalidOperationException("Only a delivered or read alert can be actioned.");
        var now = _clock();
        alert.Status = ProactiveAlertStatus.Actioned;
        alert.UpdatedAt = now;
        alert.Lifecycle.Add(Event(
            ProactiveAlertLifecycleKind.Actioned,
            now,
            actor.Trim(),
            $"Action reference: {actionReference.Trim()}; no approval was granted."));
        await _alerts.SaveAsync(alert, cancellationToken);
        return alert;
    }

    public async Task<ProactiveAlertEffectivenessReport> MeasureAsync(
        ProactiveAlertPolicy policy,
        CancellationToken cancellationToken)
    {
        ProactiveAlertPolicyValidator.Validate(policy);
        var allAlerts = (await _alerts.ListAsync(_repositoryPath, cancellationToken)).ToList();
        await ExpireDueAsync(allAlerts, _clock(), cancellationToken);
        var alerts = allAlerts
            .Where(item => item.PolicyId == policy.Id)
            .ToList();
        var delivered = alerts.Count(item => item.Lifecycle.Any(entry =>
            entry.Kind == ProactiveAlertLifecycleKind.Delivered));
        var actioned = alerts.Count(item => item.Lifecycle.Any(entry =>
            entry.Kind == ProactiveAlertLifecycleKind.Actioned));
        var ignored = alerts.Count(item =>
            item.Status == ProactiveAlertStatus.Expired &&
            item.Lifecycle.All(entry => entry.Kind != ProactiveAlertLifecycleKind.Actioned));
        var actionRate = delivered == 0 ? 0 : (decimal)actioned / delivered;
        var ignoredRate = delivered == 0 ? 0 : (decimal)ignored / delivered;
        AlertDeathCriterionDecision decision;
        string reason;
        if (delivered < policy.DeathCriteria.MinimumDeliveredAlerts)
        {
            decision = AlertDeathCriterionDecision.InsufficientSample;
            reason = $"Need {policy.DeathCriteria.MinimumDeliveredAlerts} delivered alerts; found {delivered}.";
        }
        else if (actionRate < policy.DeathCriteria.MinimumActionRate ||
                 ignoredRate > policy.DeathCriteria.MaximumIgnoredRate)
        {
            decision = AlertDeathCriterionDecision.Disable;
            reason = "Action or ignored rate crossed the preregistered death criterion.";
        }
        else
        {
            decision = AlertDeathCriterionDecision.Continue;
            reason = "Observed alert effectiveness remains within the preregistered policy bounds.";
        }
        return new ProactiveAlertEffectivenessReport
        {
            PolicyId = policy.Id,
            DeliveredAlerts = delivered,
            ActionedAlerts = actioned,
            IgnoredAlerts = ignored,
            ActionRate = actionRate,
            IgnoredRate = ignoredRate,
            Decision = decision,
            Reason = reason
        };
    }

    private IEnumerable<AlertCandidate> DeriveCandidates(
        ExecutionEvidence evidence,
        ProactiveAlertPolicy policy)
    {
        var candidates = new List<AlertCandidate>();
        var candidateGates = evidence.VerificationResults;
        var security = candidateGates.FirstOrDefault(result =>
            result.Verifier == SecurityScanVerifier.VerifierName && IsFailure(result.Status));
        if (security is not null)
        {
            candidates.Add(Candidate(
                ProactiveAlertKind.Security,
                MapSecuritySeverity(security.Severity),
                "security-scan",
                "Security verification requires human attention",
                "Authenticated security verification did not pass; sensitive finding details were omitted.",
                "Inspect the authenticated security evidence and remediate or document the policy decision.",
                evidence));
        }

        var scope = candidateGates.FirstOrDefault(result =>
            result.Verifier == "Scope" && IsFailure(result.Status));
        if (scope is not null || evidence.FinalDecision.State == TaskState.ScopeViolation)
        {
            candidates.Add(Candidate(
                ProactiveAlertKind.Scope,
                ProactiveAlertSeverity.High,
                "scope",
                "Candidate exceeded its authorized scope",
                "Authenticated scope verification requires human attention; file details were omitted.",
                "Inspect the evidence and revise the TaskContract or candidate without widening policy implicitly.",
                evidence));
        }

        if (evidence.BudgetUsage.Exhausted ||
            evidence.FinalDecision.State == TaskState.BudgetExceeded ||
            evidence.AgentResult.FailureKind == AgentFailureKind.BudgetExceeded)
        {
            candidates.Add(Candidate(
                ProactiveAlertKind.Budget,
                ProactiveAlertSeverity.High,
                "budget",
                "Execution exhausted its declared budget",
                "The authenticated execution reached a token, cost, retry, or wall-clock limit.",
                "Inspect budget evidence before changing limits or retrying the task.",
                evidence));
        }

        var failedAttempts = evidence.AgentAttempts.Count(attempt => !attempt.Success);
        if (failedAttempts >= policy.RepeatedFailureThreshold)
        {
            candidates.Add(Candidate(
                ProactiveAlertKind.RepeatedFailure,
                failedAttempts >= policy.RepeatedFailureThreshold + 2
                    ? ProactiveAlertSeverity.High
                    : ProactiveAlertSeverity.Warning,
                "agent-attempts",
                "Execution accumulated repeated agent failures",
                $"Authenticated evidence records {failedAttempts} failed attempts; diagnostic content was omitted.",
                "Inspect attempt classifications and stop retrying until the recurring cause is understood.",
                evidence));
        }

        var baselineByVerifier = evidence.BaselineVerificationResults
            .GroupBy(result => result.Verifier, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        foreach (var result in candidateGates.Where(result => IsFailure(result.Status)))
        {
            if (!baselineByVerifier.TryGetValue(result.Verifier, out var baseline) ||
                baseline.Status != VerificationStatus.Pass)
            {
                continue;
            }
            candidates.Add(Candidate(
                ProactiveAlertKind.Regression,
                ProactiveAlertSeverity.High,
                "gate:" + result.Verifier,
                "Candidate introduced a verification regression",
                "A gate that passed on the authenticated baseline failed for the candidate; output was omitted.",
                "Inspect the baseline/candidate gate evidence and fix the regression before promotion.",
                evidence));
        }

        if (evidence.FinalDecision.Decision == TaskDecision.HumanReviewRequired ||
            evidence.FinalDecision.State == TaskState.HumanReviewRequired)
        {
            candidates.Add(Candidate(
                ProactiveAlertKind.HumanReview,
                ProactiveAlertSeverity.Warning,
                "human-review",
                "Candidate is waiting for explicit human review",
                "The authenticated decision requires a human review; no response will be treated as approval.",
                "Inspect diff and gates, then explicitly approve or reject through the AECS review service.",
                evidence));
        }

        return candidates
            .Where(candidate => policy.Events.Contains(candidate.Kind))
            .Select(candidate => policy.SeverityOverrides.TryGetValue(candidate.Kind, out var severity)
                ? candidate with { Severity = severity }
                : candidate);
    }

    private async Task<bool> DeliverAsync(
        ProactiveAlert alert,
        ProactiveAlertPolicy policy,
        DateTime now,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        var delivery = new ProactiveAlertDelivery
        {
            AlertId = alert.Id,
            PolicyId = alert.PolicyId,
            Recipient = alert.Recipient,
            Kind = alert.Kind,
            Severity = alert.Severity,
            Title = alert.Title,
            Summary = alert.Summary,
            RecommendedAction = alert.RecommendedAction,
            EvidenceUri = alert.EvidenceUri,
            DeadlineAt = alert.DeadlineAt,
            ContainsCode = false,
            ContainsSecrets = false
        };
        EnsureAuthorizedContent(delivery, policy.ChannelAuthorization, _sink.Capabilities);
        alert.Lifecycle.Add(Event(
            ProactiveAlertLifecycleKind.DeliveryAttempted,
            now,
            _principal,
            $"Delivery requested from sink '{_sink.Name}'."));
        alert.LastDeliveryAttemptAt = now;
        await _alerts.SaveAsync(alert, cancellationToken);
        try
        {
            var result = await _sink.DeliverAsync(
                delivery,
                policy.ChannelAuthorization,
                cancellationToken);
            var finishedAt = _clock();
            alert.UpdatedAt = finishedAt;
            if (result.Succeeded)
            {
                alert.Status = ProactiveAlertStatus.Delivered;
                alert.DeliveredAt = finishedAt;
                alert.Lifecycle.Add(Event(
                    ProactiveAlertLifecycleKind.Delivered,
                    finishedAt,
                    _sink.Name,
                    "Sink acknowledged local alert delivery."));
                await _alerts.SaveAsync(alert, cancellationToken);
                return true;
            }
            alert.Status = ProactiveAlertStatus.DeliveryFailed;
            alert.Lifecycle.Add(Event(
                ProactiveAlertLifecycleKind.DeliveryFailed,
                finishedAt,
                _sink.Name,
                "Sink rejected alert delivery; provider detail was omitted."));
            diagnostics.Add($"Sink '{_sink.Name}' rejected alert {alert.Id:N}.");
            await _alerts.SaveAsync(alert, cancellationToken);
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failedAt = _clock();
            alert.Status = ProactiveAlertStatus.DeliveryFailed;
            alert.UpdatedAt = failedAt;
            alert.Lifecycle.Add(Event(
                ProactiveAlertLifecycleKind.DeliveryFailed,
                failedAt,
                _sink.Name,
                "Sink threw an exception; diagnostic content was omitted."));
            diagnostics.Add(
                $"Sink '{_sink.Name}' failed for alert {alert.Id:N} ({ex.GetType().Name}).");
            await _alerts.SaveAsync(alert, CancellationToken.None);
            return false;
        }
    }

    private async Task<int> ExpireDueAsync(
        IReadOnlyList<ProactiveAlert> alerts,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var alert in alerts.Where(item =>
                     item.DeadlineAt <= now &&
                     item.Status is ProactiveAlertStatus.Delivered or ProactiveAlertStatus.Read))
        {
            alert.Status = ProactiveAlertStatus.Expired;
            alert.UpdatedAt = now;
            alert.Lifecycle.Add(Event(
                ProactiveAlertLifecycleKind.Expired,
                now,
                "aecs.alert-expiration",
                "Deadline elapsed without a recorded action; no approval was inferred."));
            await _alerts.SaveAsync(alert, cancellationToken);
            count++;
        }
        return count;
    }

    private async Task<bool> ExpireIfDueAsync(
        ProactiveAlert alert,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (alert.DeadlineAt > now ||
            alert.Status is not (ProactiveAlertStatus.Delivered or ProactiveAlertStatus.Read))
        {
            return alert.Status == ProactiveAlertStatus.Expired;
        }
        alert.Status = ProactiveAlertStatus.Expired;
        alert.UpdatedAt = now;
        alert.Lifecycle.Add(Event(
            ProactiveAlertLifecycleKind.Expired,
            now,
            "aecs.alert-expiration",
            "Deadline elapsed without a recorded action; no approval was inferred."));
        await _alerts.SaveAsync(alert, cancellationToken);
        return true;
    }

    private async Task<ProactiveAlert> RequiredAlertAsync(
        Guid alertId,
        CancellationToken cancellationToken)
    {
        if (alertId == Guid.Empty)
            throw new ArgumentException("Alert id must be non-empty.", nameof(alertId));
        return await _alerts.LoadAsync(_repositoryPath, alertId, cancellationToken) ??
            throw new KeyNotFoundException($"Alert '{alertId:N}' was not found for this repository.");
    }

    private ProactiveAlert CreateAlert(
        AlertCandidate candidate,
        ProactiveAlertPolicy policy,
        string key,
        DateTime now)
    {
        var alert = new ProactiveAlert
        {
            DeduplicationKey = key,
            PolicyId = policy.Id,
            RepositoryPath = _repositoryPath,
            Recipient = policy.Recipient.Trim(),
            Kind = candidate.Kind,
            Severity = candidate.Severity,
            Status = ProactiveAlertStatus.Pending,
            TaskId = candidate.TaskId,
            RunId = candidate.RunId,
            LatestEvidenceId = candidate.EvidenceId,
            EvidenceIds = [candidate.EvidenceId],
            EvidenceUri = EvidenceUri(candidate.EvidenceId),
            Title = candidate.Title,
            Summary = candidate.Summary,
            RecommendedAction = candidate.RecommendedAction,
            DeadlineAt = Deadline(policy, candidate.Kind, now),
            CreatedAt = now,
            UpdatedAt = now
        };
        alert.Lifecycle.Add(Event(
            ProactiveAlertLifecycleKind.Created,
            now,
            _principal,
            "Alert was derived from repository-scoped authenticated evidence."));
        return alert;
    }

    private static AlertCandidate Candidate(
        ProactiveAlertKind kind,
        ProactiveAlertSeverity severity,
        string subject,
        string title,
        string summary,
        string action,
        ExecutionEvidence evidence) => new(
            kind,
            severity,
            subject,
            title,
            summary,
            action,
            evidence.TaskContract.Id,
            evidence.AgentRun.Id,
            evidence.Id);

    private static void MergeEvidence(ProactiveAlert alert, Guid evidenceId)
    {
        alert.LatestEvidenceId = evidenceId;
        alert.EvidenceUri = EvidenceUri(evidenceId);
        if (!alert.EvidenceIds.Contains(evidenceId))
            alert.EvidenceIds.Add(evidenceId);
    }

    private static string DeduplicationKey(string policyId, AlertCandidate candidate)
    {
        var value = string.Join(
            '\n',
            policyId,
            candidate.TaskId,
            candidate.Kind,
            candidate.Subject);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }

    private static DateTime Deadline(
        ProactiveAlertPolicy policy,
        ProactiveAlertKind kind,
        DateTime now) => now.AddMinutes(
            policy.DeadlineMinutes.TryGetValue(kind, out var minutes)
                ? minutes
                : policy.DefaultDeadlineMinutes);

    private static ProactiveAlertLifecycleEvent Event(
        ProactiveAlertLifecycleKind kind,
        DateTime occurredAt,
        string actor,
        string detail) => new()
        {
            Kind = kind,
            OccurredAt = occurredAt,
            Actor = actor,
            Detail = detail
        };

    private static void EnsureAuthorizedContent(
        ProactiveAlertDelivery delivery,
        AlertChannelAuthorization authorization,
        ProactiveAlertSinkCapabilities capabilities)
    {
        if (delivery.ContainsCode && (!authorization.AllowCode || !capabilities.AcceptsCode))
            throw new InvalidOperationException("Alert channel is not authorized to receive code content.");
        if (delivery.ContainsSecrets && (!authorization.AllowSecrets || !capabilities.AcceptsSecrets))
            throw new InvalidOperationException("Alert channel is not authorized to receive secret content.");
    }

    private EvidenceReadScope Scope() => new()
    {
        RepositoryPath = _repositoryPath,
        Principal = _principal
    };

    private static bool IsFailure(VerificationStatus status) =>
        status is VerificationStatus.Fail or VerificationStatus.Error;

    private static ProactiveAlertSeverity MapSecuritySeverity(Severity severity) => severity switch
    {
        Severity.Critical => ProactiveAlertSeverity.Critical,
        Severity.Error => ProactiveAlertSeverity.High,
        Severity.Warning => ProactiveAlertSeverity.Warning,
        _ => ProactiveAlertSeverity.Warning
    };

    private static void ValidateActor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 256 || actor.Any(char.IsControl))
            throw new ArgumentException("Actor must contain 1-256 printable characters.", nameof(actor));
    }

    private static string EvidenceUri(Guid evidenceId) =>
        $"aecs://evidence/{evidenceId:N}";

    private static string NormalizePath(string path) => Path.GetFullPath(path)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            NormalizePath(left),
            NormalizePath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record AlertCandidate(
        ProactiveAlertKind Kind,
        ProactiveAlertSeverity Severity,
        string Subject,
        string Title,
        string Summary,
        string RecommendedAction,
        string TaskId,
        Guid RunId,
        Guid EvidenceId);
}
