using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DPCS.DAL;

public static class DpcsDbContextExtensions
{
    extension(IServiceCollection services)
    {
        public void AddDpcsDbContextFactory(string connectionString)
        {
            services.AddDbContextFactory<DpcsDbContext>(options =>
                options.UseSqlite(connectionString));
        }
    }

    extension(IServiceProvider serviceProvider)
    {
        public void EnsureDpcsDbCreated()
        {
            using var scope = serviceProvider.CreateScope();
            var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DpcsDbContext>>();
            using var dbContext = dbContextFactory.CreateDbContext();
            dbContext.Database.EnsureCreated();
        }
    }
}