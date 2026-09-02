using AECS.Domain.Models;

namespace AECS.Domain.Interfaces;

public interface ISecurityScanner
{
    string Id { get; }
    Task<SecurityScannerResult> ScanAsync(
        SecurityScannerContext context,
        CancellationToken cancellationToken);
}
