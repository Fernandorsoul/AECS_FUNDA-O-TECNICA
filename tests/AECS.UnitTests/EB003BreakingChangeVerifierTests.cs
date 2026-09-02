using AECS.Application.SemanticLinter;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class EB003BreakingChangeVerifierTests
{
    [Fact]
    public void RemovingOneOverload_IsDetectedByDocumentationIdentity()
    {
        var type = PublicType();
        var integer = Method("int", "M:Api.Service.Get(System.Int32)", "Api.Service.Get(int)");
        var text = Method("string", "M:Api.Service.Get(System.String)", "Api.Service.Get(string)");
        var input = SemanticGraphFixture.Input(
            [type, integer, text],
            [type, integer]);

        var result = new EB003BreakingChangeVerifier().Verify(input);

        result.BreakingChanges.Should().ContainSingle(finding =>
            finding.RuleId == "EB003-REMOVED-MEMBER" &&
            finding.Symbol == "Api.Service.Get(string)");
    }

    [Fact]
    public void MovingPublicApiWithoutSemanticChange_IsNotBreaking()
    {
        var baselineType = PublicType(file: "src/App/Changed.cs");
        var candidateType = PublicType(file: "src/App/Moved.cs");
        var baselineMethod = Method(
            "method", "M:Api.Service.Get", "Api.Service.Get()", "src/App/Changed.cs");
        var candidateMethod = Method(
            "method", "M:Api.Service.Get", "Api.Service.Get()", "src/App/Moved.cs");
        var input = SemanticGraphFixture.Input(
            [baselineType, baselineMethod],
            [candidateType, candidateMethod],
            changedFiles: ["src/App/Changed.cs", "src/App/Moved.cs"]);

        new EB003BreakingChangeVerifier().Verify(input).BreakingChanges.Should().BeEmpty();
    }

    [Fact]
    public void NullableContractChange_IsBreaking()
    {
        var type = PublicType();
        var baseline = Method(
            "method", "M:Api.Service.Find(System.String)", "string? Api.Service.Find(string?)");
        var candidate = Method(
            "method", "M:Api.Service.Find(System.String)", "string Api.Service.Find(string)");
        var input = SemanticGraphFixture.Input(
            [type, baseline],
            [type, candidate]);

        new EB003BreakingChangeVerifier().Verify(input).BreakingChanges.Should()
            .ContainSingle(finding => finding.RuleId == "EB003-CHANGED-SIGNATURE");
    }

    [Fact]
    public void GenericConstraintChange_IsBreaking()
    {
        var type = PublicType();
        var baseline = Method(
            "method", "M:Api.Service.Map``1(``0)", "T Api.Service.Map<T>(T)",
            modifiers: ["constraint:T:class"]);
        var candidate = Method(
            "method", "M:Api.Service.Map``1(``0)", "T Api.Service.Map<T>(T)",
            modifiers: ["constraint:T:notnull"]);
        var input = SemanticGraphFixture.Input(
            [type, baseline],
            [type, candidate]);

        new EB003BreakingChangeVerifier().Verify(input).BreakingChanges.Should()
            .ContainSingle(finding => finding.Justification.Contains("constraint:T:notnull"));
    }

    [Fact]
    public void RemovedPrivateMember_IsIgnored()
    {
        var type = PublicType();
        var privateMethod = SemanticGraphFixture.Node(
            "private", "member", "Internal", "Api.Service.Internal()",
            containing: type.Id, accessibility: "Private", memberKind: "Method",
            documentationId: "M:Api.Service.Internal");
        var input = SemanticGraphFixture.Input(
            [type, privateMethod],
            [type]);

        new EB003BreakingChangeVerifier().Verify(input).BreakingChanges.Should().BeEmpty();
    }

    private static AECS.Domain.Models.CSharpSymbolGraphNode PublicType(
        string file = "src/App/Changed.cs") => SemanticGraphFixture.Node(
            "type", "type", "Service", "Api.Service", file: file,
            typeKind: "Class", documentationId: "T:Api.Service");

    private static AECS.Domain.Models.CSharpSymbolGraphNode Method(
        string id,
        string documentationId,
        string display,
        string file = "src/App/Changed.cs",
        IEnumerable<string>? modifiers = null) => SemanticGraphFixture.Node(
            id, "member", "Get", display, file: file, containing: "type",
            memberKind: "Method", documentationId: documentationId, modifiers: modifiers);
}
