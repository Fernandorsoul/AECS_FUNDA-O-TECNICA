namespace AECS.Infrastructure.AgentRuntime;

public sealed class LocalComputeCostPolicy
{
    public const string CurrentVersion = "aecs.local-compute-cost/v1";

    public string Version { get; init; } = CurrentVersion;
    public decimal PowerWatts { get; init; } = 200m;
    public decimal ElectricityUsdPerKwh { get; init; } = 0.20m;
    public decimal HardwareCostUsd { get; init; } = 600m;
    public decimal HardwareLifetimeHours { get; init; } = 10_000m;

    public decimal Estimate(TimeSpan duration)
    {
        Validate();
        var hours = Math.Max(0m, (decimal)duration.TotalHours);
        var electricityPerHour = PowerWatts / 1000m * ElectricityUsdPerKwh;
        var amortizationPerHour = HardwareCostUsd / HardwareLifetimeHours;
        return hours * (electricityPerHour + amortizationPerHour);
    }

    public void Validate()
    {
        if (Version != CurrentVersion || PowerWatts <= 0 ||
            ElectricityUsdPerKwh < 0 || HardwareCostUsd < 0 ||
            HardwareLifetimeHours <= 0)
        {
            throw new InvalidOperationException(
                "Local compute cost policy contains invalid or unsupported assumptions.");
        }
    }
}
