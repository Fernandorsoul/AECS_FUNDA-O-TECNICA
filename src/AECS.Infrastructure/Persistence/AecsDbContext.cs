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
    }
}
