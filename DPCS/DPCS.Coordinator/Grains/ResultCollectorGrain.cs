namespace DPCS.Coordinator.Grains;

public class ResultCollectorGrain : ResultCollectorGrainBase
{
    private readonly ClusterIdentity _clusterIdentity;

    public ResultCollectorGrain(IContext context, ClusterIdentity clusterIdentity) : base(context)
    {
        _clusterIdentity = clusterIdentity;

        Console.WriteLine($"{_clusterIdentity.Identity}: created");
    }

    public override Task StoreResult(WorkResult result)
    {
        return Task.CompletedTask;
    }
}