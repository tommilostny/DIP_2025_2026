using DPCS.DAL;
using DPCS.DAL.Entities;
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

        Console.WriteLine($"{_clusterIdentity.Identity}: results collector grain created");
    }

    public override async Task RegisterJob(JobRegistration request)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        
        if (!await dbContext.JobRecords.AnyAsync(j => j.JobId == request.JobId))
        {
            await dbContext.JobRecords.AddAsync(new JobRecordEntity
            {
                JobId = request.JobId,
                AttackMode = request.AttackMode,
                HashType = request.HashType,
                StartTime = request.StartTime.ToDateTime(),
                Status = "Running"
            });
            await dbContext.SaveChangesAsync();
        }
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

    public override async Task UpdateJobProgress(JobProgressUpdate request)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var jobRecord = await dbContext.JobRecords.FindAsync(request.JobId);
        
        if (jobRecord is null) return;

        jobRecord.ProgressPercentage = request.ProgressPercentage;
        jobRecord.Status = request.Status;
        
        // Mark the finish time if the job isn't actively running anymore
        if (request.Status is "Completed" or "Cancelled")
        {
            jobRecord.EndTime = DateTime.UtcNow;
        }

        await dbContext.SaveChangesAsync();
    }
}