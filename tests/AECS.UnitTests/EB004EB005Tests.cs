using AECS.Application.SemanticLinter;
using AECS.Application.Verification;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class EB004MissingChangeVerifierTests
{
    [Fact]
    public void ChangedContractWithUnchangedResolvedImplementation_IsReported()
    {
        var contract = Type("contract", "IService", "src/App/Changed.cs", "Interface");
        var implementation = Type("implementation", "Service", "src/App/Service.cs", "Class");
        var relation = SemanticGraphFixture.Edge("implements", implementation.Id, contract.Id);
        var input = SemanticGraphFixture.Input(
            [contract, implementation],
            [contract, implementation],
            baselineEdges: [relation],
            candidateEdges: [relation],
            changedFiles: ["src/App/Changed.cs"]);

        new EB004MissingChangeVerifier().Verify(input).MissingChanges.Should()
            .ContainSingle(finding => finding.RuleId == "EB004-MISSING-IMPLEMENTATION");
    }

    [Fact]
    public void ChangedContractAndImplementation_PassTogether()
    {
        var contract = Type("contract", "IService", "src/App/Changed.cs", "Interface");
        var implementation = Type("implementation", "Service", "src/App/Service.cs", "Class");
        var relation = SemanticGraphFixture.Edge("implements", implementation.Id, contract.Id);
        var input = SemanticGraphFixture.Input(
            [contract, implementation],
            [contract, implementation],
            baselineEdges: [relation],
            candidateEdges: [relation],
            changedFiles: ["src/App/Changed.cs", "src/App/Service.cs"]);

        new EB004MissingChangeVerifier().Verify(input).MissingChanges.Should().BeEmpty();
    }

    [Fact]
    public void UnchangedTestWithResolvedReferenceToImpact_IsReported()
    {
        var production = Type("production", "Service", "src/App/Changed.cs", "Class");
        var test = SemanticGraphFixture.Node(
            "test", "member", "ShouldWork", "ServiceTests.ShouldWork()",
            project: "tests/App.Tests/App.Tests.csproj", file: "tests/App.Tests/ServiceTests.cs",
            memberKind: "Method", documentationId: "M:ServiceTests.ShouldWork");
        var relation = SemanticGraphFixture.Edge("references", test.Id, production.Id);
        var projects = new[]
        {
            SemanticGraphFixture.Project("src/App/App.csproj"),
            SemanticGraphFixture.Project("tests/App.Tests/App.Tests.csproj", test: true)
        };
        var input = SemanticGraphFixture.Input(
            [production, test],
            [production, test],
            baselineEdges: [relation],
            candidateEdges: [relation],
            changedFiles: ["src/App/Changed.cs"],
            projects: projects);

        new EB004MissingChangeVerifier().Verify(input).MissingChanges.Should()
            .ContainSingle(finding => finding.RuleId == "EB004-MISSING-TEST");
    }

    [Fact]
    public void TestLikeFileWithoutResolvedRelation_IsNotReported()
    {
        var production = Type("production", "Service", "src/App/Changed.cs", "Class");
        var unrelated = SemanticGraphFixture.Node(
            "test", "member", "ShouldWork", "ServiceTests.ShouldWork()",
            project: "tests/App.Tests/App.Tests.csproj", file: "tests/App.Tests/ServiceTests.cs",
            memberKind: "Method");
        var input = SemanticGraphFixture.Input(
            [production, unrelated],
            [production, unrelated],
            changedFiles: ["src/App/Changed.cs"],
            projects:
            [
                SemanticGraphFixture.Project("src/App/App.csproj"),
                SemanticGraphFixture.Project("tests/App.Tests/App.Tests.csproj", test: true)
            ]);

        new EB004MissingChangeVerifier().Verify(input).MissingChanges.Should().BeEmpty();
    }

    [Fact]
    public void EntityResolvedThroughDbContextRequiresRelatedMigrationChange()
    {
        var context = Type("context", "AppDbContext", "src/App/AppDbContext.cs", "Class");
        var set = SemanticGraphFixture.Node(
            "set", "member", "Orders", "AppDbContext.Orders",
            file: "src/App/AppDbContext.cs", containing: context.Id, memberKind: "Property");
        var entity = Type("entity", "Order", "src/App/Changed.cs", "Class");
        var migration = Type("migration", "Initial", "src/App/Migrations/Initial.cs", "Class");
        var dbContext = SemanticGraphFixture.Node(
            "dbcontext", "type", "DbContext", "Microsoft.EntityFrameworkCore.DbContext",
            file: null, typeKind: "Class", external: true);
        var migrationBase = SemanticGraphFixture.Node(
            "migration-base", "type", "Migration", "Microsoft.EntityFrameworkCore.Migrations.Migration",
            file: null, typeKind: "Class", external: true);
        var edges = new[]
        {
            SemanticGraphFixture.Edge("inherits", context.Id, dbContext.Id),
            SemanticGraphFixture.Edge("references", set.Id, entity.Id),
            SemanticGraphFixture.Edge("inherits", migration.Id, migrationBase.Id)
        };
        var input = SemanticGraphFixture.Input(
            [context, set, entity, migration, dbContext, migrationBase],
            [context, set, entity, migration, dbContext, migrationBase],
            baselineEdges: edges,
            candidateEdges: edges,
            changedFiles: ["src/App/Changed.cs"]);

        new EB004MissingChangeVerifier().Verify(input).MissingChanges.Should()
            .ContainSingle(finding => finding.RuleId == "EB004-MISSING-MIGRATION");
    }

    private static AECS.Domain.Models.CSharpSymbolGraphNode Type(
        string id,
        string name,
        string file,
        string typeKind) => SemanticGraphFixture.Node(
            id, "type", name, $"App.{name}", file: file, typeKind: typeKind,
            documentationId: $"T:App.{name}");
}

public sealed class EB005HistoricalConflictVerifierTests
{
    private static readonly DateTime Now = new(2026, 8, 31, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ApprovedAdrConflict_UsesResolvedRelationAndCarriesSourceProvenance()
    {
        var agent = Member("agent", "Run", "Agent.Run()");
        var prohibited = Type("llm", "LLMOnly", file: null, external: true);
        var input = SemanticGraphFixture.Input(
            [agent],
            [agent, prohibited],
            candidateEdges: [SemanticGraphFixture.Edge("constructs", agent.Id, prohibited.Id)]);
        var decision = Decision(
            "ADR-001",
            HistoricalDecisionType.Adr,
            HistoricalPatternKind.ConstructsType,
            "LLMOnly");
        var selection = HistoricalDecisionSelector.Select(input, [decision], [], Now);

        var result = new EB005HistoricalConflictVerifier().Verify(input, selection);

        result.Conflicts.Should().ContainSingle().Which.Should().Match<DecisionConflict>(conflict =>
            conflict.Decision.Source == "docs/adr/ADR-001.md" &&
            conflict.Decision.SourceVersion == "git:abc123" &&
            conflict.SymbolId == agent.Id &&
            conflict.FilePath == "src/App/Changed.cs" &&
            conflict.RuleId == "EB005-ADR-CONFLICT");
    }

    [Fact]
    public void ApprovedIncident_CanBlockOnlyWithHumanReview()
    {
        var handler = Type("handler", "LegacyHandler");
        var input = SemanticGraphFixture.Input([handler], [handler]);
        var incident = Decision(
            "INC-042",
            HistoricalDecisionType.Incident,
            HistoricalPatternKind.TypeName,
            "LegacyHandler",
            HistoricalDecisionEnforcement.Blocking);
        var selection = HistoricalDecisionSelector.Select(input, [incident], [], Now);

        var result = new EB005HistoricalConflictVerifier().Verify(input, selection);

        result.HasBlockingConflicts.Should().BeTrue();
        incident.Review.Authority.Should().Be(HistoricalDecisionReviewAuthority.Human);
    }

    [Fact]
    public void ExpiredRule_IsNotSelected()
    {
        var handler = Type("handler", "LegacyHandler");
        var input = SemanticGraphFixture.Input([handler], [handler]);
        var expired = Decision(
            "POL-OLD",
            HistoricalDecisionType.Policy,
            HistoricalPatternKind.TypeName,
            "LegacyHandler",
            validUntil: Now.AddMinutes(-1));

        HistoricalDecisionSelector.Select(input, [expired], [], Now).Status.Should()
            .Be(HistoricalDecisionSelectionStatus.NoHistory);
    }

    [Fact]
    public void RuleOutsideImpactedProjectScope_IsNotSelected()
    {
        var handler = Type("handler", "LegacyHandler");
        var input = SemanticGraphFixture.Input([handler], [handler]);
        var decision = Decision(
            "ADR-OTHER-PROJECT",
            HistoricalDecisionType.Adr,
            HistoricalPatternKind.TypeName,
            "LegacyHandler",
            projectPath: "src/Other/Other.csproj");

        HistoricalDecisionSelector.Select(input, [decision], [], Now).Status.Should()
            .Be(HistoricalDecisionSelectionStatus.NoHistory);
    }

    [Fact]
    public void TextThatOnlyContainsPatternWithoutResolvedMatch_IsIgnored()
    {
        var node = Member("agent", "Run", "Agent.Run() // constructs LLMOnly");
        var safeTarget = Type("safe", "SafeFactory", file: null, external: true);
        var input = SemanticGraphFixture.Input(
            [node],
            [node, safeTarget],
            candidateEdges: [SemanticGraphFixture.Edge("constructs", node.Id, safeTarget.Id)]);
        var decision = Decision(
            "ADR-001",
            HistoricalDecisionType.Adr,
            HistoricalPatternKind.ConstructsType,
            "LLMOnly");
        var selection = HistoricalDecisionSelector.Select(input, [decision], [], Now);

        new EB005HistoricalConflictVerifier().Verify(input, selection)
            .Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void ActiveVersionedSuppression_RecordsButDoesNotBlockConflict()
    {
        var handler = Type("handler", "LegacyHandler");
        var input = SemanticGraphFixture.Input([handler], [handler]);
        var incident = Decision(
            "INC-042",
            HistoricalDecisionType.Incident,
            HistoricalPatternKind.TypeName,
            "LegacyHandler",
            HistoricalDecisionEnforcement.Blocking);
        var suppression = HistoricalDecisionContract.Seal(new HistoricalDecisionSuppression
        {
            Id = "SUP-1",
            Version = 2,
            DecisionId = incident.Id,
            DecisionVersion = incident.Version,
            Actor = "security-owner",
            Reason = "Temporary migration window",
            SymbolId = handler.Id,
            CreatedAt = Now.AddMinutes(-5),
            ExpiresAt = Now.AddMinutes(30)
        });
        var selection = HistoricalDecisionSelector.Select(
            input,
            [incident],
            [suppression],
            Now);

        var result = new EB005HistoricalConflictVerifier().Verify(input, selection);

        result.Conflicts.Should().BeEmpty();
        result.SuppressedConflicts.Should().ContainSingle().Which.Suppression!.Version
            .Should().Be(2);
    }

    [Fact]
    public void ContradictoryActiveRules_AreExplicitlyAmbiguous()
    {
        var handler = Type("handler", "LegacyHandler");
        var input = SemanticGraphFixture.Input([handler], [handler]);
        var prohibited = Decision(
            "ADR-1",
            HistoricalDecisionType.Adr,
            HistoricalPatternKind.TypeName,
            "LegacyHandler");
        var required = Decision(
            "POL-1",
            HistoricalDecisionType.Policy,
            HistoricalPatternKind.TypeName,
            "LegacyHandler",
            required: true);

        HistoricalDecisionSelector.Select(input, [prohibited, required], [], Now).Status
            .Should().Be(HistoricalDecisionSelectionStatus.Ambiguous);
    }

    [Fact]
    public async Task BlockingConflict_ProducesFailWithAuthenticatedProvenanceEvidence()
    {
        var handler = Type("handler", "LegacyHandler");
        var context = SemanticGraphFixture.Context([handler], [handler]);
        var input = SemanticAnalysisInput.From(context);
        var incident = Decision(
            "INC-042",
            HistoricalDecisionType.Incident,
            HistoricalPatternKind.TypeName,
            "LegacyHandler",
            HistoricalDecisionEnforcement.Blocking);
        var selection = HistoricalDecisionSelector.Select(input, [incident], [], Now);

        var result = await new EB005Verifier(selection).VerifyAsync(
            context,
            CancellationToken.None);

        result.Status.Should().Be(VerificationStatus.Fail);
        result.Severity.Should().Be(Severity.Error);
        result.Historical.Should().NotBeNull();
        result.Historical!.Conflicts.Should().ContainSingle().Which.Should()
            .Match<HistoricalConflictEvidence>(conflict =>
                conflict.DecisionId == incident.Id &&
                conflict.DecisionVersion == incident.Version &&
                conflict.SourceHash == incident.SourceHash &&
                conflict.SymbolId == handler.Id);
    }

    private static HistoricalDecision Decision(
        string id,
        HistoricalDecisionType type,
        HistoricalPatternKind patternKind,
        string patternValue,
        HistoricalDecisionEnforcement enforcement = HistoricalDecisionEnforcement.Advisory,
        DateTime? validUntil = null,
        bool required = false,
        string? projectPath = null) => HistoricalDecisionContract.Seal(new HistoricalDecision
        {
            Id = id,
            Version = 1,
            Type = type,
            Source = type == HistoricalDecisionType.Adr
                ? "docs/adr/ADR-001.md"
                : $"history/{id}.json",
            SourceVersion = "git:abc123",
            SourceHash = $"sha256:{new string('a', 64)}",
            Authority = "architecture-board",
            ValidFrom = Now.AddDays(-1),
            ValidUntil = validUntil,
            ProhibitedPatterns = required
                ? []
                : [new HistoricalDecisionPattern { Kind = patternKind, Value = patternValue }],
            RequiredPatterns = required
                ? [new HistoricalDecisionPattern { Kind = patternKind, Value = patternValue }]
                : [],
            Justification = "A reviewed historical constraint applies.",
            Enforcement = enforcement,
            ExtractedHeuristically = true,
            Scope = new HistoricalDecisionScope
            {
                ProjectPaths = projectPath is null ? [] : [projectPath]
            },
            Review = new HistoricalDecisionReview
            {
                Status = HistoricalDecisionReviewStatus.Approved,
                Authority = HistoricalDecisionReviewAuthority.Human,
                Actor = "reviewer@example.com",
                Reason = "Reviewed against the source.",
                ReviewedAt = Now.AddHours(-1)
            },
            CreatedAt = Now.AddHours(-2)
        });

    private static CSharpSymbolGraphNode Type(
        string id,
        string name,
        string? file = "src/App/Changed.cs",
        bool external = false) => SemanticGraphFixture.Node(
            id,
            "type",
            name,
            $"App.{name}",
            file: file,
            typeKind: "Class",
            documentationId: $"T:App.{name}",
            external: external);

    private static CSharpSymbolGraphNode Member(
        string id,
        string name,
        string display) => SemanticGraphFixture.Node(
            id,
            "member",
            name,
            display,
            memberKind: "Method",
            documentationId: $"M:App.Agent.{name}");
}
