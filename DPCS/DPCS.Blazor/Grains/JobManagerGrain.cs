using DPCS.Blazor.Security;

namespace DPCS.Blazor.Grains;

public sealed class JobManagerGrain : JobManagerGrainBase
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
        switch (_unfinishedJobs)
        {
            case { Count: 0 }:
                return new JobAssignment { ModeId = (int)AttackMode.Invalid };
            default:
                var assignment = _unfinishedJobs.Values.ElementAt(_globalCursor++ % _unfinishedJobs.Count);
                Console.WriteLine($"{_clusterIdentity.Identity}: assigning job {assignment.JobId} to agent {request.Address}/{request.Id}");
                return await Task.FromResult(assignment);
        }
    }

    public override async Task<JobAssignment> MaskJobSubmission(HashcatMaskJobSpecs request)
    {
        var guid = Guid.NewGuid();
        var signedJobId = JobIdSecurity.GenerateSignedId(guid);
        var assignment = new JobAssignment
        {
            JobId = signedJobId,
            ModeId = (int)AttackMode.Mask,
            HashType = request.HashType,
            Hashes = { request.Hashes },
            Masks = { request.Masks },
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
            ModeId = (int)AttackMode.Dictionary,
            HashType = request.HashType,
            Hashes = { request.Hashes },
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
    }

    public override async Task<JobsCollection> ListJobs()
    {
        return await Task.FromResult(new JobsCollection
        {
           Jobs = { _unfinishedJobs.Values }
        });
    }
}