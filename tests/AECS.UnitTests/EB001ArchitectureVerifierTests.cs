using AECS.Application.SemanticLinter;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class EB001ArchitectureVerifierTests
{
    [Fact]
    public void NewResolvedForbiddenDependency_IsReported()
    {
        var nodes = ArchitectureNodes();
        var input = SemanticGraphFixture.Input(
            nodes,
            nodes,
            candidateEdges: [SemanticGraphFixture.Edge("references", "source", "target")],
            changedFiles: ["src/Domain/Order.cs"]);

        var result = new EB001ArchitectureVerifier().Verify(input);

        result.Violations.Should().ContainSingle(finding =>
            finding.RuleId == "EB001-DOMAIN" &&
            finding.Symbol == "AECS.Domain.Order.Save()");
    }

    [Fact]
    public void PreExistingDependency_IsNotAttributedToCandidate()
    {
        var nodes = ArchitectureNodes();
        var relation = SemanticGraphFixture.Edge("references", "source", "target");
        var input = SemanticGraphFixture.Input(
            nodes,
            nodes,
            baselineEdges: [relation],
            candidateEdges: [relation],
            changedFiles: ["src/Domain/Order.cs"]);

        new EB001ArchitectureVerifier().Verify(input).Violations.Should().BeEmpty();
    }

    [Fact]
    public void ForbiddenNameWithoutResolvedRelation_IsNotReported()
    {
        var nodes = ArchitectureNodes();
        var input = SemanticGraphFixture.Input(
            nodes,
            nodes,
            changedFiles: ["src/Domain/Order.cs"]);

        new EB001ArchitectureVerifier().Verify(input).Violations.Should().BeEmpty();
    }

    [Fact]
    public void ProjectReference_IsEvaluatedSemantically()
    {
        var application = SemanticGraphFixture.Node(
            "app-project", "project", "AECS.Application", "AECS.Application",
            project: "src/AECS.Application/AECS.Application.csproj",
            file: null);
        var infrastructure = SemanticGraphFixture.Node(
            "infra-project", "project", "AECS.Infrastructure", "AECS.Infrastructure",
            project: "src/AECS.Infrastructure/AECS.Infrastructure.csproj",
            file: null);
        var input = SemanticGraphFixture.Input(
            [application, infrastructure],
            [application, infrastructure],
            candidateEdges: [SemanticGraphFixture.Edge(
                "project-reference", "app-project", "infra-project")],
            changedFiles: ["src/AECS.Application/AECS.Application.csproj"]);

        new EB001ArchitectureVerifier().Verify(input).Violations.Should()
            .ContainSingle(finding => finding.RuleId == "EB001-APP");
    }

    private static List<AECS.Domain.Models.CSharpSymbolGraphNode> ArchitectureNodes() =>
    [
        SemanticGraphFixture.Node(
            "domain-ns", "namespace", "Domain", "AECS.Domain", file: null),
        SemanticGraphFixture.Node(
            "order", "type", "Order", "AECS.Domain.Order",
            file: "src/Domain/Order.cs", containing: "domain-ns", typeKind: "Class",
            documentationId: "T:AECS.Domain.Order"),
        SemanticGraphFixture.Node(
            "source", "member", "Save", "AECS.Domain.Order.Save()",
            file: "src/Domain/Order.cs", containing: "order", memberKind: "Method",
            documentationId: "M:AECS.Domain.Order.Save"),
        SemanticGraphFixture.Node(
            "infra-ns", "namespace", "Infrastructure", "AECS.Infrastructure", file: null,
            external: true),
        SemanticGraphFixture.Node(
            "target", "type", "Repository", "AECS.Infrastructure.Repository",
            file: null, containing: "infra-ns", typeKind: "Class", external: true,
            documentationId: "T:AECS.Infrastructure.Repository")
    ];
}
