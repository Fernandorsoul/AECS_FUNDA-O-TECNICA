using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using AECS.Domain.Exceptions;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.Cryptography;

namespace AECS.Infrastructure.Repositories;

public sealed class JsonExecutionEvidenceStore : IExecutionEvidenceStore
{
    public const string CurrentSchemaVersion = EvidenceEnvelopeFormat.CurrentSchemaVersion;

    private static readonly TimeSpan EvidenceLockTimeout = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteLocks = new(
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

    private readonly string _rootPath;
    private readonly string? _keyDirectoryPath;
    private readonly Lazy<AuthenticatedEvidenceEnvelopeCodec> _envelopes;

    public JsonExecutionEvidenceStore(string rootPath)
        : this(rootPath, GetDefaultKeyDirectoryPath())
    {
    }

    public JsonExecutionEvidenceStore(string rootPath, string keyDirectoryPath)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _keyDirectoryPath = Path.GetFullPath(keyDirectoryPath);
        _envelopes = new Lazy<AuthenticatedEvidenceEnvelopeCodec>(
            () => new AuthenticatedEvidenceEnvelopeCodec(
                RsaEvidenceSignatureService.LoadOrCreate(_keyDirectoryPath)),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public JsonExecutionEvidenceStore(
        string rootPath,
        IEvidenceSignatureService signatureService)
    {
        ArgumentNullException.ThrowIfNull(signatureService);
        _rootPath = Path.GetFullPath(rootPath);
        _envelopes = new Lazy<AuthenticatedEvidenceEnvelopeCodec>(
            () => new AuthenticatedEvidenceEnvelopeCodec(signatureService));
    }

    public static string GetDefaultRootPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("AECS_EVIDENCE_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return configuredPath;

        return Path.Combine(GetAecsLocalDataRoot(), "evidence");
    }

    public static string GetDefaultKeyDirectoryPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("AECS_EVIDENCE_KEY_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return Path.GetFullPath(configuredPath);

        return Path.Combine(GetAecsLocalDataRoot(), "keys");
    }

    public void EnsureRepositoryIsolation(string repositoryPath)
    {
        var repositoryRoot = Path.GetFullPath(repositoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        EnsureOutsideRepository(_rootPath, repositoryRoot, "Evidence path");
        if (_keyDirectoryPath is not null)
            EnsureOutsideRepository(_keyDirectoryPath, repositoryRoot, "Evidence key directory");
    }

    public async Task<string> SaveAsync(
        ExecutionEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        AuthenticatedEvidenceEnvelopeCodec.ValidateEvidenceStructure(evidence);
        EnsureRepositoryIsolation(evidence.Baseline.RepositoryPath);

        Directory.CreateDirectory(_rootPath);
        var envelope = _envelopes.Value.Create(evidence);
        var targetPath = GetEvidencePath(evidence.Id);
        var temporaryPath = targetPath + $".{Guid.NewGuid():N}.tmp";

        try
        {
            await WriteEnvelopeAsync(temporaryPath, envelope, cancellationToken);
            File.Move(temporaryPath, targetPath, overwrite: false);
            return targetPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task<ExecutionEvidence?> LoadAsync(
        Guid evidenceId,
        CancellationToken cancellationToken)
    {
        var path = GetEvidencePath(evidenceId);
        if (!File.Exists(path))
            return null;

        var envelope = await ReadAndValidateEnvelopeAsync(
            path,
            evidenceId,
            cancellationToken);
        foreach (var promotionEvent in envelope.PromotionEvents)
            envelope.Evidence.Promotions.Add(promotionEvent.Promotion);
        return envelope.Evidence;
    }

    public async Task AppendPromotionAsync(
        Guid evidenceId,
        CandidatePromotionEvidence promotion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(promotion);
        var targetPath = GetEvidencePath(evidenceId);
        var writeLock = WriteLocks.GetOrAdd(targetPath, _ => new SemaphoreSlim(1, 1));
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(
                targetPath + ".lock",
                cancellationToken);
            if (!File.Exists(targetPath))
                throw new FileNotFoundException("Execution evidence not found.", targetPath);

            var envelope = await ReadAndValidateEnvelopeAsync(
                targetPath,
                evidenceId,
                cancellationToken);
            _envelopes.Value.AppendPromotion(envelope, promotion);

            var temporaryPath = targetPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await WriteEnvelopeAsync(temporaryPath, envelope, cancellationToken);
                File.Move(temporaryPath, targetPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task AppendReplayAsync(
        Guid evidenceId,
        ExecutionReplayEvidence replay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replay);
        var targetPath = GetEvidencePath(evidenceId);
        var writeLock = WriteLocks.GetOrAdd(targetPath, _ => new SemaphoreSlim(1, 1));
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(
                targetPath + ".lock",
                cancellationToken);
            if (!File.Exists(targetPath))
                throw new FileNotFoundException("Execution evidence not found.", targetPath);

            var envelope = await ReadAndValidateEnvelopeAsync(
                targetPath,
                evidenceId,
                cancellationToken);
            var existingEvent = envelope.ReplayEvents
                .FirstOrDefault(item => item.Replay.Id == replay.Id);
            if (existingEvent is not null)
            {
                if (!AuthenticatedEvidenceEnvelopeCodec.ReplayEquals(
                        existingEvent.Replay,
                        replay))
                {
                    throw new InvalidOperationException(
                        "Replay evidence ID is already associated with different content.");
                }

                return;
            }

            _envelopes.Value.AppendReplay(envelope, replay);

            var temporaryPath = targetPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await WriteEnvelopeAsync(temporaryPath, envelope, cancellationToken);
                File.Move(temporaryPath, targetPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async Task<SignedExecutionEvidenceEnvelope> ReadAndValidateEnvelopeAsync(
        string path,
        Guid expectedEvidenceId,
        CancellationToken cancellationToken)
    {
        try
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

            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("schemaVersion", out var schemaVersion) ||
                schemaVersion.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    schemaVersion.GetString(),
                    CurrentSchemaVersion,
                    StringComparison.Ordinal))
            {
                throw new EvidenceIntegrityException(
                    "Unsigned, legacy, or unsupported evidence schema was rejected.");
            }

            var envelope = document.RootElement.Deserialize<SignedExecutionEvidenceEnvelope>(
                    EvidenceEnvelopeFormat.SerializerOptions)
                ?? throw new EvidenceIntegrityException("Evidence envelope is empty.");
            if (envelope.Evidence?.Baseline is null)
                throw new EvidenceIntegrityException("Evidence envelope is incomplete.");
            EnsureRepositoryIsolation(envelope.Evidence.Baseline.RepositoryPath);
            _envelopes.Value.Validate(envelope, expectedEvidenceId);
            return envelope;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EvidenceIntegrityException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or FormatException)
        {
            throw new EvidenceIntegrityException(
                "Evidence envelope is malformed or cannot be cryptographically verified.",
                ex);
        }
    }

    private static async Task WriteEnvelopeAsync(
        string path,
        SignedExecutionEvidenceEnvelope envelope,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(
            stream,
            envelope,
            EvidenceEnvelopeFormat.SerializerOptions,
            cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<FileStream> AcquireFileLockAsync(
        string lockPath,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + EvidenceLockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }
    }

    private static string GetAecsLocalDataRoot()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
            localData = Path.GetTempPath();
        return Path.Combine(localData, "AECS");
    }

    private static void EnsureOutsideRepository(
        string targetPath,
        string repositoryRoot,
        string description)
    {
        var resolvedTarget = Path.GetFullPath(targetPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repositoryPrefix = repositoryRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(resolvedTarget, repositoryRoot, comparison) ||
            resolvedTarget.StartsWith(repositoryPrefix, comparison))
        {
            throw new InvalidOperationException(
                $"{description} '{resolvedTarget}' must be outside target repository '{repositoryRoot}'.");
        }
    }

    private string GetEvidencePath(Guid evidenceId) =>
        Path.Combine(_rootPath, $"{evidenceId:N}.json");

}
