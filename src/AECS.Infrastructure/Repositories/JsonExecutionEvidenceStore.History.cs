using System.Text.Json;
using AECS.Domain.Models;
using AECS.Infrastructure.Cryptography;

namespace AECS.Infrastructure.Repositories;

public sealed partial class JsonExecutionEvidenceStore
{
    public Task<string> SaveHistoricalDecisionAsync(
        HistoricalDecision decision,
        CancellationToken cancellationToken) => SaveHistoryAsync(
            HistoricalDecisionContract.Seal(decision),
            GetDecisionPath(decision.Id, decision.Version),
            item => item.ContentHash,
            HistoricalDecisionContract.Validate,
            cancellationToken);

    public Task<IReadOnlyList<HistoricalDecision>> LoadHistoricalDecisionsAsync(
        CancellationToken cancellationToken) => LoadHistoryAsync<HistoricalDecision>(
            GetHistoryDirectory("decisions"),
            HistoricalDecisionContract.Validate,
            cancellationToken);

    public async Task<string> SaveHistoricalDecisionSuppressionAsync(
        HistoricalDecisionSuppression suppression,
        CancellationToken cancellationToken)
    {
        var decisions = await LoadHistoricalDecisionsAsync(cancellationToken);
        if (!decisions.Any(decision =>
                decision.Id.Equals(suppression.DecisionId, StringComparison.Ordinal) &&
                decision.Version == suppression.DecisionVersion))
        {
            throw new InvalidOperationException(
                "Historical decision suppression references a decision version that is absent.");
        }
        return await SaveHistoryAsync(
            HistoricalDecisionContract.Seal(suppression),
            GetSuppressionPath(suppression.Id, suppression.Version),
            item => item.ContentHash,
            HistoricalDecisionContract.Validate,
            cancellationToken);
    }

    public Task<IReadOnlyList<HistoricalDecisionSuppression>>
        LoadHistoricalDecisionSuppressionsAsync(CancellationToken cancellationToken) =>
        LoadHistoryAsync<HistoricalDecisionSuppression>(
            GetHistoryDirectory("suppressions"),
            HistoricalDecisionContract.Validate,
            cancellationToken);

    private async Task<string> SaveHistoryAsync<T>(
        T item,
        string targetPath,
        Func<T, string> contentHash,
        Action<T, bool> validate,
        CancellationToken cancellationToken)
    {
        validate(item, true);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var writeLock = WriteLocks.GetOrAdd(targetPath, _ => new SemaphoreSlim(1, 1));
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(
                targetPath + ".lock",
                cancellationToken);
            if (File.Exists(targetPath))
            {
                var existing = await ReadHistoryAsync<T>(
                    targetPath,
                    validate,
                    cancellationToken);
                if (!string.Equals(
                        contentHash(existing),
                        contentHash(item),
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Historical record ID and version already contain different content.");
                }
                return targetPath;
            }

            var temporaryPath = targetPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 4096,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        item,
                        EvidenceEnvelopeFormat.SerializerOptions,
                        cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                File.Move(temporaryPath, targetPath, overwrite: false);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            return targetPath;
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async Task<IReadOnlyList<T>> LoadHistoryAsync<T>(
        string directory,
        Action<T, bool> validate,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
            return [];
        var items = new List<T>();
        foreach (var path in Directory.EnumerateFiles(
                     directory,
                     "*.json",
                     SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await ReadHistoryAsync<T>(path, validate, cancellationToken));
        }
        return items;
    }

    private static async Task<T> ReadHistoryAsync<T>(
        string path,
        Action<T, bool> validate,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(
            stream,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = EvidenceEnvelopeFormat.SerializerOptions.MaxDepth
            },
            cancellationToken);
        CanonicalJson.RejectDuplicateProperties(document.RootElement);
        var item = document.RootElement.Deserialize<T>(
                EvidenceEnvelopeFormat.SerializerOptions) ??
            throw new InvalidOperationException("Historical record is empty.");
        validate(item, true);
        return item;
    }

    private string GetDecisionPath(string id, int version) => Path.Combine(
        GetHistoryDirectory("decisions"),
        $"{HistoricalDecisionFingerprint.StorageKey(id)}-{version:D10}.json");

    private string GetSuppressionPath(string id, int version) => Path.Combine(
        GetHistoryDirectory("suppressions"),
        $"{HistoricalDecisionFingerprint.StorageKey(id)}-{version:D10}.json");

    private string GetHistoryDirectory(string category) =>
        Path.Combine(_rootPath, "historical-decisions", category);
}
