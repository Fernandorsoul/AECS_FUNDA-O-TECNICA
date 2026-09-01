using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AECS.Application.Jarvis;

public static class DurableExecutionHistoryFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string ToJson(object value) => JsonSerializer.Serialize(value, JsonOptions);

    public static string HistoryToText(DurableHistoryResult result, bool status = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        var output = new StringBuilder();
        if (result.Items.Count == 0)
        {
            output.AppendLine(status
                ? "No authenticated execution is available for this repository."
                : "No authenticated evidence matched the history query.");
        }
        foreach (var item in result.Items)
        {
            output.Append("[persisted] evidence=").Append(item.EvidenceId.ToString("N"))
                .Append(" task=").Append(item.TaskId)
                .Append(" run=").Append(item.RunId.ToString("N"))
                .Append(" candidate=").Append(item.CandidateId.ToString("N"))
                .Append(" decision=").Append(item.Decision)
                .Append(" risk=").Append(item.Risk)
                .Append(" model=").Append(item.Model)
                .Append(" status=").Append(item.Status)
                .AppendLine();
            output.Append("[persisted] baseline=").Append(item.BaselineCommit)
                .Append(" diff=").Append(item.DiffHash)
                .Append(" evidence-hash=").Append(item.EvidenceHash)
                .Append(" authority=").Append(item.Authority)
                .Append(" created=").Append(item.CreatedAt.ToString("O"))
                .AppendLine();
            if (status)
                output.Append("[persisted] objective=").AppendLine(item.Objective);
        }
        AppendDiagnostics(output, result.Diagnostics);
        return output.ToString();
    }

    public static string ExplanationToText(DurableExplanationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Item is null)
        {
            var missing = new StringBuilder();
            AppendDiagnostics(missing, result.Diagnostics);
            return missing.Length == 0
                ? "No authenticated evidence matched the requested execution.\n"
                : missing.ToString();
        }
        var item = result.Item;
        var execution = item.Execution;
        var facts = item.Persisted;
        var output = new StringBuilder()
            .Append("[persisted] evidence=").Append(execution.EvidenceId.ToString("N"))
            .Append(" schema=").Append(execution.EvidenceSchemaVersion)
            .Append(" authority=").Append(execution.Authority)
            .Append(" hash=").AppendLine(execution.EvidenceHash)
            .Append("[persisted] task=").Append(execution.TaskId)
            .Append(" run=").Append(execution.RunId.ToString("N"))
            .Append(" candidate=").Append(execution.CandidateId.ToString("N"))
            .Append(" baseline=").Append(execution.BaselineCommit)
            .Append(" diff=").AppendLine(execution.DiffHash)
            .Append("[persisted] created=").Append(execution.CreatedAt.ToString("O"))
            .Append(" updated=").AppendLine(execution.UpdatedAt.ToString("O"))
            .Append("[persisted] objective=").AppendLine(execution.Objective)
            .Append("[persisted] decision=").Append(execution.Decision)
            .Append(" state=").Append(execution.State)
            .Append(" decided-at=").Append(facts.DecidedAt.ToString("O"))
            .Append(" reason=").AppendLine(facts.DecisionReason)
            .Append("[persisted] budget attempts=").Append(facts.Budget.AttemptsUsed)
            .Append('/').Append(facts.Budget.MaximumAttempts)
            .Append(" tokens=").Append(facts.Budget.InputTokens).Append('+')
            .Append(facts.Budget.OutputTokens)
            .Append(" cost-usd=").Append(facts.Budget.AccountedCostUsd?.ToString(
                "F6",
                CultureInfo.InvariantCulture) ?? "unavailable")
            .Append(" cost-basis=").Append(facts.Budget.AccountedCostBasis)
            .Append(" elapsed-seconds=").Append(facts.Budget.WallClockSeconds.ToString(
                "F2",
                CultureInfo.InvariantCulture))
            .AppendLine();

        foreach (var gate in facts.Gates)
        {
            output.Append("[persisted] gate phase=").Append(gate.Phase)
                .Append(" id=").Append(gate.Id.ToString("N"))
                .Append(" verifier=").Append(gate.Verifier)
                .Append(" status=").Append(gate.Status)
                .Append(" created=").Append(gate.CreatedAt.ToString("O"))
                .Append(" message=").AppendLine(gate.Message);
        }
        foreach (var criterion in facts.AcceptanceCriteria)
        {
            output.Append("[persisted] acceptance id=").Append(criterion.CriterionId)
                .Append(" status=").Append(criterion.Status)
                .Append(" evidence=").Append(criterion.EvidenceType).Append(':')
                .Append(criterion.EvidenceReference)
                .Append(" references=").Append(string.Join(',', criterion.EvidenceReferences))
                .AppendLine();
        }
        foreach (var attempt in facts.Attempts)
        {
            output.Append("[persisted] attempt id=").Append(attempt.Id.ToString("N"))
                .Append(" number=").Append(attempt.Number)
                .Append(" success=").Append(attempt.Success)
                .Append(" failure=").Append(attempt.FailureKind)
                .Append(" retry=").Append(attempt.WillRetry)
                .Append(" started=").Append(attempt.StartedAt.ToString("O"))
                .Append(" finished=").Append(attempt.FinishedAt.ToString("O"))
                .Append(" reason=").AppendLine(attempt.Reason);
        }
        AppendContext(output, facts.Context);
        foreach (var promotion in facts.Promotions)
        {
            output.Append("[persisted] promotion id=").Append(promotion.Id.ToString("N"))
                .Append(" action=").Append(promotion.Action)
                .Append(" status=").Append(promotion.Status)
                .Append(" actor=").Append(promotion.Actor)
                .Append(" approval=").Append(promotion.ApprovalReference)
                .Append(" started=").Append(promotion.StartedAt.ToString("O"))
                .Append(" finished=").Append(promotion.FinishedAt.ToString("O"))
                .AppendLine();
        }
        output.Append("[derived] total-tokens=").Append(item.Derived.TotalTokens)
            .Append(" changed-files=").Append(item.Derived.ChangedFiles)
            .Append(" passed-gates=").Append(item.Derived.PassedGates)
            .Append(" failed-gates=").Append(item.Derived.FailedGates)
            .Append(" missing-required-gates=").Append(item.Derived.MissingRequiredGates)
            .AppendLine();
        output.Append("[interpretation] status=").Append(item.Interpretation.Status)
            .Append(" summary=").AppendLine(item.Interpretation.Summary);
        AppendDiagnostics(output, item.Diagnostics.Concat(result.Diagnostics).Distinct());
        return output.ToString();
    }

    public static string ContextToText(DurableContextResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var output = new StringBuilder();
        if (result.Execution is null || result.Context is null)
        {
            AppendDiagnostics(output, result.Diagnostics);
            return output.Length == 0
                ? "No authenticated evidence matched the requested execution.\n"
                : output.ToString();
        }
        var execution = result.Execution;
        output.Append("[persisted] evidence=").Append(execution.EvidenceId.ToString("N"))
            .Append(" task=").Append(execution.TaskId)
            .Append(" authority=").Append(execution.Authority)
            .Append(" evidence-hash=").Append(execution.EvidenceHash)
            .AppendLine();
        AppendContext(output, result.Context);
        AppendDiagnostics(output, result.Diagnostics);
        return output.ToString();
    }

    private static void AppendContext(StringBuilder output, DurableContextFact context)
    {
        output.Append("[persisted] context status=").Append(context.Status)
            .Append(" id=").Append(context.Id)
            .Append(" schema=").Append(context.SchemaVersion)
            .Append(" strategy=").Append(context.Strategy)
            .Append(" strategy-version=").Append(context.StrategyVersion)
            .Append(" hash=").Append(context.ManifestHash)
            .Append(" model=").Append(context.Model)
            .Append(" tokenizer=").Append(context.Tokenizer)
            .Append(" tokens=").Append(context.EstimatedTokens).Append('/')
            .Append(context.MaximumTokens)
            .AppendLine();
        foreach (var file in context.Files)
        {
            output.Append("[persisted] context-file path=").Append(file.Path)
                .Append(" sha256=").Append(file.Sha256)
                .Append(" included-sha256=").Append(file.IncludedSha256)
                .Append(" tokens=").Append(file.IncludedTokens)
                .Append(" rank=").Append(file.Rank)
                .Append(" truncated=").Append(file.Truncated)
                .Append(" reasons=").Append(string.Join(',', file.Reasons))
                .AppendLine();
        }
    }

    private static void AppendDiagnostics(StringBuilder output, IEnumerable<string> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
            output.Append("WARNING: ").AppendLine(diagnostic);
    }
}
