using AECS.Cli.Runtime;
using AECS.Domain.Interfaces;

namespace AECS.Cli;

internal sealed class EvidenceStoreSelection
{
    private readonly RuntimeCliOptions _runtime = new();

    public bool TryConsume(string[] args, ref int index)
    {
        var option = args[index];
        if (option is not (
            "--runtime-config" or
            "--evidence-store" or
            "--evidence-root" or
            "--key-directory"))
        {
            return false;
        }
        return _runtime.TryConsume(args, ref index);
    }

    public IExecutionEvidenceStore Create()
    {
        var resolved = AecsRuntimeConfigurationResolver.Resolve(_runtime);
        return AecsExecutionRuntime.CreateEvidenceStore(resolved);
    }

    public const string Usage =
        "[--runtime-config <config.json>] " +
        "[--evidence-store <json|postgres>] [--evidence-root <path>] " +
        "[--key-directory <path>]";
}
