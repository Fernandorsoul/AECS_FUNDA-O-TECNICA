using System.Text.RegularExpressions;

namespace AECS.Application.SemanticLinter;

public class MissingChange
{
    public string RuleId { get; init; } = string.Empty;
    public string RuleName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public RuleSeverity Severity { get; init; }
    public string ExpectedFile { get; init; } = string.Empty;
}

public class EB004Result
{
    public bool HasMissingChanges => MissingChanges.Count > 0;
    public List<MissingChange> MissingChanges { get; init; } = [];
    public int FilesAnalyzed { get; init; }
}

public class EB004MissingChangeVerifier
{
    private static readonly Regex ClassRegex = new(
        @"(?:public|internal)\s+(?:static\s+)?(?:partial\s+)?(?:class|interface)\s+(\w+)",
        RegexOptions.Compiled);

    public EB004Result Verify(string repoPath, string diffContent)
    {
        var missingChanges = new List<MissingChange>();
        var filesAnalyzed = 0;

        // Parse diff to find changed files
        var changedFiles = ParseChangedFiles(diffContent);

        foreach (var file in changedFiles)
        {
            filesAnalyzed++;
            var relativePath = file.Replace('\\', '/');

            // Check if source file changed but test file not changed
            if (IsSourceFile(relativePath) && !IsTestFile(relativePath))
            {
                var testFile = FindCorrespondingTestFile(repoPath, relativePath);
                if (testFile != null && !changedFiles.Contains(testFile))
                {
                    missingChanges.Add(new MissingChange
                    {
                        RuleId = "EB004-MISSING-TEST",
                        RuleName = "Missing Test Update",
                        FilePath = relativePath,
                        Detail = $"Source file '{relativePath}' was changed but corresponding test file '{testFile}' was not updated",
                        Severity = RuleSeverity.Warning,
                        ExpectedFile = testFile
                    });
                }
            }

            // Check if interface changed but implementation not changed
            if (IsInterfaceFile(relativePath))
            {
                var implFile = FindCorrespondingImplementation(repoPath, relativePath);
                if (implFile != null && !changedFiles.Contains(implFile))
                {
                    missingChanges.Add(new MissingChange
                    {
                        RuleId = "EB004-MISSING-IMPL",
                        RuleName = "Missing Implementation Update",
                        FilePath = relativePath,
                        Detail = $"Interface '{relativePath}' was changed but implementation '{implFile}' was not updated",
                        Severity = RuleSeverity.Error,
                        ExpectedFile = implFile
                    });
                }
            }

            // Check if model changed but migration not created
            if (IsModelFile(relativePath) && IsDomainModel(repoPath, relativePath))
            {
                var hasMigration = changedFiles.Any(f => f.Contains("Migration", StringComparison.OrdinalIgnoreCase));
                if (!hasMigration)
                {
                    missingChanges.Add(new MissingChange
                    {
                        RuleId = "EB004-MISSING-MIGRATION",
                        RuleName = "Missing Database Migration",
                        FilePath = relativePath,
                        Detail = $"Domain model '{relativePath}' was changed but no database migration was created",
                        Severity = RuleSeverity.Warning,
                        ExpectedFile = "Migrations/"
                    });
                }
            }
        }

        return new EB004Result
        {
            MissingChanges = missingChanges,
            FilesAnalyzed = filesAnalyzed
        };
    }

    private static List<string> ParseChangedFiles(string diff)
    {
        var files = new List<string>();

        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("+++ b/"))
            {
                var file = line[6..].Trim();
                if (!string.IsNullOrEmpty(file))
                    files.Add(file);
            }
        }

        return files;
    }

    private static bool IsSourceFile(string path)
    {
        return path.EndsWith(".cs") &&
               !path.Contains("Test", StringComparison.OrdinalIgnoreCase) &&
               !path.Contains("Migration", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTestFile(string path)
    {
        return path.Contains("Test", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInterfaceFile(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        return fileName.StartsWith('I') && fileName.Length > 1 && char.IsUpper(fileName[1]);
    }

    private static bool IsModelFile(string path)
    {
        return path.Contains("Models", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("Domain", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDomainModel(string repoPath, string path)
    {
        return path.Contains("Domain", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindCorrespondingTestFile(string repoPath, string sourceFile)
    {
        var fileName = Path.GetFileNameWithoutExtension(sourceFile);
        var testFileName = $"{fileName}Tests.cs";

        // Search in test directories
        var testDirs = new[] { "tests", "test", "Tests", "Test" };
        foreach (var dir in testDirs)
        {
            var testPath = Path.Combine(repoPath, dir);
            if (Directory.Exists(testPath))
            {
                var testFiles = Directory.GetFiles(testPath, testFileName, SearchOption.AllDirectories);
                if (testFiles.Length > 0)
                    return Path.GetRelativePath(repoPath, testFiles[0]).Replace('\\', '/');
            }
        }

        return null;
    }

    private static string? FindCorrespondingImplementation(string repoPath, string interfaceFile)
    {
        var fileName = Path.GetFileNameWithoutExtension(interfaceFile);
        if (fileName.StartsWith('I') && fileName.Length > 1)
        {
            var implName = fileName[1..]; // Remove 'I' prefix
            var implFileName = $"{implName}.cs";

            // Search in src directories
            var srcDirs = new[] { "src", "Src" };
            foreach (var dir in srcDirs)
            {
                var srcPath = Path.Combine(repoPath, dir);
                if (Directory.Exists(srcPath))
                {
                    var implFiles = Directory.GetFiles(srcPath, implFileName, SearchOption.AllDirectories);
                    if (implFiles.Length > 0)
                        return Path.GetRelativePath(repoPath, implFiles[0]).Replace('\\', '/');
                }
            }
        }

        return null;
    }
}
