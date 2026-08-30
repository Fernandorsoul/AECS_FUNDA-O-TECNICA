using AECS.Application.Experiments;
using AECS.Domain.Enums;

namespace AECS.Application.Experiments;

public static class ExperimentReportFormatter
{
    public static string Format(ExperimentReport report)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("AECS EXPERIMENT REPORT");
        sb.AppendLine();
        sb.AppendLine($"Tasks executed: {report.TotalTasks}");
        sb.AppendLine($"Verified: {report.VerifiedCount}");
        sb.AppendLine($"Rejected: {report.RejectedCount}");

        if (report.HumanReviewCount > 0)
            sb.AppendLine($"Human review: {report.HumanReviewCount}");

        sb.AppendLine();

        // Task details
        foreach (var result in report.Results)
        {
            var decision = result.Decision switch
            {
                TaskDecision.Verified => "VERIFIED",
                TaskDecision.Rejected => "REJECTED",
                TaskDecision.HumanReviewRequired => "HUMAN_REVIEW",
                _ => "UNKNOWN"
            };

            var rejectionReason = "";
            if (result.Decision == TaskDecision.Rejected)
            {
                var failedVerifiers = result.Verifications
                    .Where(v => v.Value == VerificationStatus.Fail)
                    .Select(v => v.Key);
                rejectionReason = $"  ({string.Join(", ", failedVerifiers)})";
            }

            var objective = result.Objective.Length > 35
                ? result.Objective[..32] + "..."
                : result.Objective;

            sb.AppendLine($"{result.TaskId,-10} {objective,-35} {result.Risk,-4} {decision,-10} {result.Duration.TotalSeconds,5:F1}s  ${result.EstimatedCost:F2}  {result.FilesChanged} file(s){rejectionReason}");
        }

        sb.AppendLine();

        // Summary
        sb.AppendLine("SUMMARY");
        sb.AppendLine($"Total duration: {report.TotalDuration.TotalSeconds:F1}s");
        sb.AppendLine($"Total estimated cost: ${report.TotalCost:F2}");
        sb.AppendLine($"First-pass verification rate: {report.FirstPassRate:F0}% ({report.VerifiedCount}/{report.TotalTasks})");
        sb.AppendLine($"Average files changed: {report.AverageFilesChanged:F1}");
        sb.AppendLine($"CPVC (Cost per Verified Change): ${report.Cpvc:F2}");

        return sb.ToString();
    }
}
