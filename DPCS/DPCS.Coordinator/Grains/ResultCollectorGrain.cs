using DPCS.Coordinator.Entities;
using Microsoft.EntityFrameworkCore;

namespace DPCS.Coordinator.Grains;

public sealed class ResultCollectorGrain : ResultCollectorGrainBase
{
    private readonly ClusterIdentity _clusterIdentity;
    private readonly IDbContextFactory<DpcsDbContext> _dbContextFactory;

    public ResultCollectorGrain(IContext context, ClusterIdentity clusterIdentity, IDbContextFactory<DpcsDbContext> dbContextFactory) : base(context)
    {
        _clusterIdentity = clusterIdentity;
        _dbContextFactory = dbContextFactory;

        Console.WriteLine($"{_clusterIdentity.Identity}: created");
    }

    public override async Task StoreResult(JobResult result)
    {
        Console.WriteLine($"{_clusterIdentity.Identity}: storing result for job {result.JobId}, recovered passwords count: {result.RecoveredPasswords.Count}");
        
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        foreach (var recovered in result.RecoveredPasswords)
        {
            if (await dbContext.CrackedPasswords.AnyAsync(p => p.Hash == recovered.Hash))
            {
                continue;
            }
            await dbContext.CrackedPasswords.AddAsync(new CrackedPasswordEntity
            {
                Hash = recovered.Hash,
                Plaintext = recovered.Plaintext,
                HashType = recovered.HashType,
                JobId = result.JobId,
                AttackMode = result.AttackMode,
                CrackedAt = result.CrackedAt.ToDateTime(),
                TimeTaken = result.TimeTaken.ToTimeSpan(),
            });
        }
        await dbContext.SaveChangesAsync();
    }
}