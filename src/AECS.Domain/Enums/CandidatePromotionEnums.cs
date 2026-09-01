namespace AECS.Domain.Enums;

public enum CandidatePromotionAction
{
    ExportPatch,
    Promote,
    Review
}

public enum CandidatePromotionStatus
{
    Exported,
    Promoted,
    Rejected,
    Failed,
    Approved,
    Declined,
    Abandoned
}

public enum PromotionApprovalKind
{
    None,
    UserConfirmation,
    Policy,
    HumanReview
}

public enum PromotionEligibility
{
    None,
    Verified,
    HumanReviewApproved
}

public enum CandidateReviewDecision
{
    Approve,
    Reject,
    Abandon
}
