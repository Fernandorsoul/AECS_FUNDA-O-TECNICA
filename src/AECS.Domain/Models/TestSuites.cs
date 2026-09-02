using System.Text.Json.Serialization;

namespace AECS.Domain.Models;

public static class TestSuiteSchema
{
    public const string ProfileVersion = "aecs.test-suites/v1";
    public const string EvidenceVersion = "aecs.test-suite-evidence/v1";
}

public enum TestGateMode
{
    Required,
    Optional,
    Disabled
}

public enum TestSuiteCategory
{
    Unit,
    Integration,
    Acceptance
}

public sealed class TestSuiteMatrix
{
    public string Version { get; init; } = TestSuiteSchema.ProfileVersion;
    public TestSuiteCommandProfile Unit { get; init; } = new();
    public TestSuiteCommandProfile Integration { get; init; } = new();
    public TestSuiteCommandProfile Acceptance { get; init; } = new();

    [JsonIgnore]
    public IEnumerable<ConfiguredTestSuite> EnabledSuites => Enumerate()
        .Where(suite => suite.Profile.Mode != TestGateMode.Disabled);

    [JsonIgnore]
    public IEnumerable<ConfiguredTestSuite> RequiredSuites => Enumerate()
        .Where(suite => suite.Profile.Mode == TestGateMode.Required);

    public TestSuiteCommandProfile Get(TestSuiteCategory category) => category switch
    {
        TestSuiteCategory.Unit => Unit,
        TestSuiteCategory.Integration => Integration,
        TestSuiteCategory.Acceptance => Acceptance,
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    private IEnumerable<ConfiguredTestSuite> Enumerate()
    {
        yield return new ConfiguredTestSuite(TestSuiteCategory.Unit, Unit);
        yield return new ConfiguredTestSuite(TestSuiteCategory.Integration, Integration);
        yield return new ConfiguredTestSuite(TestSuiteCategory.Acceptance, Acceptance);
    }
}

public sealed class TestSuiteCommandProfile
{
    public TestGateMode Mode { get; init; } = TestGateMode.Disabled;
    public string Target { get; init; } = string.Empty;
    public List<string> Arguments { get; init; } = [];
    public int TimeoutSeconds { get; init; } = 120;
}

public sealed record ConfiguredTestSuite(
    TestSuiteCategory Category,
    TestSuiteCommandProfile Profile);

public sealed class TestSuiteEvidence
{
    public string SchemaVersion { get; init; } = TestSuiteSchema.EvidenceVersion;
    public string ProfileVersion { get; init; } = TestSuiteSchema.ProfileVersion;
    public TestSuiteCategory Category { get; init; }
    public TestGateMode Mode { get; init; }
    public string Target { get; init; } = string.Empty;
    public List<string> Arguments { get; init; } = [];
    public bool DiscoveryCompleted { get; init; }
    public int Discovered { get; init; }
    public int Executed { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public Guid CommandEvidenceId { get; init; }
}
