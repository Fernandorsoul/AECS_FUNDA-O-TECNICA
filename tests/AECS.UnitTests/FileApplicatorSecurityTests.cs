using AECS.Application;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class FileApplicatorSecurityTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _workspace;
    private readonly FileApplicator _applicator = new();

    public FileApplicatorSecurityTests()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            $"aecs-file-applicator-test-{Guid.NewGuid():N}");
        _workspace = Path.Combine(_testRoot, "workspace");
        Directory.CreateDirectory(_workspace);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("../../foo")]
    [InlineData("C:\\Windows\\system32\\drivers\\etc\\hosts")]
    [InlineData("/etc/passwd")]
    [InlineData(".git/config")]
    [InlineData("src/Allowed/../../../outside")]
    public void ApplyChanges_AdversarialPath_IsRejectedWithoutWriting(string path)
    {
        var result = _applicator.ApplyChanges(Response(path), _workspace);

        result.Success.Should().BeFalse();
        result.AppliedChanges.Should().BeEmpty();
        Directory.EnumerateFiles(_testRoot, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public void ApplyChanges_OneUnsafePath_MakesWholeApplicationAtomic()
    {
        var response = Response("src/allowed.txt") + Environment.NewLine + Response("../outside.txt");

        var result = _applicator.ApplyChanges(response, _workspace);

        result.Success.Should().BeFalse();
        File.Exists(Path.Combine(_workspace, "src", "allowed.txt")).Should().BeFalse();
    }

    [Fact]
    public void ApplyChanges_NormalRelativePath_WritesInsideWorkspace()
    {
        var result = _applicator.ApplyChanges(Response("src/allowed.txt"), _workspace);

        result.Success.Should().BeTrue();
        result.AppliedChanges.Should().ContainSingle()
            .Which.FilePath.Should().Be("src/allowed.txt");
        File.ReadAllText(Path.Combine(_workspace, "src", "allowed.txt"))
            .Should().Be("candidate");
    }

    [Fact]
    public void ApplyChanges_MarkdownHeaderWithFilePrefix_CapturesOnlyThePath()
    {
        var response = "### FILE: src/allowed.txt\n```text\ncandidate\n```";

        var result = _applicator.ApplyChanges(response, _workspace);

        result.Success.Should().BeTrue();
        result.AppliedChanges.Should().ContainSingle()
            .Which.FilePath.Should().Be("src/allowed.txt");
        File.ReadAllText(Path.Combine(_workspace, "src", "allowed.txt"))
            .Should().Be("candidate");
    }

    [Fact]
    public void ApplyChanges_MarkdownHeaderWithoutFilePrefix_WritesPath()
    {
        var response = "### src/allowed.txt\n```text\ncandidate\n```";

        var result = _applicator.ApplyChanges(response, _workspace);

        result.Success.Should().BeTrue();
        result.AppliedChanges.Should().ContainSingle()
            .Which.FilePath.Should().Be("src/allowed.txt");
    }

    [Fact]
    public void ApplyChanges_MarkdownHeaderWithFilePrefix_RejectsAdversarialPath()
    {
        var response = "### FILE: ../outside.txt\n```text\ncandidate\n```";

        var result = _applicator.ApplyChanges(response, _workspace);

        result.Success.Should().BeFalse();
        result.AppliedChanges.Should().BeEmpty();
    }

    [Fact]
    public void ApplyChanges_CommentFirstLineInFence_WritesFile()
    {
        var response = "```csharp\n// result.txt\nfactorial-ok\n```";

        var result = _applicator.ApplyChanges(response, _workspace);

        result.Success.Should().BeTrue();
        result.AppliedChanges.Should().ContainSingle()
            .Which.FilePath.Should().Be("result.txt");
        File.ReadAllText(Path.Combine(_workspace, "result.txt"))
            .Should().Be("factorial-ok");
    }

    [Fact]
    public void ApplyChanges_CommentFirstLineWithTraversal_IsRejected()
    {
        var response = "```csharp\n// ../outside.txt\ncandidate\n```";

        var result = _applicator.ApplyChanges(response, _workspace);

        result.Success.Should().BeFalse();
        result.AppliedChanges.Should().BeEmpty();
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(_testRoot);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
            Directory.Delete(resolved, recursive: true);
    }

    private static string Response(string path) =>
        $"FILE: {path}\n```text\ncandidate\n```";
}
