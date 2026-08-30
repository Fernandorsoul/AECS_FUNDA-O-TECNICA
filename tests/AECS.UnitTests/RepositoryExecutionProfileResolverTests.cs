using AECS.Application.Verification;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class RepositoryExecutionProfileResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"aecs-execution-profile-{Guid.NewGuid():N}");

    public RepositoryExecutionProfileResolverTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Backend"));
        File.WriteAllText(
            Path.Combine(_root, "Backend", "Fixture.sln"),
            "fixture");
    }

    [Fact]
    public void Resolve_NestedSolution_ReturnsSafeDotNetArguments()
    {
        var result = RepositoryExecutionProfileResolver.Resolve(
            _root,
            new RepositoryExecutionProfile
            {
                WorkingDirectory = "Backend",
                Target = "Fixture.sln"
            });

        result.WorkingDirectory.Should().Be(Path.Combine(_root, "Backend"));
        result.BuildArguments.Should().Equal("build", "Fixture.sln");
        result.TestArguments.Should().Equal("test", "Fixture.sln", "--no-build");
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("Backend/../outside")]
    [InlineData(".git")]
    public void Resolve_UnsafeWorkingDirectory_FailsClosed(string workingDirectory)
    {
        var action = () => RepositoryExecutionProfileResolver.Resolve(
            _root,
            new RepositoryExecutionProfile { WorkingDirectory = workingDirectory });

        action.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("../Fixture.sln")]
    [InlineData("missing.sln")]
    [InlineData("Fixture.txt")]
    [InlineData("--configuration")]
    public void Resolve_UnsafeOrInvalidTarget_FailsClosed(string target)
    {
        var action = () => RepositoryExecutionProfileResolver.Resolve(
            _root,
            new RepositoryExecutionProfile
            {
                WorkingDirectory = "Backend",
                Target = target
            });

        action.Should().Throw<Exception>();
    }

    [Fact]
    public void Resolve_DefaultProfile_UsesWorkspaceRootWithoutTarget()
    {
        var result = RepositoryExecutionProfileResolver.Resolve(
            _root,
            new RepositoryExecutionProfile());

        result.WorkingDirectory.Should().Be(Path.GetFullPath(_root));
        result.TargetArgument.Should().BeNull();
        result.BuildArguments.Should().Equal("build");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
