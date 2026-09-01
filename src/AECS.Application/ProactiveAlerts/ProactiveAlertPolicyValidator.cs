using AECS.Domain.Models;

namespace AECS.Application.ProactiveAlerts;

public static class ProactiveAlertPolicyValidator
{
    public static void Validate(ProactiveAlertPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!string.Equals(
                policy.SchemaVersion,
                ProactiveAlertSchema.PolicyVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Alert policy must use schema '{ProactiveAlertSchema.PolicyVersion}'.");
        }
        if (string.IsNullOrWhiteSpace(policy.Id) || policy.Id.Length > 128 ||
            policy.Id.Any(char.IsControl))
        {
            throw new InvalidOperationException("Alert policy id must contain 1-128 printable characters.");
        }
        if (policy.Enabled &&
            (string.IsNullOrWhiteSpace(policy.Recipient) || policy.Recipient.Length > 256 ||
             policy.Recipient.Any(char.IsControl)))
        {
            throw new InvalidOperationException(
                "An enabled alert policy requires a printable recipient with at most 256 characters.");
        }
        if (policy.CooldownMinutes is < 1 or > 10_080)
            throw new InvalidOperationException("Alert cooldown must be between 1 minute and 7 days.");
        if (policy.LookbackHours is < 1 or > 8_760)
            throw new InvalidOperationException("Alert lookback must be between 1 hour and 1 year.");
        if (policy.RepeatedFailureThreshold is < 2 or > 20)
            throw new InvalidOperationException("Repeated failure threshold must be between 2 and 20.");
        if (policy.DefaultDeadlineMinutes is < 1 or > 10_080)
            throw new InvalidOperationException("Alert deadline must be between 1 minute and 7 days.");
        if (policy.Events is null || policy.Events.Count == 0 ||
            policy.Events.Distinct().Count() != policy.Events.Count ||
            policy.Events.Any(item => !Enum.IsDefined(item)))
        {
            throw new InvalidOperationException("Alert events must be a non-empty set of known event kinds.");
        }
        if (policy.SeverityOverrides is null || policy.SeverityOverrides.Any(item =>
                !Enum.IsDefined(item.Key) || !Enum.IsDefined(item.Value)))
        {
            throw new InvalidOperationException("Alert severity overrides contain an unknown value.");
        }
        if (policy.DeadlineMinutes is null || policy.DeadlineMinutes.Any(item =>
                !Enum.IsDefined(item.Key) || item.Value is < 1 or > 10_080))
        {
            throw new InvalidOperationException(
                "Per-event alert deadlines must be between 1 minute and 7 days.");
        }
        if (policy.ChannelAuthorization is null || policy.DeathCriteria is null)
            throw new InvalidOperationException("Alert channel authorization and death criteria are required.");
        if (policy.DeathCriteria.MinimumDeliveredAlerts < 1 ||
            policy.DeathCriteria.MinimumActionRate is < 0 or > 1 ||
            policy.DeathCriteria.MaximumIgnoredRate is < 0 or > 1)
        {
            throw new InvalidOperationException("Alert death criteria are outside their valid ranges.");
        }
    }
}
