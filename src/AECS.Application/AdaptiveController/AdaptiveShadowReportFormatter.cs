using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AECS.Domain.Models;

namespace AECS.Application.AdaptiveController;

public static class AdaptiveShadowReportFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string ToJson(AdaptiveShadowReport report) =>
        JsonSerializer.Serialize(report, JsonOptions);

    public static string ToText(AdaptiveShadowReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var output = new StringBuilder()
            .AppendLine("AECS ADAPTIVE SHADOW REPORT")
            .Append("Authenticated records: ").AppendLine(
                report.AuthenticatedRecords.ToString(CultureInfo.InvariantCulture))
            .Append("Included shadow records: ").AppendLine(
                report.IncludedShadowRecords.ToString(CultureInfo.InvariantCulture))
            .Append("Excluded records: ").AppendLine(
                report.ExcludedRecords.ToString(CultureInfo.InvariantCulture));
        foreach (var group in report.Groups)
        {
            output.Append(group.Risk).Append('/').Append(group.TaskType)
                .Append(" executions=").Append(group.Executions)
                .Append(" ready=").Append(group.ReadyRecommendations)
                .Append(" verified=").Append(group.VerifiedExecutions)
                .Append(" model-agreements=").Append(group.ModelAgreements)
                .Append(" avg-duration-seconds=")
                .Append(group.AverageDurationSeconds.ToString("F2", CultureInfo.InvariantCulture));
            if (group.AverageAccountedCostUsd.HasValue)
            {
                output.Append(" avg-accounted-cost-usd=")
                    .Append(group.AverageAccountedCostUsd.Value.ToString(
                        "F6",
                        CultureInfo.InvariantCulture));
            }
            output.AppendLine();
        }
        if (report.Groups.Count == 0)
            output.AppendLine("No authenticated adaptive shadow evidence was found.");
        foreach (var diagnostic in report.Diagnostics)
            output.Append("WARNING: ").AppendLine(diagnostic);
        return output.ToString();
    }
}
