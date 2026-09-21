namespace AECS.Domain.Models;

public static class TrajectoryEvidenceSchema
{
    public const string Version = "aecs.trajectory/v1";
}

/// <summary>
/// Signals captured for the execution posture of a run (plan §4.3.7): the
/// runtime that actually staged the work and the network exposure the control
/// plane granted per capability phase. Result-only checks are insufficient to
/// prove process invariants across the whole trajectory — this evidence binds
/// the declared/enforced posture to the run so ProcessInvariant constraints
/// (e.g. Trajectory.NoNetwork) can be assessed instead of always pending.
/// </summary>
public sealed class TrajectoryEvidence
{
    public string SchemaVersion { get; init; } = TrajectoryEvidenceSchema.Version;

    /// <summary>Effective runtime: "host" or "docker".</summary>
    public string Runtime { get; init; } = string.Empty;

    /// <summary>Capability phases for which the policy granted network access.</summary>
    public List<string> NetworkAllowedPhases { get; init; } = [];

    /// <summary>Network destinations the policy allowed (empty = none).</summary>
    public List<string> NetworkDestinations { get; init; } = [];

    public DateTime CapturedAt { get; init; } = DateTime.UtcNow;

    public static TrajectoryEvidence Capture(TaskContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var network = contract.Execution.EffectiveCapabilities.Network;
        return new TrajectoryEvidence
        {
            Runtime = contract.Execution.EffectiveRuntime,
            NetworkAllowedPhases = network.Phases.ToList(),
            NetworkDestinations = network.Destinations.ToList(),
            CapturedAt = DateTime.UtcNow
        };
    }
}
