using System.Security.Cryptography;
using System.Text;
using AECS.Domain.Enums;
using AECS.Domain.Exceptions;
using AECS.Domain.Models;
using AECS.Infrastructure.Repositories;
using FluentAssertions;
using Npgsql;

namespace AECS.IntegrationTests;

[Trait("Category", "PostgreSql")]
public sealed class PostgreSqlExecutionEvidenceStoreTests
{
    [PostgreSqlFact]
    public async Task SaveAndLoad_PreservesTheCompleteAuthenticatedExecutionChain()
    {
        await using var fixture = PostgreSqlEvidenceFixture.Create();
        var evidence = fixture.CreateEvidence();

        var location = await fixture.Store.SaveAsync(evidence, CancellationToken.None);
        var loaded = await fixture.Store.LoadAsync(evidence.Id, CancellationToken.None);

        location.Should().Be($"postgresql://aecs/execution-evidence/{evidence.Id:N}");
        location.ToLowerInvariant().Should().NotContain("password");
        loaded.Should().NotBeNull();
        loaded!.TaskContract.Id.Should().Be(evidence.TaskContract.Id);
        loaded.AgentAttempts.Should().ContainSingle()
            .Which.DecisionReason.Should().Be("verified on first attempt");
        loaded.ContextManifest.Files.Should().ContainSingle()
            .Which.Path.Should().Be("src/Program.cs");
        loaded.BaselineCommands.Should().ContainSingle()
            .Which.Arguments.Should().Equal("test", "--no-restore");
        loaded.CandidateCommands.Should().ContainSingle()
            .Which.StandardOutput.Should().Be("candidate passed");
        loaded.AcceptanceCriteriaResults.Should().ContainSingle()
            .Which.EvidenceReferences.Should().Equal("trx:test-results.trx");
        loaded.VerificationResults.Should().ContainSingle()
            .Which.Verifier.Should().Be("Tests");
        loaded.FinalDecision.Decision.Should().Be(TaskDecision.Verified);
        loaded.Promotions.Should().BeEmpty();
    }

    [PostgreSqlFact]
    public async Task Save_IsIdempotentForTheSameAggregate()
    {
        await using var fixture = PostgreSqlEvidenceFixture.Create();
        var evidence = fixture.CreateEvidence();

        var first = await fixture.Store.SaveAsync(evidence, CancellationToken.None);
        var concurrentStores = Enumerable.Range(0, 4)
            .Select(_ => fixture.CreateStore())
            .ToList();
        var retries = await Task.WhenAll(concurrentStores.Select(store =>
            store.SaveAsync(evidence, CancellationToken.None)));

        retries.Should().OnlyContain(location => location == first);
        (await fixture.Store.LoadAsync(evidence.Id, CancellationToken.None))
            .Should().NotBeNull();
    }

    [PostgreSqlFact]
    public async Task ConcurrentAppends_AreSerializedWithoutLosingEventsAndRetriesAreIdempotent()
    {
        await using var fixture = PostgreSqlEvidenceFixture.Create();
        var evidence = fixture.CreateEvidence();
        await fixture.Store.SaveAsync(evidence, CancellationToken.None);
        var stores = Enumerable.Range(0, 8)
            .Select(_ => fixture.CreateStore())
            .ToList();
        foreach (var store in stores)
            (await store.LoadAsync(evidence.Id, CancellationToken.None)).Should().NotBeNull();

        var promotions = Enumerable.Range(0, stores.Count)
            .Select(index => fixture.CreatePromotion(evidence, $"operator-{index}"))
            .ToList();
        await Task.WhenAll(stores.Select((store, index) => store.AppendPromotionAsync(
            evidence.Id,
            promotions[index],
            CancellationToken.None)));

        var retried = promotions[0];
        await Task.WhenAll(
            stores[0].AppendPromotionAsync(evidence.Id, retried, CancellationToken.None),
            stores[1].AppendPromotionAsync(evidence.Id, retried, CancellationToken.None));

        var loaded = await fixture.Store.LoadAsync(evidence.Id, CancellationToken.None);
        loaded!.Promotions.Should().HaveCount(promotions.Count);
        loaded.Promotions.Select(item => item.Id)
            .Should().BeEquivalentTo(promotions.Select(item => item.Id));
        loaded.Promotions.Select(item => item.Actor)
            .Should().OnlyHaveUniqueItems();
    }

    [PostgreSqlFact]
    public async Task DirectDatabaseTampering_IsRejectedBeforeEvidenceIsReturned()
    {
        await using var fixture = PostgreSqlEvidenceFixture.Create();
        var evidence = fixture.CreateEvidence();
        await fixture.Store.SaveAsync(evidence, CancellationToken.None);
        await fixture.TamperDecisionAsync(evidence.Id);

        var load = () => fixture.Store.LoadAsync(evidence.Id, CancellationToken.None);

        await load.Should().ThrowAsync<EvidenceIntegrityException>()
            .WithMessage("*content hash is invalid*");
    }

    [PostgreSqlFact]
    public async Task ReplayAppend_IsLinkedAuthenticatedAndTamperEvident()
    {
        await using var fixture = PostgreSqlEvidenceFixture.Create();
        var evidence = fixture.CreateEvidence();
        await fixture.Store.SaveAsync(evidence, CancellationToken.None);
        var replay = fixture.CreateReplay(evidence);

        await fixture.Store.AppendReplayAsync(
            evidence.Id,
            replay,
            CancellationToken.None);

        (await fixture.ReplayCountAsync(evidence.Id)).Should().Be(1);
        (await fixture.Store.LoadAsync(evidence.Id, CancellationToken.None)).Should().NotBeNull();

        await fixture.TamperReplayOutcomeAsync(replay.Id);
        var load = () => fixture.Store.LoadAsync(evidence.Id, CancellationToken.None);
        await load.Should().ThrowAsync<EvidenceIntegrityException>()
            .WithMessage("*replay event 1 payload hash is invalid*");
    }

    private sealed class PostgreSqlEvidenceFixture : IAsyncDisposable
    {
        private const string TemporaryRootPrefix = "aecs-postgresql-evidence-tests-";
        private readonly HashSet<Guid> _evidenceIds = [];

        private PostgreSqlEvidenceFixture(string connectionString, string rootPath)
        {
            ConnectionString = connectionString;
            RootPath = rootPath;
            KeyDirectoryPath = Path.Combine(rootPath, "keys");
            Store = CreateStore();
        }

        public string ConnectionString { get; }
        public string RootPath { get; }
        public string KeyDirectoryPath { get; }
        public PostgreSqlExecutionEvidenceStore Store { get; }

        public static PostgreSqlEvidenceFixture Create()
        {
            var connectionString = PostgreSqlExecutionEvidenceStore.GetRequiredConnectionString();
            var rootPath = Path.Combine(
                Path.GetTempPath(),
                $"{TemporaryRootPrefix}{Guid.NewGuid():N}");
            Directory.CreateDirectory(rootPath);
            return new PostgreSqlEvidenceFixture(connectionString, rootPath);
        }

        public PostgreSqlExecutionEvidenceStore CreateStore() =>
            new(ConnectionString, KeyDirectoryPath);

        public ExecutionEvidence CreateEvidence()
        {
            var evidenceId = Guid.NewGuid();
            var taskId = $"TASK-PG-{evidenceId:N}";
            var runId = Guid.NewGuid();
            var diff = "diff --git a/file.txt b/file.txt\n-old\n+new\n";
            var now = DateTime.UtcNow;
            var evidence = new ExecutionEvidence
            {
                Id = evidenceId,
                TaskContract = new TaskContract
                {
                    Id = taskId,
                    Objective = "Persist complete PostgreSQL evidence"
                },
                AgentRun = new AgentRun
                {
                    Id = runId,
                    TaskId = taskId,
                    AgentType = "TestAgent",
                    Provider = "integration-test",
                    Model = "deterministic"
                },
                AgentResult = new AgentRunResult
                {
                    Success = true,
                    ExitCode = 0,
                    StdOut = "agent succeeded"
                },
                AgentAttempts =
                [
                    new AgentAttemptEvidence
                    {
                        AttemptNumber = 1,
                        StartedAt = now,
                        FinishedAt = now.AddMilliseconds(10),
                        Success = true,
                        DecisionReason = "verified on first attempt"
                    }
                ],
                BudgetUsage = new ExecutionBudgetEvidence
                {
                    StartedAt = now,
                    FinishedAt = now.AddMilliseconds(10),
                    MaximumAttempts = 2,
                    AttemptsUsed = 1
                },
                Baseline = new BaselineSnapshot
                {
                    Commit = "0123456789abcdef",
                    Branch = "dev",
                    RepositoryPath = Path.Combine(RootPath, "repository")
                },
                BaselineCommands =
                [
                    new ExecutionCommandEvidence
                    {
                        FileName = "dotnet",
                        Arguments = ["test", "--no-restore"],
                        WorkingDirectory = Path.Combine(RootPath, "repository"),
                        ExitCode = 0,
                        StandardOutput = "baseline passed"
                    }
                ],
                ContextManifest = new ContextManifest
                {
                    Id = "CTX-PG",
                    TaskId = taskId,
                    BaselineCommit = "0123456789abcdef",
                    ManifestHash = Hash("context"),
                    Files =
                    [
                        new ContextFileManifest
                        {
                            Path = "src/Program.cs",
                            Sha256 = Hash("source"),
                            IncludedSha256 = Hash("source")
                        }
                    ]
                },
                CandidateChangeSet = new CandidateChangeSet
                {
                    TaskId = taskId,
                    AgentRunId = runId.ToString("N"),
                    BaselineCommit = "0123456789abcdef",
                    ModifiedFiles = ["file.txt"],
                    Diff = diff,
                    DiffHash = Hash(diff)
                },
                VerificationResults =
                [
                    new VerificationResult
                    {
                        AgentRunId = runId.ToString("N"),
                        Verifier = "Tests",
                        Status = VerificationStatus.Pass,
                        Severity = Severity.Critical,
                        Message = "tests passed"
                    }
                ],
                AcceptanceCriteriaResults =
                [
                    new AcceptanceCriterionResult
                    {
                        CriterionId = "AC-001",
                        Description = "tests pass",
                        Required = true,
                        EvidenceType = AcceptanceEvidenceType.Test,
                        EvidenceReference = "tests",
                        Status = VerificationStatus.Pass,
                        EvidenceReferences = ["trx:test-results.trx"]
                    }
                ],
                CandidateCommands =
                [
                    new ExecutionCommandEvidence
                    {
                        FileName = "dotnet",
                        Arguments = ["test", "--no-restore"],
                        WorkingDirectory = Path.Combine(RootPath, "repository"),
                        ExitCode = 0,
                        StandardOutput = "candidate passed"
                    }
                ],
                FinalDecision = new FinalDecisionRecord
                {
                    Decision = TaskDecision.Verified,
                    State = TaskState.Verified,
                    Reason = "all gates passed"
                },
                StateTransitions = ["Created->Verified"]
            };
            _evidenceIds.Add(evidenceId);
            return evidence;
        }

        public CandidatePromotionEvidence CreatePromotion(
            ExecutionEvidence evidence,
            string actor) => new()
            {
                ExecutionEvidenceId = evidence.Id,
                CandidateId = evidence.CandidateChangeSet.Id,
                Action = CandidatePromotionAction.ExportPatch,
                Status = CandidatePromotionStatus.Exported,
                Eligibility = PromotionEligibility.Verified,
                Actor = actor,
                ApprovalKind = PromotionApprovalKind.Policy,
                ApprovalReference = "policy/postgresql-test",
                BaselineCommit = evidence.Baseline.Commit,
                DiffHash = evidence.CandidateChangeSet.DiffHash,
                RepositoryPath = evidence.Baseline.RepositoryPath,
                OutputPath = Path.Combine(RootPath, $"{actor}.patch"),
                Message = "concurrent PostgreSQL event"
            };

        public ExecutionReplayEvidence CreateReplay(ExecutionEvidence evidence) => new()
        {
            ExecutionEvidenceId = evidence.Id,
            CandidateId = evidence.CandidateChangeSet.Id,
            Outcome = ExecutionReplayOutcome.Reproduced,
            RepositoryPath = evidence.Baseline.RepositoryPath,
            RequestedRepositoryPath = evidence.Baseline.RepositoryPath,
            BaselineCommit = evidence.Baseline.Commit,
            ExpectedDiffHash = evidence.CandidateChangeSet.DiffHash,
            ActualDiffHash = evidence.CandidateChangeSet.DiffHash,
            Message = "PostgreSQL replay reproduced the candidate",
            StartedAt = DateTime.UtcNow.AddSeconds(-1),
            FinishedAt = DateTime.UtcNow
        };

        public async Task<int> ReplayCountAsync(Guid evidenceId)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM execution_evidence_replay_events " +
                "WHERE \"ExecutionEvidenceId\" = @evidenceId";
            command.Parameters.AddWithValue("evidenceId", evidenceId);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        public async Task TamperReplayOutcomeAsync(Guid replayId)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE execution_evidence_replay_events " +
                "SET \"ReplayJson\" = jsonb_set(\"ReplayJson\", '{outcome}', '2'::jsonb) " +
                "WHERE \"Id\" = @replayId";
            command.Parameters.AddWithValue("replayId", replayId);
            await command.ExecuteNonQueryAsync();
        }

        public async Task TamperDecisionAsync(Guid evidenceId)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE execution_evidence " +
                "SET \"EvidenceJson\" = jsonb_set(" +
                "\"EvidenceJson\", '{finalDecision,decision}', '1'::jsonb) " +
                "WHERE \"Id\" = @evidenceId";
            command.Parameters.AddWithValue("evidenceId", evidenceId);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_evidenceIds.Count > 0)
            {
                await using var connection = new NpgsqlConnection(ConnectionString);
                await connection.OpenAsync();
                foreach (var evidenceId in _evidenceIds)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        "DELETE FROM execution_evidence WHERE \"Id\" = @evidenceId";
                    command.Parameters.AddWithValue("evidenceId", evidenceId);
                    await command.ExecuteNonQueryAsync();
                }
            }

            var resolvedRoot = Path.GetFullPath(RootPath);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar);
            if (!resolvedRoot.StartsWith(
                    temporaryRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolvedRoot).StartsWith(
                    TemporaryRootPrefix,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Refusing to delete unexpected fixture path: {resolvedRoot}");
            }

            if (Directory.Exists(resolvedRoot))
                Directory.Delete(resolvedRoot, recursive: true);
        }

        private static string Hash(string value)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return $"sha256:{Convert.ToHexString(digest).ToLowerInvariant()}";
        }
    }
}

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                PostgreSqlExecutionEvidenceStore.ConnectionStringEnvironmentVariable)))
        {
            Skip = $"Set {PostgreSqlExecutionEvidenceStore.ConnectionStringEnvironmentVariable} " +
                "to run PostgreSQL integration tests.";
        }
    }
}
