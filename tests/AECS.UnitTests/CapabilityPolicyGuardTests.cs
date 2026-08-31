using AECS.Application.ControlKernel;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class CapabilityPolicyGuardTests
{
    [Fact]
    public void IdenticalAdaptivePolicy_IsAccepted()
    {
        var policy = ExecutionCapabilityPolicy.RestrictiveDefault();

        var action = () => CapabilityPolicyGuard.EnsureNoExpansion(policy, policy);

        action.Should().NotThrow();
    }

    [Fact]
    public void AdaptivePolicyCannotAddProcessNetworkSecretFilesystemOrResources()
    {
        var authoritative = ExecutionCapabilityPolicy.RestrictiveDefault();
        var expanded = new ExecutionCapabilityPolicy
        {
            FileSystem = new FileSystemCapabilities
            {
                Read = ["**"],
                Write = [.. authoritative.FileSystem.Write, "artifacts/**"]
            },
            Processes =
            [
                .. authoritative.Processes,
                new ProcessCapabilityRule
                {
                    Executable = "sh",
                    ArgumentPrefix = ["-c"],
                    Phases = [ExecutionCapabilityPhases.CandidateBuild]
                }
            ],
            Network = new NetworkCapabilities
            {
                Destinations = ["*"],
                Phases = [ExecutionCapabilityPhases.CandidateBuild]
            },
            Secrets =
            [
                new SecretCapability
                {
                    Name = "DEPLOY_TOKEN",
                    Phases = [ExecutionCapabilityPhases.CandidateBuild]
                }
            ],
            Resources = new ResourceCapabilities
            {
                CpuLimit = "2.0",
                MemoryLimit = "1g",
                ProcessLimit = 512,
                WallClockSeconds = 600
            }
        };

        var action = () => CapabilityPolicyGuard.EnsureNoExpansion(
            authoritative,
            expanded);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*attempted to expand*capabilities*");
    }
}
