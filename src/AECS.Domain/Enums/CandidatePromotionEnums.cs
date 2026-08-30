namespace AECS.Domain.Enums;

public enum CandidatePromotionAction
{
    ExportPatch,
    Promote
}

public enum CandidatePromotionStatus
{
    Exported,
    Promoted,
    Rejected,
    Failed
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
