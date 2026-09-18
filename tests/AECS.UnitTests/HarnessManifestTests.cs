using AECS.Application.ContextCompiler;
using AECS.Application.Experiments;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public class HarnessManifestTests
{
    private static ExperimentVariantDefinition Variant(
        string id,
        string contextStrategy,
        int maxTokens = 12_000,
        int dependencyDepth = 2) => new()
    {
        Id = id,
        Provider = ExperimentProvider.Mock,
        Model = "mock",
        ContextStrategy = contextStrategy,
        Context = new ContextCompilationOptions
        {
            MaxTokens = maxTokens,
            DependencyDepth = dependencyDepth
        }
    };

    private static HarnessModuleConfig Module(string name) =>
        new() { Module = name, Strategy = "baseline" };

    private static HarnessManifest Draft(
        string variantId = "control",
        string? completionStrategy = "required-verifiers",
        string? toolUsePolicy = HarnessManifestContract.ImmutableToolUsePolicy,
        string contextStrategy = "graph-ranked") => new()
    {
        VariantId = variantId,
        Modules =
        [
            new HarnessModuleConfig { Module = "agent-loop", Strategy = "baseline" },
            new HarnessModuleConfig { Module = "context", Strategy = contextStrategy },
            new HarnessModuleConfig { Module = "observation", Strategy = "baseline" },
            new HarnessModuleConfig
            {
                Module = "tool-use",
                Strategy = "preauthorized",
                PolicyRef = toolUsePolicy
            },
            new HarnessModuleConfig { Module = "completion", Strategy = completionStrategy! }
        ]
    };

    [Fact]
    public void Seal_SameContent_ProducesStableHash()
    {
        var first = HarnessManifestContract.Seal(Draft());
        var second = HarnessManifestContract.Seal(Draft());

        second.ManifestHash.Should().Be(first.ManifestHash);
        first.ManifestHash.Should().StartWith("sha256:");
        HarnessManifestContract.Validate(first);
    }

    [Fact]
    public void ForVariant_GraphRankedAndNaive_ProduceDistinctReproducibleHashes()
    {
        var control = HarnessVariantBinding.ForVariant(Variant("control", "naive-path-order"));
        var candidate = HarnessVariantBinding.ForVariant(Variant("candidate", "graph-ranked"));
        var controlAgain = HarnessVariantBinding.ForVariant(Variant("control", "naive-path-order"));

        candidate.ManifestHash.Should().NotBe(control.ManifestHash);
        controlAgain.ManifestHash.Should().Be(control.ManifestHash);
        control.Modules.Should().ContainSingle(module => module.Module == "context")
            .Which.Strategy.Should().Be("naive-path-order");
        candidate.Modules.Should().ContainSingle(module => module.Module == "context")
            .Which.Strategy.Should().Be("graph-ranked");
    }

    [Fact]
    public void ForVariant_BindsAllFiveModulesToImmutableSecurityBaseline()
    {
        var manifest = HarnessVariantBinding.ForVariant(Variant("any", "graph-ranked"));

        manifest.Modules.Select(module => module.Module).Should().BeEquivalentTo(
            HarnessManifestContract.RequiredModules);
        manifest.SecurityBaseline.Should().Be(
            HarnessManifestContract.ImmutableGatesVersion);
        manifest.Modules.Should().ContainSingle(module => module.Module == "tool-use")
            .Which.PolicyRef.Should().Be(HarnessManifestContract.ImmutableToolUsePolicy);
        manifest.Modules.Should().ContainSingle(module => module.Module == "completion")
            .Which.Strategy.Should().Be("required-verifiers");
    }

    [Fact]
    public void Validate_RejectsCompletionStrategyOtherThanRequiredVerifiers()
    {
        var act = () => HarnessManifestContract.Seal(
            Draft(completionStrategy: "agent-self-report"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*non-evolvable*");
    }

    [Fact]
    public void Validate_RejectsToolUseWithoutImmutablePolicy()
    {
        var act = () => HarnessManifestContract.Seal(
            Draft(toolUsePolicy: "granted-unrestricted"));

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("self-modifying")]
    [InlineData("LLM-completion-detect")]
    public void Validate_RejectsUnknownOrForbiddenContextStrategies(string strategy)
    {
        var act = () => HarnessManifestContract.Seal(Draft(contextStrategy: strategy));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Validate_RejectsAgentLoopStrategyOtherThanBaseline()
    {
        var manifest = Draft();
        var module = manifest.Modules.Single(m => m.Module == "agent-loop");
        manifest.Modules[manifest.Modules.IndexOf(module)] = new HarnessModuleConfig
        {
            Module = "agent-loop",
            Strategy = "autonomous-retry-forever"
        };

        var act = () => HarnessManifestContract.Seal(manifest);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Validate_RejectsUnknownModuleName()
    {
        var manifest = Draft();
        manifest.Modules[manifest.Modules.FindIndex(m => m.Module == "observation")] =
            new HarnessModuleConfig { Module = "self-evaluation", Strategy = "baseline" };

        var act = () => HarnessManifestContract.Seal(manifest);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Validate_RejectsUnknownContextParameter()
    {
        var manifest = Draft();
        manifest.Modules[manifest.Modules.FindIndex(m => m.Module == "context")] =
            new HarnessModuleConfig
            {
                Module = "context",
                Strategy = "graph-ranked",
                Parameters = new Dictionary<string, string>
                {
                    ["skip_verifiers"] = "true"
                }
            };

        var act = () => HarnessManifestContract.Seal(manifest);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*non-evolvable*");
    }

    [Fact]
    public void Validate_RejectsTamperedManifestHash()
    {
        var sealedManifest = HarnessManifestContract.Seal(Draft());
        var tampered = new HarnessManifest
        {
            SchemaVersion = sealedManifest.SchemaVersion,
            VariantId = sealedManifest.VariantId,
            SecurityBaseline = sealedManifest.SecurityBaseline,
            Modules = sealedManifest.Modules,
            ManifestHash = "sha256:" + new string('0', 64)
        };

        var act = () => HarnessManifestContract.Validate(tampered);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*hash*");
    }

    [Fact]
    public void ImmutableGates_IncludeTrustBoundaryAndConstraintLedger()
    {
        HarnessManifestContract.ImmutableGates.Should().Contain(
        [
            "AgentSuccess",
            "Scope",
            "Budget",
            "ConstraintLedger"
        ]);
        HarnessManifestContract.ImmutableGates.Should().NotContain("EB002-Pattern",
            "probabilistic linters are advisory, not immutable gates");
    }
}
