using AECS.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AECS.Infrastructure.Persistence;

public sealed class AecsDbContextFactory : IDesignTimeDbContextFactory<AecsDbContext>
{
    public AecsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(
            PostgreSqlExecutionEvidenceStore.ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = "Host=localhost;Database=aecs;Username=aecs";

        var options = new DbContextOptionsBuilder<AecsDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new AecsDbContext(options);
    }
}
