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

    public override Task MaskJobSubmission(HashcatMaskJobSpecs request)
    {
        return Task.CompletedTask;
    }
    
    public override Task DictionaryJobSubmission(HashcatDictionaryJobSpecs request)
    {
        return Task.CompletedTask;
    }
}