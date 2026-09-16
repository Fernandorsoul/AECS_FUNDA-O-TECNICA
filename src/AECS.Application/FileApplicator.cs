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
    // Accepts both "FILE: path" and "### path" (markdown header) formats
    private static readonly Regex FileBlockRegex = new(
        @"(?:FILE:\s*|#{1,6}\s+)([^\r\n]+?)\s*\r?\n```(?:\w*)\r?\n(.*?)```",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex WindowsAbsolutePathRegex = new(
        @"^[a-zA-Z]:[\\/]",
        RegexOptions.Compiled);

    public List<FileChange> ParseChanges(string modelResponse, string workspacePath)
    {
        var changes = new List<FileChange>();

        if (string.IsNullOrEmpty(modelResponse))
            return changes;

        foreach (Match match in FileBlockRegex.Matches(modelResponse))
        {
            var suppliedPath = match.Groups[1].Value.Trim();
            var content = match.Groups[2].Value.Trim();

            if (string.IsNullOrEmpty(suppliedPath) || string.IsNullOrEmpty(content))
                continue;

            var relativePath = suppliedPath.Replace('\\', '/');

            changes.Add(new FileChange
            {
                FilePath = relativePath,
                Content = content,
                IsNewFile = false
            });
        }

        return changes;
    }

    public FileApplicatorResult ApplyChanges(string modelResponse, string workspacePath, string? pathPrefix = null)
    {
        var workspaceRoot = Path.GetFullPath(workspacePath);
        var parsedChanges = ParseChanges(modelResponse, workspaceRoot);
        var validatedChanges = new List<(FileChange Change, string FullPath)>();
        var errors = new List<string>();
        var seenPaths = new HashSet<string>(GetPathComparer());
        var normalizedPrefix = string.IsNullOrWhiteSpace(pathPrefix)
            ? null
            : pathPrefix.Trim().Replace('\\', '/').Trim('/');

        if (normalizedPrefix is ".")
            normalizedPrefix = null;

        foreach (var change in parsedChanges)
        {
            var normalizedPath = change.FilePath.Replace('\\', '/');
            var effectivePath = normalizedPrefix is null ||
                normalizedPath.StartsWith(normalizedPrefix + "/", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedPath, normalizedPrefix, StringComparison.OrdinalIgnoreCase)
                ? normalizedPath
                : $"{normalizedPrefix}/{normalizedPath}";
            if (!TryResolveSafePath(workspaceRoot, effectivePath, out var fullPath, out var error))
            {
                errors.Add(error);
                continue;
            }

            if (!seenPaths.Add(fullPath))
            {
                errors.Add($"Duplicate file target rejected: '{change.FilePath}'.");
                continue;
            }

            validatedChanges.Add((new FileChange
            {
                FilePath = Path.GetRelativePath(workspaceRoot, fullPath).Replace('\\', '/'),
                Content = change.Content,
                IsNewFile = !File.Exists(fullPath)
            }, fullPath));
        }

        // Validation is all-or-nothing. No file is written when one supplied path
        // crosses the workspace trust boundary.
        if (errors.Count > 0)
        {
            return new FileApplicatorResult
            {
                Success = false,
                Errors = errors
            };
        }

        var applied = new List<FileChange>();
        foreach (var (change, fullPath) in validatedChanges)
        {
            try
            {
                var directory = Path.GetDirectoryName(fullPath);
                if (directory is not null)
                    Directory.CreateDirectory(directory);

                File.WriteAllText(fullPath, change.Content);
                applied.Add(change);
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to write '{change.FilePath}': {ex.Message}");
                break;
            }
        }

        return new FileApplicatorResult
        {
            Success = errors.Count == 0,
            AppliedChanges = applied,
            Errors = errors
        };
    }

    public static bool TryResolveSafePath(
        string workspacePath,
        string suppliedPath,
        out string resolvedPath,
        out string error)
    {
        resolvedPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(suppliedPath))
        {
            error = "Empty file path rejected.";
            return false;
        }

        var normalizedInput = suppliedPath.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(suppliedPath) ||
            normalizedInput.StartsWith('/') ||
            normalizedInput.StartsWith("//", StringComparison.Ordinal) ||
            WindowsAbsolutePathRegex.IsMatch(normalizedInput))
        {
            error = $"Absolute file path rejected: '{suppliedPath}'.";
            return false;
        }

        var segments = normalizedInput.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            error = $"Path traversal rejected: '{suppliedPath}'.";
            return false;
        }

        if (segments.Any(segment => string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase)))
        {
            error = $"Git metadata path rejected: '{suppliedPath}'.";
            return false;
        }

        try
        {
            var root = Path.GetFullPath(workspacePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
            var rootPrefix = root + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (!candidate.StartsWith(rootPrefix, comparison))
            {
                error = $"Path escapes workspace: '{suppliedPath}'.";
                return false;
            }

            if (CrossesFileSystemLink(root, segments))
            {
                error = $"Symbolic link or junction path rejected: '{suppliedPath}'.";
                return false;
            }

            resolvedPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            error = $"Invalid file path '{suppliedPath}': {ex.Message}";
            return false;
        }
    }

    private static bool CrossesFileSystemLink(string root, IReadOnlyList<string> segments)
    {
        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                continue;

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
        }

        return false;
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
