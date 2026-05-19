using DPCS.DAL;
using Microsoft.EntityFrameworkCore;

namespace DPCS.Tests.Infrastructure;

public sealed class TestDbContextFactory(string connectionString) : IDbContextFactory<DpcsDbContext>
{
    public DpcsDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<DpcsDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new DpcsDbContext(options);
    }
}