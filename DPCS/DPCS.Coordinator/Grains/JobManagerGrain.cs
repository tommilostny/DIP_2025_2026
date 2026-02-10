namespace DPCS.Coordinator.Grains;

public class JobManagerGrain : JobManagerGrainBase
{
    private readonly ClusterIdentity _clusterIdentity;

    private Dictionary<ClusterIdentity, object> _jobs = [];

    public JobManagerGrain(IContext context, ClusterIdentity clusterIdentity) : base(context)
    {
        _clusterIdentity = clusterIdentity;

        Console.WriteLine($"{_clusterIdentity.Identity}: created");
    }

    public override async Task<JobAssignment> JobDiscovery()
    {
        return await Task.FromResult(new JobAssignment());
    }

    public override async Task<JobAssignment> MaskJobSubmission(HashcatMaskJobSpecs request)
    {
        var job = new JobAssignment
        {
            JobId = Guid.NewGuid().ToString(),
            ModeId = 0,
        };
        return await Task.FromResult(job);
    }
    
    public override async Task<JobAssignment> DictionaryJobSubmission(HashcatDictionaryJobSpecs request)
    {
        var job = new JobAssignment
        {
            JobId = Guid.NewGuid().ToString(),
            ModeId = 1,
        };
        return await Task.FromResult(job);
    }
}