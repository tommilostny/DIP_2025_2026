namespace DPCS.Agent.Grains;

public class NodeGuardianGrain : NodeGuardianGrainBase
{
    private readonly ClusterIdentity _clusterIdentity;

    public NodeGuardianGrain(IContext context, ClusterIdentity clusterIdentity) : base(context)
    {
        _clusterIdentity = clusterIdentity;

        Console.WriteLine($"{_clusterIdentity.Identity}: created");
    }

    public override Task StopWork()
    {
        return Task.CompletedTask;
    }
}