namespace AECS.Domain.Interfaces;

public interface IEvidenceSignatureService
{
    string Algorithm { get; }
    string KeyId { get; }

    byte[] Sign(ReadOnlySpan<byte> payload);

    bool Verify(
        string algorithm,
        string keyId,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> signature);
}
