using System.Text;
using AECS.Domain.Models;

namespace AECS.Application.ProactiveAlerts;

public static class ProactiveAlertFormatter
{
    public static string EvaluationToText(ProactiveAlertEvaluationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var output = new StringBuilder()
            .AppendLine("AECS PROACTIVE ALERT EVALUATION")
            .Append("Policy enabled: ").AppendLine(report.PolicyEnabled.ToString())
            .Append("Evidence evaluated: ").AppendLine(report.EvidenceRecordsEvaluated.ToString())
            .Append("Candidates: ").AppendLine(report.CandidatesDetected.ToString())
            .Append("Created/delivered: ").Append(report.AlertsCreated).Append('/')
            .AppendLine(report.AlertsDelivered.ToString())
            .Append("Deduplicated/escalated: ").Append(report.AlertsDeduplicated).Append('/')
            .AppendLine(report.AlertsEscalated.ToString())
            .Append("Delivery failures/expired: ").Append(report.DeliveryFailures).Append('/')
            .AppendLine(report.AlertsExpired.ToString());
        foreach (var diagnostic in report.Diagnostics)
            output.Append("WARNING: ").AppendLine(diagnostic);
        return output.ToString();
    }

    public static string AlertsToText(IReadOnlyList<ProactiveAlert> alerts)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        if (alerts.Count == 0)
            return "No proactive alerts are recorded for this repository.\n";
        var output = new StringBuilder();
        foreach (var alert in alerts)
        {
            output.Append(alert.Id.ToString("N")).Append(' ')
                .Append(alert.Severity).Append(' ')
                .Append(alert.Kind).Append(' ')
                .Append(alert.Status).Append(" task=")
                .Append(alert.TaskId).Append(" deadline=")
                .Append(alert.DeadlineAt.ToString("O")).Append(" evidence=")
                .AppendLine(alert.LatestEvidenceId.ToString("N"));
        }
        return output.ToString();
    }

    public static string EffectivenessToText(ProactiveAlertEffectivenessReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new StringBuilder()
            .AppendLine("AECS ALERT EFFECTIVENESS")
            .Append("Policy: ").AppendLine(report.PolicyId)
            .Append("Delivered/actioned/ignored: ").Append(report.DeliveredAlerts).Append('/')
            .Append(report.ActionedAlerts).Append('/').AppendLine(report.IgnoredAlerts.ToString())
            .Append("Action rate: ").AppendLine(report.ActionRate.ToString("P2"))
            .Append("Ignored rate: ").AppendLine(report.IgnoredRate.ToString("P2"))
            .Append("Death criterion: ").AppendLine(report.Decision.ToString())
            .Append("Reason: ").AppendLine(report.Reason)
            .ToString();
    }
}
