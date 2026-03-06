using System.Runtime.CompilerServices;

namespace DPCS.Coordinator.Grains;

public class JobManagerGrain : JobManagerGrainBase
{
    private readonly ClusterIdentity _clusterIdentity;

    private int _globalCursor = 0;

    private readonly Dictionary<string, JobAssignment> _unfinishedJobs = [];

    public JobManagerGrain(IContext context, ClusterIdentity clusterIdentity) : base(context)
    {
        _clusterIdentity = clusterIdentity;
        Console.WriteLine($"{_clusterIdentity.Identity}: created");
    }

    public override async Task<JobAssignment> JobDiscovery(AgentId request)
    {
        JobAssignment assignment;
        assignment = _unfinishedJobs.Count > 0
            ? _unfinishedJobs.Values.ElementAt(_globalCursor++ % _unfinishedJobs.Count)
            : new JobAssignment { ModeId = (long)AttackMode.Invalid };

        Console.WriteLine($"{_clusterIdentity.Identity}: sending job assignment to agent {request.Address}/{request.Id}: {JsonSerializer.Serialize(assignment)}");

        return await Task.FromResult(assignment);
    }

    public override async Task<JobAssignment> MaskJobSubmission(HashcatMaskJobSpecs request)
    {
        var guid = Guid.NewGuid();
        var signedJobId = JobIdSecurity.GenerateSignedId(guid);
        var assignment = new JobAssignment
        {
            JobId = signedJobId,
            ModeId = (long)AttackMode.Mask,
        };
        _unfinishedJobs[signedJobId] = assignment;

        Console.WriteLine($"{_clusterIdentity.Identity}: received mask job submission, assigned job id {signedJobId}: {JsonSerializer.Serialize(request)}");
        
        var cluster = System.Cluster();
        await cluster
            .GetJobCoordinatorGrain(assignment.JobId)
            .MaskJobInit(request, CancellationToken.None);

        return await Task.FromResult(assignment);
    }
    
    public override async Task<JobAssignment> DictionaryJobSubmission(HashcatDictionaryJobSpecs request)
    {
        var guid = Guid.NewGuid();
        var signedJobId = JobIdSecurity.GenerateSignedId(guid);
        var assignment = new JobAssignment
        {
            JobId = signedJobId,
            ModeId = (long)AttackMode.Dictionary,
        };
        _unfinishedJobs[signedJobId] = assignment;

        Console.WriteLine($"{_clusterIdentity.Identity}: received dictionary job submission, assigned job id {signedJobId}");
        
        await System.Cluster()
            .GetJobCoordinatorGrain(assignment.JobId)
            .DictionaryJobInit(request, CancellationToken.None);

        return await Task.FromResult(assignment);
    }

    public override async Task CancelJob(JobId jobId)
    {
        if (_unfinishedJobs.Remove(jobId.Id))
        {
            await System.Cluster()
                .GetJobCoordinatorGrain(jobId.Id)
                .CancelJob(CancellationToken.None);

            Console.WriteLine($"{_clusterIdentity.Identity}: cancelled job {jobId}");
        }
        else
        {
            Console.WriteLine($"{_clusterIdentity.Identity}: received cancel request with invalid job id {jobId}");
        }

        await Task.CompletedTask;
    }

    public override async Task FinishAck(JobId jobId)
    {
        if (_unfinishedJobs.Remove(jobId.Id))
        {
            Console.WriteLine($"{_clusterIdentity.Identity}: job {jobId} finished");
        }
        else
        {
            Console.WriteLine($"{_clusterIdentity.Identity}: received finish ack with invalid job id {jobId}");
        }
    }
}