using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DevMetrics.Infrastructure.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef migrations add</c> and
/// <c>dotnet ef database update</c> when invoked without a running host.
/// EF Core's tooling discovers this type automatically via reflection.
/// </summary>
/// <remarks>
/// Without this factory, the migration tool must be able to build and start
/// <c>DevMetrics.Api</c> in order to obtain an <see cref="AppDbContext"/>
/// from the DI container — which fails in CI or when the API project
/// has configuration dependencies. This factory provides a self-contained
/// context instance for tooling only.
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <inheritdoc/>
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=./Data/devmetrics.db")
            .Options;

        return new AppDbContext(options);
    }
}
