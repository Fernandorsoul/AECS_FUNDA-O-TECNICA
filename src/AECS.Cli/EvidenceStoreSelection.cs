using AECS.Domain.Interfaces;
using AECS.Infrastructure.Repositories;

namespace AECS.Cli;

internal sealed class EvidenceStoreSelection
{
    public EvidenceStoreSelection()
    {
        Backend = Environment.GetEnvironmentVariable("AECS_EVIDENCE_STORE") ?? "json";
    }

    public string Backend { get; private set; }
    public string? JsonRoot { get; private set; }
    public string? KeyDirectory { get; private set; }
    public string? ParseError { get; private set; }

    public bool TryConsume(string[] args, ref int index)
    {
        var option = args[index];
        if (option is not ("--evidence-store" or "--evidence-root" or "--key-directory"))
            return false;

        if (index + 1 >= args.Length)
        {
            ParseError = $"{option} requires a value.";
            return true;
        }

        var value = args[++index];
        switch (option)
        {
            case "--evidence-store":
                Backend = value;
                break;
            case "--evidence-root":
                JsonRoot = value;
                break;
            case "--key-directory":
                KeyDirectory = value;
                break;
        }

        return true;
    }

    public IExecutionEvidenceStore Create()
    {
        if (ParseError is not null)
            throw new InvalidOperationException(ParseError);

        if (string.Equals(Backend, "json", StringComparison.OrdinalIgnoreCase))
        {
            var root = JsonRoot ?? JsonExecutionEvidenceStore.GetDefaultRootPath();
            return KeyDirectory is null
                ? new JsonExecutionEvidenceStore(root)
                : new JsonExecutionEvidenceStore(root, KeyDirectory);
        }

        if (string.Equals(Backend, "postgres", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Backend, "postgresql", StringComparison.OrdinalIgnoreCase))
        {
            if (JsonRoot is not null)
            {
                throw new InvalidOperationException(
                    "--evidence-root is valid only when --evidence-store json is selected.");
            }

            return new PostgreSqlExecutionEvidenceStore(
                PostgreSqlExecutionEvidenceStore.GetRequiredConnectionString(),
                KeyDirectory);
        }

        throw new InvalidOperationException(
            $"Unsupported evidence store '{Backend}'. Select 'json' or 'postgres'.");
    }

    public const string Usage =
        "[--evidence-store <json|postgres>] [--evidence-root <path>] " +
        "[--key-directory <path>]";
}
