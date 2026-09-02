using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AECS.Application.ContextCompiler;
using AECS.Application.AdaptiveController;
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
    public async Task AdaptiveShadow_UsesOnlyAuthenticatedHistory_AndSignalsTamperedRecord()
    {
        await using var fixture = AuthenticatedEvidenceFixture.Create();
        var repository = Path.Combine(fixture.RootPath, "repository");
        Directory.CreateDirectory(repository);
        var paths = new List<string>();
        for (var index = 0; index < 5; index++)
        {
            var context = RepositoryContextCompiler.EmptyManifest(
                "TASK-EVIDENCE-001",
                "0123456789abcdef");
            paths.Add(await fixture.Store.SaveAsync(
                fixture.CreateEvidence(
                    context,
                    $"Fix authenticated defect {index}",
                    "trusted-local-model",
                    DateTime.UnixEpoch.AddMinutes(index)),
                CancellationToken.None));
        }
        var document = JsonNode.Parse(await File.ReadAllTextAsync(paths[0]))!.AsObject();
        document["evidence"]!["agentRun"]!["model"] = "forged-model";
        await File.WriteAllTextAsync(
            paths[0],
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var capabilities = ExecutionCapabilityPolicy.RestrictiveDefault();
        var fixedPlan = new ExecutionPlan
        {
            TaskId = "TASK-34",
            Model = "qwen2.5-coder:7b",
            Risk = RiskLevel.R0,
            Budget = ExecutionBudget.Default,
            Verification = new VerificationProfile(),
            Capabilities = capabilities
        };

        var result = await new AdaptiveController(fixture.Store, fixture.Store)
            .RecommendAsync(
                repository,
                new TaskContract
                {
                    Id = "TASK-34",
                    Objective = "Fix current authenticated defect",
                    Constraints = new TaskConstraints { SecurityRisk = RiskLevel.R0 }
                },
                fixedPlan,
                CancellationToken.None);

        result.DataStatus.Should().Be(AdaptiveShadowDataStatus.InsufficientSample);
        result.Inputs.AuthenticatedRecords.Should().Be(4);
        result.Inputs.InvalidOrTamperedRecords.Should().Be(1);
        result.SourceEvidenceIds.Should().HaveCount(4);
        result.RecommendedPlan.Model.Should().Be(fixedPlan.Model);
        result.Diagnostics.Should().ContainSingle(message => message.Contains("invalid"));
    }

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
        document["evidence"]!["taskContract"]!.AsObject()
            .ContainsKey("schemaVersion").Should().BeFalse(
                "authenticated evidence created before TaskContract v1 remains explicitly legacy");
        document["evidence"]!["taskContract"]!.AsObject()
            .ContainsKey("contractFingerprint").Should().BeFalse();
        (await File.ReadAllTextAsync(path)).Should().NotContain("PRIVATE KEY");

        var restartedStore = fixture.CreateRestartedStore();
        var loaded = await restartedStore.LoadAsync(evidence.Id, CancellationToken.None);

        loaded.Should().NotBeNull();
        TaskContractIntegrity.IsLegacy(loaded!.TaskContract).Should().BeTrue();
        loaded!.CandidateChangeSet.Diff.Should().Be(evidence.CandidateChangeSet.Diff);
        loaded.FinalDecision.Decision.Should().Be(TaskDecision.Verified);
    }

    [Fact]
    public async Task Save_VersionedTaskContract_PersistsAndValidatesCanonicalFingerprint()
    {
        await using var fixture = AuthenticatedEvidenceFixture.Create();
        var contract = TaskContractIntegrity.Seal(new TaskContract
        {
            Id = "TASK-EVIDENCE-001",
            Objective = "Verify the versioned TaskContract"
        });
        var evidence = fixture.CreateEvidence(taskContract: contract);

        var path = await fixture.Store.SaveAsync(evidence, CancellationToken.None);

        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        document["evidence"]!["taskContract"]!["schemaVersion"]!.GetValue<string>()
            .Should().Be(TaskContractSchema.CurrentVersion);
        document["evidence"]!["taskContract"]!["contractFingerprint"]!.GetValue<string>()
            .Should().Be(contract.ContractFingerprint);
        var loaded = await fixture.CreateRestartedStore().LoadAsync(
            evidence.Id,
            CancellationToken.None);
        loaded.Should().NotBeNull();
        TaskContractIntegrity.ValidateForEvidence(loaded!.TaskContract);
    }

    [Fact]
    public async Task Save_VersionedTaskContractWithInvalidFingerprint_IsRejectedBeforeSigning()
    {
        await using var fixture = AuthenticatedEvidenceFixture.Create();
        var tampered = new TaskContract
        {
            SchemaVersion = TaskContractSchema.CurrentVersion,
            ContractFingerprint = "sha256:" + new string('0', 64),
            Id = "TASK-EVIDENCE-001",
            Objective = "Tamper with a versioned TaskContract"
        };
        var evidence = fixture.CreateEvidence(taskContract: tampered);

        var save = () => fixture.Store.SaveAsync(evidence, CancellationToken.None);

        await save.Should().ThrowAsync<EvidenceIntegrityException>()
            .WithMessage("*invalid TaskContract version or fingerprint*");
    }

    [Fact]
    public async Task Save_ContextManifestV2WithInvalidFingerprint_IsRejectedBeforeSigning()
    {
        await using var fixture = AuthenticatedEvidenceFixture.Create();
        var valid = RepositoryContextCompiler.EmptyManifest(
            "TASK-EVIDENCE-001",
            "0123456789abcdef");
        var tampered = new ContextManifest
        {
            SchemaVersion = valid.SchemaVersion,
            StrategyVersion = valid.StrategyVersion,
            Id = valid.Id,
            TaskId = valid.TaskId,
            BaselineCommit = valid.BaselineCommit,
            Source = valid.Source,
            Strategy = "forged-context-strategy",
            SemanticIndex = valid.SemanticIndex,
            Tokenizer = valid.Tokenizer,
            TokenizerVersion = valid.TokenizerVersion,
            ManifestHash = valid.ManifestHash
        };
        var evidence = fixture.CreateEvidence(tampered);

        var save = () => fixture.Store.SaveAsync(evidence, CancellationToken.None);

        await save.Should().ThrowAsync<EvidenceIntegrityException>()
            .WithMessage("*Context manifest*invalid fingerprint*");
    }

    [Fact]
    public async Task Save_NaiveContextManifest_PreservesAuthenticatedStrategyEvidence()
    {
        await using var fixture = AuthenticatedEvidenceFixture.Create();
        var repository = Path.Combine(fixture.RootPath, "context-repository");
        Directory.CreateDirectory(Path.Combine(repository, "src"));
        await File.WriteAllTextAsync(
            Path.Combine(repository, "src", "Policy.cs"),
            "namespace Demo; public class Policy { }");
        var context = new RepositoryContextCompiler(
                selectionStrategy: ContextStrategyIds.NaivePathOrder)
            .Compile(
                repository,
                new TaskContract
                {
                    Id = "TASK-EVIDENCE-001",
                    Objective = "Change policy",
                    Scope = new ScopeDefinition { Allowed = ["src/**"] }
                },
                "0123456789abcdef");
        var evidence = fixture.CreateEvidence(context.Manifest);

        await fixture.Store.SaveAsync(evidence, CancellationToken.None);
        var loaded = await fixture.CreateRestartedStore().LoadAsync(
            evidence.Id,
            CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.ContextManifest.StrategyVersion.Should()
            .Be(ContextManifestSchema.NaiveStrategyVersion);
        loaded.ContextManifest.Selections.Should().ContainSingle()
            .Which.Relation.Should().Be(ContextManifestSchema.NaiveStrategyId);
    }

    [Fact]
    public async Task Save_IncompleteSemanticEvidence_IsRejectedBeforeSigning()
    {
        await using var fixture = AuthenticatedEvidenceFixture.Create();
        var evidence = fixture.CreateEvidence();
        evidence.VerificationResults.Add(new VerificationResult
        {
            AgentRunId = evidence.AgentRun.Id.ToString("N"),
            Verifier = "EB003-BreakingChange",
            Status = VerificationStatus.Pass,
            Severity = Severity.Info,
            Semantic = new SemanticVerificationEvidence
            {
                BaselineCommit = evidence.Baseline.Commit,
                ImpactedFiles = ["file.txt"]
            }
        });

        var save = () => fixture.Store.SaveAsync(evidence, CancellationToken.None);

        await save.Should().ThrowAsync<EvidenceIntegrityException>()
            .WithMessage("*Semantic verification evidence*incomplete*");
    }

    [Fact]
    public async Task Save_IncompleteHistoricalDecisionEvidence_IsRejectedBeforeSigning()
    {
        await using var fixture = AuthenticatedEvidenceFixture.Create();
        var evidence = fixture.CreateEvidence();
        evidence.VerificationResults.Add(new VerificationResult
        {
            AgentRunId = evidence.AgentRun.Id.ToString("N"),
            Verifier = "EB005-HistoricalConflict",
            Status = VerificationStatus.Pass,
            Severity = Severity.Info,
            Historical = new HistoricalDecisionVerificationEvidence
            {
                Status = HistoricalDecisionSelectionStatus.Selected,
                EvaluatedAt = DateTime.UtcNow,
                Message = "forged empty selection"
            }
        });

        var save = () => fixture.Store.SaveAsync(evidence, CancellationToken.None);

        await save.Should().ThrowAsync<EvidenceIntegrityException>()
            .WithMessage("*Historical decision verification evidence*incomplete*");
    }

    [Fact]
    public async Task Save_ValidHistoricalProvenanceAndSuppression_SurvivesRestart()
    {
        await using var fixture = AuthenticatedEvidenceFixture.Create();
        var evidence = fixture.CreateEvidence();
        var evaluatedAt = new DateTime(2026, 8, 31, 18, 0, 0, DateTimeKind.Utc);
        var decision = HistoricalDecisionContract.Seal(new HistoricalDecision
        {
            Id = "POL-SEC-1",
            Version = 2,
            Type = HistoricalDecisionType.Policy,
            Source = "policies/security.json",
            SourceVersion = "git:abc123",
            SourceHash = Hash("policy-source"),
            Authority = "security-board",
            ValidFrom = evaluatedAt.AddDays(-1),
            ProhibitedPatterns =
            [
                new HistoricalDecisionPattern
                {
                    Kind = HistoricalPatternKind.TypeName,
                    Value = "LegacyHandler"
                }
            ],
            Justification = "The legacy handler must not be introduced.",
            Enforcement = HistoricalDecisionEnforcement.Blocking,
            Review = new HistoricalDecisionReview
            {
                Status = HistoricalDecisionReviewStatus.Approved,
                Authority = HistoricalDecisionReviewAuthority.Human,
                Actor = "security@example.com",
                Reason = "Source and selector reviewed.",
                ReviewedAt = evaluatedAt.AddHours(-1)
            },
            CreatedAt = evaluatedAt.AddHours(-1)
        });
        var suppression = HistoricalDecisionContract.Seal(
            new HistoricalDecisionSuppression
            {
                Id = "SUP-SEC-1",
                Version = 1,
                DecisionId = decision.Id,
                DecisionVersion = decision.Version,
                Actor = "platform-owner",
                Reason = "Temporary migration window.",
                SymbolId = "T:App.LegacyHandler",
                CreatedAt = evaluatedAt.AddMinutes(-10),
                ExpiresAt = evaluatedAt.AddDays(1)
            });
        evidence.VerificationResults.Add(new VerificationResult
        {
            AgentRunId = evidence.AgentRun.Id.ToString("N"),
            Verifier = "EB005-HistoricalConflict",
            Status = VerificationStatus.Pass,
            Severity = Severity.Warning,
            Historical = new HistoricalDecisionVerificationEvidence
            {
                Status = HistoricalDecisionSelectionStatus.Selected,
                EvaluatedAt = evaluatedAt,
                Message = "Selected 1 approved historical decision.",
                Decisions = [decision],
                Suppressions = [suppression],
                Conflicts =
                [
                    new HistoricalConflictEvidence
                    {
                        RuleId = "EB005-POLICY-CONFLICT",
                        DecisionId = decision.Id,
                        DecisionVersion = decision.Version,
                        Source = decision.Source,
                        SourceVersion = decision.SourceVersion,
                        SourceHash = decision.SourceHash,
                        Authority = decision.Authority,
                        SymbolId = suppression.SymbolId,
                        Symbol = "App.LegacyHandler",
                        FilePath = "file.txt",
                        Severity = "Error",
                        Pattern = "TypeName:LegacyHandler",
                        Justification = "Resolved type violates the reviewed policy.",
                        Suppressed = true,
                        SuppressionId = suppression.Id,
                        SuppressionVersion = suppression.Version
                    }
                ]
            }
        });

        await fixture.Store.SaveAsync(evidence, CancellationToken.None);
        var loaded = await fixture.CreateRestartedStore().LoadAsync(
            evidence.Id,
            CancellationToken.None);

        loaded!.VerificationResults.Single(result =>
                result.Verifier == "EB005-HistoricalConflict")
            .Historical!.Conflicts.Should().ContainSingle().Which.Suppressed.Should().BeTrue();
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

        public ExecutionEvidence CreateEvidence(
            ContextManifest? contextManifest = null,
            string objective = "Verify authenticated evidence",
            string model = "",
            DateTime? createdAt = null,
            TaskContract? taskContract = null)
        {
            var effectiveTaskContract = taskContract ?? new TaskContract
            {
                Id = "TASK-EVIDENCE-001",
                Objective = objective
            };
            var taskId = effectiveTaskContract.Id;
            var runId = Guid.NewGuid();
            var diff = "diff --git a/file.txt b/file.txt\n-old\n+new\n";
            return new ExecutionEvidence
            {
                TaskContract = effectiveTaskContract,
                AgentRun = new AgentRun
                {
                    Id = runId,
                    TaskId = taskId,
                    Model = model,
                    InputTokens = 100,
                    OutputTokens = 50
                },
                AgentResult = new AgentRunResult
                {
                    Success = true,
                    Duration = TimeSpan.FromSeconds(5)
                },
                Baseline = new BaselineSnapshot
                {
                    Commit = "0123456789abcdef",
                    Branch = "main",
                    RepositoryPath = Path.Combine(RootPath, "repository")
                },
                ContextManifest = contextManifest ?? new ContextManifest(),
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
                },
                CreatedAt = createdAt ?? DateTime.UtcNow
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
