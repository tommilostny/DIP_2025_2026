using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DPCS.DAL;

public class DesignTimeDpcsDbContextFactory : IDesignTimeDbContextFactory<DpcsDbContext>
{
    public DpcsDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<DpcsDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Port=5432;Database=dpcs;Username=postgres;Password=password123");

        return new DpcsDbContext(optionsBuilder.Options);
    }
}