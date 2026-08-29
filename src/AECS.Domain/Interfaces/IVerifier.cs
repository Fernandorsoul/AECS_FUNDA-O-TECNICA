using AECS.Domain.Enums;
using AECS.Domain.Models;

namespace AECS.Domain.Interfaces;

public interface IVerifier
{
    string Name { get; }
    VerificationCategory Category { get; }
    Task<VerificationResult> VerifyAsync(VerificationContext context, CancellationToken cancellationToken);
}
