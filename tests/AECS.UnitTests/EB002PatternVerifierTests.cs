using AECS.Application.SemanticLinter;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class EB002PatternVerifierTests
{
    [Fact]
    public void NewServiceWithoutResolvedInterface_IsReported()
    {
        var ns = Namespace("app-ns", "AECS.Application.Services");
        var service = Type("service", "OrderService", "AECS.Application.Services.OrderService", ns.Id);
        var input = SemanticGraphFixture.Input(
            [ns],
            [ns, service],
            changedFiles: ["src/App/Changed.cs"]);

        new EB002PatternVerifier().Verify(input).Violations.Should()
            .ContainSingle(finding => finding.RuleId == "EB002-SERVICE-INTERFACE");
    }

    [Fact]
    public void ServiceWithActualImplementsEdge_Passes()
    {
        var ns = Namespace("app-ns", "AECS.Application.Services");
        var service = Type("service", "OrderService", "AECS.Application.Services.OrderService", ns.Id);
        var contract = SemanticGraphFixture.Node(
            "contract", "type", "IOrderService", "AECS.Application.Services.IOrderService",
            containing: ns.Id, typeKind: "Interface",
            documentationId: "T:AECS.Application.Services.IOrderService");
        var input = SemanticGraphFixture.Input(
            [ns],
            [ns, service, contract],
            candidateEdges: [SemanticGraphFixture.Edge("implements", service.Id, contract.Id)],
            changedFiles: ["src/App/Changed.cs"]);

        new EB002PatternVerifier().Verify(input).Violations.Should().BeEmpty();
    }

    [Fact]
    public void NewlyAsyncMethodWithoutSuffix_IsReported()
    {
        var type = Type("service", "OrderService", "OrderService", "");
        var baseline = SemanticGraphFixture.Node(
            "method", "member", "Load", "OrderService.Load()",
            containing: type.Id, memberKind: "Method", modifiers: []);
        var candidate = SemanticGraphFixture.Node(
            "method", "member", "Load", "OrderService.Load()",
            containing: type.Id, memberKind: "Method", modifiers: ["async"]);
        var input = SemanticGraphFixture.Input(
            [type, baseline],
            [type, candidate]);

        new EB002PatternVerifier().Verify(input).Violations.Should()
            .ContainSingle(finding => finding.RuleId == "EB002-ASYNC-NAMING");
    }

    [Fact]
    public void ControllerConstructionUsesResolvedConstructsEdge()
    {
        var ns = Namespace("controller-ns", "AECS.Api.Controllers");
        var controller = Type("controller", "OrdersController", "AECS.Api.Controllers.OrdersController", ns.Id);
        var action = SemanticGraphFixture.Node(
            "action", "member", "Get", "OrdersController.Get()",
            containing: controller.Id, memberKind: "Method");
        var service = SemanticGraphFixture.Node(
            "service", "type", "OrderService", "OrderService",
            file: null, typeKind: "Class", external: true);
        var input = SemanticGraphFixture.Input(
            [ns, controller, action, service],
            [ns, controller, action, service],
            candidateEdges: [SemanticGraphFixture.Edge("constructs", action.Id, service.Id)]);

        new EB002PatternVerifier().Verify(input).Violations.Should()
            .ContainSingle(finding =>
                finding.RuleId == "EB002-CONTROLLER-DI" &&
                finding.Severity == RuleSeverity.Error);
    }

    [Fact]
    public void ServiceTextWithoutConstructionEdge_DoesNotTriggerControllerRule()
    {
        var ns = Namespace("controller-ns", "AECS.Api.Controllers");
        var controller = Type("controller", "OrdersController", "AECS.Api.Controllers.OrdersController", ns.Id);
        var action = SemanticGraphFixture.Node(
            "action", "member", "GetOrderServiceText", "OrdersController.GetOrderServiceText()",
            containing: controller.Id, memberKind: "Method");
        var input = SemanticGraphFixture.Input(
            [ns, controller, action],
            [ns, controller, action]);

        new EB002PatternVerifier().Verify(input).Violations.Should()
            .NotContain(finding => finding.RuleId == "EB002-CONTROLLER-DI");
    }

    private static AECS.Domain.Models.CSharpSymbolGraphNode Namespace(string id, string name) =>
        SemanticGraphFixture.Node(id, "namespace", name.Split('.').Last(), name, file: null);

    private static AECS.Domain.Models.CSharpSymbolGraphNode Type(
        string id,
        string name,
        string display,
        string containing) => SemanticGraphFixture.Node(
            id,
            "type",
            name,
            display,
            containing: containing,
            typeKind: "Class",
            documentationId: $"T:{display}");
}
