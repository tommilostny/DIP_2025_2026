using DPCS.Coordinator.Entities;
using Microsoft.EntityFrameworkCore;

namespace DPCS.Coordinator;

public class DpcsDbContext(DbContextOptions<DpcsDbContext> options) : DbContext(options)
{
    public DbSet<CrackedPasswordEntity> CrackedPasswords { get; set; }
}