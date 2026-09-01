using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AECS.Domain.Enums;
using AECS.Domain.Exceptions;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Promotion;

public sealed class CandidatePromotionService
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepositoryGates = new(
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

    private readonly IProcessRunner _processRunner;
    private readonly IExecutionEvidenceStore _evidenceStore;

    public CandidatePromotionService(
        IProcessRunner processRunner,
        IExecutionEvidenceStore evidenceStore)
    {
        _processRunner = processRunner;
        _evidenceStore = evidenceStore;
    }

    public async Task<CandidateReviewSnapshot> InspectAsync(
        Guid evidenceId,
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var unavailable = new CandidateReviewSnapshot
        {
            EvidenceId = evidenceId,
            RepositoryPath = repositoryPath
        };
        try
        {
            if (string.IsNullOrWhiteSpace(repositoryPath))
                return UnavailableReview(unavailable, "An explicit repository path is required");
            _evidenceStore.EnsureRepositoryIsolation(repositoryPath);
            var evidence = await LoadEvidenceAsync(evidenceId, cancellationToken);
            if (evidence is null)
                return UnavailableReview(unavailable, "Execution evidence was not found");

            var repository = await ResolveRepositoryAsync(repositoryPath, cancellationToken);
            if (!SamePath(repository.RootPath, evidence.Baseline.RepositoryPath))
            {
                return UnavailableReview(
                    unavailable,
                    "Requested repository does not match the repository captured in evidence");
            }

            var integrityFailure = ValidateCandidateIntegrity(
                evidence,
                evidence.CandidateChangeSet.DiffHash);
            if (integrityFailure is not null)
                return UnavailableReview(unavailable, integrityFailure);

            var repositoryFailure = await ValidateBaselineAsync(
                repository.RootPath,
                evidence.Baseline,
                cancellationToken);
            var reviewable = IsReviewable(evidence);
            return new CandidateReviewSnapshot
            {
                Available = true,
                Message = reviewable
                    ? "Authenticated candidate is available for human review"
                    : "Candidate decision is not eligible for human approval",
                EvidenceId = evidence.Id,
                TaskId = evidence.TaskContract.Id,
                CandidateId = evidence.CandidateChangeSet.Id,
                RepositoryPath = repository.RootPath,
                BaselineCommit = evidence.Baseline.Commit,
                BaselineBranch = evidence.Baseline.Branch,
                Diff = evidence.CandidateChangeSet.Diff,
                DiffHash = evidence.CandidateChangeSet.DiffHash,
                ChangedFiles = [.. evidence.CandidateChangeSet.ChangedFiles],
                Risk = evidence.TaskContract.Constraints.SecurityRisk,
                Decision = evidence.FinalDecision.Decision,
                State = evidence.FinalDecision.State,
                Eligibility = GetEligibility(evidence, null),
                Reviewable = reviewable,
                RepositoryReady = repositoryFailure is null,
                RepositoryState = repositoryFailure ?? "Baseline and working tree match evidence",
                Gates = evidence.BaselineVerificationResults
                    .Select(result => ReviewGate("baseline", result))
                    .Concat(evidence.VerificationResults.Select(result =>
                        ReviewGate("candidate", result)))
                    .ToList()
            };
        }
        catch (Exception ex) when (IsEvidenceValidationFailure(ex) ||
            ex is ArgumentException or NotSupportedException)
        {
            return UnavailableReview(
                unavailable,
                $"Execution evidence failed integrity or isolation validation: {ex.Message}");
        }
    }

    public async Task<CandidateReviewResult> ReviewAsync(
        CandidateReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = DateTime.UtcNow;
        ExecutionEvidence? evidence;
        try
        {
            if (!string.IsNullOrWhiteSpace(request.RepositoryPath))
                _evidenceStore.EnsureRepositoryIsolation(request.RepositoryPath);
            evidence = await LoadEvidenceAsync(request.EvidenceId, cancellationToken);
        }
        catch (Exception ex) when (IsEvidenceValidationFailure(ex))
        {
            return UnpersistedReview(
                request,
                CandidatePromotionStatus.Rejected,
                $"Execution evidence failed integrity or isolation validation: {ex.Message}",
                now);
        }

        if (evidence is null)
        {
            return UnpersistedReview(
                request,
                CandidatePromotionStatus.Rejected,
                "Execution evidence was not found",
                now);
        }

        var failure = ValidateReviewRequest(request, evidence, now);
        string resolvedRepository = request.RepositoryPath;
        try
        {
            var repository = await ResolveRepositoryAsync(
                request.RepositoryPath,
                cancellationToken);
            resolvedRepository = repository.RootPath;
            if (!SamePath(repository.RootPath, evidence.Baseline.RepositoryPath))
            {
                failure ??=
                    "Requested repository does not match the repository captured in evidence";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or
            InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            failure ??= $"Repository validation failed: {ex.Message}";
        }

        if (failure is not null)
            return UnpersistedReview(request, CandidatePromotionStatus.Rejected, failure, now);

        var status = request.Decision switch
        {
            CandidateReviewDecision.Approve => CandidatePromotionStatus.Approved,
            CandidateReviewDecision.Reject => CandidatePromotionStatus.Declined,
            CandidateReviewDecision.Abandon => CandidatePromotionStatus.Abandoned,
            _ => CandidatePromotionStatus.Rejected
        };
        var record = CreateReviewRecord(
            evidence,
            request,
            resolvedRepository,
            status,
            request.Decision switch
            {
                CandidateReviewDecision.Approve => "Candidate was approved for controlled promotion",
                CandidateReviewDecision.Reject => "Candidate was declined by the human reviewer",
                CandidateReviewDecision.Abandon => "Human review or promotion confirmation was abandoned",
                _ => "Human review decision was rejected"
            },
            now);
        try
        {
            await _evidenceStore.AppendPromotionAsync(
                evidence.Id,
                record,
                CancellationToken.None);
            return new CandidateReviewResult
            {
                Status = record.Status,
                Message = record.Message,
                Evidence = record,
                Persisted = true
            };
        }
        catch (Exception ex)
        {
            return new CandidateReviewResult
            {
                Status = CandidatePromotionStatus.Failed,
                Message = $"Human review could not be persisted: {ex.Message}",
                Evidence = record,
                Persisted = false
            };
        }
    }

    public async Task<CandidatePromotionResult> PromoteAsync(
        CandidatePromotionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startedAt = DateTime.UtcNow;
        ExecutionEvidence? evidence;
        try
        {
            if (!string.IsNullOrWhiteSpace(request.RepositoryPath))
                _evidenceStore.EnsureRepositoryIsolation(request.RepositoryPath);
            evidence = await LoadEvidenceAsync(request.EvidenceId, cancellationToken);
        }
        catch (Exception ex) when (IsEvidenceValidationFailure(ex))
        {
            return Unpersisted(
                request.EvidenceId,
                CandidatePromotionAction.Promote,
                CandidatePromotionStatus.Rejected,
                request.Actor,
                request.Approval,
                request.RepositoryPath,
                string.Empty,
                $"Execution evidence failed integrity or isolation validation: {ex.Message}",
                startedAt);
        }

        if (evidence is null)
        {
            return Unpersisted(
                request.EvidenceId,
                CandidatePromotionAction.Promote,
                CandidatePromotionStatus.Rejected,
                request.Actor,
                request.Approval,
                request.RepositoryPath,
                string.Empty,
                "Execution evidence was not found",
                startedAt);
        }

        var validationFailure = ValidatePromotionRequest(request, evidence, out var eligibility);
        if (validationFailure is not null)
        {
            return await RecordAsync(
                evidence,
                CreateRecord(
                    evidence,
                    CandidatePromotionAction.Promote,
                    CandidatePromotionStatus.Rejected,
                    eligibility,
                    request.Actor,
                    request.Approval,
                    request.RepositoryPath,
                    string.Empty,
                    validationFailure,
                    startedAt));
        }

        string requestedPath;
        try
        {
            requestedPath = Path.GetFullPath(request.RepositoryPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return await RecordAsync(
                evidence,
                CreateRecord(
                    evidence,
                    CandidatePromotionAction.Promote,
                    CandidatePromotionStatus.Rejected,
                    eligibility,
                    request.Actor,
                    request.Approval,
                    request.RepositoryPath,
                    string.Empty,
                    $"Repository path is invalid: {ex.Message}",
                    startedAt));
        }

        var gate = RepositoryGates.GetOrAdd(requestedPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        var patchPath = string.Empty;
        var patchApplied = false;
        try
        {
            var repository = await ResolveRepositoryAsync(requestedPath, cancellationToken);
            if (!SamePath(repository.RootPath, evidence.Baseline.RepositoryPath))
            {
                return await RecordAsync(
                    evidence,
                    CreateRecord(
                        evidence,
                        CandidatePromotionAction.Promote,
                        CandidatePromotionStatus.Rejected,
                        eligibility,
                        request.Actor,
                        request.Approval,
                        repository.RootPath,
                        string.Empty,
                        "Requested repository does not match the repository captured in evidence",
                        startedAt));
            }

            FileStream repositoryLock;
            try
            {
                repositoryLock = new FileStream(
                    Path.Combine(repository.GitCommonDirectory, "aecs-promotion.lock"),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException)
            {
                return await RecordAsync(
                    evidence,
                    CreateRecord(
                        evidence,
                        CandidatePromotionAction.Promote,
                        CandidatePromotionStatus.Rejected,
                        eligibility,
                        request.Actor,
                        request.Approval,
                        repository.RootPath,
                        string.Empty,
                        "Another promotion is already in progress for this repository",
                        startedAt));
            }

            await using (repositoryLock)
            {
                var baselineFailure = await ValidateBaselineAsync(
                    repository.RootPath,
                    evidence.Baseline,
                    cancellationToken);
                if (baselineFailure is not null)
                {
                    return await RecordAsync(
                        evidence,
                        CreateRecord(
                            evidence,
                            CandidatePromotionAction.Promote,
                            CandidatePromotionStatus.Rejected,
                            eligibility,
                            request.Actor,
                            request.Approval,
                            repository.RootPath,
                            string.Empty,
                            baselineFailure,
                            startedAt));
                }

                var unsafePath = evidence.CandidateChangeSet.ChangedFiles.FirstOrDefault(path =>
                    !FileApplicator.TryResolveSafePath(
                        repository.RootPath,
                        path,
                        out _,
                        out _));
                if (unsafePath is not null)
                {
                    return await RecordAsync(
                        evidence,
                        CreateRecord(
                            evidence,
                            CandidatePromotionAction.Promote,
                            CandidatePromotionStatus.Rejected,
                            eligibility,
                            request.Actor,
                            request.Approval,
                            repository.RootPath,
                            string.Empty,
                            $"Candidate contains an unsafe path: '{unsafePath}'",
                            startedAt));
                }

                patchPath = Path.Combine(
                    Path.GetTempPath(),
                    $"aecs-promotion-{Guid.NewGuid():N}.patch");
                await File.WriteAllTextAsync(
                    patchPath,
                    evidence.CandidateChangeSet.Diff,
                    cancellationToken);

                var check = await RunGitAsync(
                    repository.RootPath,
                    ["apply", "--check", "--index", "--whitespace=nowarn", patchPath],
                    cancellationToken);
                if (!check.Succeeded)
                {
                    return await RecordAsync(
                        evidence,
                        CreateRecord(
                            evidence,
                            CandidatePromotionAction.Promote,
                            CandidatePromotionStatus.Rejected,
                            eligibility,
                            request.Actor,
                            request.Approval,
                            repository.RootPath,
                            string.Empty,
                            $"Patch preflight failed without applying changes: {Failure(check)}",
                            startedAt));
                }

                baselineFailure = await ValidateBaselineAsync(
                    repository.RootPath,
                    evidence.Baseline,
                    cancellationToken);
                if (baselineFailure is not null)
                {
                    return await RecordAsync(
                        evidence,
                        CreateRecord(
                            evidence,
                            CandidatePromotionAction.Promote,
                            CandidatePromotionStatus.Rejected,
                            eligibility,
                            request.Actor,
                            request.Approval,
                            repository.RootPath,
                            string.Empty,
                            $"Repository changed during patch preflight: {baselineFailure}",
                            startedAt));
                }

                var apply = await RunGitAsync(
                    repository.RootPath,
                    ["apply", "--index", "--whitespace=nowarn", patchPath],
                    cancellationToken);
                if (!apply.Succeeded)
                {
                    return await RecordAsync(
                        evidence,
                        CreateRecord(
                            evidence,
                            CandidatePromotionAction.Promote,
                            CandidatePromotionStatus.Rejected,
                            eligibility,
                            request.Actor,
                            request.Approval,
                            repository.RootPath,
                            string.Empty,
                            $"Atomic patch application failed: {Failure(apply)}",
                            startedAt));
                }

                patchApplied = true;
                var promotedDiff = await RunRequiredGitAsync(
                    repository.RootPath,
                    [
                        "diff", "--cached", "--binary", "--no-ext-diff",
                        evidence.Baseline.Commit, "--"
                    ],
                    cancellationToken);
                var promotedHash = Hash(promotedDiff.StandardOutput);
                if (!string.Equals(
                        promotedHash,
                        evidence.CandidateChangeSet.DiffHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    await RollbackAsync(
                        repository.RootPath,
                        evidence.Baseline.Commit,
                        CancellationToken.None);
                    patchApplied = false;
                    return await RecordAsync(
                        evidence,
                        CreateRecord(
                            evidence,
                            CandidatePromotionAction.Promote,
                            CandidatePromotionStatus.Failed,
                            eligibility,
                            request.Actor,
                            request.Approval,
                            repository.RootPath,
                            string.Empty,
                            "Post-apply diff hash differed from the verified candidate; promotion was rolled back",
                            startedAt));
                }

                var promoted = CreateRecord(
                    evidence,
                    CandidatePromotionAction.Promote,
                    CandidatePromotionStatus.Promoted,
                    eligibility,
                    request.Actor,
                    request.Approval,
                    repository.RootPath,
                    string.Empty,
                    "Verified candidate was atomically applied and staged",
                    startedAt);
                try
                {
                    await _evidenceStore.AppendPromotionAsync(
                        evidence.Id,
                        promoted,
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    await RollbackAsync(
                        repository.RootPath,
                        evidence.Baseline.Commit,
                        CancellationToken.None);
                    patchApplied = false;
                    return Result(
                        CopyAsFailed(
                            promoted,
                            $"Promotion evidence could not be persisted; repository was rolled back: {ex.Message}"));
                }

                return Result(promoted);
            }
        }
        catch (OperationCanceledException ex)
        {
            if (patchApplied)
                await RollbackAsync(requestedPath, evidence.Baseline.Commit, CancellationToken.None);
            return await RecordAsync(
                evidence,
                CreateRecord(
                    evidence,
                    CandidatePromotionAction.Promote,
                    CandidatePromotionStatus.Failed,
                    eligibility,
                    request.Actor,
                    request.Approval,
                    requestedPath,
                    string.Empty,
                    $"Promotion was cancelled: {ex.Message}",
                    startedAt));
        }
        catch (Exception ex)
        {
            var rollback = string.Empty;
            if (patchApplied)
            {
                try
                {
                    await RollbackAsync(requestedPath, evidence.Baseline.Commit, CancellationToken.None);
                }
                catch (Exception rollbackError)
                {
                    rollback = $" Rollback also failed: {rollbackError.Message}";
                }
            }

            return await RecordAsync(
                evidence,
                CreateRecord(
                    evidence,
                    CandidatePromotionAction.Promote,
                    CandidatePromotionStatus.Failed,
                    eligibility,
                    request.Actor,
                    request.Approval,
                    requestedPath,
                    string.Empty,
                    $"Promotion failed closed: {ex.Message}{rollback}",
                    startedAt));
        }
        finally
        {
            if (!string.IsNullOrEmpty(patchPath) && File.Exists(patchPath))
                File.Delete(patchPath);
            gate.Release();
        }
    }

    public async Task<CandidatePromotionResult> ExportPatchAsync(
        CandidatePatchExportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startedAt = DateTime.UtcNow;
        ExecutionEvidence? evidence;
        try
        {
            evidence = await LoadEvidenceAsync(request.EvidenceId, cancellationToken);
            if (evidence is not null)
                _evidenceStore.EnsureRepositoryIsolation(evidence.Baseline.RepositoryPath);
        }
        catch (Exception ex) when (IsEvidenceValidationFailure(ex))
        {
            return Unpersisted(
                request.EvidenceId,
                CandidatePromotionAction.ExportPatch,
                CandidatePromotionStatus.Rejected,
                request.Actor,
                new PromotionApproval(),
                string.Empty,
                request.DestinationPath,
                $"Execution evidence failed integrity or isolation validation: {ex.Message}",
                startedAt);
        }

        if (evidence is null)
        {
            return Unpersisted(
                request.EvidenceId,
                CandidatePromotionAction.ExportPatch,
                CandidatePromotionStatus.Rejected,
                request.Actor,
                new PromotionApproval(),
                string.Empty,
                request.DestinationPath,
                "Execution evidence was not found",
                startedAt);
        }

        var validationFailure = ValidateCandidateIntegrity(
            evidence,
            request.ExpectedDiffHash);
        if (string.IsNullOrWhiteSpace(request.Actor))
            validationFailure ??= "An explicit actor is required for patch export";

        string destinationPath;
        try
        {
            destinationPath = Path.GetFullPath(request.DestinationPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            validationFailure ??= $"Export path is invalid: {ex.Message}";
            destinationPath = request.DestinationPath;
        }

        try
        {
            if (validationFailure is null &&
                IsWithin(destinationPath, evidence.Baseline.RepositoryPath))
            {
                validationFailure = "Patch export path must be outside the target repository";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            validationFailure ??= $"Persisted repository path is invalid: {ex.Message}";
        }

        if (validationFailure is null && File.Exists(destinationPath))
            validationFailure = "Patch export destination already exists";

        if (validationFailure is not null)
        {
            return await RecordAsync(
                evidence,
                CreateRecord(
                    evidence,
                    CandidatePromotionAction.ExportPatch,
                    CandidatePromotionStatus.Rejected,
                    GetEligibility(evidence, null),
                    request.Actor,
                    new PromotionApproval(),
                    string.Empty,
                    destinationPath,
                    validationFailure,
                    startedAt));
        }

        var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.tmp";
        var exported = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllTextAsync(
                temporaryPath,
                evidence.CandidateChangeSet.Diff,
                cancellationToken);
            File.Move(temporaryPath, destinationPath, overwrite: false);
            exported = true;

            var record = CreateRecord(
                evidence,
                CandidatePromotionAction.ExportPatch,
                CandidatePromotionStatus.Exported,
                GetEligibility(evidence, null),
                request.Actor,
                new PromotionApproval(),
                string.Empty,
                destinationPath,
                "Candidate patch was exported without modifying the target repository",
                startedAt);
            try
            {
                await _evidenceStore.AppendPromotionAsync(
                    evidence.Id,
                    record,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                File.Delete(destinationPath);
                exported = false;
                return Result(CopyAsFailed(
                    record,
                    $"Export evidence could not be persisted; exported patch was removed: {ex.Message}"));
            }

            return Result(record);
        }
        catch (Exception ex)
        {
            if (exported && File.Exists(destinationPath))
                File.Delete(destinationPath);
            return await RecordAsync(
                evidence,
                CreateRecord(
                    evidence,
                    CandidatePromotionAction.ExportPatch,
                    CandidatePromotionStatus.Failed,
                    GetEligibility(evidence, null),
                    request.Actor,
                    new PromotionApproval(),
                    string.Empty,
                    destinationPath,
                    $"Patch export failed without a partial output: {ex.Message}",
                    startedAt));
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private async Task<ExecutionEvidence?> LoadEvidenceAsync(
        Guid evidenceId,
        CancellationToken cancellationToken)
    {
        if (evidenceId == Guid.Empty)
            return null;
        return await _evidenceStore.LoadAsync(evidenceId, cancellationToken);
    }

    private static bool IsEvidenceValidationFailure(Exception exception) =>
        exception is EvidenceIntegrityException or
            IOException or
            InvalidOperationException or
            UnauthorizedAccessException;

    private static string? ValidatePromotionRequest(
        CandidatePromotionRequest request,
        ExecutionEvidence evidence,
        out PromotionEligibility eligibility)
    {
        eligibility = GetEligibility(evidence, request.Approval);
        var integrityFailure = ValidateCandidateIntegrity(evidence, request.ExpectedDiffHash);
        if (integrityFailure is not null)
            return integrityFailure;
        if (string.IsNullOrWhiteSpace(request.Actor))
            return "An explicit actor is required for promotion";
        if (!Enum.IsDefined(request.Approval.Kind))
            return "Approval kind is invalid";
        if (request.Approval.Kind == PromotionApprovalKind.None)
            return "Explicit user, policy, or human-review confirmation is required";
        if ((request.Approval.Kind is PromotionApprovalKind.Policy or PromotionApprovalKind.HumanReview) &&
            string.IsNullOrWhiteSpace(request.Approval.Reference))
        {
            return "Policy and human-review approvals require a reference";
        }
        if (request.Approval.ConfirmedAt > DateTime.UtcNow.AddMinutes(5))
            return "Approval timestamp cannot be in the future";
        if (request.Approval.ConfirmedAt < DateTime.UtcNow.AddMinutes(-2))
            return "Promotion confirmation is stale; confirm again immediately before promotion";
        if (request.Approval.Kind == PromotionApprovalKind.HumanReview &&
            CandidateReviewReference.TryParse(request.Approval.Reference, out var reviewId))
        {
            var reviewFailure = ValidateReferencedReview(
                evidence,
                reviewId,
                request,
                DateTime.UtcNow);
            if (reviewFailure is not null)
                return reviewFailure;
        }
        if (eligibility == PromotionEligibility.None)
        {
            return evidence.FinalDecision.Decision == TaskDecision.HumanReviewRequired
                ? "HumanReviewRequired candidates need an explicit HumanReview approval"
                : $"Only Verified or HumanReviewApproved candidates can be promoted; found " +
                  $"{evidence.FinalDecision.Decision}/{evidence.FinalDecision.State}";
        }

        return null;
    }

    private static string? ValidateReviewRequest(
        CandidateReviewRequest request,
        ExecutionEvidence evidence,
        DateTime now)
    {
        var integrityFailure = ValidateCandidateIntegrity(evidence, request.ExpectedDiffHash);
        if (integrityFailure is not null)
            return integrityFailure;
        if (string.IsNullOrWhiteSpace(request.RepositoryPath))
            return "An explicit repository path is required for human review";
        if (string.IsNullOrWhiteSpace(request.Actor))
            return "An authenticated actor is required for human review";
        if (!Enum.IsDefined(request.Decision))
            return "Human review decision is invalid";
        if (string.IsNullOrWhiteSpace(request.Justification))
            return "Human review requires an explicit justification";
        if (string.IsNullOrWhiteSpace(request.PolicyReference))
            return "Human review requires a policy reference";
        if (request.ValidUntil <= now)
            return "Human review validity must end in the future";
        if (request.ValidUntil > now.AddHours(24))
            return "Human review validity cannot exceed 24 hours";
        if (request.Decision == CandidateReviewDecision.Approve && !IsReviewable(evidence))
            return "Candidate decision is not eligible for human approval";
        if (request.Decision == CandidateReviewDecision.Abandon)
        {
            if (request.RelatedReviewId.HasValue)
            {
                var related = evidence.Promotions.FirstOrDefault(item =>
                    item.Id == request.RelatedReviewId &&
                    item.Action == CandidatePromotionAction.Review &&
                    item.Status == CandidatePromotionStatus.Approved);
                if (related is null)
                    return "The review being abandoned was not found or was not approved";
            }
        }

        return null;
    }

    private static string? ValidateReferencedReview(
        ExecutionEvidence evidence,
        Guid reviewId,
        CandidatePromotionRequest request,
        DateTime now)
    {
        var review = evidence.Promotions.FirstOrDefault(item => item.Id == reviewId);
        if (review is null ||
            review.Action != CandidatePromotionAction.Review ||
            review.Status != CandidatePromotionStatus.Approved ||
            review.ReviewDecision != CandidateReviewDecision.Approve)
        {
            return "Referenced AECS human approval was not found or was not approved";
        }
        if (!string.Equals(
                review.ApprovalReference,
                request.Approval.Reference,
                StringComparison.Ordinal))
        {
            return "Referenced AECS human approval does not match its authenticated event";
        }
        if (!string.Equals(review.Actor, request.Actor, StringComparison.Ordinal))
            return "Promotion actor does not match the authenticated human approval actor";
        if (review.ValidUntil is null || review.ValidUntil <= now)
            return "Referenced AECS human approval has expired";
        if (string.IsNullOrWhiteSpace(review.Justification) ||
            string.IsNullOrWhiteSpace(review.PolicyReference))
        {
            return "Referenced AECS human approval is incomplete";
        }
        if (review.ConfirmedAt is null || request.Approval.ConfirmedAt < review.ConfirmedAt)
            return "Promotion confirmation predates the authenticated human approval";
        if (!SamePath(review.RepositoryPath, request.RepositoryPath) ||
            !string.Equals(review.BaselineCommit, evidence.Baseline.Commit, StringComparison.Ordinal) ||
            !string.Equals(review.DiffHash, evidence.CandidateChangeSet.DiffHash,
                StringComparison.OrdinalIgnoreCase))
        {
            return "Referenced AECS human approval targets a different repository or candidate";
        }
        if (evidence.Promotions.Any(item =>
                item.Action == CandidatePromotionAction.Review &&
                item.Status == CandidatePromotionStatus.Abandoned &&
                item.RelatedReviewId == review.Id &&
                item.FinishedAt >= review.FinishedAt))
        {
            return "Referenced AECS human approval was abandoned";
        }

        return null;
    }

    private static bool IsReviewable(ExecutionEvidence evidence) =>
        (evidence.FinalDecision.Decision == TaskDecision.Verified &&
         evidence.FinalDecision.State == TaskState.Verified) ||
        (evidence.FinalDecision.Decision == TaskDecision.HumanReviewRequired &&
         evidence.FinalDecision.State == TaskState.HumanReviewRequired);

    private static string? ValidateCandidateIntegrity(
        ExecutionEvidence evidence,
        string expectedDiffHash)
    {
        var candidate = evidence.CandidateChangeSet;
        if (!candidate.HasChanges)
            return "Evidence does not contain a non-empty candidate change";
        if (string.IsNullOrWhiteSpace(expectedDiffHash))
            return "The expected diff hash must be supplied explicitly";
        if (!string.Equals(candidate.BaselineCommit, evidence.Baseline.Commit, StringComparison.Ordinal))
            return "Candidate baseline commit does not match execution evidence";
        if (!string.Equals(candidate.TaskId, evidence.TaskContract.Id, StringComparison.Ordinal))
            return "Candidate task ID does not match execution evidence";
        if (!string.Equals(
                candidate.AgentRunId,
                evidence.AgentRun.Id.ToString("N"),
                StringComparison.OrdinalIgnoreCase))
        {
            return "Candidate agent run ID does not match execution evidence";
        }

        var computedHash = Hash(candidate.Diff);
        if (!string.Equals(computedHash, candidate.DiffHash, StringComparison.OrdinalIgnoreCase))
            return "Candidate diff hash does not match the persisted diff";
        if (!string.Equals(expectedDiffHash, candidate.DiffHash, StringComparison.OrdinalIgnoreCase))
            return "Confirmed diff hash does not match the verified candidate";
        return null;
    }

    private async Task<ResolvedRepository> ResolveRepositoryAsync(
        string requestedPath,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(requestedPath))
            throw new DirectoryNotFoundException($"Repository path not found: {requestedPath}");

        var root = (await RunRequiredGitAsync(
                requestedPath,
                ["rev-parse", "--show-toplevel"],
                cancellationToken))
            .StandardOutput.Trim();
        root = Path.GetFullPath(root);
        var commonDirectory = (await RunRequiredGitAsync(
                root,
                ["rev-parse", "--git-common-dir"],
                cancellationToken))
            .StandardOutput.Trim();
        if (!Path.IsPathRooted(commonDirectory))
            commonDirectory = Path.Combine(root, commonDirectory);
        commonDirectory = Path.GetFullPath(commonDirectory);
        if (!Directory.Exists(commonDirectory))
            throw new DirectoryNotFoundException($"Git common directory not found: {commonDirectory}");

        return new ResolvedRepository(root, commonDirectory);
    }

    private async Task<string?> ValidateBaselineAsync(
        string repositoryPath,
        BaselineSnapshot baseline,
        CancellationToken cancellationToken)
    {
        var commit = (await RunRequiredGitAsync(
                repositoryPath,
                ["rev-parse", "HEAD"],
                cancellationToken))
            .StandardOutput.Trim();
        if (!string.Equals(commit, baseline.Commit, StringComparison.Ordinal))
            return $"Repository HEAD changed from baseline {baseline.Commit} to {commit}";

        var branch = (await RunRequiredGitAsync(
                repositoryPath,
                ["rev-parse", "--abbrev-ref", "HEAD"],
                cancellationToken))
            .StandardOutput.Trim();
        if (!string.Equals(branch, baseline.Branch, StringComparison.Ordinal))
            return $"Repository branch changed from baseline {baseline.Branch} to {branch}";

        var status = (await RunRequiredGitAsync(
                repositoryPath,
                ["status", "--porcelain=v1", "--untracked-files=all"],
                cancellationToken))
            .StandardOutput;
        if (!string.Equals(status, baseline.GitStatus, StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(status))
        {
            return "Repository working tree or index changed after the baseline was captured";
        }

        return null;
    }

    private async Task RollbackAsync(
        string repositoryPath,
        string baselineCommit,
        CancellationToken cancellationToken)
    {
        await RunRequiredGitAsync(
            repositoryPath,
            ["reset", "--hard", baselineCommit],
            cancellationToken);
        var status = await RunRequiredGitAsync(
            repositoryPath,
            ["status", "--porcelain=v1", "--untracked-files=all"],
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(status.StandardOutput))
            throw new InvalidOperationException("Rollback did not restore the clean baseline.");
    }

    private async Task<ProcessExecutionResult> RunRequiredGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(workingDirectory, arguments, cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Git command failed ({string.Join(' ', arguments)}): {Failure(result)}");
        }

        return result;
    }

    private Task<ProcessExecutionResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) => _processRunner.RunAsync(
        new ProcessExecutionRequest
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            Timeout = GitTimeout
        },
        cancellationToken);

    private async Task<CandidatePromotionResult> RecordAsync(
        ExecutionEvidence evidence,
        CandidatePromotionEvidence record)
    {
        try
        {
            await _evidenceStore.AppendPromotionAsync(
                evidence.Id,
                record,
                CancellationToken.None);
            return Result(record);
        }
        catch (Exception ex)
        {
            return Result(CopyAsFailed(
                record,
                $"{record.Message}; promotion evidence could not be persisted: {ex.Message}"));
        }
    }

    private static CandidatePromotionEvidence CreateRecord(
        ExecutionEvidence evidence,
        CandidatePromotionAction action,
        CandidatePromotionStatus status,
        PromotionEligibility eligibility,
        string actor,
        PromotionApproval approval,
        string repositoryPath,
        string outputPath,
        string message,
        DateTime startedAt) => new()
        {
            ExecutionEvidenceId = evidence.Id,
            CandidateId = evidence.CandidateChangeSet.Id,
            Action = action,
            Status = status,
            Eligibility = eligibility,
            Actor = actor,
            ApprovalKind = approval.Kind,
            ApprovalReference = approval.Reference,
            ConfirmedAt = approval.Kind == PromotionApprovalKind.None
                ? null
                : approval.ConfirmedAt,
            BaselineCommit = evidence.Baseline.Commit,
            DiffHash = evidence.CandidateChangeSet.DiffHash,
            RepositoryPath = repositoryPath,
            OutputPath = outputPath,
            Message = message,
            StartedAt = startedAt,
            FinishedAt = DateTime.UtcNow
        };

    private static CandidatePromotionEvidence CreateReviewRecord(
        ExecutionEvidence evidence,
        CandidateReviewRequest request,
        string repositoryPath,
        CandidatePromotionStatus status,
        string message,
        DateTime startedAt)
    {
        var id = Guid.NewGuid();
        var reference = request.Decision == CandidateReviewDecision.Abandon &&
            request.RelatedReviewId.HasValue
                ? CandidateReviewReference.Create(request.RelatedReviewId.Value)
                : CandidateReviewReference.Create(id);
        return new CandidatePromotionEvidence
        {
            Id = id,
            ExecutionEvidenceId = evidence.Id,
            CandidateId = evidence.CandidateChangeSet.Id,
            Action = CandidatePromotionAction.Review,
            Status = status,
            Eligibility = GetEligibility(
                evidence,
                status == CandidatePromotionStatus.Approved
                    ? new PromotionApproval { Kind = PromotionApprovalKind.HumanReview }
                    : null),
            Actor = request.Actor,
            ApprovalKind = status == CandidatePromotionStatus.Approved
                ? PromotionApprovalKind.HumanReview
                : PromotionApprovalKind.None,
            ApprovalReference = reference,
            ConfirmedAt = startedAt,
            BaselineCommit = evidence.Baseline.Commit,
            DiffHash = evidence.CandidateChangeSet.DiffHash,
            RepositoryPath = repositoryPath,
            Message = message,
            StartedAt = startedAt,
            FinishedAt = DateTime.UtcNow,
            ReviewDecision = request.Decision,
            Justification = request.Justification.Trim(),
            ValidUntil = request.ValidUntil,
            PolicyReference = request.PolicyReference.Trim(),
            RelatedReviewId = request.RelatedReviewId
        };
    }

    private static CandidateReviewGate ReviewGate(
        string phase,
        VerificationResult result) => new()
        {
            Phase = phase,
            Verifier = result.Verifier,
            Status = result.Status,
            Message = result.Message
        };

    private static CandidateReviewSnapshot UnavailableReview(
        CandidateReviewSnapshot source,
        string message) => new()
        {
            Available = false,
            Message = message,
            EvidenceId = source.EvidenceId,
            RepositoryPath = source.RepositoryPath,
            RepositoryState = "unavailable"
        };

    private static CandidateReviewResult UnpersistedReview(
        CandidateReviewRequest request,
        CandidatePromotionStatus status,
        string message,
        DateTime startedAt) => new()
        {
            Status = status,
            Message = message,
            Persisted = false,
            Evidence = new CandidatePromotionEvidence
            {
                ExecutionEvidenceId = request.EvidenceId,
                Action = CandidatePromotionAction.Review,
                Status = status,
                Actor = request.Actor,
                RepositoryPath = request.RepositoryPath,
                Message = message,
                StartedAt = startedAt,
                FinishedAt = DateTime.UtcNow,
                ReviewDecision = request.Decision,
                Justification = request.Justification,
                ValidUntil = request.ValidUntil,
                PolicyReference = request.PolicyReference,
                RelatedReviewId = request.RelatedReviewId
            }
        };

    private static CandidatePromotionEvidence CopyAsFailed(
        CandidatePromotionEvidence source,
        string message) => new()
        {
            ExecutionEvidenceId = source.ExecutionEvidenceId,
            CandidateId = source.CandidateId,
            Action = source.Action,
            Status = CandidatePromotionStatus.Failed,
            Eligibility = source.Eligibility,
            Actor = source.Actor,
            ApprovalKind = source.ApprovalKind,
            ApprovalReference = source.ApprovalReference,
            ConfirmedAt = source.ConfirmedAt,
            BaselineCommit = source.BaselineCommit,
            DiffHash = source.DiffHash,
            RepositoryPath = source.RepositoryPath,
            OutputPath = source.OutputPath,
            Message = message,
            StartedAt = source.StartedAt,
            FinishedAt = DateTime.UtcNow,
            ReviewDecision = source.ReviewDecision,
            Justification = source.Justification,
            ValidUntil = source.ValidUntil,
            PolicyReference = source.PolicyReference,
            RelatedReviewId = source.RelatedReviewId
        };

    private static CandidatePromotionResult Unpersisted(
        Guid evidenceId,
        CandidatePromotionAction action,
        CandidatePromotionStatus status,
        string actor,
        PromotionApproval approval,
        string repositoryPath,
        string outputPath,
        string message,
        DateTime startedAt)
    {
        var record = new CandidatePromotionEvidence
        {
            ExecutionEvidenceId = evidenceId,
            Action = action,
            Status = status,
            Actor = actor,
            ApprovalKind = approval.Kind,
            ApprovalReference = approval.Reference,
            ConfirmedAt = approval.Kind == PromotionApprovalKind.None
                ? null
                : approval.ConfirmedAt,
            RepositoryPath = repositoryPath,
            OutputPath = outputPath,
            Message = message,
            StartedAt = startedAt,
            FinishedAt = DateTime.UtcNow
        };
        return Result(record);
    }

    private static CandidatePromotionResult Result(CandidatePromotionEvidence record) => new()
    {
        Status = record.Status,
        Message = record.Message,
        OutputPath = record.OutputPath,
        Evidence = record
    };

    private static PromotionEligibility GetEligibility(
        ExecutionEvidence evidence,
        PromotionApproval? approval)
    {
        if (evidence.FinalDecision.Decision == TaskDecision.Verified &&
            evidence.FinalDecision.State == TaskState.Verified)
        {
            return PromotionEligibility.Verified;
        }

        if (evidence.FinalDecision.Decision == TaskDecision.HumanReviewRequired &&
            evidence.FinalDecision.State == TaskState.HumanReviewRequired &&
            approval?.Kind == PromotionApprovalKind.HumanReview)
        {
            return PromotionEligibility.HumanReviewApproved;
        }

        return PromotionEligibility.None;
    }

    private static string Hash(string diff)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(diff)))
            .ToLowerInvariant();
        return $"sha256:{hash}";
    }

    private static string Failure(ProcessExecutionResult result)
    {
        if (result.TimedOut)
            return "timed out and the process tree was terminated";
        if (result.Cancelled)
            return "was cancelled and the process tree was terminated";
        return $"exit code {result.ExitCode}; stderr: {result.StandardError.Trim()}";
    }

    private static bool SamePath(string left, string right) => string.Equals(
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    private static bool IsWithin(string candidate, string root)
    {
        var resolvedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var resolvedCandidate = Path.GetFullPath(candidate);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(resolvedCandidate, resolvedRoot, comparison) ||
            resolvedCandidate.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, comparison);
    }

    private sealed record ResolvedRepository(string RootPath, string GitCommonDirectory);
}
