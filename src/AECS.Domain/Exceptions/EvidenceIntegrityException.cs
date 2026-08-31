namespace AECS.Domain.Exceptions;

public sealed class EvidenceIntegrityException : IOException
{
    public EvidenceIntegrityException(string message)
        : base(message)
    {
    }

    public EvidenceIntegrityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
