using AECS.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AECS.Infrastructure.Persistence;

public class AecsDbContext : DbContext
{
    public DbSet<EvidenceEvent> EvidenceEvents => Set<EvidenceEvent>();
    public DbSet<ExperimentRun> ExperimentRuns => Set<ExperimentRun>();
    public DbSet<ExecutionEvidenceRecord> ExecutionEvidenceRecords =>
        Set<ExecutionEvidenceRecord>();
    public DbSet<PromotionEvidenceRecord> PromotionEvidenceRecords =>
        Set<PromotionEvidenceRecord>();
    public DbSet<ReplayEvidenceRecord> ReplayEvidenceRecords =>
        Set<ReplayEvidenceRecord>();
    public DbSet<HistoricalDecisionStorageRecord> HistoricalDecisionRecords =>
        Set<HistoricalDecisionStorageRecord>();
    public DbSet<HistoricalDecisionSuppressionStorageRecord>
        HistoricalDecisionSuppressionRecords =>
        Set<HistoricalDecisionSuppressionStorageRecord>();


    public AecsDbContext(DbContextOptions<AecsDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EvidenceEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TaskId).HasMaxLength(50);
            entity.Property(e => e.EventType).HasMaxLength(100);
            entity.Property(e => e.Authority).HasMaxLength(50);
            entity.Property(e => e.Payload).HasColumnType("jsonb");
            entity.HasIndex(e => e.TaskId);
            entity.HasIndex(e => e.OccurredAt);
        });

        modelBuilder.Entity<ExperimentRun>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TotalCost).HasPrecision(18, 6);
            entity.Property(e => e.Cpvc).HasPrecision(18, 6);
            entity.Property(e => e.ResultsJson).HasColumnType("jsonb");
            entity.HasIndex(e => e.ExecutedAt);
        });

        modelBuilder.Entity<ExecutionEvidenceRecord>(entity =>
        {
            entity.ToTable("execution_evidence", table =>
            {
                table.HasCheckConstraint(
                    "ck_execution_evidence_event_count",
                    "\"EventCount\" >= 0");
                table.HasCheckConstraint(
                    "ck_execution_evidence_schema_version",
                    "\"SchemaVersion\" = 'aecs.execution-evidence/v1'");
            });
            entity.HasKey(record => record.Id);
            entity.Property(record => record.TaskId).HasMaxLength(100).IsRequired();
            entity.Property(record => record.SchemaVersion).HasMaxLength(64).IsRequired();
            entity.Property(record => record.EvidenceContentHash).HasMaxLength(71).IsRequired();
            entity.Property(record => record.EvidenceJson).HasColumnType("jsonb").IsRequired();
            entity.Property(record => record.EvidenceSealJson).HasColumnType("jsonb").IsRequired();
            entity.Property(record => record.ChainSealJson).HasColumnType("jsonb").IsRequired();
            entity.Property(record => record.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(record => record.UpdatedAt).HasColumnType("timestamp with time zone");
            entity.HasIndex(record => record.TaskId);
            entity.HasIndex(record => record.AgentRunId).IsUnique();
            entity.HasIndex(record => record.CandidateId).IsUnique();
            entity.HasIndex(record => record.CreatedAt);
        });

        modelBuilder.Entity<PromotionEvidenceRecord>(entity =>
        {
            entity.ToTable("execution_evidence_promotion_events", table =>
                table.HasCheckConstraint(
                    "ck_execution_evidence_promotion_sequence",
                    "\"Sequence\" > 0"));
            entity.HasKey(record => record.Id);
            entity.Property(record => record.PreviousSignature).IsRequired();
            entity.Property(record => record.PromotionJson).HasColumnType("jsonb").IsRequired();
            entity.Property(record => record.SealJson).HasColumnType("jsonb").IsRequired();
            entity.Property(record => record.SignedAt).HasColumnType("timestamp with time zone");
            entity.HasIndex(record => new { record.ExecutionEvidenceId, record.Sequence })
                .IsUnique();
            entity.HasIndex(record => record.SignedAt);
            entity.HasOne(record => record.ExecutionEvidence)
                .WithMany(evidence => evidence.PromotionEvents)
                .HasForeignKey(record => record.ExecutionEvidenceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReplayEvidenceRecord>(entity =>
        {
            entity.ToTable("execution_evidence_replay_events", table =>
                table.HasCheckConstraint(
                    "ck_execution_evidence_replay_sequence",
                    "\"Sequence\" > 0"));
            entity.HasKey(record => record.Id);
            entity.Property(record => record.PreviousSignature).IsRequired();
            entity.Property(record => record.ReplayJson).HasColumnType("jsonb").IsRequired();
            entity.Property(record => record.SealJson).HasColumnType("jsonb").IsRequired();
            entity.Property(record => record.SignedAt).HasColumnType("timestamp with time zone");
            entity.HasIndex(record => new { record.ExecutionEvidenceId, record.Sequence })
                .IsUnique();
            entity.HasIndex(record => record.SignedAt);
            entity.HasOne(record => record.ExecutionEvidence)
                .WithMany(evidence => evidence.ReplayEvents)
                .HasForeignKey(record => record.ExecutionEvidenceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<HistoricalDecisionStorageRecord>(entity =>
        {
            entity.ToTable("historical_decisions", table =>
            {
                table.HasCheckConstraint(
                    "ck_historical_decisions_version",
                    "\"Version\" > 0");
                table.HasCheckConstraint(
                    "ck_historical_decisions_schema_version",
                    $"\"SchemaVersion\" = '{HistoricalDecisionSchema.DecisionVersion}'");
            });
            entity.HasKey(record => new { record.Id, record.Version });
            entity.Property(record => record.Id).HasMaxLength(200);
            entity.Property(record => record.SchemaVersion).HasMaxLength(64);
            entity.Property(record => record.Source).HasMaxLength(1000);
            entity.Property(record => record.Authority).HasMaxLength(200);
            entity.Property(record => record.ContentHash).HasMaxLength(71);
            entity.Property(record => record.DecisionJson).HasColumnType("jsonb");
            entity.Property(record => record.ValidFrom).HasColumnType("timestamp with time zone");
            entity.Property(record => record.ValidUntil).HasColumnType("timestamp with time zone");
            entity.Property(record => record.CreatedAt).HasColumnType("timestamp with time zone");
            entity.HasIndex(record => record.ReviewStatus);
            entity.HasIndex(record => record.ValidUntil);
            entity.HasIndex(record => record.Source);
        });

        modelBuilder.Entity<HistoricalDecisionSuppressionStorageRecord>(entity =>
        {
            entity.ToTable("historical_decision_suppressions", table =>
            {
                table.HasCheckConstraint(
                    "ck_historical_decision_suppressions_version",
                    "\"Version\" > 0");
                table.HasCheckConstraint(
                    "ck_historical_decision_suppressions_schema_version",
                    $"\"SchemaVersion\" = '{HistoricalDecisionSchema.SuppressionVersion}'");
            });
            entity.HasKey(record => new { record.Id, record.Version });
            entity.Property(record => record.Id).HasMaxLength(200);
            entity.Property(record => record.SchemaVersion).HasMaxLength(64);
            entity.Property(record => record.DecisionId).HasMaxLength(200);
            entity.Property(record => record.Actor).HasMaxLength(200);
            entity.Property(record => record.ContentHash).HasMaxLength(71);
            entity.Property(record => record.SuppressionJson).HasColumnType("jsonb");
            entity.Property(record => record.ExpiresAt).HasColumnType("timestamp with time zone");
            entity.Property(record => record.CreatedAt).HasColumnType("timestamp with time zone");
            entity.HasIndex(record => new { record.DecisionId, record.DecisionVersion });
            entity.HasIndex(record => record.ExpiresAt);
            entity.HasOne<HistoricalDecisionStorageRecord>()
                .WithMany()
                .HasForeignKey(record => new { record.DecisionId, record.DecisionVersion })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
