namespace AECS.Cli.Runtime;

public sealed class RuntimeCliOptions
{
    public string? ConfigurationPath { get; private set; }
    public string? AgentMode { get; private set; }
    public bool? AllowHostExecution { get; private set; }
    public string? OllamaBaseUrl { get; private set; }
    public int? OllamaContextWindowTokens { get; private set; }
    public bool? CloudFallbackEnabled { get; private set; }
    public bool? CloudRepositoryContextAllowed { get; private set; }
    public string? CloudKey { get; private set; }
    public string? CloudCredentialEnvironmentVariable { get; private set; }
    public string? CloudModel { get; private set; }
    public string? CloudUrl { get; private set; }
    public List<string>? CloudAllowedRisks { get; private set; }
    public string? EvidenceBackend { get; private set; }
    public string? EvidenceJsonRoot { get; private set; }
    public string? EvidenceKeyDirectory { get; private set; }
    public bool ShowEffectiveConfiguration { get; private set; }
    public string? ParseError { get; private set; }

    public bool TryConsume(string[] args, ref int index)
    {
        ArgumentNullException.ThrowIfNull(args);
        var option = args[index];
        switch (option)
        {
            case "--runtime-config":
                ConfigurationPath = Value(args, ref index, option);
                return true;
            case "--mock":
                AgentMode = "mock";
                return true;
            case "--allow-host-execution":
                AllowHostExecution = true;
                return true;
            case "--ollama-url":
                OllamaBaseUrl = Value(args, ref index, option);
                return true;
            case "--ollama-context-window":
                OllamaContextWindowTokens = IntValue(args, ref index, option);
                return true;
            case "--enable-cloud-fallback":
                CloudFallbackEnabled = true;
                return true;
            case "--disable-cloud-fallback":
                CloudFallbackEnabled = false;
                return true;
            case "--allow-cloud-context":
                CloudRepositoryContextAllowed = true;
                return true;
            case "--cloud-key":
                CloudKey = Value(args, ref index, option);
                return true;
            case "--cloud-key-env":
                CloudCredentialEnvironmentVariable = Value(args, ref index, option);
                return true;
            case "--cloud-model":
                CloudModel = Value(args, ref index, option);
                return true;
            case "--cloud-url":
                CloudUrl = Value(args, ref index, option);
                return true;
            case "--cloud-risks":
                var risks = Value(args, ref index, option);
                CloudAllowedRisks = risks?.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries).ToList();
                return true;
            case "--evidence-store":
                EvidenceBackend = Value(args, ref index, option)?.ToLowerInvariant();
                return true;
            case "--evidence-root":
                EvidenceJsonRoot = Value(args, ref index, option);
                return true;
            case "--key-directory":
                EvidenceKeyDirectory = Value(args, ref index, option);
                return true;
            case "--show-effective-config":
                ShowEffectiveConfiguration = true;
                return true;
            default:
                return false;
        }
    }

    public const string Usage =
        "[--runtime-config <config.json>] [--mock] [--allow-host-execution] " +
        "[--ollama-url <url>] [--enable-cloud-fallback --allow-cloud-context] " +
        "[--cloud-key <key>|--cloud-key-env <name>] [--cloud-model <model>] " +
        "[--cloud-url <url>] [--cloud-risks <R0,R1,...>] " +
        "[--evidence-store <json|postgres>] [--evidence-root <path>] " +
        "[--key-directory <path>] [--show-effective-config]";

    private string? Value(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            ParseError = $"{option} requires a value.";
            return null;
        }
        var value = args[++index];
        if (string.IsNullOrWhiteSpace(value))
            ParseError = $"{option} requires a non-empty value.";
        return value;
    }

    private int? IntValue(string[] args, ref int index, string option)
    {
        var value = Value(args, ref index, option);
        if (value is null)
            return null;
        if (int.TryParse(value, out var parsed))
            return parsed;
        ParseError = $"{option} requires an integer value.";
        return null;
    }
}
