using System.Text;

namespace AECS.Domain.Models;

public static class ConservativeTokenCounter
{
    public const string Id = "utf8-byte-upper-bound";
    public const string Version = "1";

    public static int Count(string value) => value.Length == 0
        ? 0
        : Encoding.UTF8.GetByteCount(value);
}

public sealed class AgentContextProfile
{
    public string Adapter { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string TokenizerId { get; init; } = ConservativeTokenCounter.Id;
    public int ContextWindowTokens { get; init; } = 32_768;
    public int ReservedOutputTokens { get; init; }
    public int PromptOverheadTokens { get; init; }

    public int EffectiveTotalTokens(ExecutionBudget budget) => Math.Max(
        0,
        Math.Min(ContextWindowTokens, budget.MaxTokens));

    public int EffectiveContextTokens(ExecutionBudget budget)
    {
        var total = EffectiveTotalTokens(budget);
        return Math.Max(0, total - ReservedOutputTokens - PromptOverheadTokens);
    }

    public static AgentContextProfile Conservative(
        string adapter,
        string model,
        ExecutionBudget budget,
        int promptOverheadTokens,
        int contextWindowTokens = 32_768,
        int desiredOutputTokens = 4_096)
    {
        var total = Math.Max(0, Math.Min(contextWindowTokens, budget.MaxTokens));
        var reservedOutput = total == 0
            ? 0
            : Math.Min(desiredOutputTokens, Math.Max(1, total / 4));
        return new AgentContextProfile
        {
            Adapter = adapter,
            Model = model,
            ContextWindowTokens = contextWindowTokens,
            ReservedOutputTokens = reservedOutput,
            PromptOverheadTokens = Math.Max(0, promptOverheadTokens)
        };
    }
}
