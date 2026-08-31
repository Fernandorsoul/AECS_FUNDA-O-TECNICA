using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AECS.Domain.Enums;
using AECS.Domain.Exceptions;
using AECS.Domain.Models;
using AECS.Infrastructure.Cryptography;
using AECS.Infrastructure.Repositories;
using FluentAssertions;

namespace AECS.IntegrationTests;

public sealed class AuthenticatedEvidenceStoreTests
{
    [Fact]
    public async Task Save_WritesSignedVersionedEnvelope_ThatSurvivesStoreRestart()
    {
        await using var fixture = AuthenticatedEvidenceFixture.Create();
        var evidence = fixture.CreateEvidence();

        var path = await fixture.Store.SaveAsync(evidence, CancellationToken.None);

        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        document["schemaVersion"]!.GetValue<string>()
            .Should().Be(JsonExecutionEvidenceStore.CurrentSchemaVersion);
        document["seal"]!["algorithm"]!.GetValue<string>()
            .Should().Be(RsaEvidenceSignatureService.AlgorithmName);
        document["seal"]!["keyId"]!.GetValue<string>().Should().StartWith("sha256:");
        document["seal"]!["payloadSha256"]!.GetValue<string>().Should().StartWith("sha256:");
        document["seal"]!["signature"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        document["chainSeal"]!["signature"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        document["promotionEvents"]!.AsArray().Should().BeEmpty();
        (await File.ReadAllTextAsync(path)).Should().NotContain("PRIVATE KEY");

        var restartedStore = fixture.CreateRestartedStore();
        var loaded = await restartedStore.LoadAsync(evidence.Id, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.CandidateChangeSet.Diff.Should().Be(evidence.CandidateChangeSet.Diff);
        loaded.FinalDecision.Decision.Should().Be(TaskDecision.Verified);
    }

    [Fact]
    public async Task CoordinatedDiffHashAndDecisionTampering_IsRejected()
    {
        await using var fixture = AuthenticatedEvidenceFixture.Create();
        var evidence = fixture.CreateEvidence();
        await fixture.Store.SaveAsync(evidence, CancellationToken.None);

        await fixture.MutateEnvelopeAsync(evidence.Id, root =>
        {
            var candidate = root["evidence"]!["candidateChangeSet"]!;
            candidate["diff"] = "malicious replacement";
            candidate["diffHash"] = Hash("malicious replacement");
            root["evidence"]!["finalDecision"]!["decision"] = (int)TaskDecision.Rejected;
        });

        var load = () => fixture.Store.LoadAsync(evidence.Id, CancellationToken.None);
        await load.Should().ThrowAsync<EvidenceIntegrityException>()
            .WithMessage("*payload hash is invalid*");
    }

    [Fact]
    public async Task UnsignedLegacyEvidence_IsRejectedFailClosed()
    {
        await using var fixture = AuthenticatedEvidenceFixture.Create();
        var evidence = fixture.CreateEvidence();
        Directory.CreateDirectory(fixture.EvidencePath);
        await File.WriteAllTextAsync(
            fixture.GetEvidencePath(evidence.Id),
            JsonSerializer.Serialize(evidence));

        var load = () => fixture.Store.LoadAsync(evidence.Id, CancellationToken.None);
        await load.Should().ThrowAsync<EvidenceIntegrityException>()
            .WithMessage("*Unsigned, legacy, or unsupported*");
    }

    [Fact]
    public async Task IncompleteEvidence_IsRejectedFailClosed()
    {
        await using var fixture = AuthenticatedEvidenceFixture.Create();
        var evidence = fixture.CreateEvidence();
        await fixture.Store.SaveAsync(evidence, CancellationToken.None);
        await fixture.MutateEnvelopeAsync(evidence.Id, root => root["evidence"] = null);

        var load = () => fixture.Store.LoadAsync(evidence.Id, CancellationToken.None);
        await load.Should().ThrowAsync<EvidenceIntegrityException>()
            .WithMessage("*incomplete*");
    }

    [Fact]
    public async Task UnknownSigningKey_IsRejectedFailClosed()
    {
        await using var fixture = AuthenticatedEvidenceFixture.Create();
        var evidence = fixture.CreateEvidence();
        await fixture.Store.SaveAsync(evidence, CancellationToken.None);
        var untrustedKeyDirectory = Path.Combine(fixture.RootPath, "untrusted-keys");
        var untrustedStore = new JsonExecutionEvidenceStore(
            fixture.EvidencePath,
            RsaEvidenceSignatureService.LoadOrCreate(untrustedKeyDirectory));

        var load = () => untrustedStore.LoadAsync(evidence.Id, CancellationToken.None);
        await load.Should().ThrowAsync<EvidenceIntegrityException>()
            .WithMessage("*signature is invalid or its key is not trusted*");
    }

    [Fact]
    public async Task TamperedKeyId_IsRejectedFailClosed()
    {
        await using var fixture = AuthenticatedEvidenceFixture.Create();
        var evidence = fixture.CreateEvidence();
        await fixture.Store.SaveAsync(evidence, CancellationToken.None);
        await fixture.MutateEnvelopeAsync(
            evidence.Id,
            root => root["seal"]!["keyId"] = $"sha256:{new string('0', 64)}");

        var load = () => fixture.Store.LoadAsync(evidence.Id, CancellationToken.None);
        await load.Should().ThrowAsync<EvidenceIntegrityException>()
            .WithMessage("*payload hash is invalid*");
    }

    [Fact]
    public async Task PromotionEvents_AreSignedAndOrderTamperingIsRejected()
    {
        await using var fixture = AuthenticatedEvidenceFixture.Create();
        var evidence = fixture.CreateEvidence();
        await fixture.Store.SaveAsync(evidence, CancellationToken.None);
        await fixture.Store.AppendPromotionAsync(
            evidence.Id,
            fixture.CreatePromotion(evidence, "first"),
            CancellationToken.None);
        await fixture.Store.AppendPromotionAsync(
            evidence.Id,
            fixture.CreatePromotion(evidence, "second"),
            CancellationToken.None);

        var loaded = await fixture.Store.LoadAsync(evidence.Id, CancellationToken.None);
        loaded!.Promotions.Select(item => item.Actor).Should().Equal("first", "second");

        await fixture.MutateEnvelopeAsync(evidence.Id, root =>
        {
            var events = root["promotionEvents"]!.AsArray();
            var first = events[0]!.DeepClone();
            var second = events[1]!.DeepClone();
            events.Clear();
            events.Add(second);
            events.Add(first);
        });

        var load = () => fixture.Store.LoadAsync(evidence.Id, CancellationToken.None);
        await load.Should().ThrowAsync<EvidenceIntegrityException>()
            .WithMessage("*sequence*invalid*");
    }

    [Fact]
    public async Task PromotionApprovalTampering_IsRejected()
    {
        await using var fixture = AuthenticatedEvidenceFixture.Create();
        var evidence = fixture.CreateEvidence();
        await fixture.Store.SaveAsync(evidence, CancellationToken.None);
        await fixture.Store.AppendPromotionAsync(
            evidence.Id,
            fixture.CreatePromotion(evidence, "operator"),
            CancellationToken.None);
        await fixture.MutateEnvelopeAsync(
            evidence.Id,
            root => root["promotionEvents"]![0]!["promotion"]!["approvalReference"] =
                "forged/approval");

        var load = () => fixture.Store.LoadAsync(evidence.Id, CancellationToken.None);
        await load.Should().ThrowAsync<EvidenceIntegrityException>()
            .WithMessage("*payload hash is invalid*");
    }

    [Fact]
    public async Task PromotionTailDeletion_IsRejectedBySignedChainHead()
    {
        await using var fixture = AuthenticatedEvidenceFixture.Create();
        var evidence = fixture.CreateEvidence();
        await fixture.Store.SaveAsync(evidence, CancellationToken.None);
        await fixture.Store.AppendPromotionAsync(
            evidence.Id,
            fixture.CreatePromotion(evidence, "first"),
            CancellationToken.None);
        await fixture.Store.AppendPromotionAsync(
            evidence.Id,
            fixture.CreatePromotion(evidence, "second"),
            CancellationToken.None);
        await fixture.MutateEnvelopeAsync(
            evidence.Id,
            root => root["promotionEvents"]!.AsArray().RemoveAt(1));

        var load = () => fixture.Store.LoadAsync(evidence.Id, CancellationToken.None);
        await load.Should().ThrowAsync<EvidenceIntegrityException>()
            .WithMessage("*evidence chain head payload hash is invalid*");
    }

    [Fact]
    public async Task ReplayAndPromotionEvents_ShareOneAuthenticatedOrderedChain()
    {
        await using var fixture = AuthenticatedEvidenceFixture.Create();
        var evidence = fixture.CreateEvidence();
        await fixture.Store.SaveAsync(evidence, CancellationToken.None);
        await fixture.Store.AppendPromotionAsync(
            evidence.Id,
            fixture.CreatePromotion(evidence, "first"),
            CancellationToken.None);
        await fixture.Store.AppendReplayAsync(
            evidence.Id,
            fixture.CreateReplay(evidence),
            CancellationToken.None);
        await fixture.Store.AppendPromotionAsync(
            evidence.Id,
            fixture.CreatePromotion(evidence, "second"),
            CancellationToken.None);

        var envelope = await fixture.ReadEnvelopeAsync(evidence.Id);
        envelope["promotionEvents"]![0]!["sequence"]!.GetValue<int>().Should().Be(1);
        envelope["replayEvents"]![0]!["sequence"]!.GetValue<int>().Should().Be(2);
        envelope["promotionEvents"]![1]!["sequence"]!.GetValue<int>().Should().Be(3);
        (await fixture.Store.LoadAsync(evidence.Id, CancellationToken.None))!
            .Promotions.Should().HaveCount(2);

        await fixture.MutateEnvelopeAsync(
            evidence.Id,
            root => root["replayEvents"]![0]!["replay"]!["message"] = "forged replay");
        var load = () => fixture.Store.LoadAsync(evidence.Id, CancellationToken.None);
        await load.Should().ThrowAsync<EvidenceIntegrityException>()
            .WithMessage("*replay event 2 payload hash is invalid*");
    }

    [Fact]
    public async Task KeyRotation_PreservesOldVerificationAndSignsNewEventsWithNewKey()
    {
        await using var fixture = AuthenticatedEvidenceFixture.Create();
        var evidence = fixture.CreateEvidence();
        await fixture.Store.SaveAsync(evidence, CancellationToken.None);
        var before = await fixture.ReadEnvelopeAsync(evidence.Id);
        var oldKeyId = before["seal"]!["keyId"]!.GetValue<string>();

        var newKeyId = RsaEvidenceSignatureService.RotateKey(fixture.KeyDirectoryPath);
        var rotatedStore = fixture.CreateRestartedStore();
        (await rotatedStore.LoadAsync(evidence.Id, CancellationToken.None)).Should().NotBeNull();
        await rotatedStore.AppendPromotionAsync(
            evidence.Id,
            fixture.CreatePromotion(evidence, "after-rotation"),
            CancellationToken.None);

        var after = await fixture.ReadEnvelopeAsync(evidence.Id);
        after["seal"]!["keyId"]!.GetValue<string>().Should().Be(oldKeyId);
        after["promotionEvents"]![0]!["seal"]!["keyId"]!.GetValue<string>()
            .Should().Be(newKeyId).And.NotBe(oldKeyId);
        (await fixture.CreateRestartedStore().LoadAsync(evidence.Id, CancellationToken.None))!
            .Promotions.Should().ContainSingle();
    }

    [Fact]
    public async Task DuplicateJsonProperty_IsRejectedBeforeDeserialization()
    {
        await using var fixture = AuthenticatedEvidenceFixture.Create();
        var evidence = fixture.CreateEvidence();
        await fixture.Store.SaveAsync(evidence, CancellationToken.None);
        var path = fixture.GetEvidencePath(evidence.Id);
        var json = await File.ReadAllTextAsync(path);
        json = json.Replace(
            "{\r\n  \"schemaVersion\"",
            "{\r\n  \"schemaVersion\": \"forged\",\r\n  \"schemaVersion\"",
            StringComparison.Ordinal);
        if (!json.Contains("\"schemaVersion\": \"forged\"", StringComparison.Ordinal))
        {
            json = json.Replace(
                "{\n  \"schemaVersion\"",
                "{\n  \"schemaVersion\": \"forged\",\n  \"schemaVersion\"",
                StringComparison.Ordinal);
        }
        await File.WriteAllTextAsync(path, json);

        var load = () => fixture.Store.LoadAsync(evidence.Id, CancellationToken.None);
        await load.Should().ThrowAsync<EvidenceIntegrityException>()
            .WithMessage("*malformed or cannot be cryptographically verified*");
    }

    [Fact]
    public async Task Save_KeyDirectoryInsideTargetRepository_IsRejectedBeforeKeyCreation()
    {
        await using var fixture = AuthenticatedEvidenceFixture.Create();
        var repositoryPath = Path.Combine(fixture.RootPath, "repository");
        var unsafeKeyPath = Path.Combine(repositoryPath, ".aecs", "keys");
        Directory.CreateDirectory(repositoryPath);
        var store = new JsonExecutionEvidenceStore(fixture.EvidencePath, unsafeKeyPath);
        var evidence = fixture.CreateEvidence();

        var act = () => store.SaveAsync(evidence, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Evidence key directory*must be outside target repository*");
        Directory.Exists(unsafeKeyPath).Should().BeFalse();
    }

    private static string Hash(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"sha256:{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private sealed class AuthenticatedEvidenceFixture : IAsyncDisposable
    {
        private const string TemporaryRootPrefix = "aecs-authenticated-evidence-tests-";

        private AuthenticatedEvidenceFixture(string rootPath)
        {
            RootPath = rootPath;
            EvidencePath = Path.Combine(rootPath, "evidence");
            KeyDirectoryPath = Path.Combine(rootPath, "keys");
            Store = CreateRestartedStore();
        }

        public string RootPath { get; }
        public string EvidencePath { get; }
        public string KeyDirectoryPath { get; }
        public JsonExecutionEvidenceStore Store { get; }

        public static AuthenticatedEvidenceFixture Create()
        {
            var rootPath = Path.Combine(
                Path.GetTempPath(),
                $"{TemporaryRootPrefix}{Guid.NewGuid():N}");
            Directory.CreateDirectory(rootPath);
            return new AuthenticatedEvidenceFixture(rootPath);
        }

        public JsonExecutionEvidenceStore CreateRestartedStore() =>
            new(EvidencePath, KeyDirectoryPath);

        public ExecutionEvidence CreateEvidence()
        {
            var taskId = "TASK-EVIDENCE-001";
            var runId = Guid.NewGuid();
            var diff = "diff --git a/file.txt b/file.txt\n-old\n+new\n";
            return new ExecutionEvidence
            {
                TaskContract = new TaskContract
                {
                    Id = taskId,
                    Objective = "Verify authenticated evidence"
                },
                AgentRun = new AgentRun { Id = runId, TaskId = taskId },
                AgentResult = new AgentRunResult { Success = true },
                Baseline = new BaselineSnapshot
                {
                    Commit = "0123456789abcdef",
                    Branch = "main",
                    RepositoryPath = Path.Combine(RootPath, "repository")
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
                FinalDecision = new FinalDecisionRecord
                {
                    Decision = TaskDecision.Verified,
                    State = TaskState.Verified,
                    Reason = "all required gates passed"
                }
            };
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
                ApprovalReference = "policy/test",
                BaselineCommit = evidence.Baseline.Commit,
                DiffHash = evidence.CandidateChangeSet.DiffHash,
                RepositoryPath = evidence.Baseline.RepositoryPath,
                OutputPath = Path.Combine(RootPath, $"{actor}.patch"),
                Message = "test event"
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
            Message = "candidate reproduced",
            StartedAt = DateTime.UtcNow.AddSeconds(-1),
            FinishedAt = DateTime.UtcNow
        };

        public string GetEvidencePath(Guid evidenceId) =>
            Path.Combine(EvidencePath, $"{evidenceId:N}.json");

        public async Task<JsonObject> ReadEnvelopeAsync(Guid evidenceId) =>
            JsonNode.Parse(await File.ReadAllTextAsync(GetEvidencePath(evidenceId)))!.AsObject();

        public async Task MutateEnvelopeAsync(
            Guid evidenceId,
            Action<JsonObject> mutate)
        {
            var root = await ReadEnvelopeAsync(evidenceId);
            mutate(root);
            await File.WriteAllTextAsync(
                GetEvidencePath(evidenceId),
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        public ValueTask DisposeAsync()
        {
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
            {
                foreach (var file in Directory.EnumerateFiles(
                             resolvedRoot,
                             "*",
                             SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(resolvedRoot, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
