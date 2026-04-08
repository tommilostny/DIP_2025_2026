using DPCS.DAL.Entities;
using Microsoft.EntityFrameworkCore;

namespace DPCS.DAL;

public class DpcsDbContext(DbContextOptions<DpcsDbContext> options) : DbContext(options)
{
    public DbSet<CrackedPasswordEntity> CrackedPasswords { get; set; }
    public DbSet<JobRecordEntity> JobRecords { get; set; }
}