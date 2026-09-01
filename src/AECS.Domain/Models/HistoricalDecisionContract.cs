namespace AECS.Domain.Models;

public static class HistoricalDecisionContract
{
    public static HistoricalDecision Seal(HistoricalDecision decision)
    {
        Validate(decision, requireContentHash: false);
        var sealedDecision = Copy(decision, HistoricalDecisionFingerprint.Create(decision));
        Validate(sealedDecision, requireContentHash: true);
        return sealedDecision;
    }

    public static HistoricalDecisionSuppression Seal(
        HistoricalDecisionSuppression suppression)
    {
        Validate(suppression, requireContentHash: false);
        var sealedSuppression = Copy(
            suppression,
            HistoricalDecisionFingerprint.Create(suppression));
        Validate(sealedSuppression, requireContentHash: true);
        return sealedSuppression;
    }

    public static void Validate(
        HistoricalDecision decision,
        bool requireContentHash = true)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.SchemaVersion != HistoricalDecisionSchema.DecisionVersion ||
            string.IsNullOrWhiteSpace(decision.Id) ||
            decision.Id.Length > 200 ||
            decision.Version <= 0 ||
            !Enum.IsDefined(decision.Type) ||
            string.IsNullOrWhiteSpace(decision.Source) ||
            decision.Source.Length > 1000 ||
            string.IsNullOrWhiteSpace(decision.SourceVersion) ||
            decision.SourceVersion.Length > 500 ||
            !IsSha256(decision.SourceHash) ||
            string.IsNullOrWhiteSpace(decision.Authority) ||
            decision.Authority.Length > 200 ||
            !IsUtc(decision.ValidFrom) ||
            decision.ValidUntil is { } validUntil &&
                (!IsUtc(validUntil) || validUntil <= decision.ValidFrom) ||
            decision.ProhibitedPatterns is null ||
            decision.RequiredPatterns is null ||
            decision.ProhibitedPatterns.Count + decision.RequiredPatterns.Count == 0 ||
            decision.ProhibitedPatterns.Any(InvalidPattern) ||
            decision.RequiredPatterns.Any(InvalidPattern) ||
            HasDuplicates(decision.ProhibitedPatterns) ||
            HasDuplicates(decision.RequiredPatterns) ||
            PatternKeys(decision.ProhibitedPatterns).Intersect(
                PatternKeys(decision.RequiredPatterns),
                StringComparer.Ordinal).Any() ||
            string.IsNullOrWhiteSpace(decision.Justification) ||
            decision.Justification.Length > 4000 ||
            !Enum.IsDefined(decision.Enforcement) ||
            decision.Scope is null ||
            decision.Scope.ProjectPaths is null ||
            decision.Scope.NamespacePrefixes is null ||
            decision.Scope.SymbolKinds is null ||
            decision.Scope.ProjectPaths.Any(path => !IsSafeRepositoryPath(path)) ||
            decision.Scope.NamespacePrefixes.Any(string.IsNullOrWhiteSpace) ||
            decision.Scope.SymbolKinds.Any(kind => kind is not (
                "project" or "file" or "namespace" or "type" or "member")) ||
            decision.Review is null ||
            !Enum.IsDefined(decision.Review.Status) ||
            !Enum.IsDefined(decision.Review.Authority) ||
            !ValidReview(decision) ||
            !IsUtc(decision.CreatedAt) ||
            requireContentHash &&
                (!IsSha256(decision.ContentHash) ||
                 !string.Equals(
                     decision.ContentHash,
                     HistoricalDecisionFingerprint.Create(decision),
                     StringComparison.Ordinal)) ||
            !requireContentHash &&
                !string.IsNullOrEmpty(decision.ContentHash) &&
                !string.Equals(
                    decision.ContentHash,
                    HistoricalDecisionFingerprint.Create(decision),
                    StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Historical decision is incomplete, inconsistent, or uses an unsupported schema.");
        }
    }

    public static void Validate(
        HistoricalDecisionSuppression suppression,
        bool requireContentHash = true)
    {
        ArgumentNullException.ThrowIfNull(suppression);
        if (suppression.SchemaVersion != HistoricalDecisionSchema.SuppressionVersion ||
            string.IsNullOrWhiteSpace(suppression.Id) ||
            suppression.Id.Length > 200 ||
            suppression.Version <= 0 ||
            string.IsNullOrWhiteSpace(suppression.DecisionId) ||
            suppression.DecisionId.Length > 200 ||
            suppression.DecisionVersion <= 0 ||
            string.IsNullOrWhiteSpace(suppression.Actor) ||
            suppression.Actor.Length > 200 ||
            string.IsNullOrWhiteSpace(suppression.Reason) ||
            suppression.Reason.Length > 4000 ||
            suppression.SymbolId.Length > 1000 ||
            suppression.FilePath.Length > 1000 ||
            !string.IsNullOrEmpty(suppression.FilePath) &&
                !IsSafeRepositoryPath(suppression.FilePath) ||
            !IsUtc(suppression.CreatedAt) ||
            !IsUtc(suppression.ExpiresAt) ||
            suppression.ExpiresAt <= suppression.CreatedAt ||
            requireContentHash &&
                (!IsSha256(suppression.ContentHash) ||
                 !string.Equals(
                     suppression.ContentHash,
                     HistoricalDecisionFingerprint.Create(suppression),
                     StringComparison.Ordinal)) ||
            !requireContentHash &&
                !string.IsNullOrEmpty(suppression.ContentHash) &&
                !string.Equals(
                    suppression.ContentHash,
                    HistoricalDecisionFingerprint.Create(suppression),
                    StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Historical decision suppression is incomplete, inconsistent, or uses an unsupported schema.");
        }
    }

    private static bool ValidReview(HistoricalDecision decision)
    {
        var review = decision.Review;
        if (review.Status == HistoricalDecisionReviewStatus.Draft)
        {
            return review.Authority == HistoricalDecisionReviewAuthority.None &&
                string.IsNullOrEmpty(review.Actor) &&
                string.IsNullOrEmpty(review.Reason) &&
                review.ReviewedAt is null;
        }

        if (review.Authority == HistoricalDecisionReviewAuthority.None ||
            string.IsNullOrWhiteSpace(review.Actor) ||
            review.Actor.Length > 200 ||
            string.IsNullOrWhiteSpace(review.Reason) ||
            review.Reason.Length > 4000 ||
            review.ReviewedAt is not { } reviewedAt ||
            !IsUtc(reviewedAt))
        {
            return false;
        }

        return decision.Enforcement != HistoricalDecisionEnforcement.Blocking ||
            review.Status != HistoricalDecisionReviewStatus.Approved ||
            review.Authority == HistoricalDecisionReviewAuthority.Human;
    }

    private static bool InvalidPattern(HistoricalDecisionPattern pattern) =>
        pattern is null ||
        !Enum.IsDefined(pattern.Kind) ||
        string.IsNullOrWhiteSpace(pattern.Value) ||
        pattern.Value.Length > 500;

    private static bool HasDuplicates(IEnumerable<HistoricalDecisionPattern> patterns) =>
        PatternKeys(patterns).Distinct(StringComparer.Ordinal).Count() != patterns.Count();

    private static IEnumerable<string> PatternKeys(
        IEnumerable<HistoricalDecisionPattern> patterns) => patterns.Select(pattern =>
            $"{pattern.Kind}:{pattern.Value.Trim()}");

    private static bool IsUtc(DateTime value) => value.Kind == DateTimeKind.Utc;

    private static bool IsSha256(string value) =>
        value.Length == 71 &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(character => Uri.IsHexDigit(character) && !char.IsUpper(character));

    private static bool IsSafeRepositoryPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) ||
            value.StartsWith('/') || value.Contains('\\'))
        {
            return false;
        }
        return value.Split('/').All(segment => segment is not ("" or "." or ".."));
    }

    private static HistoricalDecision Copy(
        HistoricalDecision source,
        string contentHash) => new()
        {
            SchemaVersion = source.SchemaVersion,
            Id = source.Id,
            Version = source.Version,
            Type = source.Type,
            Source = source.Source,
            SourceVersion = source.SourceVersion,
            SourceHash = source.SourceHash,
            Authority = source.Authority,
            ValidFrom = source.ValidFrom,
            ValidUntil = source.ValidUntil,
            ProhibitedPatterns = source.ProhibitedPatterns,
            RequiredPatterns = source.RequiredPatterns,
            Justification = source.Justification,
            Enforcement = source.Enforcement,
            ExtractedHeuristically = source.ExtractedHeuristically,
            Scope = source.Scope,
            Review = source.Review,
            ContentHash = contentHash,
            CreatedAt = source.CreatedAt
        };

    private static HistoricalDecisionSuppression Copy(
        HistoricalDecisionSuppression source,
        string contentHash) => new()
        {
            SchemaVersion = source.SchemaVersion,
            Id = source.Id,
            Version = source.Version,
            DecisionId = source.DecisionId,
            DecisionVersion = source.DecisionVersion,
            Actor = source.Actor,
            Reason = source.Reason,
            SymbolId = source.SymbolId,
            FilePath = source.FilePath,
            CreatedAt = source.CreatedAt,
            ExpiresAt = source.ExpiresAt,
            ContentHash = contentHash
        };
}
