using AECS.Domain.Enums;

namespace AECS.Domain.Models;

public class ApprovalPolicy
{
    public ApprovalLevel Production { get; init; } = ApprovalLevel.None;
}
