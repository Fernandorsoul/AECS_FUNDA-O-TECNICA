using System.Text.RegularExpressions;

namespace AECS.Application;

public class FileChange
{
    public string FilePath { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public bool IsNewFile { get; init; }
}

public class FileApplicatorResult
{
    public bool Success { get; init; }
    public List<FileChange> AppliedChanges { get; init; } = [];
    public List<string> Errors { get; init; } = [];
}

public class FileApplicator
{
    private static readonly Regex FileBlockRegex = new(
        @"FILE:\s*(.+?)\s*\n```(?:\w*)\n(.*?)```",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public List<FileChange> ParseChanges(string modelResponse, string workspacePath)
    {
        var changes = new List<FileChange>();

        if (string.IsNullOrEmpty(modelResponse))
            return changes;

        var matches = FileBlockRegex.Matches(modelResponse);

        var normalizedWorkspace = workspacePath.Replace('\\', '/').TrimEnd('/');
        if (normalizedWorkspace.StartsWith("./"))
            normalizedWorkspace = normalizedWorkspace[2..];

        foreach (Match match in matches)
        {
            var relativePath = match.Groups[1].Value.Trim();
            var content = match.Groups[2].Value.Trim();

            if (string.IsNullOrEmpty(relativePath) || string.IsNullOrEmpty(content))
                continue;

            // Normalize path separators
            relativePath = relativePath.Replace('\\', '/');

            // Strip workspace prefix if model included it in the path
            if (relativePath.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
                relativePath = relativePath[normalizedWorkspace.Length..].TrimStart('/');

            // Resolve full path
            var fullPath = Path.Combine(workspacePath, relativePath);
            var isNew = !File.Exists(fullPath);

            changes.Add(new FileChange
            {
                FilePath = relativePath,
                Content = content,
                IsNewFile = isNew
            });
        }

        return changes;
    }

    public FileApplicatorResult ApplyChanges(string modelResponse, string workspacePath)
    {
        var changes = ParseChanges(modelResponse, workspacePath);
        var applied = new List<FileChange>();
        var errors = new List<string>();

        Console.WriteLine($"  [AECS] FileApplicator: parsed {changes.Count} file change(s) from model response");

        foreach (var change in changes)
        {
            try
            {
                var fullPath = Path.Combine(workspacePath, change.FilePath);

                // Create directory if it doesn't exist
                var dir = Path.GetDirectoryName(fullPath);
                if (dir != null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // Write the file
                File.WriteAllText(fullPath, change.Content);
                applied.Add(change);
                Console.WriteLine($"  [AECS] FileApplicator: wrote {change.FilePath} ({change.Content.Length} chars)");
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to write {change.FilePath}: {ex.Message}");
                Console.WriteLine($"  [AECS] FileApplicator ERROR: {ex.Message}");
            }
        }

        return new FileApplicatorResult
        {
            Success = errors.Count == 0,
            AppliedChanges = applied,
            Errors = errors
        };
    }
}
