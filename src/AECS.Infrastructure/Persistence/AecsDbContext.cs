using AECS.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AECS.Infrastructure.Persistence;

public class AecsDbContext : DbContext
{
    public DbSet<TaskContract> TaskContracts => Set<TaskContract>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    public DbSet<VerificationResult> VerificationResults => Set<VerificationResult>();
    public DbSet<EvidenceEvent> EvidenceEvents => Set<EvidenceEvent>();
    public DbSet<PolicyDecision> PolicyDecisions => Set<PolicyDecision>();
    public DbSet<ExperimentRun> ExperimentRuns => Set<ExperimentRun>();


    public AecsDbContext(DbContextOptions<AecsDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaskContract>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(50);
            entity.Property(e => e.Objective).HasMaxLength(2000);
            entity.OwnsOne(e => e.Scope, scope =>
            {
                scope.Property(s => s.Allowed).HasColumnType("jsonb");
                scope.Property(s => s.Forbidden).HasColumnType("jsonb");
            });
            entity.OwnsOne(e => e.Budget);
            entity.OwnsOne(e => e.Constraints);
            entity.OwnsOne(e => e.Verification);
            entity.OwnsOne(e => e.Approval);
        });

        modelBuilder.Entity<AgentRun>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TaskId).HasMaxLength(50);
            entity.Property(e => e.EstimatedCost).HasPrecision(18, 6);
            entity.HasIndex(e => e.TaskId);
        });

        modelBuilder.Entity<VerificationResult>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AgentRunId).HasMaxLength(50);
            entity.HasIndex(e => e.AgentRunId);
        });

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

        modelBuilder.Entity<PolicyDecision>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AgentRunId).HasMaxLength(50);
            entity.Property(e => e.Policy).HasMaxLength(200);
            entity.Property(e => e.Action).HasMaxLength(100);
            entity.HasIndex(e => e.AgentRunId);
        });

        modelBuilder.Entity<ExperimentRun>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TotalCost).HasPrecision(18, 6);
            entity.Property(e => e.Cpvc).HasPrecision(18, 6);
            entity.Property(e => e.ResultsJson).HasColumnType("jsonb");
            entity.HasIndex(e => e.ExecutedAt);
        });
    }
}
